using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Mining;
using Drycastle.ShooterCore.Wildlife;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Mining;

public class MineralsTests
{
    [Fact]
    public void TheLogHoldsEightRealAndTwentyFantasyMineralsEachOnce()
    {
        Assert.Equal(28, Minerals.All.Count);
        Assert.Equal(8, Minerals.All.Count(m => !m.Fantasy));
        Assert.Equal(28, Minerals.All.Select(m => m.Id).Distinct().Count());
        Assert.NotNull(Minerals.Find("pheonixite"));
        Assert.Null(Minerals.Find("gold"));
    }

    [Fact]
    public void TheOrderIsAFixedShuffleOfThemAll()
    {
        Assert.Equal(Minerals.All.OrderBy(m => m.Id), Minerals.LogOrder.OrderBy(m => m.Id));
        Assert.NotEqual(Minerals.All, Minerals.LogOrder);
        // the two kinds are mixed: neither the first eight nor the last eight are all real-life stones
        Assert.Contains(Minerals.LogOrder.Take(8), m => m.Fantasy);
        Assert.Contains(Minerals.LogOrder.Skip(8), m => !m.Fantasy);
    }

    [Fact]
    public void ACardIsFoundWithItsPlaceAmongTheFinders()
    {
        var none = MineralTileModel.For(null);
        Assert.Equal(28, none.Count);
        Assert.All(none, t => Assert.False(t.Found));
        var some = MineralTileModel.For(new Dictionary<string, int> { ["opal"] = 7, ["micin"] = 1, ["agate"] = 0 });
        Assert.Equal(Minerals.LogOrder, some.Select(t => t.Mineral));
        Assert.Equal((true, 7), (some.Single(t => t.Mineral.Id == "opal").Found, some.Single(t => t.Mineral.Id == "opal").Rank));
        Assert.Equal(2, some.Count(t => t.Found));
    }

    [Fact]
    public void FoundLineNamesPlaceAndMineral() =>
        Assert.Equal("You are the 7th to find Zombite!", MineralFormat.FoundLine("You are the {0} to find {1}!", "Zombite", 7));

    [Fact]
    public void FoundLineFirstFinder() =>
        Assert.Equal("You are the 1st to find Zombite!", MineralFormat.FoundLine("You are the {0} to find {1}!", "Zombite", 1));

    [Theory]
    [InlineData(1, "1st"), InlineData(2, "2nd"), InlineData(3, "3rd"), InlineData(4, "4th"), InlineData(7, "7th"), InlineData(11, "11th"), InlineData(12, "12th"),
     InlineData(13, "13th"), InlineData(21, "21st"), InlineData(22, "22nd"), InlineData(103, "103rd"), InlineData(111, "111th")]
    public void OrdinalsReadAsEnglish(int n, string text) => Assert.Equal(text, MineralFormat.Ordinal(n));

    // ---- finding (#401) ----

    static readonly MineralNode Node = new(0, "zombite", new System.Numerics.Vector3(10, 0, 10));
    static readonly System.Numerics.Vector3 Near = new(11, 0, 10);

    [Fact]
    public void ThePlaceAmongTheFindersIsTheOrderOfFirstFinds()
    {
        var book = new MineralBook();
        Assert.Equal(1, book.Add(7, "zombite"));
        Assert.Equal(2, book.Add(9, "zombite"));
        Assert.Equal(1, book.Add(9, "opal")); // places are per mineral
        Assert.Equal(2, book.Finders("zombite"));
        Assert.Equal(1, book.RanksOf(7)["zombite"]);
        Assert.Equal(2, book.RanksOf(9)["zombite"]);
    }

    [Fact]
    public void AMineralIsFoundOncePerPlayerAndUnknownOnesAreIgnored()
    {
        var book = new MineralBook();
        Assert.Equal(1, book.Add(7, "zombite"));
        Assert.Equal(0, book.Add(7, "zombite"));
        Assert.Equal(1, book.Finders("zombite"));
        Assert.Equal(0, book.Add(7, "gold"));
        Assert.Empty(book.RanksOf(8));
    }

