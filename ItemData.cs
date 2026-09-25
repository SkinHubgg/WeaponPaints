using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WeaponPaints
{
	/// <summary>
	/// Loads the item datasets (skins, gloves, agents, music kits, collectibles, and the optional pets) the
	/// menus and the weapon code read from.
	///
	/// The datasets used to be shipped inside the plugin and read synchronously with File.ReadAllText
	/// during Load(). They are now pulled from <see cref="WeaponPaintsConfig.DataUrl"/> on plugin load,
	/// on a worker thread — nothing on the game thread ever waits for the network.
	///
	/// Each dataset has exactly two sources, in order:
	///   1. "&lt;DataUrl&gt;/data/&lt;dataset&gt;.json", requested conditionally — the ETag and Last-Modified of
	///      the cached copy are sent back as If-None-Match / If-Modified-Since, so an unchanged dataset
	///      comes back as a 304 with no body: one request, no transfer, and the cache is kept.
	///   2. "&lt;plugin folder&gt;/.cache/&lt;dataset&gt;.json" — the last response that parsed, written by the plugin
	///      itself (nothing is shipped in the archive), written atomically so an unreachable DataUrl keeps a
	///      server running with the items it had before. Serving it always logs a warning with the cache's
	///      age. Overridable with WeaponPaintsConfig.CacheDirectory, which is what a server whose plugin
	///      folder is an ephemeral or read-only mount should use; the plugin runs without a cache at all if
	///      the directory cannot be used.
	///
	/// What happens when that is not enough:
	///   * every dataset resolved, but some only from the cache → the plugin runs, warns per dataset, and
	///     retries in the background (<see cref="RetryDelaysSeconds"/>) so a CDN that recovers within a few
	///     minutes is picked up without a restart;
	///   * any dataset with no source at all on the first pass → there is nothing to serve, so the plugin
	///     **refuses to run**: it asks CounterStrikeSharp to terminate it and does not retry. The server is
	///     never taken down; only the plugin stops.
	///
	/// Nothing in here throws: every failure becomes a log line and, at worst, that termination request.
	/// </summary>
	internal static class ItemData
	{
		internal const string DefaultDataUrl = "https://cdn.skinhub.gg";

		// Image for the synthesised "Default" rows, the same URL the old shipped files carried. It points at
		// upstream's repository, so removing this fork's website folder does not affect it.
		private const string DefaultSkinImageBase =
			"https://raw.githubusercontent.com/Nereziel/cs2-WeaponPaints/main/website/img/skins/";

		// Plugin-relative and self-contained, so the plugin makes no assumption about the server's layout.
		// Servers that keep the plugin folder on an ephemeral or read-only mount should point
		// WeaponPaintsConfig.CacheDirectory somewhere persistent - "../.cache" puts it beside the plugin
		// folder rather than inside it.
		private const string CacheDirectoryName = ".cache";
		private const string LegacyCacheDirectoryName = "data-cache";

		private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

		// Bounded on purpose - five retries then it stops, rather than hammering a dead CDN forever.
		// Only datasets that did not come back fresh are retried, so a run stops early once all five are.
		// internal rather than private so the test harness can shorten it; not part of the config surface.
		internal static int[] RetryDelaysSeconds = [15, 30, 60, 120, 300];

		private static readonly object Gate = new();
		private static Task? _current;

		private static int _ready;
		private static int _lateJoinLogged;
		private static int _cacheWriteFailureLogged;

		/// <summary>True once every dataset has data, from the network or from the cache.</summary>
		internal static bool Ready => Volatile.Read(ref _ready) == 1;

		private enum Source
		{
			/// <summary>Nothing usable. Fatal on the first pass — there is nothing to serve.</summary>
			None,

			/// <summary>Cache served because DataUrl could not be reached. Warned about, and retried.</summary>
			StaleCache,

			/// <summary>Read from disk without contacting DataUrl at all, because CacheDiskHours said so.</summary>
			CacheByPolicy,

			/// <summary>DataUrl answered 304: the cached copy is current. As good as fresh.</summary>
			NotModified,

			/// <summary>Downloaded and cached.</summary>
			Network
		}

		/// <summary>A source that means the data is current; anything else is worth retrying.</summary>
		private static bool IsFresh(Source source) =>
			source is Source.Network or Source.NotModified or Source.CacheByPolicy;

		/// <summary>
		/// Validators stored beside a cached payload so the next start can revalidate it cheaply. The URL they
		/// came from is part of it on purpose: validators are only meaningful to the host that issued them, and
		/// sending one host's ETag to another invites a 304 that would silently bless the wrong payload.
		/// </summary>
		private sealed record CacheMeta(
			[property: JsonProperty("url")] string? Url,
			[property: JsonProperty("etag")] string? ETag,
			[property: JsonProperty("lastModified")] string? LastModified,
			[property: JsonProperty("fetchedAt")] DateTimeOffset FetchedAt);

		/// <param name="Optional">
		/// A dataset the plugin can run without. It never makes the plugin refuse to run and is not retried when it
		/// has no source at all - pets.json, which a CDN older than the 1.41.8.2 export simply does not have, and
		/// which Pets.cs covers with a built-in list.
		/// </param>
		/// <param name="RowsProperty">
		/// When the payload is an object rather than an array, the property holding the rows. data/pets.json is
		/// `{ petItemDefindex, attributes, stages, pets: [...] }`; everything else is a bare array. A bare array is
		/// still accepted for these, so the plugin does not care which of the two a CDN publishes.
		/// </param>
		private sealed record Dataset(
			string Name,
			Action<List<JObject>> Apply,
			Func<List<JObject>, List<JObject>>? Prepare = null,
			bool Optional = false,
			string? RowsProperty = null);

		#region Reading a published skin row

		// SkinsList holds rows exactly as the CDN publishes them - nothing is renamed or rewritten. Two of
		// the fields cannot be read directly though, and that is what these accessors are for:
		//
		//   paint_index   is a JSON *string* ("44"), and null on the 20 vanilla-knife rows. Reading it as
		//                 w["paint_index"]?.ToObject<int>() throws ArgumentException on those rows, and they
		//                 are reached on the weapon-spawn path, so every read goes through SkinPaint.
		//   weapon.id     is an SFUI token on those same vanilla rows ("sfui_wpnhud_knifekaram"), never a
		//                 classname. The classname therefore comes from WeaponPaints.WeaponDefindex.
		//                 THIS LOOKUP IS DELIBERATE AND MUST STAY - it is not a leftover of the translation
		//                 layer that used to live here. Without it every knife disappears from !skins.

		/// <summary>Weapon definition index of a published skin row, or null if it has none.</summary>
		internal static int? SkinDefindex(JObject skin) => skin["weapon"]?["weapon_id"]?.Value<int?>();

		/// <summary>
		/// Paint kit of a published skin row. A null paint_index means the vanilla item, which the plugin
		/// represents as paint 0. Returns null - never throws - when paint_index is present but not a
		/// number, so a malformed row simply matches nothing.
		/// </summary>
		internal static int? SkinPaint(JObject skin)
		{
			var paintIndex = skin["paint_index"];
			if (paintIndex is null || paintIndex.Type == JTokenType.Null) return 0;

			return int.TryParse(paintIndex.ToString(), out var paint) ? paint : null;
		}

		/// <summary>
		/// Weapon classname of a published skin row, or null if its defindex is not a weapon this plugin
		/// handles. Resolved through WeaponPaints.WeaponDefindex rather than weapon.id (see above); that is
		/// also the second line of defence keeping glove rows out of the weapon menus.
		/// </summary>
		internal static string? SkinWeaponName(JObject skin) =>
			SkinDefindex(skin) is { } defindex && WeaponPaints.WeaponDefindex.TryGetValue(defindex, out var name)
				? name
				: null;

		/// <summary>legacy_model of a published skin row, false when absent.</summary>
		internal static bool SkinIsLegacyModel(JObject skin) => skin["legacy_model"]?.Value<bool?>() ?? false;

		#endregion

		// stickers and keychains are deliberately absent: the plugin reads those out of the
		// wp_player_skins columns, it never needs the catalogues. pets is optional - see Dataset.
		private static readonly Dataset[] Datasets =
		[
			new("skins", list => WeaponPaints.SkinsList = list, PrepareSkins),
			new("gloves", list => WeaponPaints.GlovesList = list),
			new("agents", list => WeaponPaints.AgentsList = list),
			new("music", list => WeaponPaints.MusicList = list),
			new("collectibles", list => WeaponPaints.PinsList = list),
			new("pets", list => WeaponPaints.PetsList = list, PreparePets, Optional: true, RowsProperty: "pets")
		];

		/// <summary>
		/// Starts a load pass and returns immediately. Safe to fire and forget: every failure mode is
		/// handled inside, so the returned task never faults. Calling it while a pass is still running
		/// returns that pass instead of starting a second one.
		/// </summary>
		/// <param name="onUnrecoverable">
		/// Invoked, from the worker thread, when at least one dataset has no source at all on the first pass -
		/// i.e. there is nothing to serve and retrying cannot help. The caller decides how to stop the plugin;
		/// ItemData only decides that it must stop.
		/// </param>
		internal static Task LoadAsync(string moduleDirectory, string moduleVersion,
			WeaponPaintsConfig config, ILogger logger, Action<string> onUnrecoverable)
		{
			var baseUrl = (string.IsNullOrWhiteSpace(config.DataUrl) ? DefaultDataUrl : config.DataUrl.Trim())
				.TrimEnd('/');
			var cacheDirectory = ResolveCacheDirectory(config.CacheDirectory, moduleDirectory);

			lock (Gate)
			{
				if (_current is { IsCompleted: false })
				{
					logger.LogWarning("Item data is already loading - not starting a second pass");
					return _current;
				}

				Interlocked.Exchange(ref _ready, 0);
				Interlocked.Exchange(ref _lateJoinLogged, 0);
				Interlocked.Exchange(ref _cacheWriteFailureLogged, 0);

				_current = Task.Run(() =>
					RunAsync(baseUrl, cacheDirectory, moduleDirectory, moduleVersion, config, logger,
						onUnrecoverable));
				return _current;
			}
		}

		/// <summary>
		/// Logs, at most once per load pass, that somebody connected before the data was ready. Cheap
		/// enough for the connect path: one volatile read when the data is already there.
		/// </summary>
		internal static void WarnIfNotReady(ILogger logger)
		{
			if (Ready) return;
			if (Interlocked.Exchange(ref _lateJoinLogged, 1) == 1) return;

			logger.LogWarning(
				"A player connected before the item data was ready - they get default items until it arrives");
		}

		private static async Task RunAsync(string baseUrl, string cacheDirectory, string moduleDirectory,
			string moduleVersion, WeaponPaintsConfig config, ILogger logger, Action<string> onUnrecoverable)
		{
			try
			{
				// Probed once per pass, not per dataset, so an unusable directory produces one line and not five.
				var cache = PrepareCacheDirectory(cacheDirectory, moduleDirectory, logger);

				if (!string.IsNullOrWhiteSpace(config.SkinsLanguage) &&
				    !config.SkinsLanguage.Trim().Equals("en", StringComparison.OrdinalIgnoreCase))
					logger.LogWarning(
						"SkinsLanguage is \"{Language}\" but it no longer does anything - {BaseUrl} publishes item " +
						"data in English only, and the plugin no longer ships localised files to fall back on",
						config.SkinsLanguage, baseUrl);

				using var client = new HttpClient { Timeout = RequestTimeout };
				client.DefaultRequestHeaders.Add("User-Agent", $"WeaponPaints/{moduleVersion}");

				// Datasets still wanted from the network. One served from cache because the fetch failed stays
				// in here, so a CDN that recovers mid-map replaces the stale copy; a fresh one drops out.
				var pending = Datasets.ToList();
				var stale = new HashSet<string>();
				var outcome = new Dictionary<string, Source>();
				var stopwatch = Stopwatch.StartNew();

				for (var attempt = 0; attempt <= RetryDelaysSeconds.Length; attempt++)
				{
					if (attempt > 0)
					{
						var delay = RetryDelaysSeconds[attempt - 1];
						logger.LogWarning(
							"{Count} dataset(s) not confirmed current by {BaseUrl} - retry {Attempt} of {Total} in {Delay}s",
							pending.Count, baseUrl, attempt, RetryDelaysSeconds.Length, delay);
						await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
					}

					var results = await Task.WhenAll(pending.Select(dataset => LoadDatasetAsync(
						dataset, client, baseUrl, cache, config.CacheDiskHours, logger)))
						.ConfigureAwait(false);

					for (var i = 0; i < pending.Count; i++) outcome[pending[i].Name] = results[i];

					var stillPending = new List<Dataset>();
					for (var i = 0; i < pending.Count; i++)
					{
						if (IsFresh(results[i]))
						{
							stale.Remove(pending[i].Name);
							continue;
						}

						if (results[i] == Source.StaleCache) stale.Add(pending[i].Name);

						// An optional dataset with nothing at all is most likely just not published yet (a 404),
						// and the plugin has a fallback for it - say so once and stop asking until the next load.
						if (pending[i].Optional && results[i] == Source.None)
						{
							logger.LogInformation(
								"{Dataset} is not available from {BaseUrl} and there is no cached copy - carrying on " +
								"without it (it is optional). It is fetched again on the next plugin load.",
								pending[i].Name, baseUrl);
							continue;
						}

						stillPending.Add(pending[i]);
					}

					// A dataset with no source at all cannot be fixed by waiting: there is nothing to serve.
					// Refuse to run rather than sit here pretending to work. Only the first pass can do this -
					// once data is in memory a later failure just leaves the previous copy in place.
					if (attempt == 0)
					{
						var nothing = pending.Where((dataset, i) => results[i] == Source.None && !dataset.Optional)
							.Select(d => d.Name).ToList();
						if (nothing.Count > 0)
						{
							var reason =
								$"no item data for {string.Join(", ", nothing)}: {baseUrl} could not be used and there " +
								$"is {(cache is null ? "no usable cache directory" : $"no usable cache in \"{cache}\"")}";
							logger.LogError(
								"{Reason}. Refusing to run rather than serve default items to everyone - the server " +
								"itself is unaffected. Check DataUrl, outbound HTTPS from this machine, and that the " +
								"plugin folder is writable, then reload the plugin.",
								reason);
							onUnrecoverable(reason);
							return;
						}
					}

					pending = stillPending;
					Interlocked.Exchange(ref _ready, 1);

					if (pending.Count == 0)
					{
						logger.LogInformation("Item data ready in {Elapsed} ms - {Summary}",
							stopwatch.ElapsedMilliseconds, Summarise(outcome));
						return;
					}
				}

				logger.LogWarning(
					"Item data is still being served from cache for {Names} - {BaseUrl} did not answer within " +
					"{Attempts} retries. Players have items, but they are as old as the cache above. Reload the " +
					"plugin to try again. Outcome: {Summary}",
					string.Join(", ", stale), baseUrl, RetryDelaysSeconds.Length, Summarise(outcome));
			}
			catch (Exception ex)
			{
				// Must never surface: this task is fire-and-forget from OnConfigParsed.
				logger.LogError(ex, "Unexpected failure while loading item data");
			}
		}

		/// <summary>
		/// Works out where the cache lives. Pure string logic with no I/O, so it can be verified without a
		/// running server.
		///
		/// The default is "&lt;ModuleDirectory&gt;/.cache". A configured path wins, and a relative one is resolved
		/// against the plugin directory rather than the process working directory - which for a game server is
		/// nowhere anybody would expect files to appear. That is what makes "../.cache" mean "beside the plugin
		/// folder", the setting for a server whose plugin folder is an ephemeral or read-only mount.
		/// </summary>
		internal static string ResolveCacheDirectory(string? configured, string moduleDirectory)
		{
			if (string.IsNullOrWhiteSpace(configured))
				return Path.GetFullPath(Path.Combine(moduleDirectory, CacheDirectoryName));

			var trimmed = configured.Trim();
			return Path.IsPathRooted(trimmed)
				? Path.GetFullPath(trimmed)
				: Path.GetFullPath(Path.Combine(moduleDirectory, trimmed));
		}

		/// <summary>
		/// Makes sure the cache directory exists and is writable, returning null when it is not - in which case
		/// the plugin runs from DataUrl with no offline fallback. Probed once per pass so an unusable directory
		/// produces a single clear line rather than one per dataset.
		/// </summary>
		private static string? PrepareCacheDirectory(string cacheDirectory, string moduleDirectory, ILogger logger)
		{
			try
			{
				Directory.CreateDirectory(cacheDirectory);

				// Creating a directory can succeed on a read-only mount in some configurations; a write cannot.
				var probe = Path.Combine(cacheDirectory, ".write-probe");
				File.WriteAllText(probe, string.Empty);
				File.Delete(probe);
			}
			catch (Exception ex)
			{
				logger.LogWarning(
					"Cannot use \"{Directory}\" for the item data cache ({Reason}). The plugin will run from DataUrl " +
					"as normal, but it has no offline fallback: if DataUrl is unreachable on a later start it will " +
					"refuse to run. Point CacheDirectory at a writable path to fix this.",
					cacheDirectory, ex.Message);
				return null;
			}

			// One-time note for anyone upgrading from a build that cached in "data-cache".
			var legacy = Path.Combine(moduleDirectory, LegacyCacheDirectoryName);
			if (Directory.Exists(legacy) &&
			    !string.Equals(Path.GetFullPath(legacy), cacheDirectory, StringComparison.Ordinal))
				logger.LogInformation(
					"The item data cache is now \"{Directory}\"; the old \"{Legacy}\" is no longer read and can be " +
					"deleted. This start re-downloads the datasets once.",
					cacheDirectory, legacy);

			return cacheDirectory;
		}

		private static async Task<Source> LoadDatasetAsync(Dataset dataset, HttpClient client, string baseUrl,
			string? cacheDirectory, int cacheDiskHours, ILogger logger)
		{
			var url = $"{baseUrl}/data/{dataset.Name}.json";
			// null means the cache directory is unusable: fetch, but neither read nor write the cache.
			var cacheFile = cacheDirectory is null ? null : Path.Combine(cacheDirectory, $"{dataset.Name}.json");
			var metaFile = cacheFile is null ? null : $"{cacheFile}.meta";
			var cached = cacheFile is not null && File.Exists(cacheFile);
			var cachedAt = cached ? File.GetLastWriteTimeUtc(cacheFile!) : DateTime.UtcNow;
			var cacheAge = DateTime.UtcNow - cachedAt;
			// Validators only count if they came from this URL - see CacheMeta.
			var meta = cached ? ReadMeta(metaFile!) : null;
			if (meta is not null && !string.Equals(meta.Url, url, StringComparison.Ordinal)) meta = null;

			// Inside CacheDiskHours the copy on disk is used as-is and DataUrl is not contacted at all. Past the
			// window we fall through to the conditional request below, which is still how freshness is
			// established - this only skips the check, it does not replace it.
			if (cached && cacheDiskHours > 0 && cacheAge < TimeSpan.FromHours(cacheDiskHours))
			{
				var fromDisk = await TryLoadCacheAsync(dataset, cacheFile!, logger).ConfigureAwait(false);
				if (fromDisk is { } diskCount)
				{
					logger.LogInformation(
						"{Dataset}: read from disk without checking {Url} - {Count} entries, cached {Age} ago, " +
						"inside CacheDiskHours={Hours}. Anything published since then will not appear until that " +
						"window passes (set CacheDiskHours to 0 to always check).",
						dataset.Name, url, diskCount, Describe(cacheAge), cacheDiskHours);
					return Source.CacheByPolicy;
				}

				logger.LogWarning(
					"Cached {Dataset} is unusable, so CacheDiskHours cannot be honoured - fetching instead",
					dataset.Name);
				meta = null;
			}

			// Two attempts at most: the second only happens if the server answered 304 but our cache turned out
			// to be unreadable, in which case we ask again without validators to get a full body.
			for (var conditional = meta is not null && cached; ; conditional = false)
			{
				try
				{
					using var request = new HttpRequestMessage(HttpMethod.Get, url);
					if (conditional) ApplyValidators(request, meta!);

					using var response = await client.SendAsync(request).ConfigureAwait(false);

					// 304 must be handled before EnsureSuccessStatusCode: it is not a 2xx, and its body is
					// empty, so falling through would either throw or look like an empty dataset.
					if (response.StatusCode == HttpStatusCode.NotModified)
					{
						var unchanged = await TryLoadCacheAsync(dataset, cacheFile!, logger).ConfigureAwait(false);
						if (unchanged is { } unchangedCount)
						{
							logger.LogInformation(
								"{Dataset} unchanged since {Published} - {Url} answered 304 with no body, using the " +
								"cached copy ({Count} entries)",
								dataset.Name, meta?.LastModified ?? "the last fetch", url, unchangedCount);
							// The payload is confirmed current, so age from now on means "since last confirmed".
							Revalidated(cacheFile!, metaFile!, meta, logger);
							return Source.NotModified;
						}

						logger.LogWarning(
							"{Url} says our cached {Dataset} is current (304) but that cache cannot be read - asking " +
							"for the full body instead",
							url, dataset.Name);
						continue;
					}

					// An optional dataset the CDN has not published yet answers 404. That is expected, not a
					// failure: without a cached copy, RunAsync logs the one "carrying on without it" line, so a
					// warning here would only make an optional dataset look broken.
					if (dataset.Optional && response.StatusCode == HttpStatusCode.NotFound)
					{
						logger.LogDebug("{Url} answered 404 - {Dataset} is not published there", url, dataset.Name);
						break;
					}

					response.EnsureSuccessStatusCode();
					var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

					if (TryApply(dataset, json, logger, out var count))
					{
						logger.LogInformation("Loaded {Count} {Dataset} from {Url}", count, dataset.Name, url);
						if (cacheFile is not null) WriteCache(cacheFile, metaFile!, url, json, response, logger);
						return Source.Network;
					}

					logger.LogWarning("{Url} returned no usable {Dataset} - trying the cache", url, dataset.Name);
				}
				catch (Exception ex)
				{
					// Covers a refused connection, DNS failure, TLS failure, a non-2xx status, a malformed
					// DataUrl (UriFormatException / InvalidOperationException) and the request timeout.
					logger.LogWarning("Could not fetch {Dataset} from {Url} ({Reason}) - trying the cache",
						dataset.Name, url, ex.Message);
				}

				break;
			}

			if (!cached) return Source.None;

			var fallback = await TryLoadCacheAsync(dataset, cacheFile!, logger).ConfigureAwait(false);
			if (fallback is not { } cachedCount) return Source.None;

			// Every cache hit that happens because the CDN could not be reached says so, with the age.
			logger.LogWarning(
				"Serving CACHED {Dataset} ({Count} entries): {BaseUrl} could not be reached. Last confirmed " +
				"current {Age} ago ({At:u}){Published}. These items may be out of date.",
				dataset.Name, cachedCount, baseUrl, Describe(cacheAge), cachedAt,
				meta?.LastModified is { } published ? $"; published {published}" : "");

			return Source.StaleCache;
		}

		/// <summary>Reads a cached payload, or null if it is missing or unusable.</summary>
		private static async Task<int?> TryLoadCacheAsync(Dataset dataset, string cacheFile, ILogger logger)
		{
			try
			{
				var json = await File.ReadAllTextAsync(cacheFile).ConfigureAwait(false);
				if (TryApply(dataset, json, logger, out var count)) return count;

				logger.LogError("Cached {Dataset} at \"{File}\" is unusable", dataset.Name, cacheFile);
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Could not read cached {Dataset} at \"{File}\"", dataset.Name, cacheFile);
			}

			return null;
		}

		private static void ApplyValidators(HttpRequestMessage request, CacheMeta meta)
		{
			if (!string.IsNullOrWhiteSpace(meta.ETag) &&
			    EntityTagHeaderValue.TryParse(meta.ETag, out var tag))
				request.Headers.IfNoneMatch.Add(tag);

			if (!string.IsNullOrWhiteSpace(meta.LastModified) &&
			    DateTimeOffset.TryParse(meta.LastModified, CultureInfo.InvariantCulture,
				    DateTimeStyles.AdjustToUniversal, out var lastModified))
				request.Headers.IfModifiedSince = lastModified;
		}

		private static CacheMeta? ReadMeta(string metaFile)
		{
			try
			{
				return File.Exists(metaFile)
					? JsonConvert.DeserializeObject<CacheMeta>(File.ReadAllText(metaFile))
					: null;
			}
			catch (Exception)
			{
				// A missing or corrupt validator file only costs one unconditional request.
				return null;
			}
		}

		/// <summary>Records that the cached payload was confirmed current, without rewriting the payload.</summary>
		private static void Revalidated(string cacheFile, string metaFile, CacheMeta? meta, ILogger logger)
		{
			try
			{
				File.SetLastWriteTimeUtc(cacheFile, DateTime.UtcNow);
				if (meta is not null) WriteMeta(metaFile, meta with { FetchedAt = DateTimeOffset.UtcNow });
			}
			catch (Exception ex)
			{
				logger.LogWarning("Could not record the revalidation of \"{File}\" ({Reason})", cacheFile, ex.Message);
			}
		}

		/// <summary>
		/// One line saying which path every dataset took, so a report of "my skins are old" can be answered
		/// from the console without guessing.
		/// </summary>
		private static string Summarise(IReadOnlyDictionary<string, Source> outcome)
		{
			var parts = new List<string>();

			foreach (var (source, label) in new[]
			         {
				         (Source.Network, "downloaded"),
				         (Source.NotModified, "unchanged, kept cache (304)"),
				         (Source.CacheByPolicy, "read from disk unchecked (CacheDiskHours)"),
				         (Source.StaleCache, "STALE cache, DataUrl unreachable"),
				         (Source.None, "MISSING")
			         })
			{
				var names = outcome.Where(entry => entry.Value == source).Select(entry => entry.Key).ToList();
				if (names.Count > 0) parts.Add($"{label}: {string.Join(", ", names)}");
			}

			return parts.Count > 0 ? string.Join(" | ", parts) : "nothing loaded";
		}

		private static string Describe(TimeSpan age) => age switch
		{
			{ TotalMinutes: < 2 } => "less than a minute",
			{ TotalHours: < 2 } => $"{(int)age.TotalMinutes} minutes",
			{ TotalDays: < 2 } => $"{(int)age.TotalHours} hours",
			_ => $"{(int)age.TotalDays} days"
		};

		private static bool TryApply(Dataset dataset, string json, ILogger logger, out int count)
		{
			count = 0;

			List<JObject>? parsed;
			try
			{
				parsed = dataset.RowsProperty is null
					? JsonConvert.DeserializeObject<List<JObject>>(json)
					: JToken.Parse(json) switch
					{
						JArray rows => rows.OfType<JObject>().ToList(),
						JObject root when root[dataset.RowsProperty] is JArray rows => rows.OfType<JObject>().ToList(),
						_ => null
					};
			}
			catch (JsonException ex)
			{
				logger.LogError("{Dataset} is not a JSON array of objects ({Reason})", dataset.Name, ex.Message);
				return false;
			}

			if (parsed is null || parsed.Count == 0) return false;

			var list = dataset.Prepare is not null ? dataset.Prepare(parsed) : parsed;
			if (list.Count == 0) return false;

			// Published only once it parsed: a bad response can never blank a list that already has data.
			dataset.Apply(list);
			count = list.Count;
			return true;
		}

		/// <summary>
		/// Writes the payload, then the validators that let the next start revalidate it with a conditional
		/// request. Payload first on purpose: a missing or stale .meta only costs one unconditional request,
		/// whereas a validator newer than its payload would make us trust a 304 for data we do not have.
		/// </summary>
		private static void WriteCache(string cacheFile, string metaFile, string url, string json,
			HttpResponseMessage response, ILogger logger)
		{
			if (!WriteAtomic(cacheFile, json, logger)) return;

			WriteMeta(metaFile, new CacheMeta(
				url,
				response.Headers.ETag?.ToString(),
				response.Content.Headers.LastModified?.ToString("o", CultureInfo.InvariantCulture),
				DateTimeOffset.UtcNow));
		}

		private static void WriteMeta(string metaFile, CacheMeta meta)
		{
			// Best effort: without it the next start simply sends no validators.
			try
			{
				File.WriteAllText(metaFile, JsonConvert.SerializeObject(meta));
			}
			catch (Exception)
			{
				// Ignored deliberately - see above.
			}
		}

		/// <summary>Writes through a temporary file so a half-written file can never be read.</summary>
		private static bool WriteAtomic(string path, string contents, ILogger logger)
		{
			var temporaryFile = $"{path}.{Environment.ProcessId}.tmp";

			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				File.WriteAllText(temporaryFile, contents);
				// Rename over the target: atomic on the same volume, so readers see old or new, never half.
				File.Move(temporaryFile, path, true);
				return true;
			}
			catch (Exception ex)
			{
				// Gated: five datasets failing the same way should not produce five identical lines.
				if (Interlocked.Exchange(ref _cacheWriteFailureLogged, 1) == 0)
					logger.LogWarning(
						"Could not write the item data cache at \"{File}\" ({Reason}) - the plugin keeps working, but " +
						"it has no offline fallback, and it will refuse to start if DataUrl is unreachable on a later " +
						"start. Point CacheDirectory at a writable path to fix this. Further cache write failures in " +
						"this pass are not repeated.",
						path, ex.Message);

				try
				{
					if (File.Exists(temporaryFile)) File.Delete(temporaryFile);
				}
				catch (Exception)
				{
					// Nothing useful to do about a leftover temp file.
				}

				return false;
			}
		}

		/// <summary>
		/// Prepares the published skin catalogue for use. It does **not** translate it: every row keeps the
		/// fields the CDN published, and the call sites in Commands.cs and WeaponAction.cs read those fields
		/// through the accessors above. Two things happen here that cannot be done at a call site:
		///
		///   1. Rows whose weapon defindex is not a weapon this plugin handles are dropped. In practice that is
		///      the 94 glove rows the catalogue carries (defindex 4725 and 5027-5035, "weapon.id" = sporty_gloves
		///      and friends), which belong in GlovesList. Filtering here rather than at each read site is
		///      deliberate: the rows are never in SkinsList at all, so a future enumeration that forgets to
		///      filter cannot put a glove in a weapon menu. If nothing at all survives the filter, an empty list
		///      is returned so the caller treats the payload as unusable and falls back to the cache instead of
		///      quietly serving nothing but Defaults.
		///
		///   2. A "Default" row is synthesised for every weapon that has none. The catalogue contains no
		///      paint 0 rows, and without these every weapon loses its Default entry in the !skins menu.
		///      THIS IS A GAP IN THE PUBLISHED DATA, NOT A SHAPE DIFFERENCE. The exporter should emit these
		///      rows; when it does, delete this loop and nothing else changes. The rows are built in the
		///      published shape, exactly like every other row, and the dedupe below means no duplicates appear
		///      in the meantime.
		/// </summary>
		private static List<JObject> PrepareSkins(List<JObject> source)
		{
			var prepared = new List<JObject>(source.Count + WeaponPaints.WeaponDefindex.Count);
			var withDefault = new HashSet<int>();

			foreach (var entry in source)
			{
				// 1. keep only rows for weapons this plugin handles
				if (SkinWeaponName(entry) is null) continue;

				prepared.Add(entry);

				if (SkinPaint(entry) == 0 && SkinDefindex(entry) is { } defindex) withDefault.Add(defindex);
			}

			if (prepared.Count == 0) return [];

			// 2. synthesise the missing Defaults - remove once the exporter emits paint 0 rows
			foreach (var (defindex, classname) in WeaponPaints.WeaponDefindex)
			{
				if (!withDefault.Add(defindex)) continue;

				var display = WeaponPaints.WeaponList.GetValueOrDefault(classname, classname);

				prepared.Add(new JObject
				{
					["weapon"] = new JObject
					{
						["id"] = classname,
						["weapon_id"] = defindex,
						["name"] = display
					},
					["paint_index"] = 0,
					["name"] = $"{display} | Default",
					["image"] = $"{DefaultSkinImageBase}{classname}.png",
					["legacy_model"] = false
				});
			}

			// Grouped per weapon with Default first, which is the order the menus read the list in.
			return prepared
				.OrderBy(entry => SkinDefindex(entry) ?? 0)
				.ThenBy(entry => SkinPaint(entry) ?? int.MaxValue)
				.ToList();
		}

		#region Reading a published pet row

		// data/pets.json rows (one per items_game pet_definitions entry), as the SkinHub exporter publishes them:
		//   { id, name, displayName, locName, kind: "egg"|"chick"|"adult", breed, model, modelKey, glb, icon }
		// Only id, displayName, kind, breed and model are read here. `model` is a VPK path the plugin hands to the
		// game as a spawn keyvalue and a precache entry, so it is only accepted when it looks like one.

		/// <summary>A published pet row as the plugin uses it, or null if the row is unusable.</summary>
		internal static WeaponPaints.PetDefinition? ParsePet(JObject row)
		{
			if (row["id"]?.Type != JTokenType.Integer || row["id"]!.Value<int>() is not (> 0 and var id)) return null;

			var model = row["model"]?.Type == JTokenType.String ? row["model"]!.Value<string>() : null;
			if (model is null || !model.StartsWith("models/", StringComparison.Ordinal) ||
			    !model.EndsWith(".vmdl", StringComparison.Ordinal) || model.Contains(".."))
				return null;

			var kind = row["kind"]?.Type == JTokenType.String ? row["kind"]!.Value<string>() : null;
			if (kind is not ("egg" or "chick" or "adult")) return null;

			var displayName = row["displayName"]?.Type == JTokenType.String ? row["displayName"]!.Value<string>() : null;
			var breed = row["breed"]?.Type == JTokenType.String ? row["breed"]!.Value<string>() : null;

			return new WeaponPaints.PetDefinition(id, string.IsNullOrWhiteSpace(displayName) ? $"Pet {id}" : displayName,
				kind, breed, model);
		}

		#endregion

		/// <summary>Keeps the pet rows the plugin can use. An empty result makes the payload count as unusable.</summary>
		private static List<JObject> PreparePets(List<JObject> source) =>
			source.Where(row => ParsePet(row) is not null).ToList();
	}
}
