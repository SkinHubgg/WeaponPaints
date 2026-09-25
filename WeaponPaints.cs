using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace WeaponPaints;

[MinimumApiVersion(338)]
public partial class WeaponPaints : BasePlugin, IPluginConfig<WeaponPaintsConfig>
{
	internal static WeaponPaints Instance { get; private set; } = new();

	public WeaponPaintsConfig Config { get; set; } = new();
    private static WeaponPaintsConfig _config { get; set; } = new();
    public override string ModuleAuthor => "Nereziel & daffyy";
	public override string ModuleDescription => "Skin, gloves, agents, knife and pet selector, standalone and web-based";
	public override string ModuleName => "WeaponPaints";
	public override string ModuleVersion => "3.4b";

	public override void Load(bool hotReload)
	{
		// Hardcoded hotfix needs to be changed later (Not needed 17.09.2025)
		//if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		//	Patch.PerformPatch("0F 85 ? ? ? ? 31 C0 B9 ? ? ? ? BA ? ? ? ? 66 0F EF C0 31 F6 31 FF 48 C7 45 ? ? ? ? ? 48 C7 45 ? ? ? ? ? 48 C7 45 ? ? ? ? ? 48 C7 45 ? ? ? ? ? 0F 29 45 ? 48 C7 45 ? ? ? ? ? C7 45 ? ? ? ? ? 66 89 45 ? E8 ? ? ? ? 41 89 C5 85 C0 0F 8E", "90 90 90 90 90 90");
		//else
		//	Patch.PerformPatch("74 ? 48 8D 0D ? ? ? ? FF 15 ? ? ? ? EB ? BA", "EB");
		
		Instance = this;

		if (hotReload)
		{
			OnMapStart(string.Empty);
			
			GPlayerWeaponsInfo.Clear();
			GPlayersKnife.Clear();
			GPlayersGlove.Clear();
			GPlayersAgent.Clear();
			GPlayersPin.Clear();
			GPlayersMusic.Clear();
			GPlayersPet.Clear();

			foreach (var player in Enumerable
				         .OfType<CCSPlayerController>(Utilities.GetPlayers().TakeWhile(_ => WeaponSync != null))
				         .Where(player => player.IsValid &&
					         !string.IsNullOrEmpty(player.IpAddress) && player is
						         { IsBot: false, Connected: PlayerConnectedState.Connected }))
			{
				var playerInfo = new PlayerInfo
				{
					UserId = player.UserId,
					Slot = player.Slot,
					Index = (int)player.Index,
					SteamId = player?.SteamID.ToString(),
					Name = player?.PlayerName,
					IpAddress = player?.IpAddress?.Split(":")[0]
				};

				_ = Task.Run(async () =>
				{
					if (WeaponSync != null) await WeaponSync.GetPlayerData(playerInfo);
				});
			}
		}

		// Item data is not read here any more - OnConfigParsed already started fetching it, see ItemData.
		RegisterListeners();
	}

	public void OnConfigParsed(WeaponPaintsConfig config)
	{
		Config = config;
		_config = config;

		if (config.DatabaseHost.Length < 1 || config.DatabaseName.Length < 1 || config.DatabaseUser.Length < 1)
		{
			// Was Unload(false), which is a no-op - BasePlugin.Unload has an empty body and this plugin only
			// overrides it to remove its pets (Pets.cs), so the plugin used to carry on loading with no database
			// at all. See Disable().
			Disable("Database credentials are not set in \"configs/plugins/WeaponPaints/WeaponPaints.json\"");
			return;
		}

		if (!File.Exists(Path.GetDirectoryName(Path.GetDirectoryName(ModuleDirectory)) + "/gamedata/weaponpaints.json"))
		{
			Disable("\"weaponpaints.json\" is missing from the \"gamedata\" directory");
			return;
		}
		
		var builder = new MySqlConnectionStringBuilder
		{
			Server = config.DatabaseHost,
			UserID = config.DatabaseUser,
			Password = config.DatabasePassword,
			Database = config.DatabaseName,
			Port = (uint)config.DatabasePort,
			Pooling = true,
			MaximumPoolSize = 640,
		};

		Database = new Database(builder.ConnectionString);

		_ = Utility.CheckDatabaseTables();
		_localizer = Localizer;

		Utility.Config = config;
		Utility.ShowAd(ModuleVersion);
		Task.Run(async () => await Utility.CheckVersion(ModuleVersion, Logger));

		// Earliest point at which DataUrl is known. Deliberately not awaited: the fetch runs on a worker
		// thread while the server finishes starting, and nothing on the game thread waits for it. Players
		// who connect before it lands get default items and a warning in the log. If it finds nothing to
		// serve at all it calls Disable() rather than letting the plugin pretend to work.
		_ = ItemData.LoadAsync(ModuleDirectory, ModuleVersion, config, Logger, Disable);
	}

	/// <summary>
	/// Stops this plugin without touching the server. CounterStrikeSharp's supported path for this is
	/// BasePlugin.TerminateSelf, which forwards to PluginContext via ISelfPluginControl.
	///
	/// Two things about it are worth writing down, both verified against CounterStrikeSharp.API 1.0.367:
	///   * it is safe to call from a worker thread - PluginContext.TerminateSelf checks
	///     Thread.CurrentThread.IsThreadPoolThread and marshals itself onto the main thread with
	///     Server.NextFrame when needed, so ItemData can call this straight from its fetch task;
	///   * after queueing (or performing) the termination it unconditionally throws NotImplementedException
	///     at the caller. The termination still happens; the exception is leftover scaffolding in that
	///     version. Hence the catch below - without it this would look like a crash instead of a clean stop.
	///
	/// BasePlugin.Unload(bool) is NOT an alternative: its body is empty, so calling it on yourself does
	/// nothing at all (the override in Pets.cs only removes the pets).
	/// </summary>
	private void Disable(string reason)
	{
		Logger.LogError("Disabling WeaponPaints: {Reason}. The server is unaffected.", reason);

		try
		{
			TerminateSelf($"WeaponPaints: {reason}");
		}
		catch (NotImplementedException)
		{
			// Expected, see above. The plugin is being terminated regardless.
		}
		catch (Exception ex)
		{
			Logger.LogError(ex,
				"CounterStrikeSharp refused to terminate the plugin. It will stay loaded but will not work - " +
				"unload it manually with \"css_plugins unload WeaponPaints\"");
		}
	}

	public override void OnAllPluginsLoaded(bool hotReload)
	{
		try
		{
			MenuApi = MenuCapability.Get();
			
			if (Config.Additional.KnifeEnabled)
				SetupKnifeMenu();
			if (Config.Additional.SkinEnabled)
				SetupSkinsMenu();
			if (Config.Additional.GloveEnabled)
				SetupGlovesMenu();
			if (Config.Additional.AgentEnabled)
				SetupAgentsMenu();
			if (Config.Additional.MusicEnabled)
				SetupMusicMenu();
			if (Config.Additional.PinsEnabled)
				SetupPinsMenu();
			if (Config.Additional.PetsEnabled)
				SetupPetsMenu();
		
			RegisterCommands();
		}
		catch (Exception)
		{
			MenuApi = null;
			Logger.LogError("Error while loading required plugins");
			throw;
		}
	}
}
