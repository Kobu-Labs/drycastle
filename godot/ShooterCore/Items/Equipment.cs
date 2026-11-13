namespace Drycastle.ShooterCore;

/// <summary>
/// One item as it exists in the match: which item (<see cref="ItemsFile.Items"/> position) and how many times it has been taken from a corpse.
/// The count belongs to the item, not to the corpse or the player, so it travels with the item from the body to the wearer and back.
/// </summary>
public readonly record struct ItemStack(int Item, int Pickups = 0);

/// <summary>What happened when a player tried to take the item a corpse carries.</summary>
public enum ItemPickUp : byte
{
    /// <summary>It was taken: worn, bagged, or worn with what was worn in that slot left on the corpse instead (<see cref="ItemPlace"/>).</summary>
    Ok,
    /// <summary>Taken the allowed number of times already: the item is gone, and the player is told.</summary>
    Crumbled,
    Gone,
    OutOfReach,
    NoItem,
    /// <summary>A ground item, with its slot taken and the bag full: nothing is taken or counted and the item stays where it is.</summary>
    NoRoom,
}

/// <summary>Where a taken item went (<see cref="ItemRules.Place"/>).</summary>
public enum ItemPlace : byte
{
    /// <summary>Nothing was placed (the item is unknown to the data).</summary>
    None,
    /// <summary>Its slot was empty: it is worn now.</summary>
    Worn,
    /// <summary>Its slot was taken: it is in the first empty bag cell.</summary>
    Bagged,
    /// <summary>The slot is taken and the bag is full: it is worn now and what was worn is handed back (the corpse takes its place).</summary>
    Swapped,
    /// <summary>The slot is taken, the bag is full and no swap was allowed: nothing changed.</summary>
    NoRoom,
}

/// <summary>What happened when a player moved an item between the bag, a worn slot and the ground (<see cref="ItemRules.Equip"/>).</summary>
public enum ItemMove : byte
{
    Ok,
    /// <summary>The cell or slot is empty (or holds an item the data does not know).</summary>
    Nothing,
    /// <summary>Taking an item off needs an empty cell and there is none: refused, nothing changed.</summary>
    BagFull,
}

/// <summary>
/// What a player wears: one item per <see cref="EquipSlot"/>. Server state of the session, apart from the profile: nothing here is
/// stored (ADR 0003 covers the profile only). A death takes it off onto the corpse (<see cref="Corpse.Items"/>), leaving the match
/// loses it, a new round keeps it. What is carried unworn is the <see cref="Bag"/>.
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

/// <summary>The rules of items: the pickup limit, where a taken item goes and the free moves between the bag and the worn slots. Pure, so the server and the tests agree.</summary>
public static class ItemRules
{
    /// <summary>Most items one player can carry, worn and bagged: a corpse holds all of them.</summary>
    public static readonly int MaxCarried = Equipment.SlotCount + Bag.Cells;

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

    /// <summary>
    /// Where an item that has come to a player goes (a corpse's or the ground's, or the admin's): into its slot when that is empty, else into
    /// the bag. When both are full, with <paramref name="swap"/> it is worn and what was worn comes back in <paramref name="displaced"/>
    /// (the corpse's way); without it nothing changes (<see cref="ItemPlace.NoRoom"/>). No pickup is counted here, see <see cref="Take"/>.
    /// </summary>
    public static ItemPlace Place(ItemsFile items, ItemStack stack, Equipment worn, Bag bag, bool swap, out ItemStack? displaced)
    {
        displaced = null;
        if (items.At(stack.Item) is not { } def) return ItemPlace.None;
        if (worn.Get(def.Slot) == null) { worn.Wear(def.Slot, stack); return ItemPlace.Worn; }
        if (bag.Add(stack) >= 0) return ItemPlace.Bagged;
        if (!swap) return ItemPlace.NoRoom;
        displaced = worn.Wear(def.Slot, stack);
        return ItemPlace.Swapped;
    }

    /// <summary>
    /// Puts the item in bag <paramref name="cell"/> on, in its slot. What was worn there goes into the same cell (a swap), so a full bag never
    /// refuses it. Free: no pickup is counted.
    /// </summary>
    public static ItemMove Equip(ItemsFile items, Equipment worn, Bag bag, int cell)
    {
        if (bag.Get(cell) is not { } stack || items.At(stack.Item) is not { } def) return ItemMove.Nothing;
        bag.Set(cell, worn.Wear(def.Slot, stack));
        return ItemMove.Ok;
    }

    /// <summary>Takes off what is worn in <paramref name="slot"/> into the first empty bag cell. A full bag refuses it (<see cref="ItemMove.BagFull"/>). Free.</summary>
    public static ItemMove Unequip(Equipment worn, Bag bag, EquipSlot slot)
    {
        if (worn.Get(slot) is not { } stack) return ItemMove.Nothing;
        if (bag.IsFull) return ItemMove.BagFull;
        worn.TakeOff(slot);
        bag.Add(stack);
        return ItemMove.Ok;
    }

    /// <summary>Takes what is worn in <paramref name="slot"/> off to be dropped; the pickup count is kept in <paramref name="dropped"/>.</summary>
    public static ItemMove DropWorn(Equipment worn, EquipSlot slot, out ItemStack dropped)
    {
        dropped = default;
        if (worn.TakeOff(slot) is not { } stack) return ItemMove.Nothing;
        dropped = stack;
        return ItemMove.Ok;
    }

    /// <summary>Takes what is in bag <paramref name="cell"/> out to be dropped; the pickup count is kept in <paramref name="dropped"/>.</summary>
    public static ItemMove DropBagged(Bag bag, int cell, out ItemStack dropped)
    {
        dropped = default;
        if (bag.Remove(cell) is not { } stack) return ItemMove.Nothing;
        dropped = stack;
        return ItemMove.Ok;
    }

    /// <summary>Everything a player carries, worn first (slot order) then the bag (cell order): what a death leaves on the corpse.</summary>
    public static List<ItemStack> All(Equipment worn, Bag bag)
    {
        var all = new List<ItemStack>(MaxCarried);
        foreach (var w in worn.Slots) if (w is { } s) all.Add(s);
        all.AddRange(bag.Held);
        return all;
    }

    /// <summary>Takes everything off and out of the bag and returns it in the order of <see cref="All"/>: a death.</summary>
    public static List<ItemStack> Strip(Equipment worn, Bag bag)
    {
        var all = All(worn, bag);
        foreach (var slot in Enum.GetValues<EquipSlot>()) worn.TakeOff(slot);
        for (var i = 0; i < Bag.Cells; i++) bag.Remove(i);
        return all;
    }
}
