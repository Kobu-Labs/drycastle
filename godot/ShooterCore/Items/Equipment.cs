namespace Drycastle.ShooterCore;

/// <summary>
/// One item as it exists in the match: which item (<see cref="ItemsFile.Items"/> position) and how many times it has been taken from a corpse.
/// The count belongs to the item, not to the corpse or the player, so it travels with the item from the body to the wearer and back.
/// </summary>
public readonly record struct ItemStack(int Item, int Pickups = 0);

/// <summary>What happened when a player tried to take the item a corpse carries.</summary>
public enum ItemPickUp : byte
{
    /// <summary>It is worn now (what was worn in that slot is on the corpse instead).</summary>
    Ok,
    /// <summary>Taken the allowed number of times already: the item is gone, and the player is told.</summary>
    Crumbled,
    Gone,
    OutOfReach,
    NoItem,
}

/// <summary>
/// What a player wears: one item per <see cref="EquipSlot"/>. Server state of the session, apart from the profile: nothing here is
/// stored (ADR 0003 covers the profile only). A death takes it off onto the corpse (<see cref="Corpse.Item"/>), leaving the match
/// loses it, a new round keeps it.
/// </summary>
public sealed class Equipment
{
    public static readonly int SlotCount = Enum.GetValues<EquipSlot>().Length;

    readonly ItemsFile _items;
    readonly ItemStack?[] _worn = new ItemStack?[SlotCount];

    public Equipment(ItemsFile items) => _items = items;

    /// <summary>A headshot on this player is a body hit (a worn item with <see cref="ItemDef.HeadshotImmune"/>). Kept up to date by <see cref="Wear"/> and <see cref="TakeOff"/>, so the hit test reads a field.</summary>
    public bool HeadGuarded { get; private set; }

    /// <summary>Set whenever the worn items changed; the host clears it after sending them to the player.</summary>
    public bool Changed { get; set; }

    public ItemStack? Get(EquipSlot slot) => _worn[(int)slot];

    /// <summary>The worn item of every slot in slot order (null for an empty one): the wire's order.</summary>
    public IReadOnlyList<ItemStack?> Slots => _worn;

    /// <summary>Puts <paramref name="item"/> on in <paramref name="slot"/> and returns what it replaced (null when the slot was empty).</summary>
    public ItemStack? Wear(EquipSlot slot, ItemStack item)
    {
        var replaced = _worn[(int)slot];
        _worn[(int)slot] = item;
        Refresh();
        return replaced;
    }

    /// <summary>Takes off what is worn in <paramref name="slot"/> and returns it (null when the slot was empty).</summary>
    public ItemStack? TakeOff(EquipSlot slot)
    {
        var was = _worn[(int)slot];
        if (was == null) return null;
        _worn[(int)slot] = null;
        Refresh();
        return was;
    }

    void Refresh()
    {
        Changed = true;
        HeadGuarded = _worn.Any(s => s is { } w && _items.At(w.Item) is { HeadshotImmune: true });
    }
}

/// <summary>The pickup rule of an item a corpse carries, pure so the server and the tests agree.</summary>
public static class ItemRules
{
    /// <summary>
    /// The item a taker gets from <paramref name="carried"/>: the same item with one more pickup counted. An item with a
    /// <see cref="ItemDef.PickupLimit"/> that has been taken that many times already is not given: the attempt makes it crumble
    /// (<see cref="ItemPickUp.Crumbled"/>) and <paramref name="taken"/> is not set. An item the data does not know is
    /// <see cref="ItemPickUp.NoItem"/>.
    /// </summary>
    public static ItemPickUp Take(ItemsFile items, ItemStack carried, out ItemStack taken)
    {
        taken = default;
        if (items.At(carried.Item) is not { } def) return ItemPickUp.NoItem;
        if (def.PickupLimit is { } limit && carried.Pickups >= limit) return ItemPickUp.Crumbled;
        taken = carried with { Pickups = carried.Pickups + 1 };
        return ItemPickUp.Ok;
    }
}
