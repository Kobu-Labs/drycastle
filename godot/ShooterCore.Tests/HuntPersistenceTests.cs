using Drycastle.ShooterCore;
using Drycastle.ShooterServer.Persistence;
using Drycastle.ShooterServer.Persistence.InMemory;
using Drycastle.ShooterServer.Persistence.Sqlite;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The Hunter's Log store (#336): one contract on the in-memory and SQLite stores (and Postgres when SHOOTER_TEST_PG is set).</summary>
public class HuntPersistenceTests
{
    public static IEnumerable<object[]> Backends => TestPostgres.Backends();

    static readonly List<string> Logged = new();
    static readonly LevelCurve Curve = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data")).Levels.Curve;
    static readonly DateTime T0 = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    static string TempDb() => Path.Combine(Path.GetTempPath(), $"shooter-test-{Guid.NewGuid():N}", "shooter.db");

    static IProgressionStore Create(string backend, string? path = null) => backend switch
    {
        "memory" => new InMemoryProgressionStore(Curve),
        "postgres" => TestPostgres.Open(Curve, Logged.Add),
        _ => SqliteProgressionStore.Open(path ?? TempDb(), Logged.Add, Curve),
    };

    static async Task<long> Player(IProgressionStore s, string token, string name)
    {
        var p = await s.Profiles.GetOrCreateAsync(token);
        await s.Profiles.SetNameAsync(p.Id, name);
        return p.Id;
    }

    static HuntRecord Animal(long player, string animal, float kg, float? d, int minute, string weapon = "sniper", string variant = "") =>
        HuntRecord.ForAnimal(player, animal, variant, kg, d, weapon, new HitDiagram(100, 120, true, false, 3, 7), 1.5f, 2.5f, 3.5f, T0.AddMinutes(minute))!;

