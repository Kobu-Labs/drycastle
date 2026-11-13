using System.Buffers.Binary;

namespace Drycastle.ShooterCore;

/// <summary>Why the server sends an <see cref="EventKind.ItemNotice"/>. The client words it (translation key), so the wire carries only the reason.</summary>
public enum ItemNoticeKind : byte
{
    None = 0,
    /// <summary>The item on the corpse had been taken as often as it may be: "The item crumbles in your hands".</summary>
    Crumbled = 1,
}

/// <summary>Payloads of the item event kinds (little-endian). An item is named by its <see cref="ItemsFile.Items"/> position plus one; 0 is none.</summary>
public static class ItemEvents
{
    static byte ToWire(int? item) => item is { } i && i is >= 0 and < ItemsFile.MaxItems ? (byte)(i + 1) : (byte)0;

    static int? FromWire(byte b) => b == 0 ? null : b - 1;

    /// <summary>Client to server, <see cref="EventKind.PickUpCorpseItem"/>: u16 corpse id.</summary>
    public static byte[] EncodePickUp(ushort corpse)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, corpse);
        return b;
    }

    public static bool TryDecodePickUp(ReadOnlySpan<byte> p, out ushort corpse)
    {
        corpse = 0;
        if (p.Length != 2) return false;
        corpse = BinaryPrimitives.ReadUInt16LittleEndian(p);
        return true;
    }

    /// <summary>Server to everyone, <see cref="EventKind.CorpseItem"/>: u16 corpse id, u8 item (0 none). Sent when a corpse that carries an item spawns and each time its item changes, and to a joiner for the corpses that carry one.</summary>
    public static byte[] EncodeCorpseItem(ushort corpse, int? item)
    {
        var b = new byte[3];
        BinaryPrimitives.WriteUInt16LittleEndian(b, corpse);
        b[2] = ToWire(item);
        return b;
    }

    public static bool TryDecodeCorpseItem(ReadOnlySpan<byte> p, out ushort corpse, out int? item)
    {
        corpse = 0; item = null;
        if (p.Length != 3) return false;
        corpse = BinaryPrimitives.ReadUInt16LittleEndian(p);
        item = FromWire(p[2]);
        return true;
    }

    /// <summary>Server to the wearer, <see cref="EventKind.Equipment"/>: u8 slot count, then one u8 item per slot in <see cref="EquipSlot"/> order (0 none). The pickup count stays on the server.</summary>
    public static byte[] EncodeEquipment(Equipment worn)
    {
        var b = new byte[1 + worn.Slots.Count];
        b[0] = (byte)worn.Slots.Count;
        for (var i = 0; i < worn.Slots.Count; i++) b[1 + i] = ToWire(worn.Slots[i]?.Item);
        return b;
    }

    public static bool TryDecodeEquipment(ReadOnlySpan<byte> p, out int?[] slots)
    {
        slots = Array.Empty<int?>();
        if (p.Length < 1 || p.Length != 1 + p[0]) return false;
        slots = new int?[p[0]];
        for (var i = 0; i < slots.Length; i++) slots[i] = FromWire(p[1 + i]);
        return true;
    }

    /// <summary>Server to one player, <see cref="EventKind.ItemNotice"/>: u8 <see cref="ItemNoticeKind"/>.</summary>
    public static byte[] EncodeNotice(ItemNoticeKind kind) => new[] { (byte)kind };

    public static bool TryDecodeNotice(ReadOnlySpan<byte> p, out ItemNoticeKind kind)
    {
        kind = ItemNoticeKind.None;
        if (p.Length != 1 || p[0] == 0 || p[0] > (byte)ItemNoticeKind.Crumbled) return false;
        kind = (ItemNoticeKind)p[0];
        return true;
    }
}
