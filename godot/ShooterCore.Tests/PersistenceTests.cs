using Suchohrad.ShooterCore;
using Suchohrad.ShooterServer.Persistence;
using Suchohrad.ShooterServer.Persistence.InMemory;
using Suchohrad.ShooterServer.Persistence.Sqlite;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>Shooter v2 persistence (ADR 0003). The same contract runs against the SQLite and in-memory stores.</summary>
public class PersistenceTests
{
    public static IEnumerable<object[]> Backends => new[] { new object[] { "memory" }, new object[] { "sqlite" } };

    static readonly List<string> Logged = new();

    static string TempDb() => Path.Combine(Path.GetTempPath(), $"shooter-test-{Guid.NewGuid():N}", "shooter.db");

    static IProgressionStore Create(string backend, string? path = null) =>
        backend == "memory" ? new InMemoryProgressionStore() : SqliteProgressionStore.Open(path ?? TempDb(), Logged.Add);

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task GetOrCreateReturnsTheSameProfileForTheSameToken(string backend)
    {
        using var store = Create(backend);
        var first = await store.Profiles.GetOrCreateAsync("token-a");
        var again = await store.Profiles.GetOrCreateAsync("token-a");
        var other = await store.Profiles.GetOrCreateAsync("token-b");

        Assert.Equal(first.Id, again.Id);
        Assert.NotEqual(first.Id, other.Id);
        Assert.Equal("token-a", first.Token);
        Assert.Equal("", first.Name);
        Assert.Equal(0, first.Xp);
        Assert.Equal(1, first.Level);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task NameAndXpPersistAndLevelFollowsXp(string backend)
    {
        using var store = Create(backend);
        var p = await store.Profiles.GetOrCreateAsync("t");
        await store.Profiles.SetNameAsync(p.Id, "Alice");
        await store.Profiles.AddXpAsync(p.Id, 60);
        await store.Profiles.AddXpAsync(p.Id, 60);

        var got = await store.Profiles.FindAsync(p.Id);
        Assert.NotNull(got);
        Assert.Equal("Alice", got!.Name);
        Assert.Equal(120, got.Xp);
        Assert.Equal(LevelCurve.LevelForXp(120), got.Level);
        Assert.Equal(2, got.Level);
        Assert.Null(await store.Profiles.FindAsync(999_999));
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = store.Profiles.AddXpAsync(p.Id, -1); });
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task NewPlayerHasFiveEmptyLoadoutsAndRoundTripsASave(string backend)
    {
        using var store = Create(backend);
        var p = await store.Profiles.GetOrCreateAsync("loadout-player");

        var empty = await store.Loadouts.GetAllAsync(p.Id);
        Assert.Equal(Loadout.SlotCount, empty.Count);
        Assert.Equal(Enumerable.Range(0, Loadout.SlotCount), empty.Select(l => l.Slot));
        Assert.All(empty, l => Assert.Equal(Loadout.Empty(l.Slot), l));

        var saved = new Loadout(2, "ak47", "pistol", "ghost", "uav", "", "");
        await store.Loadouts.SaveAsync(p.Id, saved);
        Assert.Equal(saved, (await store.Loadouts.GetAllAsync(p.Id))[2]);
        Assert.Equal(Loadout.Empty(0), (await store.Loadouts.GetAllAsync(p.Id))[0]);

        Assert.Equal(0, await store.Loadouts.GetActiveSlotAsync(p.Id));
        await store.Loadouts.SetActiveSlotAsync(p.Id, 4);
        Assert.Equal(4, await store.Loadouts.GetActiveSlotAsync(p.Id));
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = store.Loadouts.SetActiveSlotAsync(p.Id, 5); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = store.Loadouts.SaveAsync(p.Id, Loadout.Empty(7)); });
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task UnlocksAreIdempotent(string backend)
    {
        using var store = Create(backend);
        var p = await store.Profiles.GetOrCreateAsync("unlock-player");
        await store.Unlocks.GrantAsync(p.Id, new Unlock("weapon", "ak47"));
        await store.Unlocks.GrantAsync(p.Id, new Unlock("weapon", "ak47"));
        await store.Unlocks.GrantAsync(p.Id, new Unlock("perk", "ghost"));

        var all = await store.Unlocks.GetAllAsync(p.Id);
        Assert.Equal(new[] { new Unlock("perk", "ghost"), new Unlock("weapon", "ak47") }, all);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task StatsCountKillsDeathsMatchesAndPerWeapon(string backend)
    {
        using var store = Create(backend);
        var p = await store.Profiles.GetOrCreateAsync("stats-player");
        Assert.Equal(PlayerStats.Zero.Kills, (await store.Stats.GetAsync(p.Id)).Kills);

        await store.Stats.RecordKillAsync(p.Id, "ak47");
        await store.Stats.RecordKillAsync(p.Id, "ak47");
        await store.Stats.RecordKillAsync(p.Id, "pistol");
        await store.Stats.RecordDeathAsync(p.Id);
        await store.Stats.RecordMatchAsync(p.Id);

        var s = await store.Stats.GetAsync(p.Id);
        Assert.Equal(3, s.Kills);
        Assert.Equal(1, s.Deaths);
        Assert.Equal(1, s.Matches);
        Assert.Equal(2, s.WeaponKills["ak47"]);
        Assert.Equal(1, s.WeaponKills["pistol"]);
    }

    [Fact]
    public async Task SqliteDataSurvivesReopenAndMigrationsAreIdempotent()
    {
        var path = TempDb();
        long id;
        using (var store = SqliteProgressionStore.Open(path, Logged.Add))
        {
            id = (await store.Profiles.GetOrCreateAsync("durable")).Id;
            await store.Profiles.SetNameAsync(id, "Kept");
            await store.Loadouts.SaveAsync(id, new Loadout(0, "sniper", "pistol", "", "", "", ""));
        }

        // Second open runs the migration check again: nothing new should apply, data should be intact.
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            Assert.Equal(1L, (long)new Microsoft.Data.Sqlite.SqliteCommand("SELECT COUNT(*) FROM schema_migrations;", conn).ExecuteScalar()!);
        }
        using (var store = SqliteProgressionStore.Open(path, Logged.Add))
        {
            var p = await store.Profiles.GetOrCreateAsync("durable");
            Assert.Equal(id, p.Id);
            Assert.Equal("Kept", p.Name);
            Assert.Equal("sniper", (await store.Loadouts.GetAllAsync(id))[0].PrimaryWeapon);
        }
    }

    [Fact]
    public async Task SqliteFailedWriteIsLoggedAndDoesNotThrow()
    {
        using var store = SqliteProgressionStore.Open(TempDb(), Logged.Add);
        Logged.Clear();
        // No such player: the foreign key rejects the weapon_kills row. The write must log, not throw.
        await store.Stats.RecordKillAsync(424242, "ak47");
        var p = await store.Profiles.GetOrCreateAsync("still-works");
        Assert.True(p.Id > 0);
        Assert.Contains(Logged, m => m.Contains("record kill"));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99, 1)]
    [InlineData(100, 2)]
    [InlineData(400, 3)]
    public void LevelCurveIsMonotonicAndStartsAtOne(int xp, int level) =>
        Assert.Equal(level, LevelCurve.LevelForXp(xp));
}
