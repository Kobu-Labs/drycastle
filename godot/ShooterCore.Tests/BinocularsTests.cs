using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class BinocularsTests
{
    static ItemsFile Shipped => ItemsFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "items.json"));

    [Fact]
    public void The_reading_is_whole_metres_and_nothing_beyond_the_range()
    {
        Assert.Equal(320, Binoculars.Reading(319.6f));
        Assert.Equal(0, Binoculars.Reading(0.2f));
        Assert.Null(Binoculars.Reading(null));
        Assert.Null(Binoculars.Reading(Binoculars.MaxRangeM + 1));
        Assert.Null(Binoculars.Reading(float.PositiveInfinity));
    }

    [Fact]
    public void The_shipped_binoculars_ride_in_the_bag_and_have_no_pickup_limit()
    {
        var def = Shipped.Find("binoculars");
        Assert.NotNull(def);
        Assert.True(def!.Binoculars);
        Assert.True(def.BagOnly);
        Assert.Null(def.PickupLimit);
    }

    [Fact]
    public void They_are_carried_only_when_a_bag_cell_holds_them()
    {
        var items = Shipped;
        var bino = items.IndexOf("binoculars");
        var other = items.IndexOf("speaker");
        Assert.True(Binoculars.Carried(items, new int?[] { null, other, bino }));
        Assert.False(Binoculars.Carried(items, new int?[] { null, other, null }));
        Assert.False(Binoculars.Carried(items, Array.Empty<int?>()));
    }

    [Fact]
    public void Binoculars_that_can_be_worn_are_refused()
    {
        var e = Assert.Throws<DataFileException>(() => ItemsFile.Parse("""{"version":1,"items":[{"id":"b","name":"B","binoculars":true}]}"""));
        Assert.Contains("bag_only", e.Message);
    }

    [Fact]
    public void An_atlas_needs_a_model_of_the_private_pack()
    {
        Assert.Throws<DataFileException>(() => ItemsFile.Parse("""{"version":1,"items":[{"id":"b","name":"B","model":"own/b.glb","atlas":"glb/atlas/a.png"}]}"""));
        ItemsFile.Parse("""{"version":1,"items":[{"id":"b","name":"B","model":"synty/glb/props/b.glb","atlas":"glb/atlas/a.png"}]}""");
    }

    [Fact]
    public void Giving_them_puts_them_in_the_bag()
    {
        var items = Shipped;
        var worn = new Equipment(items);
        var bag = new Bag();
        Assert.Equal(ItemPlace.Bagged, ItemRules.Place(items, new ItemStack(items.IndexOf("binoculars")), worn, bag, swap: false, out _));
        Assert.True(Binoculars.Carried(items, Enumerable.Range(0, Bag.Cells).Select(c => bag.Get(c)?.Item)));
    }

    [Fact]
    public void The_pair_raised_is_the_narrowest_of_those_carried()
    {
        var items = Shipped;
        int standard = items.IndexOf("binoculars"), hunting = items.IndexOf("hunting_binoculars"), speaker = items.IndexOf("speaker");
        Assert.Equal(-1, Binoculars.Held(items, new[] { speaker }));
        Assert.Equal(standard, Binoculars.Held(items, new[] { speaker, standard }));
        Assert.Equal(hunting, Binoculars.Held(items, new[] { standard, hunting }));
        Assert.Equal(hunting, Binoculars.Held(items, new[] { hunting, standard }));
    }
}

public class BinocularsWireTests
{
    [Fact]
    public void The_raise_wish_round_trips_and_a_bad_one_is_refused()
    {
        Assert.True(Binoculars.TryDecodeRaise(Binoculars.EncodeRaise(true), out var up) && up);
        Assert.True(Binoculars.TryDecodeRaise(Binoculars.EncodeRaise(false), out up) && !up);
        Assert.False(Binoculars.TryDecodeRaise(new byte[] { 2 }, out _));
        Assert.False(Binoculars.TryDecodeRaise(Array.Empty<byte>(), out _));
    }

    [Fact]
    public void The_state_told_to_the_others_round_trips_and_a_bad_one_is_refused()
    {
        Assert.True(Binoculars.TryDecodeRaised(Binoculars.EncodeRaised(513, true, 4), out var player, out var up, out var item));
        Assert.Equal(513, player);
        Assert.True(up);
        Assert.Equal(4, item);
        Assert.True(Binoculars.TryDecodeRaised(Binoculars.EncodeRaised(7, false, 4), out player, out up, out item));
        Assert.False(up);
        Assert.Equal(0, item);
        Assert.False(Binoculars.TryDecodeRaised(new byte[] { 1, 0, 2, 0 }, out _, out _, out _));
        Assert.False(Binoculars.TryDecodeRaised(new byte[3], out _, out _, out _));
    }
}

