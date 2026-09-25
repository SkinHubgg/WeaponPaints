using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
// Only Vector3: all of System.Numerics would make CounterStrikeSharp's Vector ambiguous.
using Vector3 = System.Numerics.Vector3;

namespace WeaponPaints;

/// <summary>A player's pet, exactly as wp_player_pets stores it.</summary>
public class PetInfo
{
	/// <summary>pet_definitions id: 1 egg, 2 chick, 3 catalana, 4 silkie, 5 polish.</summary>
	public int PetId { get; set; }

	/// <summary>The "upgrade level" attribute: 0 egg, 1 chick, 2 pullet, 3 hen.</summary>
	public int Stage { get; set; } = WeaponPaints.PetStageHen;

	/// <summary>Material group index forced with the "Skin" input; null lets the seed decide.</summary>
	public int? Variant { get; set; }

	/// <summary>The "pet seed" attribute.</summary>
	public uint Seed { get; set; }

	/// <summary>Name tag for the current stage, or null.</summary>
	public string? Name { get; set; }
}

/*
 * *** CHICKEN PETS (CS2 1.41.8.2) - THE PLUGIN SPAWNS ITS OWN CHICKEN. ***
 *
 * In game a pet is a `chicken` entity (CChicken, a prop_dynamic) that carries an econ item in m_AttributeManager -
 * item 4681 "pet" with the attributes "pet id", "pet seed" and "upgrade level" - plus m_leader (the pawn it follows)
 * and m_owner (the controller, new in 1.41.8.2; the overhead label reads "{owner}'s {name}"). The update also added a
 * `chicken_model` spawn keyvalue. There is no pet field on the pawn, the controller or InventoryServices; the pet
 * loadout slot is 57.
 *
 * This is "approach B" from the SkinHub research: rather than faking loadout slot 57 and hoping the game's own
 * deploy code runs on a community server (unknown, and it would need reverse engineering of libserver), the plugin
 * creates the chicken itself and fills in the same item the game would. Everything below is written against the
 * schema and strings of 1.41.8.2; NONE OF IT HAS BEEN SEEN RUNNING ON A SERVER YET. In particular:
 *
 *   - whether the client derives the look (colour group, hue jitter, body shape) from the networked "pet seed" is
 *     inferred from strings in client.dll, not proven. If it does not, `pet_variant` still picks the colour group
 *     through the "Skin" input, and the rest is the model's default look;
 *   - the server reads "pet id", "pet seed" and "upgrade level" itself and has the model paths hardcoded, so it may
 *     pick the model on its own and override `chicken_model`. That is why the attributes are written BEFORE
 *     DispatchSpawn and why the model is checked (and set) once more after it;
 *   - m_owner and m_szCustomNameOverride2/3 are not in the CounterStrikeSharp.API 1.0.367 bindings this plugin builds
 *     against. They are written through the schema by name, which resolves against the running server, and skipped
 *     (with one log line) if the server does not have them;
 *   - chicken follow AI may drop its leader on its own, so the leader is re-asserted every second.
 *
 * The name follows the game's one-name-per-stage layout (see WritePetName): it always goes in m_szCustomName, the only
 * name buffer that is networked, and in the current stage's own buffer on top of that.
 *
 * PetTeamIntro.cs puts the same pet item into the team intro's m_petItem - experimental, off by default
 * (PetTeamIntroExperimental).
 *
 * Lifecycle: a pet is spawned the frame after its owner spawns (and when their row arrives from the database while
 * they are alive), follows them, stays where it is when they die, comes back on their next spawn if it was killed or
 * a round restart cleaned it up, and is removed on team change, disconnect, map end and plugin unload. Eggs never
 * leave the nest. A player the game itself gave a pet (a chicken whose m_owner is them - real pullets can deploy
 * from about 2026-10-06) gets no second one from here.
 *
 * Valve's server guidelines forbid giving players items they do not own, and this is exactly that - the same risk
 * the rest of WeaponPaints already carries. PetsEnabled turns it off.
 */
public partial class WeaponPaints
{
	internal const int PetStageEgg = 0;
	internal const int PetStageChick = 1;
	internal const int PetStagePullet = 2;
	internal const int PetStageHen = 3;

	/// <summary>items_game item 4681 "pet": item_quality unique, cannot trade, nameable, noteam.</summary>
	private const ushort PetItemDefIndex = 4681;

	private const int ItemQualityUnique = 4;

	/// <summary>
	/// Longest pet name, in code points. The in-game rename box stops at 20 (vanity_pet_info.js SetMaxChars(20)), but
	/// the item buffers are 161 bytes and the website may write up to the column width - `pet_name varchar(32)`, which
	/// is also what @skinhub/cdn formatPetRow cuts to. The column is the limit here, so a name typed on the site is not
	/// shortened again in game, and a name typed in game always fits the column (MySQL counts code points).
	/// </summary>
	private const int PetNameMaxCodePoints = 32;

	/// <summary>m_szCustomName and m_szCustomNameOverride2/3 are char[161].</summary>
	private const int ItemNameBufferBytes = 161;

	private const float PetThinkSeconds = 1.0f;
	private const float PetSpawnDistance = 40f;
	private const float PetMaxFollowDistance = 1500f;