    [Fact]
    public void ReplayingTheStoredFindsGivesTheSamePlaces()
    {
        var t = DateTime.UtcNow;
        var book = MineralBook.From(new[] { new MineralFind(3, "opal", t), new MineralFind(5, "opal", t.AddSeconds(1)), new MineralFind(3, "agate", t.AddSeconds(2)) });
        Assert.Equal(1, book.RanksOf(3)["opal"]);
        Assert.Equal(2, book.RanksOf(5)["opal"]);
        Assert.Equal(1, book.RanksOf(3)["agate"]);
    }

    [Fact]
    public void ANodeCanBeTakenOnlyInReachWhileActiveAndNotFoundYet()
    {
        var book = new MineralBook();
        Assert.Equal(MineralPickResult.Ok, book.Check(1, Node, true, Near));
        Assert.Equal(MineralPickResult.UnknownNode, book.Check(1, null, true, Near));
        Assert.Equal(MineralPickResult.TooFar, book.Check(1, Node, true, new System.Numerics.Vector3(30, 0, 10)));
        Assert.Equal(MineralPickResult.Inactive, book.Check(1, Node, false, Near));
        book.Add(1, "zombite");
        Assert.Equal(MineralPickResult.AlreadyFound, book.Check(1, Node, true, Near)); // the node stays, the player cannot take it again
        Assert.Equal(MineralPickResult.Ok, book.Check(2, Node, true, Near)); // any number of players can take it while it is active
    }

    [Fact]
    public void AMineralWithNoRuleIsAlwaysActive() => Assert.True(MineralActivity.IsActive("zombite", default));

    [Fact]
    public void TheEventsRoundTrip()
    {
        Assert.True(MineralRules.TryDecodePick(MineralRules.EncodePick(3), out var id));
        Assert.Equal(3, id);
        var nodes = MineralRules.DecodeNodes(MineralRules.EncodeNodes(new[] { (Node, true), (new MineralNode(1, "opal", new System.Numerics.Vector3(1, 2, 3)), false) }))!;
        Assert.Equal(2, nodes.Count);
        Assert.Equal(Node, nodes[0].Node);
        Assert.True(nodes[0].Active);
        Assert.Equal("opal", nodes[1].Node.MineralId);
        Assert.False(nodes[1].Active);
        var finds = MineralRules.DecodeFinds(MineralRules.EncodeFinds(new Dictionary<string, int> { ["opal"] = 4, ["zombite"] = 1 }, (byte)MineralRules.IndexOf("opal")))!.Value;
        Assert.Equal(4, finds.Ranks["opal"]);
        Assert.Equal("opal", finds.JustFound);
        Assert.Null(MineralRules.DecodeFinds(MineralRules.EncodeFinds(new Dictionary<string, int>()))!.Value.JustFound);
        Assert.Null(MineralRules.DecodeNodes(new byte[] { 2, 0 }));
    }

    [Fact]
    public async Task TheStoresKeepFindsOnceAndReplayThemInOrder()
    {
        var curve = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data")).Levels.Curve;
        using var mem = new Drycastle.ShooterServer.Persistence.InMemory.InMemoryProgressionStore(curve);
        var path = Path.Combine(Path.GetTempPath(), $"shooter-test-{Guid.NewGuid():N}", "shooter.db");
        using var sql = Drycastle.ShooterServer.Persistence.Sqlite.SqliteProgressionStore.Open(path, _ => { }, curve);
        foreach (Drycastle.ShooterServer.Persistence.IProgressionStore store in new Drycastle.ShooterServer.Persistence.IProgressionStore[] { mem, sql })
        {
            var a = await store.Profiles.GetOrCreateAsync("a");
            var b = await store.Profiles.GetOrCreateAsync("b");
            var t = DateTime.UtcNow;
            await store.Minerals.AppendAsync(new MineralFind(a.Id, "zombite", t));
            await store.Minerals.AppendAsync(new MineralFind(b.Id, "zombite", t.AddSeconds(1)));
            await store.Minerals.AppendAsync(new MineralFind(a.Id, "zombite", t.AddSeconds(2))); // the same player and mineral again: not stored
            var all = await store.Minerals.GetAllAsync();
            Assert.Equal(new[] { a.Id, b.Id }, all.Select(f => f.PlayerId));
        }
    }
}

