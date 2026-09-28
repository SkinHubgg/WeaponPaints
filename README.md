# CS2 Weapon Paints

## Description
Unfinished, unoptimized and not fully functional ugly demo weapon paints plugin for **[CSSharp](https://docs.cssharp.dev/docs/guides/getting-started.html)**. 

## Created [Discord server](https://discord.gg/d9CvaYPSFe) where you can discuss about plugin.

### Consider to donate instead of buying from unknown sources.
[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/E1E2G0P2O) or [![Donate on Steam](https://github.com/Nereziel/cs2-WeaponPaints/assets/32937653/a0d53822-4ca7-4caf-83b4-e1a9b5f8c94e)](https://steamcommunity.com/tradeoffer/new/?partner=41515647&token=gW2W-nXE)

## Features
- Changes only paint, seed and wear on weapons, knives, gloves and agents
- MySQL based
- Data syncs on player connect
- Added command **`!wp`** to refresh skins ***(with cooldown in seconds can be configured)***
- Added command **`!ws`** to show website
- Added command **`!knife`** to show menu with knives
- Added command **`!gloves`** to show menu with gloves
- Added command **`!agents`** to show menu with agents
- Added command **`!pins`** to show menu with pins
- Added command **`!music`** to show menu with music
- Added command **`!pet`** to show menu with chicken pets (CS2 1.41.8.2), see [Pets](#pets)
- Added command **`!pethat`** to put one of the ten photo booth hats on the pet, see [Pet hats](#pet-hats)
- Added command **`!petemote`** to make the pet do a trick, squat, sleep and more, see [Pet emotes](#pet-emotes)
- Stickers and charms on the **C4** (CS2 1.41.8.2), and on guns saved as "Default" (paint 0), see [C4 stickers](#c4-stickers)
- Translations support, submit a PR if you want to share your translation

## ⚙️ Requirements
**Ensure all the following dependencies are installed before proceeding**
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)
- [PlayerSettings](https://github.com/NickFox007/PlayerSettingsCS2) - Required by MenuManagerCS2
- [AnyBaseLibCS2](https://github.com/NickFox007/AnyBaseLibCS2) - Required by PlayerSettings
- [MenuManagerCS2](https://github.com/NickFox007/MenuManagerCS2)
- MySQL database

## CS2 Server
- Have working CounterStrikeSharp (**with RUNTIME!**)
- Download from Release and copy plugin to plugins
- Run server with plugin, **it will generate config if installed correctly!**
- Edit `addons/counterstrikesharp/configs/`**`plugins/WeaponPaints/WeaponPaints.json`** include database credentials
- In `addons/counterstrikesharp/configs/`**`core.json`** set **FollowCS2ServerGuidelines** to **`false`**
- Copy from plugins folder gamedata file **`weaponpaints.json`** to folder **`addons/counterstrikesharp/gamedata/`**

## Item data

Skins, gloves, agents, music kits and collectibles are **not shipped with the plugin**. They are fetched
once, when the plugin loads, from the address in `DataUrl` (default `https://cdn.skinhub.gg`), requested as
`<DataUrl>/data/<dataset>.json`.

- The fetch runs on a worker thread. The server never waits for it, and neither does a player connect.
  Anyone who joins in the second or two before it lands sees default items and is noted in the log.
- Requests are **conditional**. The `ETag` and `Last-Modified` of the cached copy are stored beside it and sent
  back as `If-None-Match` / `If-Modified-Since`, so a dataset that has not changed answers **304 with no body**:
  one request, nothing transferred, cache kept. Nothing is re-downloaded until it actually changes.
- The last response that parsed is cached in **`<plugin folder>/.cache/`**. Override it with `CacheDirectory`;
  a relative value there is resolved against the plugin folder, never the process working directory, so
  `"../.cache"` puts the cache *beside* the plugin folder instead of inside it — which is what to use when the
  plugin folder is an ephemeral or read-only mount, as it often is on containerised servers. If the directory
  cannot be created or written the plugin still runs from `DataUrl` and simply has no offline fallback, saying
  so **once** in the log.
- If `DataUrl` is unreachable or broken, the plugin serves that cache **and warns, every time, with its age** —
  so an outage cannot leave a server with no items, and you can see how old what you are serving is.
- A dataset that was served from cache is retried on a worker thread after 15s, 30s, 60s, 120s and 300s and
  then given up on, so a CDN that recovers within about nine minutes is picked up without restarting the
  server. After that, `css_plugins reload WeaponPaints` (or a server restart) retries.
- **If there is nothing to serve at all** — `DataUrl` unusable *and* no usable cache, which is the
  first-ever-start-with-no-network case — the plugin **refuses to run**. It logs why and asks
  CounterStrikeSharp to terminate it, rather than sitting there giving everyone default items. **The server is
  not affected**; only the plugin stops. Nothing is shipped in the plugin archive to fall back on, which is the
  trade for an archive that does not carry ~84 MB of JSON.
- `CacheDiskHours` (default `12`) is how long the copy on disk is used as-is, with **no request at all**. Past
  that window the conditional revalidation above runs as normal — the window only skips the check, it does not
  replace it. Set it to `0` to always revalidate.
- Because that window is on by default, **new items can be up to `CacheDiskHours` hours late after a CS2
  update**.
  One summary line per start says which path every dataset took, so that is diagnosable at a glance:

  ```
  Item data ready in 88 ms - unchanged, kept cache (304): skins, gloves | read from disk unchecked
  (CacheDiskHours): agents, music, collectibles
  ```

  The labels are `downloaded`, `unchanged, kept cache (304)`, `read from disk unchecked (CacheDiskHours)`,
  `STALE cache, DataUrl unreachable` and `MISSING`.
- `DataUrl` publishes English item names only. `SkinsLanguage` no longer does anything and the plugin warns
  at load if it is set to something other than `en`.
- `pets` (`<DataUrl>/data/pets.json`) is **optional**. A CDN that has not published it yet does not stop the
  plugin: it logs one line, uses its built-in list of the five `pet_definitions`, and asks again on the next load.

## C4 stickers

CS2 1.41.8.2 (22 Sep 2026) made the C4 sticker-capable. The C4 has no paint kits, so every C4 row is
`weapon_paint_id = 0`, and the plugin used to drop paint-0 rows before it applied stickers or a charm. Now a paint-0
row that carries a sticker, a charm or a name tag is applied on its own branch - for the C4 **and** for any gun
saved as "Default". A paint-0 row with nothing on it is still ignored.

- Row: `wp_player_skins(steamid, weapon_team 2 or 0, weapon_defindex 49, weapon_paint_id 0, weapon_sticker_0..4,
  weapon_keychain)`.
- All five slots are written on the C4. The model authors only four sticker homes, so the fifth borrows home 1
  and is shifted onto the side of the bomb, the same way the fifth sticker works on the four-home guns. A site
  writes slot 4 with 0 in the sticker's second field, like any other slot; the plugin does the shift.
- The C4 always uses its one hd mesh. It is not added to the `!skins` menu (there is nothing to paint).
- The bomb is handed out by the game with `GiveNamedItem`, which the plugin already hooks, so no extra setup.
- When the bomb is planted, the planted bomb gets the same stickers if the game did not carry them over itself.
  **Untested in game** - nobody has seen a stickered planted bomb yet.

## Pets

CS2 1.41.8.2 added chicken pets (item 4681). The plugin spawns its own `chicken` that follows the player, carrying
the pet id, stage, seed and name, and a `<player>'s <name>` label. **This has not been run on a server yet** - it is
built from the 1.41.8.2 schema and checked against the 1.41.8.4 and 1.41.8.5 game code.

How the look is set, the same way the game sets it for a real pet:

- **Colour** is the item's style, `pet_variant`: a material group index (Catalana 0-13, Silkie 0-9, Polish 0-12; 0 is
  the default colour, the chick has none). The server sets it, so the plugin sends it to the chicken on every spawn.
  No colour (`NULL`) or an index the model does not have means the default colour. The seed never picks the colour.
  Without this the game rolls a new random colour every time the chicken spawns. A pet picked in the `!pet` menu
  gets a colour rolled with the weights the game's model files carry for this; `!pet color` changes it. It is sent
  with the game's own `Skin` input, which does what the game's pet spawn does (read in the 1.41.8.5 server): it looks
  the index up in the chicken's current model and writes the networked colour field.
- **Seed** (`pet_seed`) drives the body shape of a pullet or hen and a small colour jitter (hue, brightness and so on).
  The client rolls both from the item. New seeds are picked from 1 to 2147483646, where every value is a different
  look. Seed 0 is refused because it switches the jitter off.
- **Stage** (`pet_stage`) is clamped to 0-3. The pet's size is set once when it spawns, so any change respawns it.

Things to watch when testing:

- whether the pet's shape and colour jitter match the website's viewer for the same seed (the viewer's roll now
  matches the game code bit for bit, and the chicken that follows you builds its look from the item once, when it
  spawns);
- the one `Pet colour check` line in the server log: `m_materialGroup` should be the group's token - default
  `0x75de364e`, 1 `0x257bb367`, 2 `0x505179cd`, 3 `0xda411775`, 4 `0xdf125af1`, 5 `0x98eacc60`, 6 `0xd5d9acb3`,
  7 `0x85ad0a57`, 8 `0xc6c35f5a`, 9 `0x936b824e`, 10 `0xb13d1d67`, 11 `0x9913e8e1`, 12 `0xb018be21`, 13 `0xdae98fdb`.
  `0` means the index was past the end of the model's groups;
- whether it keeps following (the leader is re-asserted every second) and whether the model is right per stage;
- eggs never leave the nest; a chick uses `models/chicken/chick.vmdl`, a pullet or hen its breed's model.

Lifecycle: every life of the owner gets its own pet. When the owner spawns, the pet is queued and tried a frame later,
a quarter second later and then every second until the owner is really alive (read from the player's pawn). The
pet from the previous life (still standing where the owner died, or already killed) is removed together with its hat,
and a new one spawns behind the owner and follows the new pawn. So the pet always comes back with its owner, also on
deathmatch and respawn servers, and there is never a second one. While the owner is dead the pet stays where it is.
A pet killed while its owner is alive comes back on their next spawn. It is removed on team change, disconnect, map
end and plugin unload. A player the game itself gave a pet (a chicken whose owner is them) gets no second one. At
every round start the game removes each chicken that has an owner but is not that owner's real pet, which includes the
plugin's; the plugin puts every living owner's pet back one frame later.

Before 3.4c the pet was tried once, a frame after the owner spawned. If the owner did not count as alive at that exact
moment, or an old pet the plugin had lost track of was still around (it then looked like a pet the game had given the
player), nothing tried again and the pet stayed away until `!pet` was used.

Every chicken and hat the plugin spawns is named `weaponpaints_pet` / `weaponpaints_pet_hat`. Every 5 seconds the
plugin removes any of them it no longer tracks, including ones left behind by a plugin reload.

Debug log: set `PetDebugLog` to `true` to log each step with a `[pet debug]` prefix: owner spawned (with the
pawn's and the controller's alive state), queued pet waits, pet spawned / kept / replaced (and why), owner died (and
where the pet was), pet gone (killed, deleted, removed by the game), stray removed, hat put on / removed, emote asked /
started / dropped. It is several lines per player per round, so switch it off again after testing.

Names: the game keeps one name per stage (chick, pullet, hen), but only the item's main custom name is sent to
players, so that is where the label's name comes from at every stage. The plugin has one name per player (the current
stage's) and writes it there, plus on that stage's own name field. The in-game rename box stops at 20 characters; the
plugin accepts up to 32 (the column width), so a longer name typed on a website is not cut again.

Commands (`CommandPet`, default `pet`):

| Command | What it does |
| --- | --- |
| `!pet` | Menu: pick a pet and its stage, or None. "New random look" rolls a new seed and colour |
| `!pet name <text>` | Name the pet (32 characters max, the column width; empty clears it) |
| `!pet seed [number]` | A new random seed (shape and colour jitter), or a specific one (not 0) |
| `!pet color <number\|random\|default>` | Pick a colour (material group index), roll one, or go back to the default |
| `!pet stage <chick\|pullet\|hen>` | Change the stage: pullet or hen for a breed. The Chick pet is always a chick; pick it from the `!pet` menu |
| `!pet off` | No pet |
| `!pethat` | Menu: pick a hat for the pet, or None |
| `!pethat <1-10\|name\|none>` | Pick a hat by menu number or name (`top_hat`, `nose_glasses`...; `top hat` works too) |
| `!petemote` | Menu: trick, squat, sleep, feed, shoulder, panic, idle |
| `!petemote <trick\|squat\|sleep\|feed\|shoulder\|panic\|idle>` | Play one; `!petemote help` lists them |

Config (`Additional`): `PetsEnabled` (default `true`), `CommandPet` (default `["pet"]`), `PetPermission` (default
`""` = everyone, e.g. `"@css/vip"` - also decides whether a player's pet is spawned at all),
`PetTeamIntroExperimental` (default `false`, see [Team intro](#team-intro-experimental)), `PetHatsEnabled` (default
`true`), `CommandPetHat` (default `["pethat"]`), `PetEmotesEnabled` (default `true`), `CommandPetEmote` (default
`["petemote"]`), `PetEmoteCooldownSeconds` (default `10`), `PetDebugLog` (default `false`). `!pethat` shares the
`CmdRefreshCooldownSeconds` cooldown with `!pet`; `!petemote` has its own.

Table (created automatically):

```sql
CREATE TABLE IF NOT EXISTS `wp_player_pets` (
  `steamid`     varchar(18)  NOT NULL PRIMARY KEY,  -- one pet per player, pets are "noteam"
  `pet_id`      int          NOT NULL,              -- 1 egg, 2 chick, 3 catalana, 4 silkie, 5 polish
  `pet_stage`   tinyint      NOT NULL DEFAULT 3,    -- 0 egg, 1 chick, 2 pullet, 3 hen
  `pet_variant` int          NULL,                  -- item style = material group index; NULL = the default colour
  `pet_seed`    int unsigned NOT NULL DEFAULT 0,    -- "pet seed" attribute
  `pet_name`    varchar(32)  NULL,                  -- name tag
  `pet_hat`     varchar(32)  NULL                   -- photo booth hat id, see Pet hats; NULL = no hat
);
```

`pet_hat` is new in 3.4c. On every load the plugin adds it to an existing `wp_player_pets` when it is missing (it
checks `information_schema` first, so running it again, or on several servers at once, is safe). The website writes
the same column. Until the column exists, pets still load and save, only the hat is not saved.

The pet and hat models are precached on map load, so a plugin loaded mid-map may show pets on the default chicken
model, and no hats, until the next map.

### Pet hats

`!pethat` puts one of the ten hats from the game's pet photo booth on the pet. In a match the game has no hats; the
plugin spawns the hat model and fixes it to the chicken's head, the way the booth does:

| # | `pet_hat` | Name | Model (`models/photobooth/silly_hats/`) | Sits on |
| --- | --- | --- | --- | --- |
| 1 | `helmet` | Helmet | `helmet.vmdl` | `head_attach` |
| 2 | `armor` | Armor Helmet | `armor_helmet.vmdl` | `head_attach` |
| 3 | `alien` | Alien | `alien.vmdl` | `head_attach` |
| 4 | `banana` | Banana | `banana.vmdl` | `head_attach` |
| 5 | `glasses` | Glasses | `glasses.vmdl` | `eyewear_attach` |
| 6 | `nose_glasses` | Nose Glasses | `nose_glasses.vmdl` | `eyewear_attach` |
| 7 | `party` | Party Hat | `party.vmdl` | `head_attach` |
| 8 | `sprout` | Sprout | `sprout.vmdl` | `head_attach` |
| 9 | `top_hat` | Top Hat | `top_hat.vmdl` | `head_attach` |
| 10 | `wizard_hat` | Wizard Hat | `wizard_hat.vmdl` | `head_attach` |

- `pet_hat` holds the id exactly as the CDN's `data/petPhotobooth.json` names it (`headwear.items[].name`); `NULL` is
  no hat. An id the plugin does not know shows no hat and is saved back unchanged.
- The hat's own scale is the booth's, per stage: chick 1.0, pullet 0.43, hen 0.55. The pet's size (the game spawns
  pets at 1.4 x their stage size) applies on top.
- The hat is not solid. Changing the hat swaps only the hat, the pet stays. The hat goes away with the pet and comes
  back with the next one, and it stays on through a change of pet, like the name. A hat that keeps disappearing by
  itself is tried 3 times per pet, then given up on with one warning; taking it off or picking a hat again
  starts that count over.
- Eggs never leave the nest, so a hat on an egg is saved but not shown.
- **Untested in game.** The first hat after each plugin load logs one `Pet hat check` line: the parent should be the
  pet, the attachment index should not be -1, and the scales show what the server applied.

### Pet emotes

`!petemote` makes the pet play one of the moves the game's own chicken can play in a match. The server runs the
pet's animation and every player sees it. The plugin can pick the move, but not which version of it: the game rolls
that itself every time, so the menu shows which numbered versions the game picks from.

| Emote | Game activity | What plays (the game picks one) |
| --- | --- | --- |
| Trick | 7 | tricks 1-10; 1 roll in 11 is empty and shows nothing |
| Squat | 1 | sits down, one of squat loops 1, 3, 4, stands up |
| Sleep | 10 | sits down, sleeps for 11 s, stands up |
| Feed | 9 | about 24 s of pecking |
| Shoulder | 11 | one of the 3 shoulder perch poses, on the ground |
| Panic | 6 | one of 2 panic reactions, then a short run |
| Idle | 0 | one of 3 idles |

- The pet starts the emote when it finishes what it is doing now: at once while it walks or runs, usually within a
  few seconds, up to about 13 s after a squat or a sleep and 25 s after a feed. If it has not started after 30 s the
  request is dropped.
- Asking for the move the pet is doing right now (Idle while it stands, Squat while it squats in stay mode, Trick
  during a trick) queues it behind the current one: it plays once the pet has done something else in between. A pet
  that keeps doing that same move never gets there, and the request is dropped after 30 s.
- `PetEmoteCooldownSeconds` (default 10) is per player.
- Not offered: Hungry, which ends in a loop the pet never leaves by itself (it would stop following). Walk, run,
  glide and land come from moving, and turning needs a value only the game sets. Photo poses, the growth reveal and
  the held (inspect) view only exist in the game's menus.
- On casual, competitive and wingman the game puts every owned pet into "stay" mode 5 s after the freeze time ends
  (it stops following and squats). The plugin still sets the owner as the pet's leader every second. A pet spawned
  after that point (a respawn mid-round) is a new chicken and follows again until the next round. Whether Sleep, Feed
  and Shoulder are played while the pet is in stay mode is not known yet.
- **Untested in game.** With `PetDebugLog` on, each emote logs when it started and after how long, or that it was
  dropped and what the pet was doing instead. A request for the move the pet was already doing says so on the
  `asked` line, logs when the pet left that move, and only then logs `started`.

### Team intro (experimental)

**Off by default, and untested in game.** With `PetTeamIntroExperimental: true` (and `PetsEnabled`), the plugin also
puts each player's pet into the team intro, the line-up the camera sweeps over before the first round. CS2 1.41.8.2
gave every intro spot a pet item next to the agent, gloves and weapon. When the intro starts, the plugin writes the
player's pet into that item one frame later, the same way it fills the chicken's item. It checks again half a second
later in case the game overwrote it.

- Only pullets and hens. The game deploys pets from the pullet stage, so chicks and eggs are left out.
- A pet the game put there itself (a player who really owns one) is left alone.
- `!pet color` does not reach the intro. The game colours the intro pet from the item's style, which a server cannot
  send, so it always has its default colour. Its shape and colour jitter still come from the seed.
- The intro only runs with `mp_team_intro_type` on (the default `auto` means: when `mp_halftime` is set), and only on
  maps that have the intro spots and cameras. The official maps do; most workshop maps do not.
- The end-of-match line-up is **not** covered. It reads the pet from the player's loadout, which a plugin cannot
  reach without faking the loadout slot.

Each intro logs one line with how many pets were written. If the game rewrites a pet item after the intro starts, that
is logged once. Not known yet: whether the client draws a pet from a plugin-written item at all, whether it needs a real
item id, and whether the server rewrites the item after the event. If nothing shows, turn the option off again.

## Plugin Configuration
<details>
  <summary>Click to expand</summary>
<code><pre>{
	"Version": 4, // Don't touch
	"DatabaseHost": "", // MySQL host
	"DatabasePort": 3306, // MySQL port
	"DatabaseUser": "", // MySQL username
	"DatabasePassword": "", // MySQL user password
	"DatabaseName": "", // MySQL database name
	"CmdRefreshCooldownSeconds": 60, // Cooldown time in refreshing skins (!wp command)
	"Prefix": "[WeaponPaints]", // Prefix every chat message
	"Website": "example.com/skins", // Website used in WebsiteMessageCommand (!ws command)
"Messages": {
	"WebsiteMessageCommand": "Visit {WEBSITE} where you can change skins.", // Information about website where player can change skins (!ws command) Set to empty to disable
	"SynchronizeMessageCommand": "Type !wp to synchronize chosen skins.", // Information about skins refreshing (!ws command) Set to empty to disable
	"KnifeMessageCommand": "Type !knife to open knife menu.", // Information about knife menu (!ws command) Set to empty to disable
	"CooldownRefreshCommand": "You can\u0027t refresh weapon paints right now.", // Cooldown information (!wp command) Set to empty to disable
	"SuccessRefreshCommand": "Refreshing weapon paints.", // Information about refreshing skins (!wp command) Set to empty to disable
	"ChosenKnifeMenu": "You have chosen {KNIFE} as your knife.", // Information about choosen knife (!knife command) Set to empty to disable
	"ChosenSkinMenu": "You have chosen {SKIN} as your skin.", // Information about choosen skin (!skins command) Set to empty to disable
	"ChosenKnifeMenuKill": "To correctly apply skin for knife, you need to type !kill.", // Information about suicide after knife selection (!knife command) Set to empty to disable
	"KnifeMenuTitle": "Knife Menu.",  // Menu title (!knife menu)
	"WeaponMenuTitle": "Weapon Menu.", // Menu title (!skins menu)
	"SkinMenuTitle": "Select skin for {WEAPON}" // Menu title (!skins menu, after weapon select)
},
"Additional": {
	"KnifeEnabled": true, // Enable or disable knife feature
	"SkinEnabled": true, // Enable or disable skin feature
	"CommandWpEnabled": true, // Enable or disable refreshing command
	"CommandKillEnabled": true, // Enable or disable kill command
	"CommandKnife": "knife", // Name of knife menu command, u can change to for e.g, knives
	"CommandSkin": "ws", // Name of skin information command, u can change to for e.g, skins
	"CommandSkinSelection": "skins", // Name of skins menu command, u can change to for e.g, weapons
	"CommandRefresh": "wp", // Name of skin refreshing command, u can change to for e.g, refreshskins
	"CommandKill": "kill", // Name of kill command, u can change to for e.g, suicide
	"GiveRandomKnife": false,  // Give random knife to players if they didn't choose
	"GiveRandomSkins": false  // Give random skins to players if they didn't choose
},
</pre></code>
</details>
    
## Web install
The bundled PHP website was removed from this fork — this repository is the plugin only. `Website` in the
config and the `!ws` command still just print whatever URL you point them at.

## Troubleshooting
<details>
**Skins are not changing:**
Set FollowCSGOGuidelines to false in cssharp’s core.jcon config

**Database error table does not exists:**
Plugin is not loaded or configured with mysql credentials. Tables are auto-created by plugin.

</details>

### Use this plugin at your own risk! Using this may lead to GSLT ban or something else Valve come with. [Valve Server guidelines](https://blog.counter-strike.net/index.php/server_guidelines/)
