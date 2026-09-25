using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace WeaponPaints;

/*
 * *** STICKERS AND CHARMS ON ROWS WITH NO PAINT - THE C4 FIRST OF ALL. ***
 *
 * CS2 1.41.8.2 (2026-09-22) added `weapon_supports_stickers` to the `c4` prefab and rebuilt weapon_c4.vmdl with
 * four sticker homes. The C4 has no paint kits, so every C4 row in wp_player_skins is `weapon_paint_id = 0`, and
 * the plugin used to drop every paint-0 row on the floor before it reached SetStickers / SetKeychain: the paint path
 * returns on `fallbackPaintKit == 0`, and HasChangedPaint answers false for `Paint <= 0`. That is also why a charm
 * on the C4 (possible since charms shipped) never showed, and why the vanilla "Default" rows of every gun lost
 * their stickers, charm and name tag.
 *
 * The fix is a separate branch, not a loosened HasChangedPaint: gloves, StatTrak, the knife pickup and the random
 * skin option all rely on "has a paint" meaning Paint > 0, and none of them should start firing for a row that only
 * carries stickers. So:
 *   - HasWeaponRow           - "the player has a row for this weapon", whatever its paint;
 *   - HasNoPaintExtras       - that row carries something to apply (a sticker, a charm or a name tag);
 *   - TryApplyNoPaintRow     - called from GivePlayerWeaponSkin before the paint path, for everything but knives.
 * A paint-0 row with nothing on it keeps the old early return, exactly as before.
 *
 * The C4 reaches GivePlayerWeaponSkin like every other weapon - the game hands it out with GiveNamedItem at round
 * start (hooked in Events.OnGiveNamedItemPost) and OnEntitySpawned sees "weapon_c4" too. Players never buy it, and a
 * T who picks up a dropped bomb gets the same entity with the attributes still on it, so nothing else is needed.
 */
public partial class WeaponPaints
{
	/// <summary>
	/// weapon_c4. Deliberately NOT in WeaponDefindex / WeaponList: those drive the !skins menu, RefreshWeapons and
	/// PrepareSkins' synthesised Default rows, and the C4 has no paint kit to list in any of them.
	/// </summary>
	internal const int C4DefIndex = 49;

	/*
	 * *** ALL FIVE SLOTS ARE WRITTEN ON THE C4, AND THE FIFTH BORROWS A HOME LIKE THE 29 FOUR-HOME GUNS. ***
	 *
	 * weapon_c4.vmdl (1.41.8.2) authors exactly four homes - 0 Autograph, 1 Team1, 2 Team2, 3 Map - all on body_hd
	 * at scale 8, with no mesh groups and no legacy mesh, and its material bakes g_vSticker4Scale [0 0]. So slot 4
	 * anchored to its own index would draw nothing, exactly the AK's problem. The SkinHub viewer keeps five slots on
	 * the C4 and puts the fifth on the +X side face of the brick (stickerSlots.ts, measured against the mesh and the
	 * sticker mask), and StickerAnchors carries the generated row for it: slot 4 hangs off home 1 and is shifted to
	 * where the viewer draws it. StickerAnchors.For resolves the C4 by C4DefIndex, since 49 is not in WeaponDefindex.
	 *
	 * Unverified in game: whether the game's own C4 has a fifth sticker (community sites say five, the model says
	 * four). The anchor mechanism itself is the one verified on the AK.
	 */

	/// <summary>
	/// The row that decorated this round's bomb and whose account it was given to, so the planted bomb can be given
	/// the same stickers even after the carrier dropped it, died or left. Cleared at round prestart and map start.
	/// </summary>
	private WeaponInfo? _c4StickerSource;
	private uint _c4StickerAccountId;

	/// <summary>
	/// Like HasChangedPaint, but true for any row the player has for this weapon - including paint 0.
	/// </summary>
	private static bool HasWeaponRow(CCSPlayerController player, int weaponDefIndex, out WeaponInfo? weaponInfo)
	{
		weaponInfo = null;

		if (!GPlayerWeaponsInfo.TryGetValue(player.Slot, out var teamInfo) ||
		    !teamInfo.TryGetValue(player.Team, out var teamWeapons) ||
		    !teamWeapons.TryGetValue(weaponDefIndex, out var value))
			return false;

		weaponInfo = value;
		return true;
	}

	/// <summary>
	/// True when the row carries something that is not a paint. A sticker column with id 0 is the DDL default
	/// ('0;0;0;0;0;0;0' parses as a valid, empty sticker), and KeyChain is never null - an unset one has id 0 - so
	/// both are checked by id.
	/// </summary>
	private static bool HasNoPaintExtras(WeaponInfo weaponInfo) =>
		weaponInfo.Stickers.Any(sticker => sticker.Id != 0) ||
		weaponInfo.KeyChain is { Id: > 0 } ||
		!string.IsNullOrEmpty(weaponInfo.Nametag);

