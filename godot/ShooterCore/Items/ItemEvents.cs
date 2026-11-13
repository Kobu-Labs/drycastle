using System.Buffers.Binary;

namespace Drycastle.ShooterCore;

/// <summary>Why the server sends an <see cref="EventKind.ItemNotice"/>. The client words it (translation key), so the wire carries only the reason.</summary>
public enum ItemNoticeKind : byte
{
    None = 0,
    /// <summary>The item on the corpse (or the ground) had been taken as often as it may be: "The item crumbles in your hands".</summary>
    Crumbled = 1,
    /// <summary>An item could not be taken off or picked up: its slot is taken and the bag is full. "Your bag is full".</summary>
    BagFull = 2,
}

/// <summary>Where a dropped item comes from (<see cref="EventKind.DropItem"/>).</summary>
public enum ItemHome : byte
{
    /// <summary>A worn slot: the index is an <see cref="EquipSlot"/>.</summary>
    Worn = 0,
    /// <summary>A bag cell: the index is the cell.</summary>
    Bag = 1,
}

/// <summary>
/// Payloads of the item event kinds (little-endian). An item is named by its <see cref="ItemsFile.Items"/> position plus one; 0 is none.
/// An item with its pickup count is two bytes (item, pickups clamped to 255): an item with no limit can be taken more often than that, and
/// the screens only show the count.
/// </summary>
public static class ItemEvents
{
    static byte ToWire(int? item) => item is { } i && i is >= 0 and < ItemsFile.MaxItems ? (byte)(i + 1) : (byte)0;

    static int? FromWire(byte b) => b == 0 ? null : b - 1;

    static void Put(Span<byte> b, ItemStack? stack)
    {
        b[0] = ToWire(stack?.Item);
        b[1] = stack is { } s && b[0] != 0 ? (byte)Math.Clamp(s.Pickups, 0, 255) : (byte)0;
    }

    static ItemStack? Read(ReadOnlySpan<byte> b) => FromWire(b[0]) is { } item ? new ItemStack(item, b[1]) : null;

    /// <summary>Client to server, <see cref="EventKind.PickUpCorpseItem"/>: u16 corpse id, u8 index of the item in the corpse's list.</summary>
    public static byte[] EncodePickUp(ushort corpse, int index)
    {
        var b = new byte[3];
        BinaryPrimitives.WriteUInt16LittleEndian(b, corpse);
        b[2] = (byte)Math.Clamp(index, 0, 255);
        return b;
    }

    public static bool TryDecodePickUp(ReadOnlySpan<byte> p, out ushort corpse, out int index)
    {
        corpse = 0; index = 0;
        if (p.Length != 3) return false;
        corpse = BinaryPrimitives.ReadUInt16LittleEndian(p);
        index = p[2];
        return true;
    }

    /// <summary>
    /// Server to everyone, <see cref="EventKind.CorpseItems"/>: u16 corpse id, u8 count, then per item u8 item and u8 pickups. Sent when a corpse
    /// that carries items spawns and each time its list changes (an empty list tells that the last one is gone), and to a joiner for the
    /// corpses that carry any.
    /// </summary>
    public static byte[] EncodeCorpseItems(ushort corpse, IReadOnlyList<ItemStack> items)
    {
        var n = Math.Min(items.Count, ItemRules.MaxCarried);
        var b = new byte[3 + 2 * n];
        BinaryPrimitives.WriteUInt16LittleEndian(b, corpse);
        b[2] = (byte)n;
        for (var i = 0; i < n; i++) Put(b.AsSpan(3 + 2 * i, 2), items[i]);
        return b;
    }

    public static bool TryDecodeCorpseItems(ReadOnlySpan<byte> p, out ushort corpse, out List<ItemStack> items)
    {
        corpse = 0; items = new List<ItemStack>();
        if (p.Length < 3 || p[2] > ItemRules.MaxCarried || p.Length != 3 + 2 * p[2]) return false;
        corpse = BinaryPrimitives.ReadUInt16LittleEndian(p);
        for (var i = 0; i < p[2]; i++)
        {
            if (Read(p.Slice(3 + 2 * i, 2)) is not { } stack) { items.Clear(); return false; } // an empty entry in a list is not a list
            items.Add(stack);
        }
        return true;
    }

    /// <summary>
    /// Server to the owner, <see cref="EventKind.Equipment"/>: u8 slot count, then per slot u8 item and u8 pickups in <see cref="EquipSlot"/> order
    /// (0 none); u8 cell count, then the same per bag cell. Both together, so a swap between them is never seen half done.
    /// </summary>
    public static byte[] EncodeEquipment(Equipment worn, Bag bag)
    {
        var slots = worn.Slots;
        var cells = bag.Contents;
        var b = new byte[2 + 2 * slots.Count + 2 * cells.Count];
        b[0] = (byte)slots.Count;
        for (var i = 0; i < slots.Count; i++) Put(b.AsSpan(1 + 2 * i, 2), slots[i]);
        var at = 1 + 2 * slots.Count;
        b[at] = (byte)cells.Count;
        for (var i = 0; i < cells.Count; i++) Put(b.AsSpan(at + 1 + 2 * i, 2), cells[i]);
        return b;
    }

