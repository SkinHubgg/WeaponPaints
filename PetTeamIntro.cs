using System.Collections.Concurrent;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace WeaponPaints;

/*
 * *** EXPERIMENTAL - PLUGIN PETS IN THE TEAM INTRO (CS2 1.41.8.2). OFF UNLESS PetTeamIntroExperimental IS true. ***
 *
 * The team intro (the line-up the camera sweeps over before the first round, and after halftime) is drawn from
 * point entities the map places: team_intro_terrorist / _counterterrorist, the wingman_intro_* pair and the new
 * rush_intro_* pair. All of them are CCSGO_TeamPreviewCharacterPosition underneath, and 1.41.8.2 added a fourth econ
 * item to it next to the agent, the gloves and the weapon: m_petItem. All nine of its fields are network vars, and the
 * schema is byte-identical in builds 2000913, 2000914 and 2000917. When the intro starts, the server writes each
 * player's xuid and loadout items into those spots (native code, not seen - inferred from how the inventory simulator
 * has to hook GetItemInLoadout to get its items in), and the client spawns the preview agent, gloves, weapon and pet
 * from what arrives. The client cannot read another player's pet any other way, so an m_petItem that looks like the
 * game's own - item 4681 with "pet id", "upgrade level" and "pet seed" in its networked attributes - is the one thing
 * that could make it draw a plugin pet.
 *
 * So on team_intro_start, one frame later (the game has written its own fields by then), every intro spot whose xuid
 * is a player with a plugin pet gets that pet written into m_petItem exactly the way CreatePet fills the chicken's item
 * (FillPetItem), and the field is marked changed. Half a second later it is checked once more, in case the game writes
 * the spots again after the event; a pet still carrying the item id this plugin wrote is left as it is, so the client
 * does not see the item change mid-intro.
 *
 *   - Only pullets and hens. The game deploys a pet from the pullet stage ("cleared to deploy with you"); whether the
 *     intro would even draw a chick or an egg is unknown, and a broken preview is worse than none.
 *   - A spot the game filled itself (an initialised item this plugin did not write - a player who really owns a pet)
 *     is left alone, like the chicken gives way to a game-spawned pet.
 *   - The spots are reused from intro to intro, and the game may leave m_petItem alone for a player with no pet. So an
 *     item this plugin wrote earlier is cleared again when the player now on that spot has no plugin pet, and on
 *     plugin unload. The bookkeeping (spot handle -> item id written) is dropped at map end, with the spots.
 *   - pet_variant cannot reach the intro. The client colours the intro pet from the item's style (client.dll 2000917
 *     0x180d4ad5b), and the server has no way to send one: the style override is a client-only field and a plugin
 *     item has no GC inventory entry. So the intro pet always has its model's default colour. The seed and the stage
 *     do reach it - the client copies the whole item into the pet (0x180d4acba) - so its body shape and colour
 *     jitter come from the seed, as on the chicken.
 *
 * NONE OF THIS HAS BEEN SEEN RUNNING. Unknown until a server test: whether the client draws a pet for a plugin-filled
 * item at all, whether it wants a real (GC-issued) item id, whether it shows below the pullet stage, and whether the
 * server rewrites the field after the event. Each intro logs one line with what was written, and the first rewrite by
 * the game is logged once. The intro itself only runs with mp_team_intro_type on (the default "auto" means: when
 * mp_halftime is set) and on maps that ship the team_intro_* spots and cameras - the official maps do, most workshop
 * maps do not.
 *
 * The end-of-match line-up is NOT covered. It does not read these entities: endofmatch-characters.js takes the pet
 * from the match data of every player (GetAllPlayersMatchDataJSO().allplayerdata[].items), which the game builds from
 * the loadout. The only ways in are faking loadout slot 57 (a GetItemInLoadout hook, "approach A") or, untried,
 * rewriting the per-player item lists in the end-of-match user message (CS_UM_EndOfMatchAllPlayersData, presumably
 * where that match data comes from), which needs nested protobuf access that CounterStrikeSharp's UserMessage API does
 * not have - it reads and adds scalar fields only.
 *
 * m_petItem is not in the CounterStrikeSharp.API 1.0.367 bindings this plugin builds against (1.0.375 generates it as
 * CCSGO_TeamPreviewCharacterPosition.PetItem), so it is reached through the schema by name, which resolves against the
 * running server - the same Schema.GetDeclaredClass call the generated property makes. A server without the field logs
 * one warning and nothing else happens.
 */
public partial class WeaponPaints
{
	private const string TeamPreviewClass = "CCSGO_TeamPreviewCharacterPosition";
	private const string TeamPreviewPetField = "m_petItem";