	/// <summary>
	/// Applies a paint-0 row that still has stickers, a charm or a name tag, and returns true when it did - in which
	/// case the caller must not run the paint path. Returns false (and touches nothing) for rows with a paint, rows
	/// with nothing to apply, and players with no row.
	///
	/// What it deliberately does NOT do, compared with the paint path: no FallbackPaintKit, no "set item texture *"
	/// attributes, no StatTrak (an unpainted weapon has no StatTrak in the game either) and no mesh group change. The
	/// weapon keeps the mesh the game spawned it with, which for an unpainted gun is the hd one - the published
	/// "Default" rows say `legacy_model: false` - and the C4 has only one mesh.
	/// </summary>
	private bool TryApplyNoPaintRow(CCSPlayerController player, CBasePlayerWeapon weapon, int weaponDefIndex)
	{
		if (!HasWeaponRow(player, weaponDefIndex, out var weaponInfo) || weaponInfo == null) return false;
		if (weaponInfo.Paint > 0 || !HasNoPaintExtras(weaponInfo)) return false;

		var item = weapon.AttributeManager.Item;

		item.AttributeList.Attributes.RemoveAll();
		item.NetworkedDynamicAttributes.Attributes.RemoveAll();

		UpdatePlayerEconItemId(item);
		item.CustomName = weaponInfo.Nametag;

		// The published Default row says which mesh an unpainted weapon uses (hd for all of them today). The C4 is
		// forced to hd: it has no legacy mesh, and StickerAnchors has no entry for it either way.
		var isLegacyModel = weaponDefIndex != C4DefIndex &&
		                    SkinsList.FirstOrDefault(skin =>
			                    ItemData.SkinDefindex(skin) == weaponDefIndex && ItemData.SkinPaint(skin) == 0) is { } row &&
		                    ItemData.SkinIsLegacyModel(row);

		// Only a charm that is actually set. The paint path writes slot 0 unconditionally (id 0 when unset); on a
		// weapon that had no attributes at all a moment ago there is nothing to overwrite, so there is no reason to.
		if (weaponInfo.KeyChain is { Id: > 0 }) SetKeychain(player, weapon);
		if (weaponInfo.Stickers.Count > 0) SetStickers(player, weapon, isLegacyModel);

		if (weaponDefIndex == C4DefIndex)
		{
			_c4StickerSource = weaponInfo;
			_c4StickerAccountId = (uint)player.SteamID;
		}

		return true;
	}

	private void RegisterC4Listeners()
	{
		RegisterEventHandler<EventRoundPrestart>(OnRoundPrestartC4);
		RegisterEventHandler<EventBombPlanted>(OnBombPlantedC4);
		RegisterListener<Listeners.OnMapStart>(_ => _c4StickerSource = null);
	}

	private HookResult OnRoundPrestartC4(EventRoundPrestart @event, GameEventInfo info)
	{
		// Prestart, not round_start: the new bomb is handed out while the round restarts, and a reset on round_start
		// could land after it and wipe the row that just decorated it.
		_c4StickerSource = null;
		_c4StickerAccountId = 0;
		return HookResult.Continue;
	}

	/*
	 * *** THE PLANTED BOMB IS A DIFFERENT ENTITY WITH ITS OWN ECON ITEM. UNTESTED IN GAME. ***
	 *
	 * Planting removes weapon_c4 and creates planted_c4 (CPlantedC4), which has its own m_AttributeManager (server
	 * and client schema, unchanged by 1.41.8.2). Whether the game copies the carried bomb's item across - and with it
	 * our attributes - is not known; nobody has seen a stickered planted bomb yet.
	 *
	 * So: if the planted bomb's item already has networked attributes, the game copied something and it is left
	 * alone. If it has none, the stickers of the row that decorated this round's bomb are written onto it, in the
	 * same tick the bomb was planted (bomb_planted fires from the plant itself), before it is first sent to anyone.
	 *
	 * This hangs off the bomb_planted GAME EVENT on purpose, not an OnEntitySpawned listener for "planted_c4": the
	 * event carries no dependency on CounterStrikeSharp's entity listeners, which were reported broken on 1.41.8.2
	 * before the CSS update that fixed this plugin on the owner's servers. Only stickers are copied - whether the
	 * planted bomb draws a charm is unknown.
	 */
	private HookResult OnBombPlantedC4(EventBombPlanted @event, GameEventInfo info)
	{
		if (!Config.Additional.SkinEnabled) return HookResult.Continue;

		var source = _c4StickerSource;
		if (source == null || !source.Stickers.Any(sticker => sticker.Id != 0)) return HookResult.Continue;

		try
		{
			foreach (var planted in Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4"))
			{
				if (!planted.IsValid) continue;

				var item = planted.AttributeManager.Item;
				if (item.NetworkedDynamicAttributes.Attributes.Count > 0) continue;

				if (item.ItemDefinitionIndex == 0) item.ItemDefinitionIndex = C4DefIndex;
				UpdatePlayerEconItemId(item);
				item.AccountID = _c4StickerAccountId;
				item.Initialized = true;

				WriteStickerAttributes(item, C4DefIndex, source, false);
				Utilities.SetStateChanged(planted, "CPlantedC4", "m_AttributeManager");
			}
		}
		catch (Exception ex)
		{
			Logger.LogWarning("Could not copy the C4 stickers onto the planted bomb: {Reason}", ex.Message);
		}

		return HookResult.Continue;
	}
}
