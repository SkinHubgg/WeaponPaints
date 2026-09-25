using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;

namespace WeaponPaints
{
	public partial class WeaponPaints
	{
		private void GivePlayerWeaponSkin(CCSPlayerController player, CBasePlayerWeapon weapon)
		{
			if (!Config.Additional.SkinEnabled) return;
			if (!GPlayerWeaponsInfo.TryGetValue(player.Slot, out _)) return;
			
			bool isKnife = weapon.DesignerName.Contains("knife") || weapon.DesignerName.Contains("bayonet");
			
			switch (isKnife)
			{
				case true when !HasChangedKnife(player, out var _):
					return;
				
				case true:
				{
					var newDefIndex = WeaponDefindex.FirstOrDefault(x => x.Value == GPlayersKnife[player.Slot][player.Team]);
					if (newDefIndex.Key == 0) return;

					if (weapon.AttributeManager.Item.ItemDefinitionIndex != newDefIndex.Key)
					{
						SubclassChange(weapon, (ushort)newDefIndex.Key);
					}

					weapon.AttributeManager.Item.ItemDefinitionIndex = (ushort)newDefIndex.Key;
					weapon.AttributeManager.Item.EntityQuality = 3;
					
					weapon.AttributeManager.Item.AttributeList.Attributes.RemoveAll();
					weapon.AttributeManager.Item.NetworkedDynamicAttributes.Attributes.RemoveAll();
					break;
				}
				default:
					weapon.AttributeManager.Item.EntityQuality = 0;
					break;
			}

			UpdatePlayerEconItemId(weapon.AttributeManager.Item);

			int weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;
			int fallbackPaintKit;
			
			weapon.AttributeManager.Item.AccountID = (uint)player.SteamID;
			
			List<JObject> skinInfo;
			bool isLegacyModel;

			// A row with no paint that still carries stickers, a charm or a name tag - every C4 row, and a gun saved
			// as "Default" with stickers on it. The paint path below returns on paint 0 before it reaches the
			// stickers, so these used to be dropped. See C4Stickers.cs.
			if (!isKnife && TryApplyNoPaintRow(player, weapon, weaponDefIndex))
				return;

			// The C4 has no paint kits: a "random skin" for it would be paint 0 plus texture attributes that describe
			// nothing, so the bomb is left alone.
			if (_config.Additional.GiveRandomSkin && weaponDefIndex != C4DefIndex &&
			    !HasChangedPaint(player, weaponDefIndex, out _))
			{
				// Random skins
				weapon.FallbackPaintKit = GetRandomPaint(weaponDefIndex);
				weapon.FallbackSeed = 0;
				weapon.FallbackWear = 0.01f;
			
				weapon.AttributeManager.Item.NetworkedDynamicAttributes.Attributes.RemoveAll();
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "set item texture prefab", GetRandomPaint(weaponDefIndex));
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "set item texture seed", 0);
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "set item texture wear", 0.01f);
			
