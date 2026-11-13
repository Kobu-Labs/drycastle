using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Issue #267 (ADR 0018): the bag, the free moves between it and the worn slots, a corpse with several items, ground items and their wire payloads.</summary>
public class BagTests
{
    // three items for the one slot there is: a hat (no limit), a mask (two pickups) and the shipped Bigfoot head's shape
    static readonly ItemsFile Items = ItemsFile.Parse("""{"version":1,"items":[{"id":"hat","name":"Hat"},{"id":"mask","name":"Mask","pickup_limit":2},{"id":"cap","name":"Cap"}]}""");
    const int Hat = 0, Mask = 1, Cap = 2;

    static (Equipment worn, Bag bag) Kit() => (new Equipment(Items), new Bag());

    static Bag Full(int pickups = 0)
    {
        var bag = new Bag();
        for (var i = 0; i < Bag.Cells; i++) bag.Add(new ItemStack(Cap, pickups + i));
        return bag;
    }

    static (WorldEntityManager world, CorpseBook book) Setup()
    {
        var types = new WorldEntityRegistry();
        CorpseRules.Register(types);
        GroundItemRules.Register(types);
        var world = new WorldEntityManager(types);
        return (world, new CorpseBook(world));
    }

    // ---- the bag ----

    [Fact]
    public void TheBagHasSixCellsFilledFromTheFirstEmptyOne()
    {
        Assert.Equal(6, Bag.Cells);
        var bag = new Bag();
        Assert.Equal(0, bag.Add(new ItemStack(Hat)));
        Assert.Equal(1, bag.Add(new ItemStack(Mask, 1)));
        Assert.Equal(new ItemStack(Hat), bag.Remove(0));
        Assert.Null(bag.Remove(0));                      // nothing left to remove
        Assert.Equal(0, bag.Add(new ItemStack(Cap)));    // the hole is filled first
        Assert.True(bag.Changed);
        for (var i = 2; i < Bag.Cells; i++) bag.Add(new ItemStack(Hat));
        Assert.True(bag.IsFull);
        Assert.Equal(-1, bag.Add(new ItemStack(Hat)));   // full: nothing changes
        Assert.Equal(Bag.Cells, bag.Count);
        Assert.Null(bag.Get(-1));
        Assert.Null(bag.Get(Bag.Cells));
    }

    // ---- equip and unequip: free moves ----

    [Fact]
    public void EquippingFromTheBagWearsItAndLeavesTheCellEmptyWithNoPickupCounted()
    {
        var (worn, bag) = Kit();
        bag.Set(2, new ItemStack(Mask, 1));
        Assert.Equal(ItemMove.Ok, ItemRules.Equip(Items, worn, bag, 2));
        Assert.Equal(new ItemStack(Mask, 1), worn.Get(EquipSlot.Headgear)); // the count is the item's, unchanged
        Assert.Null(bag.Get(2));
    }

    [Fact]
    public void EquippingIntoATakenSlotSwapsTheWornItemIntoTheSameCellEvenWithAFullBag()
    {
        var (worn, _) = Kit();
        var bag = Full();
        worn.Wear(EquipSlot.Headgear, new ItemStack(Hat, 7));
        Assert.Equal(ItemMove.Ok, ItemRules.Equip(Items, worn, bag, 4));
        Assert.Equal(new ItemStack(Cap, 4), worn.Get(EquipSlot.Headgear));
        Assert.Equal(new ItemStack(Hat, 7), bag.Get(4)); // swapped, count kept
        Assert.True(bag.IsFull);
    }

    [Fact]
    public void EquippingAnEmptyOrMissingCellDoesNothing()
    {
        var (worn, bag) = Kit();
        Assert.Equal(ItemMove.Nothing, ItemRules.Equip(Items, worn, bag, 0));
        Assert.Equal(ItemMove.Nothing, ItemRules.Equip(Items, worn, bag, 99));
        Assert.Equal(ItemMove.Nothing, ItemRules.Equip(Items, worn, bag, -1));
        Assert.Null(worn.Get(EquipSlot.Headgear));
    }

