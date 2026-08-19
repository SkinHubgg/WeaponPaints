namespace WeaponPaints
{
	public class WeaponInfo
	{
		public int Paint { get; set; }
		public int Seed { get; set; }
		public float Wear { get; set; }
		public string Nametag { get; set; } = "";
		public bool StatTrak { get; set; }
		public int StatTrakCount { get; set; }
		public KeyChainInfo? KeyChain { get; set; }
		public List<StickerInfo> Stickers { get; set; } = new();
	}

	public class StickerInfo
	{
		/// <summary>
		/// The column this sticker came from - `weapon_sticker_&lt;Slot&gt;` - and therefore the slot the
		/// game must be told about.
		///
		/// It exists because the alternative was `Stickers.IndexOf(sticker)`, the sticker's POSITION IN
		/// THE LIST. The parser `continue`s past an empty or malformed column, so one bad column silently
		/// promoted every later sticker into a lower slot: a player with slots 0, 2 and 4 filled and slot
		/// 0 unreadable got their slot-2 sticker rendered in slot 0 and their slot-4 in slot 1. It also
		/// only worked at all because StickerInfo is a class - `IndexOf` on a value type matches by
		/// equality, so two identical placements would both resolve to the first one's index.
		/// </summary>
		public int Slot { get; set; }

		public uint Id { get; set; }
		public uint Schema { get; set; }
		public float OffsetX { get; set; }
		public float OffsetY { get; set; }
		public float Wear { get; set; }
		public float Scale { get; set; }
		public float Rotation { get; set; }
	}

	public class KeyChainInfo
	{
		public uint Id { get; set; }
		public float OffsetX { get; set; }
		public float OffsetY { get; set; }
		public float OffsetZ { get; set; }
		public uint Seed { get; set; }
	}
}