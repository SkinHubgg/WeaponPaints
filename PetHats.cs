using System.Collections.Concurrent;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace WeaponPaints;

/*
 * *** PET HATS (!pethat) - THE PHOTO BOOTH HATS ON THE IN-WORLD PET. UNTESTED ON A SERVER. ***
 *
 * The game has no hat on a pet in a match: the ten "silly hats" exist only in the pet photo booth, where the client
 * attaches the hat model to the pet (popup_pet_photobooth.js _ApplyAttachment: AttachModelToItemWithScale(model, pet,
 * attachPoint, headwearScale)). The plugin does the server-side equivalent and every client draws it:
 *
 *   - a prop_dynamic with the hat's model (models/photobooth/silly_hats/*.vmdl, all ten in pak01, precached with the
 *     pet models), named PetHatEntityName, not solid;
 *   - "SetParent" to the chicken, then "SetParentAttachment" to the chicken's head_attach - eyewear_attach for the
 *     two glasses, the booth's rule. That puts the hat's origin and orientation on the attachment with no offset. The
 *     hat models carry a one-bone skeleton rolled -90 that cancels the attachment's +90 roll, which is how the booth
 *     lines them up too;
 *   - its own (local) scale set to the booth's headwearScale for the stage: chick 1.0, pullet 0.43, hen 0.55. The
 *     pet's scale sits above it in the hierarchy (the server spawns a pet at stage scale x 1.4: 0.35 / 0.84 / 1.4), the
 *     same way the booth's number is relative to the pet it sits on. The pullet's smaller number makes up for the
 *     pullet shape preset's bigger head, which the CLIENT builds from the seed - so the hat only fits if the client
 *     applies the head bone's scale to the attached hat, as it does in the booth.
 *
 * The id is wp_player_pets.pet_hat, a column the SkinHub website also writes: the hat id exactly as CDN
 * data/petPhotobooth.json headwear.items[].name spells it (PetHats below is that list). NULL is no hat. An id the
 * plugin does not know draws no hat and is written back unchanged.
 *
 * Lifecycle: the hat belongs to one chicken entity. It is created with the pet (CreatePet), re-checked every second
 * by the pet timer (put back if it went missing, replaced if the pet, the hat or the stage changed), and killed with
 * the pet - by RemovePlayerPet, at once when the chicken is deleted by anyone else (OnEntityDeletedPets), and by the
 * stray sweep for anything named PetHatEntityName that nothing tracks. A hat that fails to appear three times on the
 * same pet is given up on for that pet, with one warning. Taking the hat off, or picking a hat in !pethat, starts the
 * count over, so only a hat that keeps going missing by itself is given up on.
 *
 * Not known until a server test: whether the attachment and the scale look like the booth (the plugin logs one
 * "Pet hat check" line with the hat's parent, attachment and scales a frame after the first hat), and whether the
 * client scales the hat with the pullet's head.
 */
public partial class WeaponPaints
{
	/// <summary>A photo booth hat: its pet_hat id, model and the chicken attachment it sits on.</summary>
	internal sealed record PetHat(string Id, string Model, string Attachment);

	/// <summary>
	/// data/petPhotobooth.json headwear.items (CS2 1.41.8.5), in the booth's order - the menu order and the numbers
	/// !pethat takes. Changes only with a game update, like BuiltInPets.
	/// </summary>
	internal static readonly PetHat[] PetHats =
	[
		new("helmet", "models/photobooth/silly_hats/helmet.vmdl", "head_attach"),
		new("armor", "models/photobooth/silly_hats/armor_helmet.vmdl", "head_attach"),
		new("alien", "models/photobooth/silly_hats/alien.vmdl", "head_attach"),
		new("banana", "models/photobooth/silly_hats/banana.vmdl", "head_attach"),
		new("glasses", "models/photobooth/silly_hats/glasses.vmdl", "eyewear_attach"),
		new("nose_glasses", "models/photobooth/silly_hats/nose_glasses.vmdl", "eyewear_attach"),
		new("party", "models/photobooth/silly_hats/party.vmdl", "head_attach"),
		new("sprout", "models/photobooth/silly_hats/sprout.vmdl", "head_attach"),
		new("top_hat", "models/photobooth/silly_hats/top_hat.vmdl", "head_attach"),
		new("wizard_hat", "models/photobooth/silly_hats/wizard_hat.vmdl", "head_attach")
	];

