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
built from the 1.41.8.2 schema and strings. Things to watch when testing:

- whether the pet's colour and shape change with the seed (the client is expected to derive them from it). If not,
  `pet_variant` still picks the colour;
- whether it keeps following (the leader is re-asserted every second) and whether the model is right per stage;
- eggs never leave the nest; a chick uses `models/chicken/chick.vmdl`, a pullet or hen its breed's model.

Lifecycle: spawned the frame after the owner spawns, stays put when the owner dies, comes back on the owner's next
spawn if it was killed, and is removed on team change, disconnect, map end and plugin unload. A player the game
itself gave a pet (a chicken whose owner is them) gets no second one.

Names: the game keeps one name per stage (chick, pullet, hen), but only the item's main custom name is sent to
players, so that is where the label's name comes from at every stage. The plugin has one name per player (the current
stage's) and writes it there, plus on that stage's own name field. The in-game rename box stops at 20 characters; the
plugin accepts up to 32 (the column width), so a longer name typed on a website is not cut again.

Commands (`CommandPet`, default `pet`):

| Command | What it does |
| --- | --- |
| `!pet` | Menu: pick a pet and its stage, or None |
| `!pet name <text>` | Name the pet (32 characters max, the column width; empty clears it) |
| `!pet seed [number]` | A new random look, or a specific seed |
| `!pet color <number\|random>` | Force a colour (material group index), or let the seed decide |
| `!pet stage <chick\|pullet\|hen>` | Change the stage |
| `!pet off` | No pet |

Config (`Additional`): `PetsEnabled` (default `true`), `CommandPet` (default `["pet"]`), `PetPermission` (default
`""` = everyone, e.g. `"@css/vip"` - also decides whether a player's pet is spawned at all),
`PetTeamIntroExperimental` (default `false`, see [Team intro](#team-intro-experimental)).

Table (created automatically):

```sql
CREATE TABLE IF NOT EXISTS `wp_player_pets` (
  `steamid`     varchar(18)  NOT NULL PRIMARY KEY,  -- one pet per player, pets are "noteam"
  `pet_id`      int          NOT NULL,              -- 1 egg, 2 chick, 3 catalana, 4 silkie, 5 polish
  `pet_stage`   tinyint      NOT NULL DEFAULT 3,    -- 0 egg, 1 chick, 2 pullet, 3 hen
  `pet_variant` int          NULL,                  -- material group index; NULL = let the seed decide
  `pet_seed`    int unsigned NOT NULL DEFAULT 0,    -- "pet seed" attribute
  `pet_name`    varchar(32)  NULL                   -- name tag
);
```

The pet models are precached on map load, so a plugin loaded mid-map may show pets on the default chicken model until
the next map.

### Team intro (experimental)

**Off by default, and untested in game.** With `PetTeamIntroExperimental: true` (and `PetsEnabled`), the plugin also
puts each player's pet into the team intro, the line-up the camera sweeps over before the first round. CS2 1.41.8.2
gave every intro spot a pet item next to the agent, gloves and weapon. When the intro starts, the plugin writes the
player's pet into that item one frame later, the same way it fills the chicken's item. It checks again half a second
later in case the game overwrote it.

- Only pullets and hens. The game deploys pets from the pullet stage, so chicks and eggs are left out.
- A pet the game put there itself (a player who really owns one) is left alone.
- `!pet color` does not reach the intro. The intro pet's look comes from the seed only.
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
