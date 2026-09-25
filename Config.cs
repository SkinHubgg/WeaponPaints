using CounterStrikeSharp.API.Core;
using System.Text.Json.Serialization;

namespace WeaponPaints
{
	public class Additional
	{
		[JsonPropertyName("KnifeEnabled")]
		public bool KnifeEnabled { get; set; } = true;

		[JsonPropertyName("GloveEnabled")]
		public bool GloveEnabled { get; set; } = true;

		[JsonPropertyName("MusicEnabled")]
		public bool MusicEnabled { get; set; } = true;

		[JsonPropertyName("AgentEnabled")]
		public bool AgentEnabled { get; set; } = true;

		[JsonPropertyName("SkinEnabled")]
		public bool SkinEnabled { get; set; } = true;

		[JsonPropertyName("PinsEnabled")]
		public bool PinsEnabled { get; set; } = true;

		// Chicken pets (CS2 1.41.8.2). The plugin spawns a "chicken" that follows the player, carrying the pet id,
		// stage, seed and name from wp_player_pets. See Pets.cs.
		[JsonPropertyName("PetsEnabled")]
		public bool PetsEnabled { get; set; } = true;

		// CounterStrikeSharp permission needed for !pet and for a pet to be spawned at all, e.g. "@css/vip".
		// Empty means everyone.
		[JsonPropertyName("PetPermission")]
		public string PetPermission { get; set; } = "";

		// EXPERIMENTAL, untested in game. Also writes each player's plugin pet (pullet or hen) into the team intro
		// line-up (the intro's m_petItem), on maps that have the intro. Needs PetsEnabled. See PetTeamIntro.cs.
		[JsonPropertyName("PetTeamIntroExperimental")]
		public bool PetTeamIntroExperimental { get; set; } = false;

		[JsonPropertyName("CommandWpEnabled")]
		public bool CommandWpEnabled { get; set; } = true;

		[JsonPropertyName("CommandKillEnabled")]
		public bool CommandKillEnabled { get; set; } = true;

		[JsonPropertyName("CommandKnife")]
		public List<string> CommandKnife { get; set; } = ["knife"];

		[JsonPropertyName("CommandMusic")]
		public List<string> CommandMusic { get; set; } = ["music"];
		
		[JsonPropertyName("CommandPin")]
		public List<string> CommandPin { get; set; } = ["pin", "pins", "coin", "coins"];

		[JsonPropertyName("CommandGlove")]
		public List<string> CommandGlove { get; set; } = ["gloves"];

		[JsonPropertyName("CommandPet")]
		public List<string> CommandPet { get; set; } = ["pet"];

		[JsonPropertyName("CommandAgent")]
		public List<string> CommandAgent { get; set; } = ["agents"];
		
		[JsonPropertyName("CommandStattrak")]
		public List<string> CommandStattrak { get; set; } = ["stattrak", "st"];

		[JsonPropertyName("CommandSkin")]
		public List<string> CommandSkin { get; set; } = ["ws"];

		[JsonPropertyName("CommandSkinSelection")]
		public List<string> CommandSkinSelection { get; set; } = ["skins"];

		[JsonPropertyName("CommandRefresh")]
		public List<string> CommandRefresh { get; set; } = ["wp"];

		[JsonPropertyName("CommandKill")]
		public List<string> CommandKill { get; set; } = ["kill"];

		[JsonPropertyName("GiveRandomKnife")]
		public bool GiveRandomKnife { get; set; } = false;

		[JsonPropertyName("GiveRandomSkin")]
		public bool GiveRandomSkin { get; set; } = false;

		[JsonPropertyName("ShowSkinImage")]
		public bool ShowSkinImage { get; set; } = true;
	}

	public class WeaponPaintsConfig : BasePluginConfig
	{
        [JsonPropertyName("ConfigVersion")] public override int Version { get; set; } = 11;

        [JsonPropertyName("SkinsLanguage")]
		public string SkinsLanguage { get; set; } = "en";

		// Base address the item datasets (skins, gloves, agents, music, collectibles, and the optional pets) are pulled from
		// on plugin load. The plugin requests "<DataUrl>/data/<dataset>.json" and keeps a copy of the last
		// successful response under CacheDirectory so an outage cannot leave a server with no items.
		// Leave empty to use the default.
		[JsonPropertyName("DataUrl")]
		public string DataUrl { get; set; } = "https://cdn.skinhub.gg";

		// How many hours the copy of a dataset on disk is used as-is, with no request to DataUrl at all.
		// Past that window the plugin revalidates as normal: a conditional request, which answers 304 with no
		// body when nothing has changed, or downloads the new version when it has.
		//
		// Set to 0 to always revalidate and never skip the check. Note that any window means new items can be
		// up to that many hours late after a CS2 update; the summary line logged at load says which path every
		// dataset took, so "my skins are old" is answerable from the console.
		[JsonPropertyName("CacheDiskHours")]
		public int CacheDiskHours { get; set; } = 12;

		// Where the item data cache is kept. Empty (the default) means "<plugin folder>/.cache".
		// A relative path is resolved against the plugin folder, never the process working directory, so
		// "../.cache" puts the cache beside the plugin folder instead of inside it - which is what to use when
		// the plugin folder is an ephemeral or read-only mount, as it often is on containerised servers.
		// If the directory cannot be created or written the plugin still runs from DataUrl, it just has no
		// offline fallback, and it says so once in the log.
		[JsonPropertyName("CacheDirectory")]
		public string CacheDirectory { get; set; } = "";

		[JsonPropertyName("DatabaseHost")]
		public string DatabaseHost { get; set; } = "";

		[JsonPropertyName("DatabasePort")]
		public int DatabasePort { get; set; } = 3306;

		[JsonPropertyName("DatabaseUser")]
		public string DatabaseUser { get; set; } = "";

		[JsonPropertyName("DatabasePassword")]
		public string DatabasePassword { get; set; } = "";

		[JsonPropertyName("DatabaseName")]
		public string DatabaseName { get; set; } = "";

		[JsonPropertyName("CmdRefreshCooldownSeconds")]
		public int CmdRefreshCooldownSeconds { get; set; } = 3;

		[JsonPropertyName("Website")]
		public string Website { get; set; } = "example.com/skins";

		[JsonPropertyName("Additional")]
		public Additional Additional { get; set; } = new();
		
		[JsonPropertyName("MenuType")]
		public string MenuType { get; set; } = "selectable";
	}
}