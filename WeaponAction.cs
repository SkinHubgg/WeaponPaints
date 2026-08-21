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

			if (_config.Additional.GiveRandomSkin &&
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

			if (weaponInfo.KeyChain != null) SetKeychain(player, weapon);
			if (weaponInfo.Stickers.Count > 0) SetStickers(player, weapon);

			// See the note above: SkinPaint is null-safe, a direct ToObject<int>() on paint_index is not.
			skinInfo = SkinsList
				.Where(w =>
					ItemData.SkinDefindex(w) == weaponDefIndex &&
					ItemData.SkinPaint(w) == fallbackPaintKit)
				.ToList();

			isLegacyModel = skinInfo.Count <= 0 || ItemData.SkinIsLegacyModel(skinInfo[0]);
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

		private void SetStickers(CCSPlayerController? player, CBasePlayerWeapon weapon)
		{
			if (player == null || !player.IsValid) return;

			int weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;

			if (!HasChangedPaint(player ,weaponDefIndex, out var weaponInfo) || weaponInfo == null)
				return;

			foreach (var sticker in weaponInfo.Stickers)
			{
				// The COLUMN this sticker came from, not its position in the list. See StickerInfo.Slot
				// for what `Stickers.IndexOf(sticker)` used to do to a player with a gap in their slots.
				int stickerSlot = sticker.Slot;

				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
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
				 * *** THE COLUMN WINS WHEN IT NAMES AN ANCHOR, AND THAT IS WHAT THE FIFTH SLOT NEEDS. ***
				 *
				 * A slot's home is the weapon MODEL's, and a weapon does not have one per slot. Read out of the
				 * live game files rather than inferred:
				 *
				 *   - `weapon_rif_ak47.vmdl_c` authors FOUR `StickerMarkup` homes, `Index` 0..3, on both
				 *     `body_hd` and `body_legacy`. There is no home 4.
				 *   - `weapon_rif_ak47.vmat_c` bakes `g_vSticker4Offset [0 0]` and `g_vSticker4Scale [0 0]`, and a
				 *     zero scale is the pixel shader's SKIP, not a size: `csgo_weapon_vulkan_50_ps.static512`
				 *     line 647 breaks out of the slot when `g_vStickerNScale.x` or `.y` is 0.
				 *   - so slot 4 anchored to home 4 points at a home the AK has not got, and the fifth sticker
				 *     draws nothing. That is the reported disappearance, and it is not a plugin bug.
				 *
				 * 11 of the 35 sticker-capable weapons are in that shape; the other 24 author a fifth home on at
				 * least one mesh variant, which is why a fifth sticker appears on some guns and not on the AK.
				 *
				 * So the fifth has to hang off a home the weapon DOES author, with its offsets as deltas from
				 * that home. Only the website knows the markup table, so the anchor travels in the column that
				 * has always been there for it: `schema` is now READ, and 0 means "not named, use the slot's own
				 * index". That keeps today's behaviour EXACTLY for all 300,780 existing rows, and limits the
				 * blast radius to rows something deliberately writes an anchor into.
				 *
				 * The cost of 0 meaning "unset" is that the column cannot name home 0. It does not need to: of
				 * the 29 weapon+mesh variants with no fifth home, 28 have a home at index 1..3 whose authored
				 * SIZE is the one the fifth should be drawn at, so the anchor is picked from those. Only the
				 * Galil's legacy mesh has to borrow a home of a different size.
				 *
				 * `ViewAsFloat` because `sticker slot N schema` is `stored_as_integer 1`, the same as
				 * `sticker slot N id`. UNVERIFIED for this attribute specifically: the only value ever written
				 * to it was 0, where the bit pattern and the plain float agree. If a named anchor does nothing in
				 * game, a plain `(float)stickerAnchor` is the next thing to try.
				 */
				uint stickerAnchor = sticker.Schema != 0 ? sticker.Schema : (uint)stickerSlot;

				if (sticker.OffsetX != 0 || sticker.OffsetY != 0 || sticker.Schema != 0)
					CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
						$"sticker slot {stickerSlot} schema", ViewAsFloat(stickerAnchor));

				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
					$"sticker slot {stickerSlot} offset x", sticker.OffsetX);
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
					$"sticker slot {stickerSlot} offset y", sticker.OffsetY);
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
					$"sticker slot {stickerSlot} wear", sticker.Wear);
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
					$"sticker slot {stickerSlot} scale", sticker.Scale);
				CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
					$"sticker slot {stickerSlot} rotation", sticker.Rotation);
			}

			if (_temporaryPlayerWeaponWear.TryGetValue(player.Slot, out var playerWear) &&
				playerWear.TryGetValue(weaponDefIndex, out float storedWear))
			{
				weapon.FallbackWear = storedWear;
			}
		}

		private void SetKeychain(CCSPlayerController? player, CBasePlayerWeapon weapon)
		{
			if (player == null || !player.IsValid) return;

			int weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;

			if (!HasChangedPaint(player, weaponDefIndex, out var value) || value?.KeyChain == null)
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
			UpdateWeaponMeshGroupMask(weapon, isLegacy);
		}

		private static void GivePlayerAgent(CCSPlayerController player)
		{
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