	/// <summary>
	/// "pet food expiration date" is written a month ahead. A pet whose food ran out is retired in the game, and an
	/// item with no date at all is not something the client has ever been sent - a date in the future is the one
	/// state known to be "alive and fed". The unit (unix seconds) is inferred from the attribute class
	/// (deployment_date, stored_as_integer).
	/// </summary>
	private const int PetFoodDays = 30;

	private const uint InvalidEntityHandle = 0xFFFFFFFF;

	/// <summary>A pet_definitions entry as the plugin uses it. Kind is "egg", "chick" or "adult".</summary>
	internal sealed record PetDefinition(int Id, string DisplayName, string Kind, string? Breed, string Model);

	/// <summary>
	/// items_game pet_definitions (1.41.8.2), used whenever data/pets.json is not available - a CDN that has not
	/// published it yet must not leave the !pet menu empty. Names are the exporter's displayName: csgo_english's
	/// loc_name without the leading "Pet ", with the breed spelled out for the adults.
	/// </summary>
	private static readonly PetDefinition[] BuiltInPets =
	[
		new(1, "Chicken Egg", "egg", null, "models/chicken/egg_pristine.vmdl"),
		new(2, "Chick", "chick", null, "models/chicken/chick.vmdl"),
		new(3, "Catalana Chicken", "adult", "catalana", "models/chicken/chicken.vmdl"),
		new(4, "Silkie Chicken", "adult", "silkie", "models/chicken/chicken_silkie.vmdl"),
		new(5, "Polish Chicken", "adult", "polish", "models/chicken/chicken_polish.vmdl")
	];

	internal static List<JObject> PetsList = [];
	internal static readonly ConcurrentDictionary<int, PetInfo> GPlayersPet = new();

	/// <summary>
	/// Slots whose pet should be (re)spawned as soon as they are alive. Filled from the database loader, which runs on
	/// a worker thread, and drained by the pet timer on the game thread - so no worker ever touches an entity.
	/// </summary>
	private static readonly ConcurrentDictionary<int, byte> PetSpawnQueue = new();

	/// <summary>What a spawned pet looks like. Equal looks mean the live chicken can be kept as it is.</summary>
	private sealed record PetLook(int PetId, int Stage, int? Variant, uint Seed, string? Name, string Model);

	/// <summary>
	/// A chicken this plugin spawned. It is found again by its raw entity handle (index plus serial number), so an
	/// index the engine has since handed to another entity is never mistaken for it. Not by the item id: the server
	/// reads the pet attributes itself and may rewrite the item after spawn, and a pet that stopped matching would be
	/// dropped from here while it still stands there - never removed again, and counted as a game-spawned pet that
	/// blocks its owner from ever getting one of ours. ItemId is only kept to log, once, whether that happens.
	/// </summary>
	private sealed record ActivePet(uint Handle, ulong ItemId, ulong SteamId, PetLook Look);

	private readonly ConcurrentDictionary<int, ActivePet> _activePets = new();
	private static readonly ConcurrentDictionary<string, short> PetSchemaOffsets = new();
	private static int _petOwnerMissingLogged;
	private static int _petItemRewriteLogged;
	private static bool? _petLeaderNetworked;
	private int _petThinkTicks;

	internal static void QueuePetSpawn(int slot) => PetSpawnQueue[slot] = 0;

	/// <summary>The published pet rows when data/pets.json loaded, the built-in ones otherwise.</summary>
	internal static IReadOnlyList<PetDefinition> PetDefinitions()
	{
		var published = PetsList.Select(ItemData.ParsePet).OfType<PetDefinition>().ToList();
		return published.Count > 0 ? published : BuiltInPets;
	}

	private static PetDefinition? FindPet(int petId) => PetDefinitions().FirstOrDefault(pet => pet.Id == petId);

	/// <summary>Stages a pet can be picked at: the chick def only as a chick, the breeds as pullet or hen.</summary>
	private static int[] PetStagesFor(PetDefinition pet) => pet.Kind switch
	{
		"chick" => [PetStageChick],
		"adult" => [PetStagePullet, PetStageHen],
		_ => []
	};

	private static string PetStageKey(int stage) => stage switch
	{
		PetStageEgg => "egg",
		PetStageChick => "chick",
		PetStagePullet => "pullet",
		_ => "hen"
	};

	private string PetStageLabel(int stage) => Localizer[$"wp_pet_stage_{PetStageKey(stage)}"];

	/// <summary>
	/// What a stored pet looks like when it is spawned, or null when it does not leave the nest (an egg). Rows the
	/// website wrote with an odd combination are normalised rather than refused: the chick def is always stage 1,
	/// a breed at stage 1 is drawn as a chick (with its breed's pet id - whether "pet id" changes at hatch is not
	/// known), and the stage is clamped to 0..3.
	/// </summary>
	private static PetLook? ResolvePetLook(PetDefinition pet, PetInfo info)
	{
		var stage = Math.Clamp(info.Stage, PetStageEgg, PetStageHen);
		if (pet.Kind == "egg" || stage == PetStageEgg) return null;
		if (pet.Kind == "chick") stage = PetStageChick;

		var model = stage == PetStageChick
			? PetDefinitions().FirstOrDefault(definition => definition.Kind == "chick")?.Model ?? "models/chicken/chick.vmdl"
			: pet.Model;

		return new PetLook(pet.Id, stage, info.Variant, info.Seed, SanitizePetName(info.Name), model);
	}

