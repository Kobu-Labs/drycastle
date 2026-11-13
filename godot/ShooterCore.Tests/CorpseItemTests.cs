using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Issue #261: the items a corpse carries, the pickup limit that makes it crumble, the item events and the admin's /give. The bag, the corpse's several items and the ground items are in <see cref="BagTests"/>.</summary>
public class CorpseItemTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly GameData Shipped = GameData.Load(DataDir);
    static ItemsFile Items => Shipped.Items;
    static int Bigfoot => Items.IndexOf("bigfoot_head");

    // ---- the corpse and the pickup limit ----

    static (WorldEntityManager world, CorpseBook book) Setup()
    {
        var types = new WorldEntityRegistry();
        CorpseRules.Register(types);
        var world = new WorldEntityManager(types);
        return (world, new CorpseBook(world));
    }

    static Corpse CorpseWith(ItemStack? item, Vector3? at = null) => new(7, at ?? Vector3.Zero, 0f, 0, 60, item is { } i ? new[] { i } : null);

    static ItemPickUp TakeFirst(CorpseBook book, ushort id, Equipment worn, Bag? bag = null, Vector3? from = null) =>
        book.TryPickUpItem(id, 0, from ?? Vector3.Zero, worn, bag ?? new Bag(), Items, out _);

    [Fact]
    public void TheItemCanBeTakenThreeTimesAndTheFourthAttemptCrumblesIt()
    {
        var (_, book) = Setup();
        var corpse = CorpseWith(new ItemStack(Bigfoot)); // dropped by its first wearer, taken no time yet
        var id = book.Add(corpse)!.Value;
        var player = new Equipment(Items);

        for (var taken = 1; taken <= 3; taken++)
        {
            Assert.Equal(ItemPickUp.Ok, TakeFirst(book, id, player));
            Assert.Equal(new ItemStack(Bigfoot, taken), player.Get(EquipSlot.Headgear)); // the count travels with the item
            Assert.Null(corpse.Item);
            corpse.AddItem(player.TakeOff(EquipSlot.Headgear)!.Value); // the wearer dies: it drops again, count kept
            Assert.Equal(taken, corpse.Item!.Value.Pickups);
        }

        Assert.Equal(ItemPickUp.Crumbled, TakeFirst(book, id, player));
        Assert.Null(corpse.Item);
        Assert.Null(player.Get(EquipSlot.Headgear));
        Assert.False(player.HeadGuarded);
        Assert.Equal(ItemPickUp.NoItem, TakeFirst(book, id, player)); // it is gone for good
    }

    [Fact]
    public void ThePickupCountBelongsToTheItemNotTheCorpse()
    {
        var (_, book) = Setup();
        var first = book.Add(CorpseWith(new ItemStack(Bigfoot, 2)))!.Value;
        var player = new Equipment(Items);
        Assert.Equal(ItemPickUp.Ok, TakeFirst(book, first, player)); // the third and last pickup
        // it drops on a brand new corpse with its count of three: that one crumbles it at once
        var second = book.Add(CorpseWith(player.TakeOff(EquipSlot.Headgear)))!.Value;
        Assert.Equal(ItemPickUp.Crumbled, TakeFirst(book, second, new Equipment(Items)));
    }

    [Fact]
    public void TakingAnItemWithTheSlotTakenAndTheBagFullLeavesWhatWasWornOnTheCorpse()
    {
        var items = ItemsFile.Parse("""{"version":1,"items":[{"id":"hat","name":"Hat"},{"id":"mask","name":"Mask","pickup_limit":2}]}""");
        var (_, book) = Setup();
        var corpse = CorpseWith(new ItemStack(1));
        var id = book.Add(corpse)!.Value;
        var player = new Equipment(items);
        player.Wear(EquipSlot.Headgear, new ItemStack(0, 5));
        var bag = new Bag();
        for (var i = 0; i < Bag.Cells; i++) bag.Add(new ItemStack(0, i));

        Assert.Equal(ItemPickUp.Ok, book.TryPickUpItem(id, 0, Vector3.Zero, player, bag, items, out var placed));
        Assert.Equal(ItemPlace.Swapped, placed);
        Assert.Equal(new ItemStack(1, 1), player.Get(EquipSlot.Headgear));
        Assert.Equal(new ItemStack(0, 5), corpse.Item); // the hat, with its own count, can be taken back
    }

    [Fact]
    public void ARefusedItemPickupChangesNothing()
    {
        var (_, book) = Setup();
        var corpse = CorpseWith(new ItemStack(Bigfoot));
        var id = book.Add(corpse)!.Value;
        var empty = book.Add(CorpseWith(null, new Vector3(50, 0, 0)))!.Value;
        var player = new Equipment(Items);

        Assert.Equal(ItemPickUp.OutOfReach, TakeFirst(book, id, player, from: new Vector3(10, 0, 0)));
        Assert.Equal(ItemPickUp.Gone, TakeFirst(book, 9999, player));
        Assert.Equal(ItemPickUp.NoItem, TakeFirst(book, empty, player, from: new Vector3(50, 0, 0)));
        Assert.Equal(ItemPickUp.NoItem, book.TryPickUpItem(id, 1, Vector3.Zero, player, new Bag(), Items, out _)); // no second item
        Assert.Equal(new ItemStack(Bigfoot), corpse.Item);
        Assert.Null(player.Get(EquipSlot.Headgear));
    }

    [Fact]
    public void TheCorpseCapDespawnsTheOldestCorpseWithoutAnItemFirst()
    {
        var (world, book) = Setup();
        var carrying = book.Add(CorpseWith(new ItemStack(Bigfoot)))!.Value; // the oldest of all
        var plain = new List<ushort>();
        for (var i = 1; i < CorpseRules.MaxCorpses; i++) plain.Add(book.Add(CorpseWith(null, new Vector3(i, 0, 0)))!.Value);
        Assert.Equal(CorpseRules.MaxCorpses, book.Count);

        book.Add(CorpseWith(null, new Vector3(99, 0, 0)));

        Assert.Equal(CorpseRules.MaxCorpses, book.Count);
        Assert.NotNull(book.Get(carrying));       // the item corpse stays
        Assert.Null(book.Get(plain[0]));          // the oldest corpse without an item went
    }

    // ---- the wire ----

    [Fact]
    public void ItemEventsSurviveTheWire()
    {
        Assert.True(ItemEvents.TryDecodePickUp(ItemEvents.EncodePickUp(513, 4), out var corpse, out var at));
        Assert.Equal(513, corpse);
        Assert.Equal(4, at);

        var list = new[] { new ItemStack(Bigfoot, 2), new ItemStack(0, 300) };
        Assert.True(ItemEvents.TryDecodeCorpseItems(ItemEvents.EncodeCorpseItems(7, list), out var c, out var items));
        Assert.Equal(7, c);
        Assert.Equal(new[] { new ItemStack(Bigfoot, 2), new ItemStack(0, 255) }, items); // the count is clamped to its byte
        Assert.True(ItemEvents.TryDecodeCorpseItems(ItemEvents.EncodeCorpseItems(7, Array.Empty<ItemStack>()), out _, out var none));
        Assert.Empty(none);

        var worn = new Equipment(Items);
        worn.Wear(EquipSlot.Headgear, new ItemStack(Bigfoot, 2));
        var bag = new Bag();
        bag.Set(3, new ItemStack(0, 1));
        Assert.True(ItemEvents.TryDecodeEquipment(ItemEvents.EncodeEquipment(worn, bag), out var slots, out var cells));
        Assert.Equal(new ItemStack?[] { new ItemStack(Bigfoot, 2) }, slots); // the pickup counts travel now: the screens show them
        Assert.Equal(Bag.Cells, cells.Length);
        Assert.Equal(new ItemStack(0, 1), cells[3]);
        Assert.Equal(1, cells.Count(x => x != null));
        Assert.True(ItemEvents.TryDecodeEquipment(ItemEvents.EncodeEquipment(new Equipment(Items), new Bag()), out var bare, out var empty));
        Assert.Equal(new ItemStack?[] { null }, bare);
        Assert.All(empty, x => Assert.Null(x));

        Assert.True(ItemEvents.TryDecodeNotice(ItemEvents.EncodeNotice(ItemNoticeKind.Crumbled), out var notice));
        Assert.Equal(ItemNoticeKind.Crumbled, notice);
        Assert.True(ItemEvents.TryDecodeNotice(ItemEvents.EncodeNotice(ItemNoticeKind.BagFull), out notice));
        Assert.Equal(ItemNoticeKind.BagFull, notice);
        Assert.False(ItemEvents.TryDecodeNotice(new byte[] { 0 }, out _));
        Assert.False(ItemEvents.TryDecodeNotice(new byte[] { 9 }, out _));
        Assert.False(ItemEvents.TryDecodePickUp(new byte[2], out _, out _));
    }

    [Fact]
    public void ItemEventKindsAreTheirOwnBlockAndTheProtocolMovedOn()
    {
        Assert.Equal(new[] { 190, 191, 192, 193 }, new[] { EventKind.PickUpCorpseItem, EventKind.CorpseItems, EventKind.Equipment, EventKind.ItemNotice }.Select(k => (int)k));
        Assert.Equal(new[] { 200, 201, 202, 203 }, new[] { EventKind.EquipItem, EventKind.UnequipItem, EventKind.DropItem, EventKind.TakeGroundItem }.Select(k => (int)k));
        Assert.True(Wire.ProtocolVersion >= 31);
    }

    // ---- the admin's way to get it ----

    [Fact]
    public void GiveTakesAnItemAndOptionallyAPlayerAndNeedsAnAdmin()
    {
        var ids = Items.Items.Select(i => i.Id).ToList();
        var admin = new ChatContext(true, new[] { "car" }, ids);
        var guest = new ChatContext(false, new[] { "car" }, ids);

        var self = ChatCommands.Parse("/give BIGFOOT_HEAD", admin);
        Assert.True(self.Ok);
        Assert.Equal(new[] { "bigfoot_head" }, self.Args);
        Assert.Equal(new[] { "bigfoot_head", "Bob" }, ChatCommands.Parse("/give bigfoot_head Bob", admin).Args);
        Assert.False(ChatCommands.Parse("/give", admin).Ok);
        Assert.False(ChatCommands.Parse("/give helmet", admin).Ok);
        Assert.Contains("admin", ChatCommands.Parse("/give bigfoot_head", guest).Error);
        Assert.Contains(ChatCommands.Hint("/give b", admin).Suggestions, s => s.Insert == "bigfoot_head");
    }
}