    public static bool TryDecodeEquipment(ReadOnlySpan<byte> p, out ItemStack?[] worn, out ItemStack?[] bag)
    {
        worn = Array.Empty<ItemStack?>(); bag = Array.Empty<ItemStack?>();
        if (p.Length < 1) return false;
        var slots = p[0];
        var at = 1 + 2 * slots;
        if (p.Length < at + 1) return false;
        var cells = p[at];
        if (p.Length != at + 1 + 2 * cells) return false;
        worn = new ItemStack?[slots];
        for (var i = 0; i < slots; i++) worn[i] = Read(p.Slice(1 + 2 * i, 2));
        bag = new ItemStack?[cells];
        for (var i = 0; i < cells; i++) bag[i] = Read(p.Slice(at + 1 + 2 * i, 2));
        return true;
    }

    /// <summary>
    /// Server to everyone but the wearer, <see cref="EventKind.WornItems"/>: u16 player id, then the same slots as <see cref="EncodeEquipment"/> (u8 count, one u8 item per
    /// slot, 0 none). Sent when what a player wears changes, and to a joiner for each player who wears something, so every client can draw it.
    /// </summary>
    public static byte[] EncodeWorn(ushort player, Equipment worn)
    {
        var b = new byte[2 + 1 + worn.Slots.Count];
        BinaryPrimitives.WriteUInt16LittleEndian(b, player);
        b[2] = (byte)worn.Slots.Count;
        for (var i = 0; i < worn.Slots.Count; i++) b[3 + i] = ToWire(worn.Slots[i]?.Item);
        return b;
    }

    public static bool TryDecodeWorn(ReadOnlySpan<byte> p, out ushort player, out int?[] slots)
    {
        player = 0; slots = Array.Empty<int?>();
        if (p.Length < 3 || p.Length != 3 + p[2]) return false;
        player = BinaryPrimitives.ReadUInt16LittleEndian(p);
        slots = new int?[p[2]];
        for (var i = 0; i < slots.Length; i++) slots[i] = FromWire(p[3 + i]);
        return true;
    }

    /// <summary>Server to one player, <see cref="EventKind.ItemNotice"/>: u8 <see cref="ItemNoticeKind"/>.</summary>
    public static byte[] EncodeNotice(ItemNoticeKind kind) => new[] { (byte)kind };

    public static bool TryDecodeNotice(ReadOnlySpan<byte> p, out ItemNoticeKind kind)
    {
        kind = ItemNoticeKind.None;
        if (p.Length != 1 || p[0] == 0 || p[0] > (byte)ItemNoticeKind.BagFull) return false;
        kind = (ItemNoticeKind)p[0];
        return true;
    }

    /// <summary>Client to server, <see cref="EventKind.EquipItem"/>: u8 bag cell.</summary>
    public static byte[] EncodeEquip(int cell) => new[] { (byte)Math.Clamp(cell, 0, 255) };

    public static bool TryDecodeEquip(ReadOnlySpan<byte> p, out int cell)
    {
        cell = 0;
        if (p.Length != 1) return false;
        cell = p[0];
        return true;
    }

    /// <summary>Client to server, <see cref="EventKind.UnequipItem"/>: u8 <see cref="EquipSlot"/>.</summary>
    public static byte[] EncodeUnequip(EquipSlot slot) => new[] { (byte)slot };

    public static bool TryDecodeUnequip(ReadOnlySpan<byte> p, out EquipSlot slot)
    {
        slot = default;
        if (p.Length != 1 || !Enum.IsDefined((EquipSlot)p[0])) return false;
        slot = (EquipSlot)p[0];
        return true;
    }

    /// <summary>Client to server, <see cref="EventKind.DropItem"/>: u8 <see cref="ItemHome"/>, u8 index (an <see cref="EquipSlot"/> or a bag cell).</summary>
    public static byte[] EncodeDrop(ItemHome home, int index) => new[] { (byte)home, (byte)Math.Clamp(index, 0, 255) };

    public static bool TryDecodeDrop(ReadOnlySpan<byte> p, out ItemHome home, out int index)
    {
        home = default; index = 0;
        if (p.Length != 2 || !Enum.IsDefined((ItemHome)p[0])) return false;
        home = (ItemHome)p[0];
        index = p[1];
        return true;
    }

    /// <summary>Client to server, <see cref="EventKind.TakeGroundItem"/>: u16 ground item id (a world entity id).</summary>
    public static byte[] EncodeTakeGround(ushort id)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, id);
        return b;
    }

    public static bool TryDecodeTakeGround(ReadOnlySpan<byte> p, out ushort id)
    {
        id = 0;
        if (p.Length != 2) return false;
        id = BinaryPrimitives.ReadUInt16LittleEndian(p);
        return true;
    }
}
