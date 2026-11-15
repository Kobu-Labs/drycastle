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
}