	/// <summary>The lowest stage written into the intro. See the header.</summary>
	private const int TeamIntroMinPetStage = PetStagePullet;

	/// <summary>How long after team_intro_start the spots are checked a second time. The intro lasts 6.5 s by default.</summary>
	private const float TeamIntroRecheckSeconds = 0.5f;

	/// <summary>
	/// The intro spots, by designer name. FindAllEntitiesByDesignerName matches a substring, and "_intro_" also finds
	/// the intro cameras, so the exact names are checked before anything is read. The team_select_* spots derive from
	/// the same class but are not part of the intro.
	/// </summary>
	private static readonly HashSet<string> TeamIntroSpotNames = new(StringComparer.Ordinal)
	{
		"team_intro_terrorist",
		"team_intro_counterterrorist",
		"wingman_intro_terrorist",
		"wingman_intro_counterterrorist",
		"rush_intro_terrorist",
		"rush_intro_counterterrorist"
	};

	/// <summary>Intro spot (raw entity handle) -> the item id this plugin last wrote into its m_petItem.</summary>
	private readonly ConcurrentDictionary<uint, ulong> _teamIntroPetItems = new();

	private static int _teamIntroFieldMissingLogged;
	private static int _teamIntroRewriteLogged;

	private void RegisterPetTeamIntro()
	{
		if (!Config.Additional.PetTeamIntroExperimental) return;

		RegisterEventHandler<EventTeamIntroStart>(OnTeamIntroStartPets);
		Logger.LogInformation(
			"PetTeamIntroExperimental is on: plugin pets (pullet and hen) are written into the team intro. " +
			"This is experimental and has not been seen working in game yet.");
	}

	private HookResult OnTeamIntroStartPets(EventTeamIntroStart @event, GameEventInfo info)
	{
		Server.NextFrame(() => FillTeamIntroPets(false));
		AddTimer(TeamIntroRecheckSeconds, () => FillTeamIntroPets(true), TimerFlags.STOP_ON_MAPCHANGE);
		return HookResult.Continue;
	}