    [Fact]
    public void UnequippingPutsTheItemInTheFirstEmptyCellAndAFullBagRefusesIt()
    {
        var (worn, bag) = Kit();
        bag.Set(0, new ItemStack(Hat));
        worn.Wear(EquipSlot.Headgear, new ItemStack(Mask, 2));
        Assert.Equal(ItemMove.Ok, ItemRules.Unequip(worn, bag, EquipSlot.Headgear));
        Assert.Null(worn.Get(EquipSlot.Headgear));
        Assert.Equal(new ItemStack(Mask, 2), bag.Get(1));
        Assert.Equal(ItemMove.Nothing, ItemRules.Unequip(worn, bag, EquipSlot.Headgear)); // nothing worn now

        var full = Full();
        worn.Wear(EquipSlot.Headgear, new ItemStack(Mask, 2));
        Assert.Equal(ItemMove.BagFull, ItemRules.Unequip(worn, full, EquipSlot.Headgear));
        Assert.Equal(new ItemStack(Mask, 2), worn.Get(EquipSlot.Headgear)); // refused: nothing changed
        Assert.Equal(Bag.Cells, full.Count);
    }

    [Fact]
    public void WearingAHeadshotImmuneItemFromTheBagGuardsTheHeadAndBaggingItDoesNot()
    {
        var items = ItemsFile.Parse("""{"version":1,"items":[{"id":"helm","name":"Helm","headshot_immune":true}]}""");
        var worn = new Equipment(items);
        var bag = new Bag();
        bag.Set(0, new ItemStack(0));
        Assert.False(worn.HeadGuarded);                  // carried, not worn: no guard
        ItemRules.Equip(items, worn, bag, 0);
        Assert.True(worn.HeadGuarded);
        ItemRules.Unequip(worn, bag, EquipSlot.Headgear);
        Assert.False(worn.HeadGuarded);
    }

    // ---- drop ----

    [Fact]
    public void DroppingTakesTheItemFromItsPlaceKeepingItsPickupCount()
    {
        var (worn, bag) = Kit();
        worn.Wear(EquipSlot.Headgear, new ItemStack(Mask, 1));
        bag.Set(5, new ItemStack(Hat, 3));

        Assert.Equal(ItemMove.Ok, ItemRules.DropWorn(worn, EquipSlot.Headgear, out var a));
        Assert.Equal(new ItemStack(Mask, 1), a);
        Assert.Null(worn.Get(EquipSlot.Headgear));
        Assert.Equal(ItemMove.Ok, ItemRules.DropBagged(bag, 5, out var b));
        Assert.Equal(new ItemStack(Hat, 3), b);
        Assert.Null(bag.Get(5));

        Assert.Equal(ItemMove.Nothing, ItemRules.DropWorn(worn, EquipSlot.Headgear, out _));
        Assert.Equal(ItemMove.Nothing, ItemRules.DropBagged(bag, 5, out _));
        Assert.Equal(ItemMove.Nothing, ItemRules.DropBagged(bag, 77, out _));
    }

    // ---- where a taken item goes ----

    [Fact]
    public void ATakenItemGoesToItsEmptySlotElseTheBagElseSwapsIfAllowed()
    {
        var (worn, bag) = Kit();
        Assert.Equal(ItemPlace.Worn, ItemRules.Place(Items, new ItemStack(Hat), worn, bag, swap: true, out _));
        Assert.Equal(new ItemStack(Hat), worn.Get(EquipSlot.Headgear));
        Assert.Equal(0, bag.Count);

        Assert.Equal(ItemPlace.Bagged, ItemRules.Place(Items, new ItemStack(Mask, 1), worn, bag, swap: true, out _));
        Assert.Equal(new ItemStack(Mask, 1), bag.Get(0));
        Assert.Equal(new ItemStack(Hat), worn.Get(EquipSlot.Headgear)); // the slot was taken: what is worn stays

        var full = Full();
        Assert.Equal(ItemPlace.NoRoom, ItemRules.Place(Items, new ItemStack(Mask), worn, full, swap: false, out var none));
        Assert.Null(none);
        Assert.Equal(new ItemStack(Hat), worn.Get(EquipSlot.Headgear));

        Assert.Equal(ItemPlace.Swapped, ItemRules.Place(Items, new ItemStack(Mask, 4), worn, full, swap: true, out var displaced));
        Assert.Equal(new ItemStack(Hat), displaced);
        Assert.Equal(new ItemStack(Mask, 4), worn.Get(EquipSlot.Headgear));
        Assert.Equal(ItemPlace.None, ItemRules.Place(Items, new ItemStack(50), worn, bag, swap: true, out _)); // not in items.json
    }

