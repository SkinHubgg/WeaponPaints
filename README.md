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