	/// <summary>
	/// Makes every intro spot's m_petItem match the plugin pet of the player standing on it: written when they have
	/// one, cleared when an earlier write of ours is there and they have none, untouched when the game put a pet there
	/// itself. <paramref name="recheck"/> is the second pass, which leaves an intact write of ours alone. Game thread only.
	/// </summary>
	private void FillTeamIntroPets(bool recheck)
	{
		if (!Config.Additional.PetsEnabled || !Config.Additional.PetTeamIntroExperimental) return;

		if (PetSchemaOffset(TeamPreviewClass, TeamPreviewPetField) <= 0)
		{
			if (Interlocked.Exchange(ref _teamIntroFieldMissingLogged, 1) == 0)
				Logger.LogWarning(
					"This server has no CCSGO_TeamPreviewCharacterPosition::m_petItem (it predates CS2 1.41.8.2) - " +
					"PetTeamIntroExperimental does nothing");
			return;
		}

		// The spots carry SteamID64s in m_xuid. Bots have 0 there and are never matched.
		var players = new Dictionary<ulong, CCSPlayerController>();
		foreach (var player in Utilities.GetPlayers())
			if (player is { IsValid: true, IsBot: false, IsHLTV: false } && player.SteamID != 0)
				players[player.SteamID] = player;

		int spots = 0, written = 0, cleared = 0, gamePets = 0;

		foreach (var spot in Utilities.FindAllEntitiesByDesignerName<CCSGO_TeamPreviewCharacterPosition>("_intro_"))
		{
			if (!spot.IsValid || !TeamIntroSpotNames.Contains(spot.DesignerName)) continue;
			spots++;

			try
			{
				var handle = spot.EntityHandle.Raw;
				var item = Schema.GetDeclaredClass<CEconItemView>(spot.Handle, TeamPreviewClass, TeamPreviewPetField);
				var wroteHere = _teamIntroPetItems.TryGetValue(handle, out var writtenItemId);
				var ours = wroteHere && item.ItemID == writtenItemId;

				// A write of ours from an earlier intro that is gone by this intro's first pass was replaced when this
				// intro started, not after it - forget it now. Otherwise, when the player now on the spot has no plugin
				// pet, nothing below would drop the entry and the second pass would report it as a rewrite after
				// team_intro_start (and, logging only once, hide a real one later).
				if (!recheck && wroteHere && !ours) _teamIntroPetItems.TryRemove(handle, out _);

				// Diagnostic only, for the first server test: says whether the game writes the spots again after
				// team_intro_start. Only a write made by this intro's first pass can get here.
				if (recheck && wroteHere && !ours && Interlocked.Exchange(ref _teamIntroRewriteLogged, 1) == 0)
					Logger.LogInformation(
						"The game rewrote a team intro pet item after team_intro_start (item id {Before} -> {After}, def " +
						"{Def}) - the plugin pet is written again unless the game put its own pet there.",
						writtenItemId, item.ItemID, item.ItemDefinitionIndex);

				// Whatever the game put there itself stays.
				if (!ours && item.Initialized && item.ItemDefinitionIndex != 0)
				{
					_teamIntroPetItems.TryRemove(handle, out _);
					if (item.ItemDefinitionIndex == PetItemDefIndex) gamePets++;
					continue;
				}

				PetLook? look = null;
				ulong steamId = 0;
				if (spot.Xuid != 0 && players.TryGetValue(spot.Xuid, out var owner))
				{
					look = TeamIntroPetLook(owner);
					steamId = owner.SteamID;
				}

				if (look == null)
				{
					if (ours)
					{
						ClearPetItem(item);
						_teamIntroPetItems.TryRemove(handle, out _);
						Utilities.SetStateChanged(spot, TeamPreviewClass, TeamPreviewPetField);
						cleared++;
					}

					continue;
				}

				// The second pass keeps a write of ours that is still there, so the pet does not change item id in
				// the middle of the intro. A write from an earlier intro is always redone: the player may differ.
				if (recheck && ours) continue;

				item.NetworkedDynamicAttributes.Attributes.RemoveAll();
				item.AttributeList.Attributes.RemoveAll();
				FillPetItem(item, steamId, look);
				_teamIntroPetItems[handle] = item.ItemID;
				Utilities.SetStateChanged(spot, TeamPreviewClass, TeamPreviewPetField);
				written++;
			}
			catch (Exception ex)
			{
				Logger.LogWarning("Could not write a pet into the team intro spot {Spot}: {Reason}", spot.DesignerName,
					ex.Message);
			}
		}

		if (recheck && written == 0 && cleared == 0) return;

		Logger.LogInformation(
			"Team intro{Pass}: {Written} plugin pet(s) written, {Cleared} old one(s) cleared, {GamePets} pet(s) " +
			"left to the game, on {Spots} intro spot(s){NoSpots}",
			recheck ? " (second pass)" : "", written, cleared, gamePets, spots,
			spots == 0 ? " - this map has no team intro spots" : "");
	}

	/// <summary>The look to put in the intro for this player, or null when they have none to show there.</summary>
	private PetLook? TeamIntroPetLook(CCSPlayerController player)
	{
		if (!PlayerMayUsePets(player) ||
		    !GPlayersPet.TryGetValue(player.Slot, out var petInfo) ||
		    FindPet(petInfo.PetId) is not { } definition ||
		    ResolvePetLook(definition, petInfo) is not { } look ||
		    look.Stage < TeamIntroMinPetStage)
			return null;

		return look;
	}

	/// <summary>
	/// Takes back every intro pet this plugin wrote, so an unload (or turning the option off and reloading) does not
	/// leave plugin pets in the spots for whoever stands there next. Nothing to do after a map end.
	/// </summary>
	private void ClearTeamIntroPets()
	{
		foreach (var (handle, itemId) in _teamIntroPetItems)
		{
			_teamIntroPetItems.TryRemove(handle, out _);

			var spot = new CHandle<CCSGO_TeamPreviewCharacterPosition>(handle).Value;
			if (spot == null || !spot.IsValid) continue;

			var item = Schema.GetDeclaredClass<CEconItemView>(spot.Handle, TeamPreviewClass, TeamPreviewPetField);
			if (item.ItemID != itemId) continue;

			ClearPetItem(item);
			Utilities.SetStateChanged(spot, TeamPreviewClass, TeamPreviewPetField);
		}
	}

	/// <summary>An empty item view: no definition, not initialised, no attributes, no names.</summary>
	private static void ClearPetItem(CEconItemView item)
	{
		item.NetworkedDynamicAttributes.Attributes.RemoveAll();
		item.AttributeList.Attributes.RemoveAll();
		item.ItemDefinitionIndex = 0;
		item.EntityQuality = 0;
		item.EntityLevel = 0;
		item.ItemID = 0;
		item.ItemIDLow = 0;
		item.ItemIDHigh = 0;
		item.AccountID = 0;
		item.Initialized = false;
		WritePetName(item, null, PetStageEgg);
	}
}