public class StandardItemTests
{
    static ItemsFile Shipped => ItemsFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "items.json"));

    [Fact]
    public void Every_life_starts_with_the_binoculars_in_the_bag_once()
    {
        var items = Shipped;
        var bag = new Bag();
        Assert.True(ItemRules.IssueStandard(items, bag));
        Assert.True(Binoculars.Carried(items, Enumerable.Range(0, Bag.Cells).Select(c => bag.Get(c)?.Item)));
        Assert.False(ItemRules.IssueStandard(items, bag)); // already carried: no second pair
        Assert.Equal(1, bag.Count);
    }

    [Fact]
    public void An_owned_upgrade_is_issued_in_place_of_the_standard_pair_and_never_beside_it()
    {
        var items = Shipped;
        var up = items.Find("hunting_binoculars")!;
        Assert.Equal(HuntTier.Bronze, up.RewardTierOf);
        Assert.True(Binoculars.FovOf(up) < Binoculars.FovOf(items.Find("binoculars")!)); // more zoom
        var bag = new Bag();
        Assert.True(ItemRules.IssueStandard(items, bag, new HashSet<string> { "hunting_binoculars" }));
        Assert.Equal(1, bag.Count);
        Assert.Equal(items.IndexOf("hunting_binoculars"), bag.Get(0)?.Item);
        Assert.False(ItemRules.IssueStandard(items, bag, new HashSet<string>())); // a pair is carried: no standard one beside it
        var plain = new Bag();
        ItemRules.IssueStandard(items, plain, new HashSet<string>()); // not owned: the standard pair
        Assert.Equal(items.IndexOf("binoculars"), plain.Get(0)?.Item);
    }

    [Fact]
    public void Only_the_upgrade_marks_animals_for_thirty_seconds()
    {
        var items = Shipped;
        Assert.Equal(30f, Binoculars.MarkSecondsOf(items.Find("hunting_binoculars")!));
        Assert.Equal(0f, Binoculars.MarkSecondsOf(items.Find("binoculars")!));
        var e = Assert.Throws<DataFileException>(() => ItemsFile.Parse("""{"version":1,"items":[{"id":"s","name":"S","bag_only":true,"mark_seconds":5}]}"""));
        Assert.Contains("mark_seconds", e.Message);
    }

    [Fact]
    public void The_upgrade_is_not_left_on_a_corpse()
    {
        var items = Shipped;
        var bag = new Bag();
        bag.Add(new ItemStack(items.IndexOf("hunting_binoculars")));
        Assert.Empty(ItemRules.StripForCorpse(items, new Equipment(items), bag));
    }

    [Fact]
    public void A_full_bag_is_issued_nothing()
    {
        var items = Shipped;
        var bag = new Bag();
        for (var c = 0; c < Bag.Cells; c++) bag.Set(c, new ItemStack(items.IndexOf("speaker")));
        Assert.False(ItemRules.IssueStandard(items, bag));
        Assert.Equal(Bag.Cells, bag.Count);
    }

    [Fact]
    public void A_death_leaves_everything_on_the_corpse_but_the_standard_items()
    {
        var items = Shipped;
        var worn = new Equipment(items);
        var bag = new Bag();
        ItemRules.IssueStandard(items, bag);
        bag.Add(new ItemStack(items.IndexOf("speaker")));
        worn.Wear(EquipSlot.Neck, new ItemStack(items.IndexOf("gold_chain"), 1));
        var left = ItemRules.StripForCorpse(items, worn, bag);
        Assert.Equal(new[] { items.IndexOf("gold_chain"), items.IndexOf("speaker") }, left.Select(s => s.Item));
        Assert.Equal(0, bag.Count);
        Assert.Null(worn.Get(EquipSlot.Neck));
        // and the next life has them again
        Assert.True(ItemRules.IssueStandard(items, bag));
    }

    [Fact]
    public void A_standard_item_that_can_be_worn_is_refused()
    {
        var e = Assert.Throws<DataFileException>(() => ItemsFile.Parse("""{"version":1,"items":[{"id":"b","name":"B","standard":true}]}"""));
        Assert.Contains("bag_only", e.Message);
    }
}