	internal static string? SanitizePetName(string? name)
	{
		if (string.IsNullOrWhiteSpace(name)) return null;

		// No control characters, and none of the characters the chat colour tags and the centre-HTML image use.
		var clean = new string(name.Where(c => !char.IsControl(c) && c is not ('{' or '}' or '<' or '>')).ToArray()).Trim();

		// Whole text elements only, so a cut never splits a surrogate pair or an emoji sequence, and never more code
		// points than the column holds.
		var kept = new System.Text.StringBuilder();
		var codePoints = 0;
		var elements = StringInfo.GetTextElementEnumerator(clean);
		while (elements.MoveNext())
		{
			var element = elements.GetTextElement();
			var elementCodePoints = element.EnumerateRunes().Count();
			if (codePoints + elementCodePoints > PetNameMaxCodePoints) break;

			kept.Append(element);
			codePoints += elementCodePoints;
		}

		clean = kept.ToString().Trim();
		return clean.Length == 0 ? null : clean;
	}

	private static uint RandomPetSeed() => (uint)Random.Shared.NextInt64(1, (long)uint.MaxValue + 1);

	private bool PlayerMayUsePets(CCSPlayerController player) =>
		string.IsNullOrWhiteSpace(Config.Additional.PetPermission) ||
		AdminManager.PlayerHasPermissions(player, Config.Additional.PetPermission.Trim());

	#region Lifecycle

