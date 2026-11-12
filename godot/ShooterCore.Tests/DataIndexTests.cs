using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The id index behind the data file Find methods (T3-019): same answers as the LINQ scans it replaces.</summary>
public class DataIndexTests
{
    sealed class Item
    {
        public string? Id { get; init; }
        public int Tag { get; init; }
    }

    [Fact]
    public void Finds_by_id_and_reuses_the_index()
    {
        var items = new List<Item> { new() { Id = "a", Tag = 1 }, new() { Id = "b", Tag = 2 } };
        Dictionary<string, Item>? cache = null;
        Assert.Equal(2, DataIndex.Find(ref cache, items, i => i.Id, "b")!.Tag);
        Assert.NotNull(cache);
        items.Add(new Item { Id = "c", Tag = 3 }); // the index is built once: a later item is not seen
        Assert.Null(DataIndex.Find(ref cache, items, i => i.Id, "c"));
    }

    [Fact]
    public void First_item_with_a_duplicate_id_wins_like_FirstOrDefault()
    {
        var items = new List<Item> { new() { Id = "x", Tag = 1 }, new() { Id = "x", Tag = 2 } };
        Dictionary<string, Item>? cache = null;
        Assert.Equal(1, DataIndex.Find(ref cache, items, i => i.Id, "x")!.Tag);
    }

    [Fact]
    public void Null_or_missing_ids_find_nothing()
    {
        var items = new List<Item> { new() { Id = null, Tag = 1 }, new() { Id = "a", Tag = 2 } };
        Dictionary<string, Item>? cache = null;
        Assert.Null(DataIndex.Find(ref cache, items, i => i.Id, null));
        Assert.Null(DataIndex.Find(ref cache, items, i => i.Id, "zzz"));
        Assert.Equal(2, DataIndex.Find(ref cache, items, i => i.Id, "a")!.Tag);
    }
}
