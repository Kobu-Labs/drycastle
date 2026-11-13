using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Issue #261: the item a corpse carries, the pickup limit that makes it crumble, the item events and the admin's /give.</summary>
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

    static Corpse CorpseWith(ItemStack? item, Vector3? at = null) => new(7, at ?? Vector3.Zero, 0f, 0, 60, item);

    [Fact]
    public void TheItemCanBeTakenThreeTimesAndTheFourthAttemptCrumblesIt()
    {
        var (_, book) = Setup();
        var corpse = CorpseWith(new ItemStack(Bigfoot)); // dropped by its first wearer, taken no time yet
        var id = book.Add(corpse)!.Value;
        var player = new Equipment(Items);

        for (var taken = 1; taken <= 3; taken++)
        {
            Assert.Equal(ItemPickUp.Ok, book.TryPickUpItem(id, Vector3.Zero, player, Items));
            Assert.Equal(new ItemStack(Bigfoot, taken), player.Get(EquipSlot.Headgear)); // the count travels with the item
            Assert.Null(corpse.Item);
            corpse.CarryItem(player.TakeOff(EquipSlot.Headgear)); // the wearer dies: it drops again, count kept
            Assert.Equal(taken, corpse.Item!.Value.Pickups);
        }

        Assert.Equal(ItemPickUp.Crumbled, book.TryPickUpItem(id, Vector3.Zero, player, Items));
        Assert.Null(corpse.Item);
        Assert.Null(player.Get(EquipSlot.Headgear));
        Assert.False(player.HeadGuarded);
        Assert.Equal(ItemPickUp.NoItem, book.TryPickUpItem(id, Vector3.Zero, player, Items)); // it is gone for good
    }

    [Fact]
    public void ThePickupCountBelongsToTheItemNotTheCorpse()
    {
        var (_, book) = Setup();
        var first = book.Add(CorpseWith(new ItemStack(Bigfoot, 2)))!.Value;
        var player = new Equipment(Items);
        Assert.Equal(ItemPickUp.Ok, book.TryPickUpItem(first, Vector3.Zero, player, Items)); // the third and last pickup
        // it drops on a brand new corpse with its count of three: that one crumbles it at once
        var second = book.Add(CorpseWith(player.TakeOff(EquipSlot.Headgear)))!.Value;
        Assert.Equal(ItemPickUp.Crumbled, book.TryPickUpItem(second, Vector3.Zero, new Equipment(Items), Items));
    }

    [Fact]
    public void TakingAnItemLeavesWhatWasWornOnTheCorpse()
    {
        var items = ItemsFile.Parse("""{"version":1,"items":[{"id":"hat","name":"Hat"},{"id":"mask","name":"Mask","pickup_limit":2}]}""");
        var (_, book) = Setup();
        var corpse = CorpseWith(new ItemStack(1));
        var id = book.Add(corpse)!.Value;
        var player = new Equipment(items);
        player.Wear(EquipSlot.Headgear, new ItemStack(0, 5));

        Assert.Equal(ItemPickUp.Ok, book.TryPickUpItem(id, Vector3.Zero, player, items));
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

        Assert.Equal(ItemPickUp.OutOfReach, book.TryPickUpItem(id, new Vector3(10, 0, 0), player, Items));
        Assert.Equal(ItemPickUp.Gone, book.TryPickUpItem(9999, Vector3.Zero, player, Items));
        Assert.Equal(ItemPickUp.NoItem, book.TryPickUpItem(empty, new Vector3(50, 0, 0), player, Items));
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
        Assert.True(ItemEvents.TryDecodePickUp(ItemEvents.EncodePickUp(513), out var corpse));
        Assert.Equal(513, corpse);

        Assert.True(ItemEvents.TryDecodeCorpseItem(ItemEvents.EncodeCorpseItem(7, Bigfoot), out var c, out var item));
        Assert.Equal(7, c);
        Assert.Equal(Bigfoot, item);
        Assert.True(ItemEvents.TryDecodeCorpseItem(ItemEvents.EncodeCorpseItem(7, null), out _, out var none));
        Assert.Null(none);

        var worn = new Equipment(Items);
        worn.Wear(EquipSlot.Headgear, new ItemStack(Bigfoot, 2));
        Assert.True(ItemEvents.TryDecodeEquipment(ItemEvents.EncodeEquipment(worn), out var slots));
        Assert.Equal(new int?[] { Bigfoot }, slots); // the pickup count stays on the server
        Assert.True(ItemEvents.TryDecodeEquipment(ItemEvents.EncodeEquipment(new Equipment(Items)), out var bare));
        Assert.Equal(new int?[] { null }, bare);

        Assert.True(ItemEvents.TryDecodeNotice(ItemEvents.EncodeNotice(ItemNoticeKind.Crumbled), out var notice));
        Assert.Equal(ItemNoticeKind.Crumbled, notice);
        Assert.False(ItemEvents.TryDecodeNotice(new byte[] { 0 }, out _));
        Assert.False(ItemEvents.TryDecodePickUp(new byte[1], out _));
    }

    [Fact]
    public void ItemEventKindsAreTheirOwnBlockAndTheProtocolMovedOn()
    {
        Assert.Equal(new[] { 190, 191, 192, 193 }, new[] { EventKind.PickUpCorpseItem, EventKind.CorpseItem, EventKind.Equipment, EventKind.ItemNotice }.Select(k => (int)k));
        Assert.True(Wire.ProtocolVersion >= 28);
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