    // ---- a death: everything onto the corpse ----

    [Fact]
    public void ACorpseHoldsEverythingTheBodyCarriedWornFirstThenTheBag()
    {
        var (worn, bag) = Kit();
        worn.Wear(EquipSlot.Headgear, new ItemStack(Mask, 1));
        for (var i = 0; i < Bag.Cells; i++) bag.Set(i, new ItemStack(Cap, i));
        Assert.Equal(7, ItemRules.MaxCarried);

        var all = ItemRules.All(worn, bag);
        Assert.Equal(7, all.Count);
        Assert.Equal(new ItemStack(Mask, 1), all[0]);
        Assert.Equal(new ItemStack(Cap, 5), all[6]);

        var corpse = new Corpse(3, Vector3.Zero, 0, 0, 0, all);
        Assert.Equal(7, corpse.Items.Count);
        Assert.False(corpse.AddItem(new ItemStack(Hat)));   // up to seven
        Assert.Equal(new ItemStack(Mask, 1), corpse.Item);  // the first, for the single row

        var more = new Corpse(3, Vector3.Zero, 0, 0, 0, all.Concat(all));
        Assert.Equal(7, more.Items.Count);                   // a longer list is cut at the capacity
    }

    [Fact]
    public void ADeathStripsTheWearerAndTheBagClean()
    {
        var (worn, bag) = Kit();
        worn.Wear(EquipSlot.Headgear, new ItemStack(Mask, 1));
        bag.Set(1, new ItemStack(Hat));
        bag.Set(4, new ItemStack(Cap, 2));

        var all = ItemRules.Strip(worn, bag);

        Assert.Equal(new[] { new ItemStack(Mask, 1), new ItemStack(Hat), new ItemStack(Cap, 2) }, all);
        Assert.Null(worn.Get(EquipSlot.Headgear));
        Assert.Equal(0, bag.Count);
        Assert.True(worn.Changed);
        Assert.True(bag.Changed);
        Assert.Empty(ItemRules.Strip(worn, bag)); // nothing left to lose twice
    }

    // ---- taking from a corpse ----

    [Fact]
    public void EachItemTakenFromACorpseCountsOnePickupAndGoesToTheSlotThenTheBag()
    {
        var (_, book) = Setup();
        var corpse = new Corpse(3, Vector3.Zero, 0, 0, 0, new[] { new ItemStack(Hat), new ItemStack(Mask, 1), new ItemStack(Cap, 9) });
        var id = book.Add(corpse)!.Value;
        var (worn, bag) = Kit();

        Assert.Equal(ItemPickUp.Ok, book.TryPickUpItem(id, 1, Vector3.Zero, worn, bag, Items, out var p1));
        Assert.Equal(ItemPlace.Worn, p1);
        Assert.Equal(new ItemStack(Mask, 2), worn.Get(EquipSlot.Headgear)); // one pickup counted, into the empty slot
        Assert.Equal(new[] { new ItemStack(Hat), new ItemStack(Cap, 9) }, corpse.Items);

        Assert.Equal(ItemPickUp.Ok, book.TryPickUpItem(id, 1, Vector3.Zero, worn, bag, Items, out var p2));
        Assert.Equal(ItemPlace.Bagged, p2);                                  // the slot is taken now: the bag
        Assert.Equal(new ItemStack(Cap, 10), bag.Get(0));
        Assert.Equal(new ItemStack(Mask, 2), worn.Get(EquipSlot.Headgear));  // what is worn stays
        Assert.Equal(new[] { new ItemStack(Hat) }, corpse.Items);
    }