	private void RegisterPetListeners()
	{
		if (!Config.Additional.PetsEnabled) return;

		RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawnPet);
		RegisterEventHandler<EventPlayerTeam>(OnPlayerTeamPet);
		RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnectPet);
		RegisterListener<Listeners.OnMapEnd>(OnMapEndPets);
		RegisterListener<Listeners.OnServerPrecacheResources>(OnPrecachePets);

		AddTimer(PetThinkSeconds, PetThink, TimerFlags.REPEAT);

		RegisterPetTeamIntro();
	}

	/// <summary>
	/// The pet models, precached for every map. A plugin loaded mid-map misses this until the next map, which may
	/// leave pets on the default model until then. The egg is left out: it never spawns.
	/// </summary>
	private void OnPrecachePets(ResourceManifest manifest)
	{
		foreach (var model in BuiltInPets.Concat(PetDefinitions())
			         .Where(pet => pet.Kind != "egg")
			         .Select(pet => pet.Model)
			         .Distinct(StringComparer.Ordinal))
			manifest.AddResource(model);
	}

	private HookResult OnPlayerSpawnPet(EventPlayerSpawn @event, GameEventInfo info)
	{
		var player = @event.Userid;
		if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return HookResult.Continue;

		var slot = player.Slot;
		var steamId = player.SteamID;

		// Next frame: by then the pawn stands at its spawn point, and a round restart has finished its clean-up.
		Server.NextFrame(() =>
		{
			var current = Utilities.GetPlayerFromSlot(slot);
			if (current == null || !current.IsValid || current.SteamID != steamId) return;

			PetSpawnQueue.TryRemove(slot, out _);
			SpawnPlayerPet(current);
		});

		return HookResult.Continue;
	}

	private HookResult OnPlayerTeamPet(EventPlayerTeam @event, GameEventInfo info)
	{
		var player = @event.Userid;
		if (player == null || !player.IsValid) return HookResult.Continue;

		// Any team change sends the pet home; the owner's next spawn brings it back.
		RemovePlayerPet(player.Slot);
		return HookResult.Continue;
	}

	private HookResult OnPlayerDisconnectPet(EventPlayerDisconnect @event, GameEventInfo info)
	{
		var player = @event.Userid;
		if (player == null || !player.IsValid) return HookResult.Continue;

		RemovePlayerPet(player.Slot);
		GPlayersPet.TryRemove(player.Slot, out _);
		PetSpawnQueue.TryRemove(player.Slot, out _);
		return HookResult.Continue;
	}

	private void OnMapEndPets()
	{
		// The chickens go with the map; only the bookkeeping is left to clear. The same for the team intro spots.
		_activePets.Clear();
		PetSpawnQueue.Clear();
		_teamIntroPetItems.Clear();
	}

	/// <summary>
	/// Removes every pet this plugin spawned, so a reload does not leave orphaned chickens behind, and takes its pets
	/// back out of the team intro spots. On a server shutdown the map ends first, OnMapEndPets has already emptied both
	/// lists, and this touches nothing.
	/// </summary>
	public override void Unload(bool hotReload)
	{
		try
		{
			foreach (var slot in _activePets.Keys) RemovePlayerPet(slot);
			ClearTeamIntroPets();
		}
		catch (Exception ex)
		{
			Logger.LogWarning("Could not remove the pets on unload: {Reason}", ex.Message);
		}

		base.Unload(hotReload);
	}

	/// <summary>
	/// Once a second: spawn queued pets for players who are alive, drop pets that died or were cleaned up (they come
	/// back on their owner's next spawn), re-assert the leader, bring back a pet that fell far behind, and give way
	/// to a pet the game spawned for the same player.
	/// </summary>
	private void PetThink()
	{
		foreach (var slot in PetSpawnQueue.Keys)
		{
			var queued = Utilities.GetPlayerFromSlot(slot);
			if (queued == null || !queued.IsValid)
			{
				PetSpawnQueue.TryRemove(slot, out _);
				continue;
			}

			// Dead or not spawned yet: their spawn event handles it.
			if (!queued.PawnIsAlive) continue;

			PetSpawnQueue.TryRemove(slot, out _);
			SpawnPlayerPet(queued);
		}

		if (_activePets.IsEmpty) return;

		// Looking for game-spawned pets walks every entity, so it runs every fifth tick rather than every second.
		var scanForGamePets = ++_petThinkTicks % 5 == 0;
		HashSet<uint>? gameOwners = null;

		foreach (var (slot, active) in _activePets)
		{
			var chicken = ResolvePet(active);
			if (chicken == null)
			{
				_activePets.TryRemove(slot, out _);
				continue;
			}

			var player = Utilities.GetPlayerFromSlot(slot);
			if (player == null || !player.IsValid || player.SteamID != active.SteamId)
			{
				RemovePlayerPet(slot);
				continue;
			}

			// A dead owner's pet stays where it is - the pet book has pages for pets lost to fire, the Zeus and the
			// planted bomb, so pets clearly outlive their owner's round.
			var pawn = player.PlayerPawn.Value;
			if (!player.PawnIsAlive || pawn == null || !pawn.IsValid) continue;

			if (scanForGamePets && (gameOwners ??= GameSpawnedPetOwners()).Contains(pawn.Controller.Raw))
			{
				RemovePlayerPet(slot);
				continue;
			}

			try
			{
				AssertPetLeader(chicken, player);

				if (chicken.AbsOrigin is { } petOrigin && pawn.AbsOrigin is { } ownerOrigin &&
				    Vector3.Distance(new Vector3(petOrigin.X, petOrigin.Y, petOrigin.Z),
					    new Vector3(ownerOrigin.X, ownerOrigin.Y, ownerOrigin.Z)) > PetMaxFollowDistance &&
				    PetSpawnPoint(pawn) is var (position, angles))
					chicken.Teleport(position, angles, Vector3.Zero);
			}
			catch (Exception ex)
			{
				Logger.LogWarning("Pet upkeep failed for slot {Slot}: {Reason}", slot, ex.Message);
			}
		}
	}

	/// <summary>
	/// Makes the world match the player's stored pet: spawns it, keeps the live one when nothing about it changed
	/// (bringing it to the player), replaces it when something did, and removes it when there is no pet to show.
	/// Game thread only.
	/// </summary>
	private void SpawnPlayerPet(CCSPlayerController player)
	{
		if (!Config.Additional.PetsEnabled || !Utility.IsPlayerValid(player)) return;

		var slot = player.Slot;

		// "No pet to show" first, before the alive check: `!pet off` (or a revoked PetPermission) from a dead or
		// not yet spawned owner has to take the chicken away now, and PetThink skips dead owners, so nothing else
		// would until their next spawn.
		if (!PlayerMayUsePets(player) ||
		    !GPlayersPet.TryGetValue(slot, out var petInfo) ||
		    FindPet(petInfo.PetId) is not { } definition ||
		    ResolvePetLook(definition, petInfo) is not { } look)
		{
			RemovePlayerPet(slot);
			return;
		}

		// A change of look while dead waits for the owner's next spawn, which replaces the pet.
		var pawn = player.PlayerPawn.Value;
		if (!player.PawnIsAlive || pawn == null || !pawn.IsValid || player.Team is CsTeam.None or CsTeam.Spectator)
			return;

		try
		{
			if (GameSpawnedPetOwners().Contains(pawn.Controller.Raw))
			{
				RemovePlayerPet(slot);
				return;
			}

			if (_activePets.TryGetValue(slot, out var active) && ResolvePet(active) is { } existing)
			{
				if (active.SteamId == player.SteamID && active.Look == look)
				{
					if (PetSpawnPoint(pawn) is var (position, angles))
						existing.Teleport(position, angles, Vector3.Zero);
					AssertPetLeader(existing, player);
					return;
				}

				KillPetEntity(existing);
			}

			_activePets.TryRemove(slot, out _);
			CreatePet(player, pawn, look);
		}
		catch (Exception ex)
		{
			Logger.LogWarning("Could not spawn the pet for {Player}: {Reason}", player.PlayerName, ex.Message);
		}
	}

	private void CreatePet(CCSPlayerController player, CCSPlayerPawn pawn, PetLook look)
	{
		if (PetSpawnPoint(pawn) is not var (position, angles)) return;

		var chicken = Utilities.CreateEntityByName<CChicken>("chicken");
		if (chicken == null || !chicken.IsValid)
		{
			Logger.LogWarning("Could not create a chicken entity for {Player}'s pet", player.PlayerName);
			return;
		}

		try
		{
			// The item first, and all of it before DispatchSpawn: the server reads the pet attributes itself, most
			// likely while spawning (see the header).
			var item = chicken.AttributeManager.Item;
			FillPetItem(item, player.SteamID, look);

			var ownerHandle = pawn.Controller.Raw;
			WritePetOwner(chicken, ownerHandle, false);
			chicken.Leader.Raw = player.PlayerPawn.Raw;

			var keyValues = new CEntityKeyValues();
			keyValues.SetString("chicken_model", look.Model);
			keyValues.SetVector("origin", position.X, position.Y, position.Z);
			keyValues.SetAngle("angles", angles.X, angles.Y, angles.Z);
			chicken.DispatchSpawn(keyValues);

			// Spawn may have reset any of these, and the model may have been picked by the server's own pet code.
			EnsurePetModel(chicken, look.Model);
			if (look.Variant is { } variant) chicken.AcceptInput("Skin", value: variant.ToString(CultureInfo.InvariantCulture));
			WritePetOwner(chicken, ownerHandle, true);
			AssertPetLeader(chicken, player);
			chicken.Teleport(position, angles, Vector3.Zero);

			_activePets[player.Slot] = new ActivePet(chicken.EntityHandle.Raw, item.ItemID, player.SteamID, look);
		}
		catch (Exception ex)
		{
			Logger.LogWarning("Could not spawn the pet for {Player}: {Reason}", player.PlayerName, ex.Message);
			KillPetEntity(chicken);
		}
	}

	private void RemovePlayerPet(int slot)
	{
		if (!_activePets.TryRemove(slot, out var active)) return;
		KillPetEntity(ResolvePet(active));
	}

	private void KillPetEntity(CChicken? chicken)
	{
		if (chicken == null || !chicken.IsValid) return;

		try
		{
			// The same deferred Kill the rest of the plugin uses for weapons.
			chicken.AddEntityIOEvent("Kill", chicken, null, "", 0.0f);
		}
		catch (Exception)
		{
			try
			{
				chicken.Remove();
			}
			catch (Exception ex)
			{
				Logger.LogWarning("Could not remove a pet: {Reason}", ex.Message);
			}
		}
	}

	private CChicken? ResolvePet(ActivePet active)
	{
		// CHandle.Get compares the whole handle, serial number included, so a reused index resolves to null.
		var chicken = new CHandle<CChicken>(active.Handle).Value;
		if (chicken == null || !chicken.IsValid || chicken.DesignerName != "chicken") return null;

		// Diagnostic only, for the first server test: says whether the game rewrites the pet item after spawn.
		var itemId = chicken.AttributeManager.Item.ItemID;
		if (itemId != active.ItemId && Interlocked.Exchange(ref _petItemRewriteLogged, 1) == 0)
			Logger.LogInformation(
				"A pet's item id changed after spawn ({Before} -> {After}, def {Def}) - the server rewrites the pet " +
				"item on its own. The pet is still tracked by its entity handle.",
				active.ItemId, itemId, chicken.AttributeManager.Item.ItemDefinitionIndex);

		return chicken;
	}

	/// <summary>Controllers (as raw handles) that own a chicken this plugin did not spawn.</summary>
	private HashSet<uint> GameSpawnedPetOwners()
	{
		var owners = new HashSet<uint>();
		if (PetSchemaOffset("CChicken", "m_owner") <= 0) return owners;

		var ours = _activePets.Values.Select(active => active.Handle).ToHashSet();

		foreach (var chicken in Utilities.FindAllEntitiesByDesignerName<CChicken>("chicken"))
		{
			if (!chicken.IsValid || ours.Contains(chicken.EntityHandle.Raw)) continue;

			var owner = Schema.GetRef<uint>(chicken.Handle, "CChicken", "m_owner");
			if (owner != 0 && owner != InvalidEntityHandle) owners.Add(owner);
		}

		return owners;
	}

	/// <summary>Behind the player, on the floor they stand on, facing the way they face.</summary>
	private static (Vector3 Position, Vector3 Angles)? PetSpawnPoint(CCSPlayerPawn pawn)
	{
		if (pawn.AbsOrigin is not { } origin) return null;

		var yaw = pawn.EyeAngles.Y;
		var radians = yaw * MathF.PI / 180f;

		return (new Vector3(origin.X - MathF.Cos(radians) * PetSpawnDistance,
				origin.Y - MathF.Sin(radians) * PetSpawnDistance,
				origin.Z + 2f),
			new Vector3(0f, yaw, 0f));
	}

	#endregion

	#region Writing the pet item

	/// <summary>
	/// The numeric pet attributes, through the same CAttributeList setter the stickers use. All of them are
	/// `stored_as_integer` in items_game, so they go in as the integer's bit pattern (ViewAsFloat), like sticker ids.
	/// </summary>
	private static void WritePetAttributes(nint attributeList, PetLook look)
	{
		var fedUntil = (uint)DateTimeOffset.UtcNow.AddDays(PetFoodDays).ToUnixTimeSeconds();

		CAttributeListSetOrAddAttributeValueByName.Invoke(attributeList, "pet id", ViewAsFloat((uint)look.PetId));
		CAttributeListSetOrAddAttributeValueByName.Invoke(attributeList, "upgrade level", ViewAsFloat((uint)look.Stage));
		CAttributeListSetOrAddAttributeValueByName.Invoke(attributeList, "pet seed", ViewAsFloat(look.Seed));
		CAttributeListSetOrAddAttributeValueByName.Invoke(attributeList, "pet food expiration date", ViewAsFloat(fedUntil));
	}

	/// <summary>
	/// Fills an item view as the game's pet item 4681 for this look, with this player as its owner. The chicken's item
	/// (CreatePet, before DispatchSpawn) and the team intro's m_petItem (PetTeamIntro.cs) are both filled here, so the
	/// client is sent the same item either way.
	/// </summary>
	private void FillPetItem(CEconItemView item, ulong steamId, PetLook look)
	{
		item.ItemDefinitionIndex = PetItemDefIndex;
		item.EntityQuality = ItemQualityUnique;
		item.EntityLevel = 1; // min_ilevel = max_ilevel = 1 on item 4681
		UpdatePlayerEconItemId(item);
		item.AccountID = (uint)steamId;
		item.Initialized = true;

		WritePetAttributes(item.NetworkedDynamicAttributes.Handle, look);
		WritePetAttributes(item.AttributeList.Handle, look);
		WritePetName(item, look.Name, look.Stage);
	}

	/// <summary>
	/// The name, where the game keeps it for this stage. A pet has one name per stage - "custom name attr" (111) for the
	/// chick, "custom name attr 2" (328) for the pullet, "custom name attr 3" (329) for the hen - and the item view has
	/// a buffer for each: m_szCustomName, m_szCustomNameOverride2 and m_szCustomNameOverride3 (the last two new in
	/// 1.41.8.2). They are STRING attributes, which the float setter cannot write, so the buffers are written directly.
	///
	/// But m_szCustomName is the only one of them that is a network var: the server's network var strings (build
	/// 2000913) list m_szCustomName and none of the overrides. So it is the one name a client ever receives, and the
	/// overhead label ({owner}'s "{name}", drawn by the client) can only come from it. The current stage's name
	/// therefore always goes in m_szCustomName - for a pullet and a hen too - and the stage's own buffer carries it as
	/// well, for anything on the server that reads the per-stage field. The other stage's buffer is emptied: the player
	/// has one name (wp_player_pets.pet_name is the current stage's name), and an item view can be reused - the team
	/// intro's m_petItem still holds whatever the last intro wrote there. m_szCustomNameOverride (no number, older
	/// than the pets) is not a stage name and is left alone.
	/// </summary>
	private static void WritePetName(CEconItemView item, string? name, int stage)
	{
		var text = name ?? "";
		item.CustomName = text;

		WriteItemNameBuffer(item, "m_szCustomNameOverride2", stage == PetStagePullet ? text : "");
		WriteItemNameBuffer(item, "m_szCustomNameOverride3", stage == PetStageHen ? text : "");
	}

	/// <summary>
	/// Writes a char[161] name buffer of CEconItemView that CounterStrikeSharp has no setter for. Its own string writer
	/// (Schema.SetStringBytes, what CustomName uses) is internal, so this does the same by hand: UTF-8 bytes, then a
	/// terminator. SanitizePetName caps a name at 32 code points, at most 128 bytes, inside the 161-byte buffer - and
	/// the length is checked anyway. Skipped when this server has no such field.
	/// </summary>
	private static void WriteItemNameBuffer(CEconItemView item, string field, string text)
	{
		// Offset 0 means "not found" - never write there, it is the item's vtable.
		var offset = PetSchemaOffset("CEconItemView", field);
		if (offset <= 0) return;

		var bytes = System.Text.Encoding.UTF8.GetBytes(text);
		if (bytes.Length >= ItemNameBufferBytes) return;

		var address = item.Handle + offset;
		Marshal.Copy(bytes, 0, address, bytes.Length);
		Marshal.WriteByte(address, bytes.Length, 0);
	}

	private void WritePetOwner(CChicken chicken, uint controllerHandle, bool networked)
	{
		if (PetSchemaOffset("CChicken", "m_owner") <= 0)
		{
			if (Interlocked.Exchange(ref _petOwnerMissingLogged, 1) == 0)
				Logger.LogWarning(
					"This server has no CChicken::m_owner (it predates CS2 1.41.8.2) - pets spawn without an owner, " +
					"so they carry no \"<player>'s <name>\" label");
			return;
		}

		Schema.GetRef<uint>(chicken.Handle, "CChicken", "m_owner") = controllerHandle;
		if (networked) Utilities.SetStateChanged(chicken, "CChicken", "m_owner");
	}

	private static void AssertPetLeader(CChicken chicken, CCSPlayerController player)
	{
		var pawnHandle = player.PlayerPawn.Raw;
		var leader = chicken.Leader;
		if (leader.Raw == pawnHandle) return;

		leader.Raw = pawnHandle;

		_petLeaderNetworked ??= Schema.IsSchemaFieldNetworked("CChicken", "m_leader");
		if (_petLeaderNetworked == true) Utilities.SetStateChanged(chicken, "CChicken", "m_leader");
	}

	private static void EnsurePetModel(CChicken chicken, string model)
	{
		var current = chicken.CBodyComponent?.SceneNode?.GetSkeletonInstance()?.ModelState.ModelName;
		if (!string.IsNullOrEmpty(current) && !string.Equals(current, model, StringComparison.OrdinalIgnoreCase))
			chicken.SetModel(model);
	}

	/// <summary>A schema field's offset on the running server, or 0 when it has no such field. Cached.</summary>
	private static short PetSchemaOffset(string className, string field) =>
		PetSchemaOffsets.GetOrAdd($"{className}::{field}", _ =>
		{
			try
			{
				return Schema.GetSchemaOffset(className, field);
			}
			catch (Exception)
			{
				return 0;
			}
		});

	#endregion

	#region !pet

	private void SetupPetsMenu()
	{
		_config.Additional.CommandPet.ForEach(c =>
		{
			AddCommand($"css_{c}", "Pet menu", (player, info) =>
			{
				// Closed between round_end and round_start, like every other WeaponPaints command.
				if (!Utility.IsPlayerValid(player) || player == null || !_gBCommandsAllowed) return;
				OnCommandPet(player, info);
			});
		});
	}

	/// <summary>
	/// !pet opens the menu. The rest are chat subcommands, because a name and a seed are typed, not picked:
	///   !pet name &lt;text&gt;      - name the pet (empty clears it)
	///   !pet seed [number]      - a new random look, or a specific seed
	///   !pet color &lt;n|random&gt;  - force a colour (material group) or let the seed pick
	///   !pet stage &lt;stage&gt;     - chick / pullet / hen
	///   !pet off                - no pet
	/// </summary>
	private void OnCommandPet(CCSPlayerController player, CommandInfo command)
	{
		if (!PlayerMayUsePets(player))
		{
			PrintPet(player, "wp_pet_no_permission");
			return;
		}

		// One cooldown for the menu and every subcommand: each change respawns the chicken and writes the database,
		// so `!pet seed` must not be spammable.
		if (CommandsCooldown.TryGetValue(player.Slot, out var cooldownEndTime) && DateTime.UtcNow < cooldownEndTime)
		{
			PrintPet(player, "wp_command_cooldown");
			return;
		}

		CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(Config.CmdRefreshCooldownSeconds);

		var subcommand = command.ArgCount > 1 ? command.GetArg(1).Trim().ToLowerInvariant() : "";
		var argument = PetCommandArgument(command);

		switch (subcommand)
		{
			case "":
				OpenPetMenu(player);
				break;
			case "name":
				SetPetName(player, argument);
				break;
			case "seed":
				SetPetSeed(player, argument);
				break;
			case "color":
			case "colour":
			case "variant":
				SetPetVariant(player, argument);
				break;
			case "stage":
				SetPetStage(player, argument);
				break;
			case "off":
			case "none":
			case "remove":
				ChoosePet(player, null, PetStageEgg);
				break;
			default:
				PrintPet(player, "wp_pet_usage");
				break;
		}
	}

	/// <summary>Everything after the subcommand, unquoted - a name may contain spaces.</summary>
	private static string PetCommandArgument(CommandInfo command)
	{
		var args = command.ArgString.Trim();
		var space = args.IndexOfAny([' ', '\t']);
		var rest = space < 0 ? "" : args[(space + 1)..].Trim();

		return rest.Length >= 2 && rest[0] == '"' && rest[^1] == '"' ? rest[1..^1] : rest;
	}

	private void OpenPetMenu(CCSPlayerController player)
	{
		var slot = player.Slot;

		// Built per invocation, like the agents menu: PetsList arrives asynchronously after plugin load.
		var menu = Utility.CreateMenu(Localizer["wp_pet_menu_title"]);
		if (menu == null) return;
		menu.PostSelectAction = PostSelectAction.Close;

		menu.AddMenuOption(Localizer["None"], (p, _) => ChoosePet(p, null, PetStageEgg));

		foreach (var pet in PetDefinitions().Where(pet => PetStagesFor(pet).Length > 0))
		{
			var chosen = pet;
			menu.AddMenuOption(pet.DisplayName, (p, _) =>
			{
				var stages = PetStagesFor(chosen);
				if (stages.Length == 1)
				{
					ChoosePet(p, chosen, stages[0]);
					return;
				}

				// Next frame, so this menu has finished closing before the stage menu opens.
				Server.NextFrame(() => OpenPetStageMenu(p, chosen, stages));
			});
		}

		if (GPlayersPet.ContainsKey(slot))
			menu.AddMenuOption(Localizer["wp_pet_menu_random_look"], (p, _) => SetPetSeed(p, ""));

		menu.Open(player);
	}

	private void OpenPetStageMenu(CCSPlayerController player, PetDefinition pet, int[] stages)
	{
		if (!Utility.IsPlayerValid(player)) return;

		var menu = Utility.CreateMenu(Localizer["wp_pet_menu_stage_title", pet.DisplayName]);
		if (menu == null) return;
		menu.PostSelectAction = PostSelectAction.Close;

		foreach (var stage in stages)
		{
			var chosen = stage;
			menu.AddMenuOption(PetStageLabel(stage), (p, _) => ChoosePet(p, pet, chosen));
		}

		menu.Open(player);
	}

	/// <summary>Picks a pet (or none, with a null pet), saves it and makes the world match.</summary>
	private void ChoosePet(CCSPlayerController player, PetDefinition? pet, int stage)
	{
		if (!Utility.IsPlayerValid(player)) return;

		var slot = player.Slot;
		PetInfo? next = null;

		if (pet != null)
		{
			GPlayersPet.TryGetValue(slot, out var current);
			var samePet = current != null && current.PetId == pet.Id;

			next = new PetInfo
			{
				PetId = pet.Id,
				Stage = stage,
				// The same pet keeps its seed and colour, so changing the stage does not change the look.
				Seed = samePet ? current!.Seed : RandomPetSeed(),
				Variant = samePet ? current!.Variant : null,
				Name = current?.Name
			};

			GPlayersPet[slot] = next;
			ShowPetImage(player, pet.Id, stage);
			PrintPet(player, "wp_pet_menu_select", $"{pet.DisplayName} ({PetStageLabel(stage)})");
		}
		else
		{
			GPlayersPet.TryRemove(slot, out _);
			PrintPet(player, "wp_pet_menu_removed");
		}

		SavePet(player, next);
		ShowPetChange(player);
	}

	private void SetPetName(CCSPlayerController player, string argument)
	{
		if (!GPlayersPet.TryGetValue(player.Slot, out var pet))
		{
			PrintPet(player, "wp_pet_none");
			return;
		}

		pet.Name = SanitizePetName(argument);

		if (pet.Name == null) PrintPet(player, "wp_pet_name_cleared");
		else PrintPet(player, "wp_pet_name_set", pet.Name);

		SavePet(player, pet);
		ShowPetChange(player);
	}

	private void SetPetSeed(CCSPlayerController player, string argument)
	{
		if (!GPlayersPet.TryGetValue(player.Slot, out var pet))
		{
			PrintPet(player, "wp_pet_none");
			return;
		}

		uint seed;
		if (string.IsNullOrWhiteSpace(argument) || argument.Equals("random", StringComparison.OrdinalIgnoreCase))
			seed = RandomPetSeed();
		else if (!uint.TryParse(argument.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out seed))
		{
			PrintPet(player, "wp_pet_invalid");
			return;
		}

		pet.Seed = seed;
		PrintPet(player, "wp_pet_seed_set", seed.ToString(CultureInfo.InvariantCulture));

		SavePet(player, pet);
		ShowPetChange(player);
	}

	private void SetPetVariant(CCSPlayerController player, string argument)
	{
		if (!GPlayersPet.TryGetValue(player.Slot, out var pet))
		{
			PrintPet(player, "wp_pet_none");
			return;
		}

		var value = argument.Trim().ToLowerInvariant();

		if (value is "" or "random" or "auto" or "seed")
		{
			pet.Variant = null;
			PrintPet(player, "wp_pet_variant_cleared");
		}
		// The breeds have 9 to 13 colour groups; 63 is a generous ceiling that still refuses nonsense.
		else if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var variant) && variant <= 63)
		{
			pet.Variant = variant;
			PrintPet(player, "wp_pet_variant_set", variant.ToString(CultureInfo.InvariantCulture));
		}
		else
		{
			PrintPet(player, "wp_pet_invalid");
			return;
		}

		SavePet(player, pet);
		ShowPetChange(player);
	}

	private void SetPetStage(CCSPlayerController player, string argument)
	{
		if (!GPlayersPet.TryGetValue(player.Slot, out var pet))
		{
			PrintPet(player, "wp_pet_none");
			return;
		}

		var stage = argument.Trim().ToLowerInvariant() switch
		{
			"chick" or "1" => PetStageChick,
			"pullet" or "2" => PetStagePullet,
			"hen" or "3" => PetStageHen,
			_ => -1
		};

		if (FindPet(pet.PetId) is not { } definition || !PetStagesFor(definition).Contains(stage))
		{
			PrintPet(player, "wp_pet_invalid");
			return;
		}

		pet.Stage = stage;
		PrintPet(player, "wp_pet_stage_set", PetStageLabel(stage));

		SavePet(player, pet);
		ShowPetChange(player);
	}

	/// <summary>
	/// Shows a change the player just made. The !pet command is closed between round_end and round_start, but a menu
	/// opened before round_end can still be picked from after it. Like the knife menu, such a pick is saved and shown
	/// on the owner's next spawn: a chicken spawned now would only be caught by the round restart clean-up. Taking a
	/// pet away creates nothing, so that still happens at once.
	/// </summary>
	private void ShowPetChange(CCSPlayerController player)
	{
		if (_gBCommandsAllowed || !GPlayersPet.ContainsKey(player.Slot)) SpawnPlayerPet(player);
	}

	private void SavePet(CCSPlayerController player, PetInfo? pet)
	{
		if (WeaponSync == null) return;

		var playerInfo = new PlayerInfo
		{
			UserId = player.UserId,
			Slot = player.Slot,
			Index = (int)player.Index,
			SteamId = player.SteamID.ToString(),
			Name = player.PlayerName,
			IpAddress = player.IpAddress?.Split(":")[0]
		};

		// A copy: the worker must not see this object change under it.
		var snapshot = pet == null
			? null
			: new PetInfo { PetId = pet.PetId, Stage = pet.Stage, Variant = pet.Variant, Seed = pet.Seed, Name = pet.Name };

		_ = Task.Run(async () => await WeaponSync.SyncPetToDatabase(playerInfo, snapshot));
	}

	/// <summary>
	/// The SkinHub thumbnail for this pet and stage (petthumbs/&lt;id&gt;-&lt;stage&gt;.png beside the datasets), shown
	/// the same way the other menus show their item image.
	/// </summary>
	private void ShowPetImage(CCSPlayerController player, int petId, int stage)
	{
		if (!Config.Additional.ShowSkinImage) return;

		var baseUrl = (string.IsNullOrWhiteSpace(Config.DataUrl) ? ItemData.DefaultDataUrl : Config.DataUrl.Trim())
			.TrimEnd('/');
		var slot = player.Slot;

		_playerWeaponImage[slot] = $"{baseUrl}/petthumbs/{petId}-{PetStageKey(stage)}.png";
		AddTimer(2.0f, () => _playerWeaponImage.Remove(slot), TimerFlags.STOP_ON_MAPCHANGE);
	}

	private void PrintPet(CCSPlayerController player, string key, params object[] args)
	{
		// An empty translation turns a message off, the same as everywhere else in the plugin.
		if (string.IsNullOrEmpty(Localizer[key])) return;
		player.Print(args.Length == 0 ? Localizer[key] : Localizer[key, args]);
	}

	#endregion
}
