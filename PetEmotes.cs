using System.Collections.Concurrent;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;
using Microsoft.Extensions.Logging;

namespace WeaponPaints;

/*
 * *** PET EMOTES (!petemote) - WHAT THE IN-WORLD CHICKEN CAN REALLY PLAY FROM A PLUGIN. UNTESTED ON A SERVER. ***
 *
 * The pet's animation is the server's own AnimGraph2 (chicken.vnmgraph), and every client sees exactly what the
 * server's graph plays, clip sounds included. The graph is driven by the chicken AI's activity, EChickenActivity:
 * Idle 0, Squat 1, Walk 2, Run 3, Glide 4, Land 5, Panic 6, Trick 7, TurnInPlace 8, Feed 9, Sleep 10, Shoulder 11,
 * LowOnFood 12. Which clip plays inside a category (which trick, which squat loop) is the graph parameter
 * action_variation, and the server always rolls it itself (RandomInt(0, 2^24), taken modulo the option count at
 * server 0x1812b0090). It is not a schema field and no entity input sets it. So without native signatures a plugin
 * picks the CATEGORY and the game picks the variant: the menu shows the numbered variants the game picks from, and a
 * number typed after the category gets told so rather than ignored silently. An exact variant would need the native
 * CCS2ChickenGraphController::SetAction, of which only a Windows signature is known (no Linux one), so it is not used.
 *
 * How: CChicken.m_desiredActivity (schema, not networked; CounterStrikeSharp 1.0.367 binds it as DesiredActivity, and
 * its enum has no names for 11 and 12) is written on every server tick until m_currentActivity shows the emote, then
 * left alone and the AI carries on by itself afterwards. The AI takes a desired activity at its next ACTION_COMPLETED
 * (the end of an idle, squat or trick clip, 0.2 s early) or at once while walking or running, so an emote starts
 * within a few seconds; about 13 s at worst after a squat or a sleep, 25 s after a feed. The AI writes the field too
 * (stay mode re-queues Squat, Trick queues Idle, Panic queues Run), hence writing it every tick. After
 * PetEmoteStartSeconds it is given up. An emote the pet is already playing when asked is not a start: at a clip end
 * the AI takes the desired activity even when it equals the current one (server 0x18033ecef..0x18033ed83), so writing
 * it then would only repeat it, and the current activity would never show the change. Such a request waits unwritten
 * until the pet does something else, is written from then on, and counts as started when the pet comes back to it.
 *
 * The categories (emotes spec, in-world section: the levers that need no signature), with the variants the game
 * rolls from in a match:
 *   trick    7   chick_trick01..10; the in-game list has an 11th, empty slot, so 1 roll in 11 shows nothing
 *   squat    1   squat_start, one loop (loop01, loop01 again, loop03 or loop04), squat_stand
 *   sleep    10  squat_start, sleep_loop01 (11.2 s, once), squat_stand - the server never plays it by itself
 *   feed     9   chick: chickbaby_feed02, pullet and hen: chick_feed01 - about 24 s of pecking; never played by the server
 *   shoulder 11  chick_shoulder_01/02/03 (odds 2:2:1), the loadout perch pose, played on the ground; never by the server
 *   panic    6   chick_react01/02; the AI follows a panic with a short run, like after being shot
 *   idle     0   chick_idle01..03
 * Left out: Hungry (LowOnFood 12) - its intro ends in a loop that never completes, so the AI would never take the next
 * activity and the pet would stop following until it respawns. Walk, Run, Glide and Land come from moving; Turn needs
 * turn_angle, a graph parameter only the AI sets. Photo poses, the growth reveal, retirement and the held view are
 * client-only actions with no server path.
 *
 * Stay mode: on classic game types (casual, competitive, wingman) the game's chicken manager puts every chicken with an
 * owner into "stay" 5 s after round_freeze_end (a flag at CChicken+0x32e1, not in the schema): no walking, squats on
 * repeat, and the follow code drops the leader, which PetThink re-asserts every second. A new chicken entity is the
 * only way out, which is one reason every owner spawn gets a new pet. Whether the AI takes Sleep, Feed and Shoulder
 * while in stay mode is not known: the native SetDesiredActivity refuses them there, the plain field write may not.
 */
public partial class WeaponPaints
{
	/// <summary>An emote: its id (chat argument and lang key), EChickenActivity number, and the variants shown.</summary>
	private sealed record PetEmote(string Id, int Activity, string Variants);