    static HuntQuery Q(int page = 0, IReadOnlyList<string>? species = null, string weapon = "", long player = 0, HuntSort sort = HuntSort.Time, bool desc = true, params string[] hidden) =>
        new(page, species, weapon, player, sort, desc, hidden);

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task AHuntRoundTripsEveryFieldWithTheHuntersName(string backend)
    {
        using var store = Create(backend);
        var ann = await Player(store, "a", "Ann");
        var diagram = new HitDiagram(0, 255, false, true, 255, 0);
        var h = HuntRecord.ForAnimal(ann, "moose", "moose_male_02", 512.34f, 187.46f, "sniper", diagram, -10.25f, 3.5f, 99f, T0)!;
        await store.Hunts.AppendAsync(h);
        await store.Hunts.AppendAsync(HuntRecord.ForAnimal(ann, "boar", "", 90, 4, HuntWeapons.RunOver, null, 0, 0, 0, T0.AddMinutes(1))!);
        await store.Hunts.AppendAsync(HuntRecord.ForMystery(ann, Mysteries.Bigfoot, 1, 2, 3, T0.AddMinutes(2)));

        var all = await store.Hunts.GetAllAsync();
        Assert.Equal(3, all.Count);
        Assert.All(all, r => Assert.Equal("Ann", r.Hunter));
        Assert.True(all[0].Hunt.Id > 0);
        Assert.Equal(h with { Id = all[0].Hunt.Id }, all[0].Hunt);
        Assert.Equal(new[] { "moose", "boar", "bigfoot" }, all.Select(r => r.Hunt.Species));
        Assert.Null(all[1].Hunt.DistanceM);
        Assert.Null(all[1].Hunt.Diagram);
        Assert.Equal((Mysteries.Bigfoot, (float?)null, HuntWeapons.Event), (all[2].Hunt.Species, all[2].Hunt.Kg, all[2].Hunt.Weapon));
        Assert.Equal(DateTimeKind.Utc, all[0].Hunt.AtUtc.Kind);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task AHuntOfAnUnknownPlayerIsNotStoredAndNothingThrows(string backend)
    {
        using var store = Create(backend);
        await store.Hunts.AppendAsync(Animal(424242, "deer", 70, 10, 0));
        Assert.Empty(await store.Hunts.GetAllAsync());
        Assert.Empty((await store.Hunts.PageAsync(Q())).Rows);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task PagesFilterSortAndCountLikeTheRules(string backend)
    {
        using var store = Create(backend);
        var ann = await Player(store, "a", "Ann");
        var bea = await Player(store, "b", "bea");
        var rows = new List<HuntRecord>
        {
            Animal(bea, "deer", 80, 10, 0),
            Animal(ann, "stag", 200, 50, 1, "ak47"),
            Animal(bea, "doe", 90, null, 2, HuntWeapons.RunOver),
            HuntRecord.ForMystery(ann, Mysteries.Bigfoot, 0, 0, 0, T0.AddMinutes(3)),
            Animal(ann, "moose", 500, 120, 4, "sniper", "moose_male_01"),
        };
        foreach (var r in rows) await store.Hunts.AppendAsync(r);

        async Task<string> Order(HuntQuery q) => string.Join(",", (await store.Hunts.PageAsync(q)).Rows.Select(r => r.Hunt.Exact));
        Assert.Equal("moose,bigfoot,doe,stag,deer", await Order(Q()));
        Assert.Equal("deer,stag,doe,bigfoot,moose", await Order(Q(desc: false)));
        Assert.Equal("doe,deer", await Order(Q(species: new[] { "deer" })));
        Assert.Equal("doe", await Order(Q(weapon: HuntWeapons.RunOver)));
        Assert.Equal("moose,bigfoot,stag", await Order(Q(player: ann)));
        Assert.Equal("", await Order(Q(species: Array.Empty<string>())));
        Assert.Equal("bigfoot", await Order(Q(species: new[] { "bigfoot" })));
        Assert.Equal("moose,stag,doe,deer,bigfoot", await Order(Q(sort: HuntSort.Kg))); // the null weight last
        Assert.Equal("deer,doe,stag,moose,bigfoot", await Order(Q(sort: HuntSort.Kg, desc: false)));
        Assert.Equal("moose,stag,deer,bigfoot,doe", await Order(Q(sort: HuntSort.Distance))); // nulls last, ties newer first
        Assert.Equal("moose,bigfoot,stag,doe,deer", await Order(Q(sort: HuntSort.Hunter, desc: false))); // Ann before bea whatever the case, the newer hunt first within
        Assert.Equal("doe,deer,moose,stag,bigfoot", await Order(Q(sort: HuntSort.Species, desc: false, hidden: Mysteries.Bigfoot))); // hidden last
        var weaponOrder = (await store.Hunts.PageAsync(Q(sort: HuntSort.Weapon, desc: false))).Rows.Select(r => r.Hunt.Weapon).ToList();
        Assert.Equal(weaponOrder.OrderBy(w => w, StringComparer.Ordinal), weaponOrder);

        var page = await store.Hunts.PageAsync(Q());
        Assert.Equal(5, page.Total);
        var empty = await store.Hunts.PageAsync(Q(page: 1));
        Assert.Equal((5, 0), (empty.Total, empty.Rows.Count));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task APageIsFiftyHuntsAndTheLastPageTheRest(string backend)
    {
        using var store = Create(backend);
        var ann = await Player(store, "a", "Ann");
        for (var i = 0; i < 53; i++) await store.Hunts.AppendAsync(Animal(ann, "fox", 5 + i % 3, 10 + i, i));
        var p0 = await store.Hunts.PageAsync(Q());
        var p1 = await store.Hunts.PageAsync(Q(page: 1));
        Assert.Equal((53, 50, 3), (p0.Total, p0.Rows.Count, p1.Rows.Count));
        Assert.Empty(p0.Rows.Select(r => r.Hunt.Id).Intersect(p1.Rows.Select(r => r.Hunt.Id)));
        Assert.True(p0.Rows[0].Hunt.AtUtc > p1.Rows[0].Hunt.AtUtc);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task TheBookFromTheStoreEqualsTheBookBuiltLive(string backend)
    {
        using var store = Create(backend);
        var ann = await Player(store, "a", "Ann");
        var bea = await Player(store, "b", "Bea");
        var hunts = new[] { Animal(ann, "deer", 70, 40, 0), Animal(bea, "stag", 150, 90, 1), Animal(ann, "stag", 150, 20, 2), HuntRecord.ForMystery(bea, Mysteries.Bigfoot, 0, 0, 0, T0.AddMinutes(3)) };
        var live = new HuntBook();
        foreach (var h in hunts) { live.Add(h); await store.Hunts.AppendAsync(h); }
        var replay = HuntBook.From(await store.Hunts.GetAllAsync());
        var viewer = replay.CaughtBy(bea);
        Assert.Equal(live.Records(viewer).Select(r => (r.Species, r.Heaviest?.PlayerId, r.Longest?.PlayerId, r.First?.PlayerId)),
            replay.Records(viewer).Select(r => (r.Species, r.Heaviest?.PlayerId, r.Longest?.PlayerId, r.First?.PlayerId)));
        Assert.Equal("Ann", replay.NameOf(ann));
        Assert.Equal(2, replay.PlayerLog(ann, viewer)!.Species);
    }

    [Fact]
    public async Task SqliteHuntsSurviveReopeningTheDatabase()
    {
        var path = TempDb();
        long id;
        using (var store = SqliteProgressionStore.Open(path, Logged.Add, Curve))
        {
            id = await Player(store, "a", "Ann");
            await store.Hunts.AppendAsync(Animal(id, "wolf", 44.4f, 123.4f, 0));
        }
        using var again = SqliteProgressionStore.Open(path, Logged.Add, Curve);
        var rows = await again.Hunts.GetAllAsync();
        Assert.Equal((id, "wolf", 44.4f, 123.4f), (rows.Single().Hunt.PlayerId, rows.Single().Hunt.Species, rows.Single().Hunt.Kg!.Value, rows.Single().Hunt.DistanceM!.Value));
    }

    [Fact]
    public async Task SqliteAHuntOfAnUnknownPlayerLogsInsteadOfThrowing()
    {
        using var store = SqliteProgressionStore.Open(TempDb(), Logged.Add, Curve);
        Logged.Clear();
        await store.Hunts.AppendAsync(Animal(424242, "deer", 70, 10, 0));
        Assert.Contains(Logged, m => m.Contains("append hunt"));
    }
}