public class MineralDropTests
{
    static readonly MineralDrop Drop = new("pheonixite", new System.Numerics.Vector3(100, 5, 200), 7);

    [Fact]
    public void ADropBecomesADynamicNodeAnybodyWhoHasNotFoundItCanTakeUntilTakenAndTheKillerGetsNoPriority()
    {
        var set = new MineralNodeSet();
        var place = set.AddStatic("opal", new System.Numerics.Vector3(1, 0, 1))!;
        var node = set.AddDrop(Drop)!;
        Assert.True(node.Id >= MineralNodeSet.FirstDynamicId);
        Assert.Equal(place, set.Find(place.Id));
        Assert.Equal(node, set.Find(node.Id));
        Assert.Equal(new[] { place, node }, set.Nodes);
        var at = new System.Numerics.Vector3(101, 5, 201);
        var book = new MineralBook();
        const long killer = 1, other = 2;
        Assert.Equal(MineralPickResult.Ok, book.Check(other, node, MineralActivity.IsActive(node.MineralId, default), at)); // anybody
        Assert.Equal(MineralPickResult.Ok, book.Check(killer, node, true, at));
        Assert.Equal(MineralPickResult.TooFar, book.Check(other, node, true, at + new System.Numerics.Vector3(20, 0, 0)));
        Assert.Equal(1, book.Add(other, node.MineralId)); // the find lands in the Mineral Log with its place among the finders
        Assert.True(book.Has(other, "pheonixite"));
        Assert.Equal(MineralPickResult.AlreadyFound, book.Check(other, node, true, at));
        Assert.Equal(MineralPickResult.Ok, book.Check(killer, node, true, at)); // the node was not reserved or used up for the others
        Assert.Equal(2, book.Add(killer, node.MineralId));
        Assert.Same(node, set.Find(node.Id)); // a pickup does not use it up
        Assert.Empty(set.Expire(59.9));
        Assert.Equal(new[] { node.Id }, set.Expire(60)); // its minute is over: it burns away
        Assert.Null(set.Find(node.Id));
        Assert.Equal(MineralPickResult.UnknownNode, book.Check(3, set.Find(node.Id), true, at));
        Assert.False(set.Remove(place.Id)); // a static node never goes
        Assert.Contains(place, set.Nodes);
    }

    [Fact]
    public void TheRoundEndsTheDropsButNotThePlacesNodes()
    {
        var set = new MineralNodeSet();
        var place = set.AddStatic("opal", default)!;
        var a = set.AddDrop(Drop)!;
        var b = set.AddDrop(Drop)!;
        Assert.NotEqual(a.Id, b.Id);
        Assert.True(set.ClearDrops());
        Assert.Equal(new[] { place }, set.Nodes);
        Assert.False(set.ClearDrops());
        Assert.NotEqual(a.Id, set.AddDrop(Drop)!.Id); // ids are not reused at once
    }

    [Fact]
    public void ADynamicNodeRoundTripsInTheNodesPayload()
    {
        var set = new MineralNodeSet();
        var node = set.AddDrop(Drop)!;
        var back = MineralRules.DecodeNodes(MineralRules.EncodeNodes(new[] { (node, true) }))!;
        Assert.Equal((node, true), Assert.Single(back));
    }
}