    [Fact]
    public void WithTheSlotTakenAndTheBagFullTheCorpseTakeIsASwapIntoTheSameListPlace()
    {
        var (_, book) = Setup();
        var corpse = new Corpse(3, Vector3.Zero, 0, 0, 0, new[] { new ItemStack(Cap), new ItemStack(Hat, 4) });
        var id = book.Add(corpse)!.Value;
        var (worn, _) = Kit();
        worn.Wear(EquipSlot.Headgear, new ItemStack(Mask, 2));
        var bag = Full();

        Assert.Equal(ItemPickUp.Ok, book.TryPickUpItem(id, 1, Vector3.Zero, worn, bag, Items, out var placed));
        Assert.Equal(ItemPlace.Swapped, placed);
        Assert.Equal(new ItemStack(Hat, 5), worn.Get(EquipSlot.Headgear));        // counted
        Assert.Equal(new[] { new ItemStack(Cap), new ItemStack(Mask, 2) }, corpse.Items); // the worn item, count kept, takes the place; the swap counts no pickup
        Assert.Equal(Bag.Cells, bag.Count);
    }

    [Fact]
    public void AnItemAtItsPickupLimitCrumblesOffTheCorpseAndTheListCloses()
    {
        var (_, book) = Setup();
        var corpse = new Corpse(3, Vector3.Zero, 0, 0, 0, new[] { new ItemStack(Hat), new ItemStack(Mask, 2), new ItemStack(Cap) });
        var id = book.Add(corpse)!.Value;
        var (worn, bag) = Kit();

        Assert.Equal(ItemPickUp.Crumbled, book.TryPickUpItem(id, 1, Vector3.Zero, worn, bag, Items, out var placed));
        Assert.Equal(ItemPlace.None, placed);
        Assert.Equal(new[] { new ItemStack(Hat), new ItemStack(Cap) }, corpse.Items);
        Assert.Null(worn.Get(EquipSlot.Headgear));
        Assert.Equal(0, bag.Count);
    }

    // ---- ground items ----

    [Fact]
    public void ADroppedItemIsAWorldEntityThatKeepsItsItemPickupsAndPlace()
    {
        var (world, book) = Setup();
        var spawned = new List<WorldSpawned>();
        world.Spawned += spawned.Add;
        var id = book.Drop(7, new Vector3(5, 2, -3), new ItemStack(Mask, 1))!.Value;

        var item = book.GetGround(id)!;
        Assert.Equal(GroundItemRules.TypeId, item.TypeId);
        Assert.Equal(7, item.Owner);
        Assert.Equal(1, book.GroundCount);
        Assert.Equal(0, book.CorpseCount);
        var s = Assert.Single(spawned).State;
        Assert.Equal(Mask, GroundItemRules.ItemOf(s));
        Assert.Equal(1, GroundItemRules.PickupsOf(s));
        Assert.Equal(5f, s.Position.X, 0.01);
        Assert.Equal(2f, s.Position.Y, 0.01);

        Assert.True(WorldCodec.TryDecodeSpawn(WorldCodec.EncodeSpawn(id, GroundItemRules.TypeId, s), out var rid, out var rtype, out var rs));
        Assert.Equal((id, GroundItemRules.TypeId), (rid, rtype));
        Assert.Equal((Mask, 1), (GroundItemRules.ItemOf(rs), GroundItemRules.PickupsOf(rs)));
    }