	/// <summary>targetname of every hat prop this plugin spawns (see PetEntityName).</summary>
	internal const string PetHatEntityName = "weaponpaints_pet_hat";

	/// <summary>Hats created for one pet before the plugin stops trying on that pet.</summary>
	private const int PetHatMaxAttempts = 3;

	/// <summary>The booth's growth[].headwearScale: chick 1.0, pullet 0.43, hen 0.55 (an unknown level uses the hen's).</summary>
	private static float PetHatScale(int stage) => stage switch
	{
		PetStageChick => 1.0f,
		PetStagePullet => 0.43f,
		_ => 0.55f
	};

	/// <summary>A hat this plugin spawned: its prop, the chicken it sits on, which hat and for which stage.</summary>
	private sealed record ActivePetHat(uint Handle, uint PetHandle, string HatId, int Stage);

	private readonly ConcurrentDictionary<int, ActivePetHat> _petHats = new();

	/// <summary>Per slot: the pet and hat the last creates were for, and how many there were.</summary>
	private readonly ConcurrentDictionary<int, (uint PetHandle, string HatId, int Attempts)> _petHatAttempts = new();

	private static int _petHatCheckLogged;

	/// <summary>The hat for a stored pet_hat id, or null for none and for an id this plugin does not know.</summary>
	internal static PetHat? FindPetHat(string? id) =>
		string.IsNullOrWhiteSpace(id)
			? null
			: PetHats.FirstOrDefault(hat => string.Equals(hat.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

	private string PetHatLabel(PetHat hat) => Localizer[$"wp_pet_hat_{hat.Id}"];

	#region The hat entity

	/// <summary>
	/// Makes the slot's hat match its stored pet_hat on this chicken: keeps a live hat that is already right, and
	/// otherwise removes it and, when there should be one, creates it. Game thread only.
	/// </summary>
	private void SyncPetHat(int slot, CChicken chicken, PetLook look)
	{
		var wanted = Config.Additional.PetHatsEnabled && GPlayersPet.TryGetValue(slot, out var info)
			? FindPetHat(info.Hat)
			: null;
		var petHandle = chicken.EntityHandle.Raw;

		if (_petHats.TryGetValue(slot, out var current))
		{
			if (wanted != null && current.PetHandle == petHandle && current.HatId == wanted.Id &&
			    current.Stage == look.Stage && ResolvePetHat(current) != null)
				return;

			RemovePetHat(slot);
		}

		// No hat wanted (taken off, or hats switched off): the tries below only count a hat that keeps going missing
		// on its own, so the next hat put on starts them over.
		if (wanted == null)
		{
			_petHatAttempts.TryRemove(slot, out _);
			return;
		}

		// A hat that keeps failing or disappearing (a model this server cannot load, say) is not recreated every
		// second: a few tries per pet and hat, then one warning.
		var attempts = _petHatAttempts.TryGetValue(slot, out var last) && last.PetHandle == petHandle &&
		               last.HatId == wanted.Id
			? last.Attempts
			: 0;

		if (attempts >= PetHatMaxAttempts)
		{
			if (attempts == PetHatMaxAttempts)
			{
				_petHatAttempts[slot] = (petHandle, wanted.Id, attempts + 1);
				Logger.LogWarning("The {Hat} pet hat did not stay on the pet in slot {Slot} after {Attempts} tries - " +
				                  "not trying again on this pet", wanted.Id, slot, attempts);
			}

			return;
		}

		_petHatAttempts[slot] = (petHandle, wanted.Id, attempts + 1);
		CreatePetHat(slot, chicken, wanted, look.Stage);
	}

	private bool CreatePetHat(int slot, CChicken chicken, PetHat hat, int stage)
	{
		var prop = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic");
		if (prop == null || !prop.IsValid)
		{
			Logger.LogWarning("Could not create a prop_dynamic for the {Hat} pet hat", hat.Id);
			return false;
		}

		_spawnedPetEntities[prop.EntityHandle.Raw] = 0;

		try
		{
			var keyValues = new CEntityKeyValues();
			keyValues.SetString("targetname", PetHatEntityName);
			keyValues.SetString("model", hat.Model);
			// Not solid: bullets, grenades and players go through the hat as if it were not there.
			keyValues.SetInt("solid", 0);
			if (chicken.AbsOrigin is { } origin) keyValues.SetVector("origin", origin.X, origin.Y, origin.Z);
			prop.CreateNonSolid = true;
			prop.DispatchSpawn(keyValues);

			// The model keyvalue should have set it; SetModel when it did not.
			var model = prop.CBodyComponent?.SceneNode?.GetSkeletonInstance()?.ModelState.ModelName;
			if (!string.Equals(model, hat.Model, StringComparison.OrdinalIgnoreCase)) prop.SetModel(hat.Model);

			// Parent first, then the attachment: SetParentAttachment puts the hat's origin and orientation on the
			// attachment itself (no offset kept), like the booth's attach.
			prop.AcceptInput("SetParent", chicken, prop, "!activator");
			prop.AcceptInput("SetParentAttachment", chicken, prop, hat.Attachment);

			// The hat's own scale; the pet's sits above it (see the header).
			if (prop.CBodyComponent?.SceneNode is { } node)
			{
				node.Scale = PetHatScale(stage);
				Utilities.SetStateChanged(prop, "CBaseEntity", "m_CBodyComponent");
			}

			var active = new ActivePetHat(prop.EntityHandle.Raw, chicken.EntityHandle.Raw, hat.Id, stage);
			_petHats[slot] = active;

			PetDebug("Hat {Hat} 0x{Handle:x8} put on pet 0x{Pet:x8} at {Attachment}, scale {Scale}", hat.Id,
				active.Handle, active.PetHandle, hat.Attachment, PetHatScale(stage));
			LogPetHatCheckOnce(active, hat);
			return true;
		}
		catch (Exception ex)
		{
			Logger.LogWarning("Could not put the {Hat} hat on a pet: {Reason}", hat.Id, ex.Message);
			KillPetEntity(prop);
			return false;
		}
	}

	/// <summary>
	/// Diagnostic, for the first server test, once per plugin load: a frame after the first hat, whether it is still
	/// there, what it is parented to and on which attachment, and the scales the server computed.
	/// </summary>
	private void LogPetHatCheckOnce(ActivePetHat active, PetHat hat)
	{
		if (Interlocked.Exchange(ref _petHatCheckLogged, 1) != 0) return;

		Server.NextFrame(() =>
		{
			try
			{
				var prop = ResolvePetHat(active);
				var chicken = new CHandle<CChicken>(active.PetHandle).Value;
				if (prop == null)
				{
					Logger.LogInformation("Pet hat check: the {Hat} hat was gone a frame after it was created", hat.Id);
					return;
				}

				// Parent 0 or an attachment index of -1 means the SetParent / SetParentAttachment inputs did not take.
				var node = prop.CBodyComponent?.SceneNode;
				Logger.LogInformation(
					"Pet hat check: {Hat} ({Model}) parent 0x{Parent:x8} (pet 0x{Pet:x8}), {Attachment} = attachment " +
					"index {Index} token 0x{Token:x8}, scale {Scale} x pet {PetScale} = {AbsScale}",
					hat.Id, node?.GetSkeletonInstance()?.ModelState.ModelName,
					node?.PParent?.Owner?.EntityHandle.Raw ?? 0, active.PetHandle, hat.Attachment,
					node?.ParentAttachmentOrBone, node?.HierarchyAttachName.Value, node?.Scale,
					chicken?.CBodyComponent?.SceneNode?.AbsScale, node?.AbsScale);
			}
			catch (Exception ex)
			{
				Logger.LogInformation("Pet hat check skipped: {Reason}", ex.Message);
			}
		});
	}

	private CDynamicProp? ResolvePetHat(ActivePetHat hat)
	{
		var prop = new CHandle<CDynamicProp>(hat.Handle).Value;
		if (prop == null || !prop.IsValid || !prop.DesignerName.StartsWith("prop_dynamic", StringComparison.Ordinal))
			return null;

		return IsMarkedForDelete(prop) ? null : prop;
	}

	/// <summary>Stops tracking the slot's hat and kills it.</summary>
	private void RemovePetHat(int slot)
	{
		if (!_petHats.TryRemove(slot, out var hat)) return;

		var prop = ResolvePetHat(hat);
		if (prop == null) return;

		PetDebug("Hat {Hat} 0x{Handle:x8} removed from pet 0x{Pet:x8}", hat.HatId, hat.Handle, hat.PetHandle);
		KillPetEntity(prop);
	}

	#endregion

	#region !pethat

	private void SetupPetHatCommands()
	{
		if (!Config.Additional.PetHatsEnabled) return;

		Config.Additional.CommandPetHat.ForEach(c =>
		{
			AddCommand($"css_{c}", "Pet hat menu", (player, info) =>
			{
				// Closed between round_end and round_start, like every other WeaponPaints command.
				if (!Utility.IsPlayerValid(player) || player == null || !_gBCommandsAllowed) return;
				OnCommandPetHat(player, info);
			});
		});
	}

	/// <summary>
	/// !pethat opens the menu; !pethat &lt;name|1-10|none&gt; picks straight away. Names are the pet_hat ids
	/// (top_hat, nose_glasses...; spaces and dashes are read as underscores), numbers the menu order.
	/// </summary>
	private void OnCommandPetHat(CCSPlayerController player, CommandInfo command)
	{
		if (!PlayerMayUsePets(player))
		{
			PrintPet(player, "wp_pet_no_permission");
			return;
		}

		if (!GPlayersPet.ContainsKey(player.Slot))
		{
			PrintPet(player, "wp_pet_none");
			return;
		}

		// The !pet cooldown: a pick writes the database and replaces a prop.
		if (CommandsCooldown.TryGetValue(player.Slot, out var cooldownEndTime) && DateTime.UtcNow < cooldownEndTime)
		{
			PrintPet(player, "wp_command_cooldown");
			return;
		}

		CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(Config.CmdRefreshCooldownSeconds);

		var argument = command.ArgString.Trim().Trim('"').Trim();
		if (argument.Length == 0)
		{
			OpenPetHatMenu(player);
			return;
		}

		if (!TryParsePetHat(argument, out var hat))
		{
			PrintPet(player, "wp_pet_hat_usage");
			return;
		}

		SetPetHat(player, hat);
	}

	private static bool TryParsePetHat(string text, out PetHat? hat)
	{
		hat = null;
		var key = text.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

		if (key is "none" or "off" or "remove" or "0") return true;

		if (int.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
			    out var number))
		{
			if (number < 1 || number > PetHats.Length) return false;
			hat = PetHats[number - 1];
			return true;
		}

		hat = FindPetHat(key);
		return hat != null;
	}

	private void OpenPetHatMenu(CCSPlayerController player)
	{
		var menu = Utility.CreateMenu(Localizer["wp_pet_hat_menu_title"]);
		if (menu == null) return;
		menu.PostSelectAction = PostSelectAction.Close;

		menu.AddMenuOption(Localizer["None"], (p, _) => SetPetHat(p, null));

		foreach (var hat in PetHats)
		{
			var chosen = hat;
			menu.AddMenuOption(PetHatLabel(hat), (p, _) => SetPetHat(p, chosen));
		}

		menu.Open(player);
	}

	/// <summary>Picks a hat (or none), saves it and puts it on the pet that is out now, if there is one.</summary>
	private void SetPetHat(CCSPlayerController player, PetHat? hat)
	{
		if (!Utility.IsPlayerValid(player)) return;

		if (!GPlayersPet.TryGetValue(player.Slot, out var pet))
		{
			PrintPet(player, "wp_pet_none");
			return;
		}

		pet.Hat = hat?.Id;

		// A pick by the player gets fresh tries, also for the same hat again after the plugin gave up on it.
		_petHatAttempts.TryRemove(player.Slot, out _);

		if (hat == null) PrintPet(player, "wp_pet_hat_removed");
		else PrintPet(player, "wp_pet_hat_set", PetHatLabel(hat));

		if (!PetHatColumnReady)
			Logger.LogWarning("wp_player_pets has no pet_hat column (yet) - {Player}'s hat is shown but not saved",
				player.PlayerName);

		SavePet(player, pet);

		// Only the hat changes, so the chicken stays; dead owner or not, the pet that is out gets it now.
		if (_activePets.TryGetValue(player.Slot, out var active) && ResolvePet(active) is { } chicken)
			SyncPetHat(player.Slot, chicken, active.Look);
	}

	#endregion
}