				weapon.AttributeManager.Item.AttributeList.Attributes.RemoveAll();
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.AttributeList.Handle, "set item texture prefab", GetRandomPaint(weaponDefIndex));
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.AttributeList.Handle, "set item texture seed", 0);
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.AttributeList.Handle, "set item texture wear", 0.01f);
			
				fallbackPaintKit = weapon.FallbackPaintKit;
			
				if (fallbackPaintKit == 0)
					return;
			
				// ItemData.SkinPaint, not w["paint_index"]?.ToObject<int>(): paint_index is null on the
				// vanilla-knife rows and ToObject<int>() throws ArgumentException on those. This runs for
				// every weapon a player spawns with.
				skinInfo = SkinsList
					.Where(w =>
						ItemData.SkinDefindex(w) == weaponDefIndex &&
						ItemData.SkinPaint(w) == fallbackPaintKit)
					.ToList();

				isLegacyModel = skinInfo.Count <= 0 || ItemData.SkinIsLegacyModel(skinInfo[0]);
				UpdatePlayerWeaponMeshGroupMask(player, weapon, isLegacyModel);
				return;
			}

			if (!HasChangedPaint(player, weaponDefIndex, out var weaponInfo) || weaponInfo == null)
				return;

			//Log($"Apply on {weapon.DesignerName}({weapon.AttributeManager.Item.ItemDefinitionIndex}) paint {gPlayerWeaponPaints[steamId.SteamId64][weapon.AttributeManager.Item.ItemDefinitionIndex]} seed {gPlayerWeaponSeed[steamId.SteamId64][weapon.AttributeManager.Item.ItemDefinitionIndex]} wear {gPlayerWeaponWear[steamId.SteamId64][weapon.AttributeManager.Item.ItemDefinitionIndex]}");

			weapon.AttributeManager.Item.AttributeList.Attributes.RemoveAll();
			weapon.AttributeManager.Item.NetworkedDynamicAttributes.Attributes.RemoveAll();
			
			UpdatePlayerEconItemId(weapon.AttributeManager.Item);

			weapon.AttributeManager.Item.CustomName = weaponInfo.Nametag;
			weapon.FallbackPaintKit = weaponInfo.Paint;
			
			weapon.FallbackSeed = weaponInfo is { Paint: 38, Seed: 0 } ? _fadeSeed++ : weaponInfo.Seed;
			
			weapon.FallbackWear = weaponInfo.Wear;
			CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "set item texture prefab", weapon.FallbackPaintKit);

			if (weaponInfo.StatTrak)
			{			
				weapon.AttributeManager.Item.EntityQuality = 9;

				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "kill eater", ViewAsFloat((uint)weaponInfo.StatTrakCount));
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "kill eater score type", 0);
				
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.AttributeList.Handle, "kill eater", ViewAsFloat((uint)weaponInfo.StatTrakCount));
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.AttributeList.Handle, "kill eater score type", 0);
			}

			fallbackPaintKit = weapon.FallbackPaintKit;

			if (fallbackPaintKit == 0)
				return;

			// See the note above: SkinPaint is null-safe, a direct ToObject<int>() on paint_index is not.
			skinInfo = SkinsList
				.Where(w =>
					ItemData.SkinDefindex(w) == weaponDefIndex &&
					ItemData.SkinPaint(w) == fallbackPaintKit)
				.ToList();

			/*
			 * *** RESOLVED BEFORE THE STICKERS ARE APPLIED, NOT AFTER, AND THAT IS THE ONLY REASON THIS MOVED. ***
			 *
			 * The mesh variant is an INPUT to sticker placement now: each variant binds its own material and
			 * its own markup, so the fifth slot's borrowed home differs between them and on five weapons they
			 * do not even want the same one (see StickerAnchors). This pair used to be computed below the two
			 * Set* calls purely because UpdatePlayerWeaponMeshGroupMask is the only thing that used it.
			 *
			 * Nothing else changes: the mesh group mask is still applied last, off the same value.
			 */
			// The C4 is always its one hd mesh (weapon_c4.vmdl has no mesh groups). It cannot have a paint, so this
			// is belt and braces - the no-paint branch above is where a C4 row actually goes.
			isLegacyModel = weaponDefIndex != C4DefIndex &&
			                (skinInfo.Count <= 0 || ItemData.SkinIsLegacyModel(skinInfo[0]));

			if (weaponInfo.KeyChain != null) SetKeychain(player, weapon);
			if (weaponInfo.Stickers.Count > 0) SetStickers(player, weapon, isLegacyModel);

			UpdatePlayerWeaponMeshGroupMask(player, weapon, isLegacyModel);
		}
		
		// silly method to update sticker when call RefreshWeapons()
		private void IncrementWearForWeaponWithStickers(CCSPlayerController player, CBasePlayerWeapon weapon)
		{
			int weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;
			if (!HasChangedPaint(player, weaponDefIndex, out var weaponInfo) || weaponInfo == null ||
			    weaponInfo.Stickers.Count <= 0) return;
			
			float wearIncrement = 0.001f;
			float currentWear = weaponInfo.Wear;

			var playerWear = _temporaryPlayerWeaponWear.GetOrAdd(player.Slot, _ => new ConcurrentDictionary<int, float>());

			float incrementedWear = playerWear.AddOrUpdate(
				weaponDefIndex,
				currentWear + wearIncrement,
				(_, oldWear) => Math.Min(oldWear + wearIncrement, 1.0f)
			);

			weapon.FallbackWear = incrementedWear;
		}

		private void SetStickers(CCSPlayerController? player, CBasePlayerWeapon weapon, bool isLegacyModel)
		{
			if (player == null || !player.IsValid) return;

			int weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;

			// HasWeaponRow, not HasChangedPaint: a row with no paint reaches here too now (the C4, and a "Default"
			// gun with stickers - see C4Stickers.cs). On the paint path both return the same row.
			if (!HasWeaponRow(player, weaponDefIndex, out var weaponInfo) || weaponInfo == null)
				return;

			WriteStickerAttributes(weapon.AttributeManager.Item, weaponDefIndex, weaponInfo, isLegacyModel);

			if (_temporaryPlayerWeaponWear.TryGetValue(player.Slot, out var playerWear) &&
				playerWear.TryGetValue(weaponDefIndex, out float storedWear))
			{
				weapon.FallbackWear = storedWear;
			}
		}

		/// <summary>
		/// The sticker attributes of one row, written onto any econ item - a weapon's, or the planted bomb's (which is
		/// not a weapon, see C4Stickers.OnBombPlantedC4). Split out of SetStickers for that reason only; the body is
		/// unchanged. The C4's fifth slot takes its borrowed home from StickerAnchors like any four-home gun.
		/// </summary>
		private static void WriteStickerAttributes(CEconItemView item, int weaponDefIndex, WeaponInfo weaponInfo, bool isLegacyModel)
		{
			foreach (var sticker in weaponInfo.Stickers)
			{
				// The COLUMN this sticker came from, not its position in the list. See StickerInfo.Slot
				// for what `Stickers.IndexOf(sticker)` used to do to a player with a gap in their slots.
				int stickerSlot = sticker.Slot;

				CAttributeListSetOrAddAttributeValueByName.Invoke(item.NetworkedDynamicAttributes.Handle,
					$"sticker slot {stickerSlot} id", ViewAsFloat(sticker.Id));

				/*
				 * *** ONLY WHEN THE STICKER HAS ACTUALLY BEEN MOVED, and writing it unconditionally was a
				 * REGRESSION I shipped. ***
				 *
				 * The owner's report: "the weaponpaints fix just made all the stickers go to the first
				 * sticker placement/position instead of putting them on the position we chose".
				 *
				 * The reasoning that led to writing it always was that `schema` is what makes custom
				 * coordinates apply - true - and that a sticker at 0,0 is indistinguishable from one that
				 * was never moved. That second half is the mistake. A sticker nobody dragged carries the
				 * column default, and the website has no control that sets `schema` to anything but 0
				 * (`DEFAULT_STICKER.schema` is 0 and the row default is `0;0;0;0;0;0;0`), so writing it
				 * unconditionally means every untouched sticker is now explicitly told to use schema 0
				 * rather than left alone to sit at its own slot's authored home. Five stickers all told
				 * to use one schema is five stickers in one place.
				 *
				 * The guard is therefore back exactly as it was. The other half of that commit -
				 * `sticker.Slot` instead of `Stickers.IndexOf(sticker)` - is KEPT: the column index is
				 * right where the list position is only accidentally right, and it is what a player with
				 * a gap in their slots needs.
				 */
				/*
				 * *** THE VALUE IS THE SLOT'S OWN INDEX, NOT 0 - and this is an EXPERIMENT, stated as one. ***
				 *
				 * What is known: `sticker slot N schema` is a real econ attribute (items_game ids 290-295,
				 * six slots, `stored_as_integer 1`, `hidden 1`) with **no declared range, no default, and
				 * no item in the game that sets it**. It appears in no shader uniform and in no field of
				 * the inspect protobuf - so the claim in 9d000024 that "an inspect link carries the schema"
				 * was simply false, and the reasoning built on it was worthless.
				 *
				 * What is observed, in game, by the owner:
				 *   - written as 0 on EVERY sticker  -> all five stickers land on ONE position
				 *   - written as 0 only on moved ones -> still clustered and misaligned
				 *
				 * Both readings are consistent with `schema` naming WHICH SLOT'S AUTHORED HOME the sticker
				 * is anchored to. Everything pointed at 0 is everything pointed at slot 0's home, which is
				 * exactly the picture. The plugin's original author agreed: his first version wrote
				 * `stickerSlot` here, and it was only later changed to a literal 0.
				 *
				 * So each sticker is anchored to its OWN slot, which under that reading is the identity -
				 * the placement it would have had anyway - and the offsets are then deltas from it.
				 *
				 * *** THE GUARD STAYS. *** Only a sticker the user actually moved gets the attribute. A
				 * sticker nobody dragged is left with no attribute at all, which is the behaviour that has
				 * always worked, and it keeps the blast radius to the stickers this is trying to fix. If
				 * this reading is wrong, the failure is confined to moved stickers rather than all of them.
				 *
				 * *** THE COLUMN WINS WHEN IT NAMES AN ANCHOR; OTHERWISE THE FIFTH SLOT GETS ONE FROM HERE. ***
				 *
				 * A slot's home is the weapon MODEL's, and a weapon does not have one per slot. 29 of the 69
				 * weapon+mesh variants author only four, so slot 4 anchored to its own index points at a home
				 * that does not exist: the material bakes `g_vSticker4Scale [0 0]`, the pixel shader treats a
				 * zero scale as a SKIP, and the fifth sticker draws nothing at all. `StickerAnchors` is the
				 * measured table of which home each of those 29 borrows instead, and by how far - the whole
				 * argument, and the in-game verification, are written down there.
				 *
				 * *** THE PLUGIN DOES THE SUBSTITUTION, SO A WEBSITE DOES NOT HAVE TO. *** This used to require
				 * the site to write the anchor and pre-shift the offsets, on the reasoning that only the site
				 * knew the markup table. It knew the markup; it did not need to know anything else, because the
				 * weapon and the mesh variant are both resolved right here. A site that never ported the table
				 * simply had an invisible fifth sticker, which is what this removes for all of them at once.
				 *
				 * *** A ROW THAT NAMES ITS OWN ANCHOR IS LEFT ALONE, AND THAT IS WHAT KEEPS EXISTING ROWS RIGHT. ***
				 * A site already doing the shift writes a non-zero `schema` AND offsets measured from that
				 * anchor; adding this table's delta on top would double-shift it. So the column is checked
				 * first and the table is only reached when the column says nothing.
				 *
				 * *** AND THE GUARD HAS TO ADMIT AN UNMOVED FIFTH STICKER. *** The rule above is "only a sticker
				 * the user actually moved gets the attribute", because an untouched sticker left alone sits at
				 * its own authored home - correct, and the behaviour that has always worked. A fifth sticker on
				 * one of the 29 has no such home to be left alone at, so `anchored` opens the guard for it even
				 * at 0,0. Every other slot is unaffected: `StickerAnchors.For` returns null for them.
				 *
				 * `ViewAsFloat` because `sticker slot N schema` is `stored_as_integer 1`, the same as
				 * `sticker slot N id`. UNVERIFIED for this attribute specifically: the only value ever written
				 * to it was 0, where the bit pattern and the plain float agree. If a named anchor does nothing in
				 * game, a plain `(float)stickerAnchor` is the next thing to try.
				 */
				uint stickerAnchor;
				float stickerOffsetX = sticker.OffsetX;
				float stickerOffsetY = sticker.OffsetY;
				bool anchored;

				if (sticker.Schema != 0)
				{
					stickerAnchor = sticker.Schema;
					anchored = true;
				}
				else if (StickerAnchors.For(weaponDefIndex, isLegacyModel, stickerSlot) is { } borrowed)
				{
					stickerAnchor = borrowed.Anchor;
					stickerOffsetX += borrowed.Dx;
					stickerOffsetY += borrowed.Dy;
					anchored = true;
				}
				else
				{
					stickerAnchor = (uint)stickerSlot;
					anchored = false;
				}

				if (stickerOffsetX != 0 || stickerOffsetY != 0 || anchored)
					CAttributeListSetOrAddAttributeValueByName.Invoke(item.NetworkedDynamicAttributes.Handle,
						$"sticker slot {stickerSlot} schema", ViewAsFloat(stickerAnchor));

				CAttributeListSetOrAddAttributeValueByName.Invoke(item.NetworkedDynamicAttributes.Handle,
					$"sticker slot {stickerSlot} offset x", stickerOffsetX);
				CAttributeListSetOrAddAttributeValueByName.Invoke(item.NetworkedDynamicAttributes.Handle,
					$"sticker slot {stickerSlot} offset y", stickerOffsetY);
				CAttributeListSetOrAddAttributeValueByName.Invoke(item.NetworkedDynamicAttributes.Handle,
					$"sticker slot {stickerSlot} wear", sticker.Wear);
				CAttributeListSetOrAddAttributeValueByName.Invoke(item.NetworkedDynamicAttributes.Handle,
					$"sticker slot {stickerSlot} scale", sticker.Scale);
				CAttributeListSetOrAddAttributeValueByName.Invoke(item.NetworkedDynamicAttributes.Handle,
					$"sticker slot {stickerSlot} rotation", sticker.Rotation);
			}
		}

		private void SetKeychain(CCSPlayerController? player, CBasePlayerWeapon weapon)
		{
			if (player == null || !player.IsValid) return;

			int weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;

			// HasWeaponRow for the same reason as SetStickers: a charm on the C4 or on a "Default" gun has no paint.
			if (!HasWeaponRow(player, weaponDefIndex, out var value) || value?.KeyChain == null)
				return;
			
			var keyChain = value.KeyChain;

			CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
				"keychain slot 0 id", ViewAsFloat(keyChain.Id));
			CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
				"keychain slot 0 offset x", keyChain.OffsetX);
			CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
				"keychain slot 0 offset y", keyChain.OffsetY);
			CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
				"keychain slot 0 offset z", keyChain.OffsetZ);
			CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
				"keychain slot 0 seed", ViewAsFloat(keyChain.Seed));
		}

		private static void GiveKnifeToPlayer(CCSPlayerController? player)
		{
			if (!_config.Additional.KnifeEnabled || player == null || !player.IsValid) return;

			if (PlayerHasKnife(player)) return;

			//string knifeToGive = (CsTeam)player.TeamNum == CsTeam.Terrorist ? "weapon_knife_t" : "weapon_knife";
			player.GiveNamedItem(CsItem.Knife);
			Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");
		}

		private static bool PlayerHasKnife(CCSPlayerController? player)
		{
			if (!_config.Additional.KnifeEnabled) return false;

			if (player == null || !player.IsValid || !player.PlayerPawn.IsValid)
			{
				return false;
			}

			if (player.PlayerPawn.Value == null || player.PlayerPawn.Value.WeaponServices == null || player.PlayerPawn.Value.ItemServices == null)
				return false;

			var weapons = player.PlayerPawn.Value.WeaponServices?.MyWeapons;
			if (weapons == null) return false;
			foreach (var weapon in weapons)
			{
				if (!weapon.IsValid || weapon.Value == null || !weapon.Value.IsValid) continue;
				if (weapon.Value.DesignerName.Contains("knife") || weapon.Value.DesignerName.Contains("bayonet"))
				{
					return true;
				}
			}
			return false;
		}

		private void RefreshWeapons(CCSPlayerController? player)
		{
			if (!_gBCommandsAllowed) return;
			if (player == null || !player.IsValid || player.PlayerPawn.Value == null || (LifeState_t)player.LifeState != LifeState_t.LIFE_ALIVE)
				return;
			if (player.PlayerPawn.Value.WeaponServices == null || player.PlayerPawn.Value.ItemServices == null)
				return;

			var weapons = player.PlayerPawn.Value.WeaponServices.MyWeapons;

			if (weapons.Count == 0)
				return;
			if (player.Team is CsTeam.None or CsTeam.Spectator)
				return;

			var hasKnife = false;
			
			Dictionary<string, List<(int, int)>> weaponsWithAmmo = [];

			foreach (var weapon in weapons)
			{
				if (!weapon.IsValid || weapon.Value == null ||
					!weapon.Value.IsValid || !weapon.Value.DesignerName.Contains("weapon_"))
					continue;
				
				CCSWeaponBaseGun gun = weapon.Value.As<CCSWeaponBaseGun>();

				if (weapon.Value.Entity == null) continue;
				if (!weapon.Value.OwnerEntity.IsValid) continue;
				if (gun.Entity == null) continue;
				if (!gun.IsValid) continue;

				try
				{
					CCSWeaponBaseVData? weaponData = weapon.Value.As<CCSWeaponBase>().VData;

					if (weaponData == null) continue;

					if (weaponData.GearSlot is gear_slot_t.GEAR_SLOT_RIFLE or gear_slot_t.GEAR_SLOT_PISTOL)
					{
						if (!WeaponDefindex.TryGetValue(weapon.Value.AttributeManager.Item.ItemDefinitionIndex, out var weaponByDefindex))
							continue;

						int clip1 = weapon.Value.Clip1;
						int reservedAmmo = weapon.Value.ReserveAmmo[0];

						if (!weaponsWithAmmo.TryGetValue(weaponByDefindex, out var value))
						{
							value = [];
							weaponsWithAmmo.Add(weaponByDefindex, value);
						}

						value.Add((clip1, reservedAmmo));

						if (gun.VData == null) return;
						
						weapon.Value?.AddEntityIOEvent("Kill", weapon.Value, null, "", 0.1f);
					}

					if (weaponData.GearSlot == gear_slot_t.GEAR_SLOT_KNIFE)
					{
						weapon.Value?.AddEntityIOEvent("Kill", weapon.Value, null, "", 0.1f);
						hasKnife = true;
					}
				}
				catch (Exception ex)
				{
					Logger.LogWarning(ex.Message);
				}
			}

			AddTimer(0.23f, () =>
					{
						if (!_gBCommandsAllowed) return;

						if (!PlayerHasKnife(player) && hasKnife)
						{
							var newKnife = new CBasePlayerWeapon(player.GiveNamedItem(CsItem.Knife));
							var newWeapon = new CBasePlayerWeapon(player.GiveNamedItem(CsItem.USP));
							player.GiveNamedItem(CsItem.Knife);
							player.ExecuteClientCommand("slot3");

							Server.NextFrame(() =>
							{
								try
								{
									if (newKnife != null && newKnife.IsValid)
										newKnife.AddEntityIOEvent("Kill", newKnife, null, "", 0.01f);
									if (newWeapon != null && newWeapon.IsValid)
										newWeapon.AddEntityIOEvent("Kill", newWeapon, null, "", 0.01f);
								}
								catch (Exception ex)
								{
									Logger.LogWarning("Error AddEntityIOEvent " + ex.Message);
								}
							});
						}


						foreach (var entry in weaponsWithAmmo)
						{
							foreach (var ammo in entry.Value)
							{
								var newWeapon = new CBasePlayerWeapon(player.GiveNamedItem(entry.Key));
								Server.NextFrame(() =>
						{
							try
							{
								newWeapon.Clip1 = ammo.Item1;
								newWeapon.ReserveAmmo[0] = ammo.Item2;

								IncrementWearForWeaponWithStickers(player, newWeapon);
							}
							catch (Exception ex)
							{
								Logger.LogWarning("Error setting weapon properties: " + ex.Message);
							}
						});
							}
						}
					}, TimerFlags.STOP_ON_MAPCHANGE);
		}

		private void GivePlayerGloves(CCSPlayerController player)
		{
			if (!Utility.IsPlayerValid(player) || (LifeState_t)player.LifeState != LifeState_t.LIFE_ALIVE) return;

			CCSPlayerPawn? pawn = player.PlayerPawn.Value;
			if (pawn == null || !pawn.IsValid)
				return;

			CEconItemView item = pawn.EconGloves;

			item.NetworkedDynamicAttributes.Attributes.RemoveAll();
			item.AttributeList.Attributes.RemoveAll();

			//force gloves model refresh to prevent model overlap
			player.ExecuteClientCommand("lastinv");
			Instance.AddTimer(0.08f, () =>
			{	
				try
				{
					if (!player.IsValid)
						return;

					if (!player.PawnIsAlive)
						return;

					if (!GPlayersGlove.TryGetValue(player.Slot, out var gloveInfo) ||
					    !gloveInfo.TryGetValue(player.Team, out var gloveId) ||
					    gloveId == 0 ||
					    !HasChangedPaint(player, gloveId, out var weaponInfo) || weaponInfo == null)
						return;

					item.ItemDefinitionIndex = gloveId;
					
					UpdatePlayerEconItemId(item);

					item.NetworkedDynamicAttributes.Attributes.RemoveAll();
					CAttributeListSetOrAddAttributeValueByName.Invoke(item.NetworkedDynamicAttributes.Handle, "set item texture prefab", weaponInfo.Paint);
					CAttributeListSetOrAddAttributeValueByName.Invoke(item.NetworkedDynamicAttributes.Handle, "set item texture seed", weaponInfo.Seed);
					CAttributeListSetOrAddAttributeValueByName.Invoke(item.NetworkedDynamicAttributes.Handle, "set item texture wear", weaponInfo.Wear);

					item.AttributeList.Attributes.RemoveAll();
					CAttributeListSetOrAddAttributeValueByName.Invoke(item.AttributeList.Handle, "set item texture prefab", weaponInfo.Paint);
					CAttributeListSetOrAddAttributeValueByName.Invoke(item.AttributeList.Handle, "set item texture seed", weaponInfo.Seed);
					CAttributeListSetOrAddAttributeValueByName.Invoke(item.AttributeList.Handle, "set item texture wear", weaponInfo.Wear);

					item.Initialized = true;
				
					//force gloves model refresh to prevent model overlap
					player.ExecuteClientCommand("lastinv");
					SetBodygroup(pawn, "first_or_third_person", 0);
					AddTimer(0.2f, () => SetBodygroup(pawn, "first_or_third_person", 1), TimerFlags.STOP_ON_MAPCHANGE);
				}
				catch (Exception) { }
			}, TimerFlags.STOP_ON_MAPCHANGE);
		}

		private static int GetRandomPaint(int defindex)
		{
			if (SkinsList.Count == 0)
				return 0;

			Random rnd = new Random();

			// Filter weapons by the provided defindex
			var filteredWeapons = SkinsList.Where(w => ItemData.SkinDefindex(w) == defindex).ToList();

			if (filteredWeapons.Count == 0)
				return 0;

			var randomWeapon = filteredWeapons[rnd.Next(filteredWeapons.Count)];

			return ItemData.SkinPaint(randomWeapon) ?? 0;
		}

		//xstage idea on css discord
		public static void SubclassChange(CBasePlayerWeapon weapon, ushort itemD)
		{
			weapon.AcceptInput("ChangeSubclass", value: itemD.ToString());
		}

		public static void SetBodygroup(CCSPlayerPawn pawn, string group, int value)
		{
			pawn.AcceptInput("SetBodygroup", value:$"{group},{value}");
		}

		private void UpdateWeaponMeshGroupMask(CBaseEntity weapon, bool isLegacy = false)
		{
				if (weapon.CBodyComponent?.SceneNode == null) return;
				//var skeleton = weapon.CBodyComponent.SceneNode.GetSkeletonInstance();
				// skeleton.ModelState.MeshGroupMask = isLegacy ? 2UL : 1UL;

				weapon.AcceptInput("SetBodygroup", value: $"body,{(isLegacy ? 1 : 0)}");
		}

		private void UpdatePlayerWeaponMeshGroupMask(CCSPlayerController player, CBasePlayerWeapon weapon, bool isLegacy)
		{
			// weapon_c4.vmdl has no mesh groups (m_meshGroups = []): there is no "body" group to set and no legacy
			// mesh to pick, so the bomb always keeps its one hd mesh.
			if (weapon.AttributeManager.Item.ItemDefinitionIndex == C4DefIndex) return;

			UpdateWeaponMeshGroupMask(weapon, isLegacy);
		}

		private static void GivePlayerAgent(CCSPlayerController player)
		{
			/*
			 * *** DO NOT SET A MODEL BEFORE THE AGENT DATASET HAS LOADED. ***
			 *
			 * `AgentsList` starts empty and is filled by `ItemData` from `<DataUrl>/data/agents.json`.
			 * That fetch is DELIBERATELY NOT AWAITED - `WeaponPaints.cs` says so - so it runs on a worker
			 * while the server is already accepting connections. A player joining inside that window
			 * reaches here with the list still empty.
			 *
			 * What arrives here is a model path out of the DATABASE, not out of the list, so an empty
			 * list does not stop the `SetModel` call - it only removes the one thing that could have
			 * told us the path is still a real agent. Setting an unvalidated model on a pawn is how a
			 * player ends up looking wrong or not rendering at all, and it is silent: the try/catch
			 * below swallows whatever comes back.
			 *
			 * So this waits instead. A player who joins in that window keeps the default model for a few
			 * seconds and gets their agent on the next application, which is a far better failure than a
			 * broken one. If the fetch never succeeds at all, nobody gets an agent and the log below says
			 * why - which beats every player on the server looking wrong with no explanation.
			 */
			if (AgentsList.Count == 0)
			{
				Utility.Log("[WeaponPaints] agents dataset not loaded yet - skipping agent for this player");
				return;
			}

			if (!GPlayersAgent.TryGetValue(player.Slot, out var value)) return;

			var model = player.TeamNum == 3 ? value.CT : value.T;
			if (string.IsNullOrEmpty(model)) return;

			if (player.PlayerPawn.Value == null)
				return;

			try
			{
				Server.NextFrame(() =>
				{
					player.PlayerPawn.Value.SetModel(
						$"agents/models/{model}.vmdl"
					);
				});
			}
			catch (Exception)
			{
			}
		}

		private static void GivePlayerMusicKit(CCSPlayerController player)
		{
			if (player.IsBot) return;
			if (!GPlayersMusic.TryGetValue(player.Slot, out var musicInfo) ||
			    !musicInfo.TryGetValue(player.Team, out var musicId) || musicId == 0) return;
			
			if (player.InventoryServices == null) return;

			player.MusicKitID = musicId;
			// player.MvpNoMusic = false;
			player.InventoryServices.MusicID = musicId;
			Utilities.SetStateChanged(player, "CCSPlayerController", "m_iMusicKitID");
			// Utilities.SetStateChanged(player, "CCSPlayerController", "m_bMvpNoMusic");
			Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");
			// player.MusicKitMVPs = musicId;
			// Utilities.SetStateChanged(player, "CCSPlayerController", "m_iMusicKitMVPs");
		}

		private static void GivePlayerPin(CCSPlayerController player)
		{
			if (!GPlayersPin.TryGetValue(player.Slot, out var pinInfo) ||
			    !pinInfo.TryGetValue(player.Team, out var pinId)) return;
			if (player.InventoryServices == null) return;
			
			player.InventoryServices.Rank[5] = pinId > 0 ? (MedalRank_t)pinId : MedalRank_t.MEDAL_RANK_NONE;
			Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");
		}
		
		private void GiveOnItemPickup(CCSPlayerController player)
		{
			var pawn = player.PlayerPawn.Value;
			if (pawn == null) return;
			
			var myWeapons = pawn.WeaponServices?.MyWeapons;
			if (myWeapons == null) return;
			
			foreach (var handle in myWeapons)
			{
				var weapon = handle.Value;
			
				if (weapon == null || !weapon.IsValid) continue;
				if (myWeapons.Count == 1)
				{
					var newWeapon = new CBasePlayerWeapon(player.GiveNamedItem(CsItem.USP));
					weapon.AddEntityIOEvent("Kill", weapon, null, "", 0.01f);
					player.GiveNamedItem(CsItem.Knife);
					player.ExecuteClientCommand("slot3");
					newWeapon.AddEntityIOEvent("Kill", newWeapon, null, "", 0.01f);
				}
					
				GivePlayerWeaponSkin(player, weapon);
			}
		}
		
		private void UpdatePlayerEconItemId(CEconItemView econItemView)
		{
			var itemId = _nextItemId++;
			
			econItemView.ItemID = itemId;
			econItemView.ItemIDLow = (uint)itemId & 0xFFFFFFFF;
			econItemView.ItemIDHigh = (uint)itemId >> 32;
		}

		private static CCSPlayerController? GetPlayerFromItemServices(CCSPlayer_ItemServices itemServices)
		{
			var pawn = itemServices.Pawn.Value;
			if (!pawn.IsValid || !pawn.Controller.IsValid || pawn.Controller.Value == null) return null;
			var player = new CCSPlayerController(pawn.Controller.Value.Handle);
			return !Utility.IsPlayerValid(player) ? null : player;
		}

		private static bool HasChangedKnife(CCSPlayerController player, out string? knifeValue)
		{
			knifeValue = null;

			// Check if player has knife info for their slot and team
			if (!GPlayersKnife.TryGetValue(player.Slot, out var knife) ||
			    !knife.TryGetValue(player.Team, out var value) ||
			    value == "weapon_knife") return false;
			knifeValue = value; // Assign the knife value to the out parameter
			return true;
		}
		
		private static bool HasChangedPaint(CCSPlayerController player, int weaponDefIndex, out WeaponInfo? weaponInfo)
		{
			weaponInfo = null;

			// Check if player has weapons info for their slot and team
			if (!GPlayerWeaponsInfo.TryGetValue(player.Slot, out var teamInfo) || 
			    !teamInfo.TryGetValue(player.Team, out var teamWeapons))
			{
				return false;
			}

			// Check if the specified weapon has a paint/skin change
			if (!teamWeapons.TryGetValue(weaponDefIndex, out var value) || value.Paint <= 0) return false;
			
			weaponInfo = value; // Assign the out variable when it exists
			return true;
		}

		private static float ViewAsFloat(uint value)
		{
			return BitConverter.Int32BitsToSingle((int)value);
		}
	}
}