    [Fact]
    public void AnyoneTakesAGroundItemCountingOnePickupTheDropperToo()
    {
        var (world, book) = Setup();
        var id = book.Drop(7, Vector3.Zero, new ItemStack(Mask, 0))!.Value;
        var (worn, bag) = Kit();

        Assert.Equal(ItemPickUp.OutOfReach, book.TryTakeGround(id, new Vector3(9, 0, 0), worn, bag, Items, out _));
        Assert.Equal(ItemPickUp.Gone, book.TryTakeGround(999, Vector3.Zero, worn, bag, Items, out _));
        Assert.NotNull(book.GetGround(id));

        Assert.Equal(ItemPickUp.Ok, book.TryTakeGround(id, Vector3.Zero, worn, bag, Items, out var placed)); // the dropper's own retake
        Assert.Equal(ItemPlace.Worn, placed);
        Assert.Equal(new ItemStack(Mask, 1), worn.Get(EquipSlot.Headgear));
        Assert.Null(book.GetGround(id));
        Assert.Equal(0, world.Count);

        // dropped again and taken by someone else: the count is the item's
        ItemRules.DropWorn(worn, EquipSlot.Headgear, out var dropped);
        var again = book.Drop(7, Vector3.Zero, dropped)!.Value;
        var (other, otherBag) = Kit();
        Assert.Equal(ItemPickUp.Ok, book.TryTakeGround(again, Vector3.Zero, other, otherBag, Items, out _));
        Assert.Equal(new ItemStack(Mask, 2), other.Get(EquipSlot.Headgear));
        // the limit of two is reached: the next take crumbles it
        ItemRules.DropWorn(other, EquipSlot.Headgear, out var last);
        var third = book.Drop(8, Vector3.Zero, last)!.Value;
        Assert.Equal(ItemPickUp.Crumbled, book.TryTakeGround(third, Vector3.Zero, new Equipment(Items), new Bag(), Items, out _));
        Assert.Null(book.GetGround(third));
        Assert.Equal(0, world.Count);
    }

    [Fact]
    public void AGroundItemGoesToTheBagWhenTheSlotIsTakenAndIsNotTakenWhenBothAreFull()
    {
        var (_, book) = Setup();
        var id = book.Drop(7, Vector3.Zero, new ItemStack(Mask, 2))!.Value; // one pickup left... none: its limit is two
        var (worn, bag) = Kit();
        worn.Wear(EquipSlot.Headgear, new ItemStack(Hat));

        // both full: refused, not counted, and not crumbled although the limit is reached
        var full = Full();
        Assert.Equal(ItemPickUp.NoRoom, book.TryTakeGround(id, Vector3.Zero, worn, full, Items, out _));
        Assert.Equal(new ItemStack(Mask, 2), book.GetGround(id)!.Stack);
        Assert.Equal(new ItemStack(Hat), worn.Get(EquipSlot.Headgear));

        var cap = book.Drop(7, Vector3.Zero, new ItemStack(Cap, 3))!.Value;
        Assert.Equal(ItemPickUp.Ok, book.TryTakeGround(cap, Vector3.Zero, worn, bag, Items, out var placed));
        Assert.Equal(ItemPlace.Bagged, placed);
        Assert.Equal(new ItemStack(Cap, 4), bag.Get(0));
    }

    [Fact]
    public void AGroundItemExpiresOnTheCorpseTimer()
    {
        var (world, book) = Setup();
        var id = book.Drop(7, Vector3.Zero, new ItemStack(Hat))!.Value;
        world.Tick(GroundItemRules.LifetimeSeconds - 1);
        Assert.NotNull(book.GetGround(id));
        world.Tick(1.5f);
        Assert.Null(book.GetGround(id));
        Assert.Equal(CorpseRules.LifetimeSeconds, GroundItemRules.LifetimeSeconds);
        Assert.Equal(0, book.Count);
    }