	/// <summary>Menu order. Variants are the numbered clips the game rolls from in a match (see the header).</summary>
	private static readonly PetEmote[] PetEmotes =
	[
		new("trick", 7, "1-10"),
		new("squat", 1, "1, 3, 4"),
		new("sleep", 10, ""),
		new("feed", 9, ""),
		new("shoulder", 11, "1-3"),
		new("panic", 6, "1-2"),
		new("idle", 0, "1-3")
	];

	/// <summary>How long an emote may wait for the AI to take it before it is dropped.</summary>
	private const float PetEmoteStartSeconds = 30f;

	/// <summary>
	/// An emote being written into a pet until it starts: the pet it is for, which one, and when it was asked for.
	/// WaitsForChange: the pet was already in that activity when asked and has not left it since, so it is not written
	/// yet (see PetEmoteTick).
	/// </summary>
	private sealed record PendingPetEmote(uint PetHandle, PetEmote Emote, float QueuedAt, bool WaitsForChange);

	private readonly ConcurrentDictionary<int, PendingPetEmote> _petEmotes = new();
	private readonly ConcurrentDictionary<int, DateTime> _petEmoteCooldowns = new();

	private void RegisterPetEmotes()
	{
		if (!Config.Additional.PetEmotesEnabled) return;

		RegisterListener<Listeners.OnTick>(PetEmoteTick);
	}

	private string PetEmoteLabel(PetEmote emote) => Localizer[$"wp_pet_emote_{emote.Id}"];

	private string PetEmoteOption(PetEmote emote) =>
		emote.Variants.Length == 0 ? PetEmoteLabel(emote) : $"{PetEmoteLabel(emote)} ({emote.Variants})";

	/// <summary>Drops a slot's pending emote, and its cooldown unless <paramref name="keepCooldown"/>.</summary>
	private void ForgetPetEmotes(int slot, bool keepCooldown = false)
	{
		_petEmotes.TryRemove(slot, out _);
		if (!keepCooldown) _petEmoteCooldowns.TryRemove(slot, out _);
	}

	private void ClearPetEmotes()
	{
		_petEmotes.Clear();
		_petEmoteCooldowns.Clear();
	}

	/// <summary>
	/// Every server tick while an emote is pending: writes it into the pet's desired activity until the pet's current
	/// activity is the emote, then lets go. An emote the pet was already in when asked is only written once the pet has
	/// left that activity. Nothing to do (one dictionary check) the rest of the time.
	/// </summary>
	private void PetEmoteTick()
	{
		if (_petEmotes.IsEmpty) return;

		foreach (var (slot, pending) in _petEmotes)
		{
			try
			{
				if (!_activePets.TryGetValue(slot, out var active) || active.Handle != pending.PetHandle ||
				    ResolvePet(active) is not { } chicken)
				{
					_petEmotes.TryRemove(slot, out _);
					PetDebug("Emote {Emote} for slot {Slot} dropped - the pet it was for is gone", pending.Emote.Id, slot);
					continue;
				}

				var activity = (ChickenActivity)pending.Emote.Activity;
				var waited = Server.CurrentTime - pending.QueuedAt;
				var current = chicken.CurrentActivity;

				if (pending.WaitsForChange)
				{
					// Asked for what the pet was already doing, which is not a start. Not written while the pet stays in
					// it (the AI would take it again at every clip end and repeat it back to back); written from the
					// moment the pet does something else, and started when it comes back.
					if (current == activity)
					{
						if (waited > PetEmoteStartSeconds)
						{
							_petEmotes.TryRemove(slot, out _);
							PetDebug("Emote {Emote} on pet 0x{Pet:x8} dropped after {Seconds:0} s - the pet was already " +
							         "in that activity when asked and never left it, so no new start was seen",
								pending.Emote.Id, pending.PetHandle, waited);
						}

						continue;
					}

					_petEmotes.TryUpdate(slot, pending with { WaitsForChange = false }, pending);
					PetDebug("Emote {Emote}: pet 0x{Pet:x8} left that activity for {Current} after {Seconds:0.0} s - " +
					         "asking for it again now", pending.Emote.Id, pending.PetHandle, (int)current, waited);
				}
				else if (current == activity)
				{
					_petEmotes.TryRemove(slot, out _);
					PetDebug("Emote {Emote} started on pet 0x{Pet:x8} after {Seconds:0.0} s", pending.Emote.Id,
						pending.PetHandle, waited);
					continue;
				}

				if (waited > PetEmoteStartSeconds)
				{
					_petEmotes.TryRemove(slot, out _);
					PetDebug("Emote {Emote} on pet 0x{Pet:x8} not taken after {Seconds:0} s (current activity {Current}, " +
					         "desired {Desired}) - dropped", pending.Emote.Id, pending.PetHandle, waited,
						(int)chicken.CurrentActivity, (int)chicken.DesiredActivity);
					continue;
				}

				chicken.DesiredActivity = activity;
			}
			catch (Exception ex)
			{
				_petEmotes.TryRemove(slot, out _);
				Logger.LogWarning("Pet emote failed for slot {Slot}: {Reason}", slot, ex.Message);
			}
		}
	}