    [Fact]
    public void GroundItemsShareTheCorpseCapAndTheOnesCarryingNothingGoFirst()
    {
        var (_, book) = Setup();
        var ground = book.Drop(1, Vector3.Zero, new ItemStack(Hat))!.Value;             // the oldest of all
        var carrier = book.Add(new Corpse(1, Vector3.Zero, 0, 0, 0, new[] { new ItemStack(Cap) }))!.Value;
        var plain = new List<ushort>();
        while (book.Count < CorpseRules.MaxCorpses) plain.Add(book.Add(new Corpse(2, Vector3.Zero, 0, 0, 0))!.Value);
        Assert.Equal(CorpseRules.MaxCorpses, book.Count);

        book.Add(new Corpse(3, Vector3.Zero, 0, 0, 0));
        Assert.Equal(CorpseRules.MaxCorpses, book.Count);
        Assert.NotNull(book.GetGround(ground));  // the item on the ground stays
        Assert.NotNull(book.Get(carrier));       // so does the corpse with an item
        Assert.Null(book.Get(plain[0]));         // the oldest corpse without one went

        book.Drop(4, Vector3.Zero, new ItemStack(Mask));
        Assert.Null(book.Get(plain[1]));
        Assert.Equal(CorpseRules.MaxCorpses, book.Count);

        // only carriers are left: the oldest of them goes, a ground item or a corpse alike
        for (var i = 0; i < CorpseRules.MaxCorpses; i++) book.Drop(5, Vector3.Zero, new ItemStack(Hat));
        Assert.Equal(CorpseRules.MaxCorpses, book.Count);
        Assert.Null(book.GetGround(ground));
        Assert.Null(book.Get(carrier));
    }

    // ---- the wire ----

    [Fact]
    public void TheBagRequestsAndTheGroundItemSurviveTheWire()
    {
        Assert.True(ItemEvents.TryDecodeEquip(ItemEvents.EncodeEquip(5), out var cell));
        Assert.Equal(5, cell);
        Assert.True(ItemEvents.TryDecodeUnequip(ItemEvents.EncodeUnequip(EquipSlot.Headgear), out var slot));
        Assert.Equal(EquipSlot.Headgear, slot);
        Assert.True(ItemEvents.TryDecodeDrop(ItemEvents.EncodeDrop(ItemHome.Bag, 3), out var home, out var at));
        Assert.Equal((ItemHome.Bag, 3), (home, at));
        Assert.True(ItemEvents.TryDecodeDrop(ItemEvents.EncodeDrop(ItemHome.Worn, (int)EquipSlot.Headgear), out home, out at));
        Assert.Equal((ItemHome.Worn, 0), (home, at));
        Assert.True(ItemEvents.TryDecodeTakeGround(ItemEvents.EncodeTakeGround(60000), out var id));
        Assert.Equal(60000, id);

        Assert.False(ItemEvents.TryDecodeEquip(new byte[2], out _));
        Assert.False(ItemEvents.TryDecodeUnequip(new byte[] { 9 }, out _));       // no such slot
        Assert.False(ItemEvents.TryDecodeDrop(new byte[] { 2, 0 }, out _, out _)); // no such place
        Assert.False(ItemEvents.TryDecodeDrop(new byte[1], out _, out _));
        Assert.False(ItemEvents.TryDecodeTakeGround(new byte[3], out _));
    }

    [Fact]
    public void MalformedItemListsAreRefused()
    {
        var good = ItemEvents.EncodeCorpseItems(1, new[] { new ItemStack(Hat), new ItemStack(Mask) });
        Assert.True(ItemEvents.TryDecodeCorpseItems(good, out _, out var items));
        Assert.Equal(2, items.Count);
        Assert.False(ItemEvents.TryDecodeCorpseItems(good.AsSpan(0, good.Length - 1), out _, out _)); // cut short
        var empty = (byte[])good.Clone();
        empty[3] = 0;                                                                                  // an entry with no item
        Assert.False(ItemEvents.TryDecodeCorpseItems(empty, out _, out var none));
        Assert.Empty(none);
        var tooMany = new byte[3 + 2 * 8];
        tooMany[2] = 8;
        Assert.False(ItemEvents.TryDecodeCorpseItems(tooMany, out _, out _));                           // more than a body carries

        var eq = ItemEvents.EncodeEquipment(new Equipment(Items), new Bag());
        Assert.False(ItemEvents.TryDecodeEquipment(eq.AsSpan(0, eq.Length - 1), out _, out _));
        Assert.False(ItemEvents.TryDecodeEquipment(Array.Empty<byte>(), out _, out _));
    }
}