	#region !petemote

	private void SetupPetEmoteCommands()
	{
		if (!Config.Additional.PetEmotesEnabled) return;

		Config.Additional.CommandPetEmote.ForEach(c =>
		{
			AddCommand($"css_{c}", "Pet emote menu", (player, info) =>
			{
				// Closed between round_end and round_start, like every other WeaponPaints command.
				if (!Utility.IsPlayerValid(player) || player == null || !_gBCommandsAllowed) return;
				OnCommandPetEmote(player, info);
			});
		});
	}

	/// <summary>
	/// !petemote opens the menu, !petemote &lt;trick|squat|sleep|feed|shoulder|panic|idle&gt; plays one, and
	/// !petemote help lists them with their variants.
	/// </summary>
	private void OnCommandPetEmote(CCSPlayerController player, CommandInfo command)
	{
		if (!PlayerMayUsePets(player))
		{
			PrintPet(player, "wp_pet_no_permission");
			return;
		}

		var words = command.ArgString.Trim().Trim('"').ToLowerInvariant()
			.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

		if (words.Length == 0)
		{
			OpenPetEmoteMenu(player);
			return;
		}

		if (PetEmotes.FirstOrDefault(emote => emote.Id == words[0]) is not { } chosen)
		{
			PrintPet(player, "wp_pet_emote_usage");
			return;
		}

		// "!petemote trick 3": the game rolls the variant, and the player is told so rather than left guessing.
		if (words.Length > 1) PrintPet(player, "wp_pet_emote_random");

		PlayPetEmote(player, chosen);
	}

	private void OpenPetEmoteMenu(CCSPlayerController player)
	{
		var menu = Utility.CreateMenu(Localizer["wp_pet_emote_menu_title"]);
		if (menu == null) return;
		menu.PostSelectAction = PostSelectAction.Close;

		foreach (var emote in PetEmotes)
		{
			var chosen = emote;
			menu.AddMenuOption(PetEmoteOption(emote), (p, _) =>
			{
				if (Utility.IsPlayerValid(p)) PlayPetEmote(p, chosen);
			});
		}

		PrintPet(player, "wp_pet_emote_hint");
		menu.Open(player);
	}

	/// <summary>Queues an emote on the player's pet, with the per-player cooldown.</summary>
	private void PlayPetEmote(CCSPlayerController player, PetEmote emote)
	{
		var slot = player.Slot;

		if (!_activePets.TryGetValue(slot, out var active) || ResolvePet(active) is not { } chicken)
		{
			PrintPet(player, "wp_pet_emote_no_pet");
			return;
		}

		if (_petEmoteCooldowns.TryGetValue(slot, out var readyAt) && DateTime.UtcNow < readyAt)
		{
			PrintPet(player, "wp_pet_emote_cooldown",
				Math.Ceiling((readyAt - DateTime.UtcNow).TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
			return;
		}

		_petEmoteCooldowns[slot] = DateTime.UtcNow.AddSeconds(Math.Max(0, Config.Additional.PetEmoteCooldownSeconds));
		// Written at once, unless the pet is doing that very activity now: then the clip it is playing and the AI's
		// own next move go first (see PetEmoteTick).
		var target = (ChickenActivity)emote.Activity;
		var alreadyIn = chicken.CurrentActivity == target;
		_petEmotes[slot] = new PendingPetEmote(active.Handle, emote, Server.CurrentTime, alreadyIn);
		if (!alreadyIn) chicken.DesiredActivity = target;

		PrintPet(player, "wp_pet_emote_queued", PetEmoteLabel(emote));
		PetDebug("{Owner} asked pet 0x{Pet:x8} for {Emote} (activity {Activity}; now {Current}){Already}",
			PetOwnerLabel(player, slot), active.Handle, emote.Id, emote.Activity, (int)chicken.CurrentActivity,
			alreadyIn ? " - already in that activity, so it waits until the pet has done something else" : "");
	}

	#endregion
}
