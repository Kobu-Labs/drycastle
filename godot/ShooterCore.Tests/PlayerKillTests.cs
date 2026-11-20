using Drycastle.ShooterCore;
using Drycastle.ShooterServer.Persistence;
using Drycastle.ShooterServer.Persistence.InMemory;
using Drycastle.ShooterServer.Persistence.Sqlite;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The longest player kill of the Hunter's Log (#430): the rules, the wire and the store.</summary>
public class PlayerKillTests
{
    static readonly DateTime T0 = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
    static readonly LevelCurve Curve = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data")).Levels.Curve;
    static readonly List<string> Logged = new();

    static PlayerKillRecord Kill(long killer, long victim, float m, int minute, string weapon = "sniper") =>
        PlayerKillRecord.For(killer, victim, m, weapon, 1, 2, 3, T0.AddMinutes(minute))!;

    [Fact]
    public void ABetterKillBeatsAndATieKeepsTheEarlierOne()
    {
        var book = new PlayerKillBook();
        Assert.Equal(new PlayerKillOutcome(true, true), book.Add(Kill(1, 9, 100, 0)));
        Assert.Equal(new PlayerKillOutcome(false, false), book.Add(Kill(1, 8, 60, 1)));
        Assert.Equal(new PlayerKillOutcome(false, false), book.Add(Kill(1, 8, 100, 2))); // a tie keeps the earlier
        Assert.Equal(9, book.Of(1)!.VictimId);
        Assert.Equal(new PlayerKillOutcome(true, false), book.Add(Kill(2, 9, 99.9f, 3))); // own best, not the server's
        Assert.Equal(new PlayerKillOutcome(true, false), book.Add(Kill(3, 9, 100, 4))); // ties the server: the first holds it
        Assert.Equal(1, book.Server!.KillerId);
        Assert.Equal(new PlayerKillOutcome(true, true), book.Add(Kill(1, 7, 100.1f, 5)));
        Assert.Equal(7, book.Of(1)!.VictimId);
        Assert.Null(book.Of(42));
    }

    [Fact]
    public void AKillOfNoPlayerOrWithoutADistanceCountsForNothing()
    {
        Assert.Null(PlayerKillRecord.For(0, 2, 50, "sniper", 0, 0, 0, T0)); // a guest, a bot or a sandbox player kills
        Assert.Null(PlayerKillRecord.For(1, 0, 50, "sniper", 0, 0, 0, T0)); // and is killed
        Assert.Null(PlayerKillRecord.For(1, 1, 50, "sniper", 0, 0, 0, T0)); // a suicide
        Assert.Null(PlayerKillRecord.For(1, 2, null, "sniper", 0, 0, 0, T0)); // a grenade, a vehicle, a blade: no distance
        Assert.Null(PlayerKillRecord.For(1, 2, 0.04f, "sniper", 0, 0, 0, T0));
        Assert.Null(PlayerKillRecord.For(1, 2, 50, "", 0, 0, 0, T0));
        Assert.Equal(123.5f, PlayerKillRecord.For(1, 2, 123.46f, "sniper", 0, 0, 0, T0)!.DistanceM);
    }

    [Fact]
    public void ReplayingTheStoredBestsBuildsTheSameBookWithTheNames()
    {
        var rows = new[]
        {
            new PlayerKillRow(Kill(1, 2, 80, 0), "Ann", "Bob"),
            new PlayerKillRow(Kill(2, 1, 150, 1), "Bob", "Ann"),
            new PlayerKillRow(Kill(3, 1, 150, 2), "Cyd", "Ann"), // ties Bob's, which is earlier
        };
        var book = PlayerKillBook.From(rows);
        Assert.Equal(("Bob", "Ann", 150f), (book.Server!.Killer, book.Server.Victim, book.Server.DistanceM));
        Assert.Equal("Cyd", book.Of(3)!.Killer);
        Assert.Null(new PlayerKillBook().Server);
    }

    [Fact]
    public void TheViewsRoundTripOnTheWire()
    {
        var kill = new PlayerKillView(12, "Anička", 13, "Bob", 187.5f, "sniper", 1_780_000_000);
        var rec = new RecordsAnswer(5, HuntStatus.Ok, 3, new[] { new SpeciesRecordView("wolf", false, false, null, null, null) }, kill);
        var back = HuntEvents.DecodeRecords(HuntEvents.EncodeRecords(rec))!;
        Assert.Equal(kill, back.Kill);
        Assert.Null(HuntEvents.DecodeRecords(HuntEvents.EncodeRecords(rec with { Kill = null }))!.Kill);
        Assert.Null(HuntEvents.DecodeRecords(HuntEvents.EncodeRecords(rec)[..^1]));

        var log = new PlayerLogView(12, "Anička", 0, 0, null, null, Array.Empty<LifeEntry>(), kill);
        Assert.Equal(kill, HuntEvents.DecodePlayerLog(HuntEvents.EncodePlayerLog(new PlayerLogAnswer(6, HuntStatus.Ok, 12, log)))!.Log!.Kill);
        Assert.Null(HuntEvents.DecodePlayerLog(HuntEvents.EncodePlayerLog(new PlayerLogAnswer(6, HuntStatus.Ok, 12, log with { Kill = null })))!.Log!.Kill);
    }

    [Fact]
    public void AServerRecordKillIsAnnouncedAsItsOwnLine()
    {
        var view = new HuntView(12, "Anička", "", "", "", null, 187.5f, "sniper", null, 1_780_000_000, false);
        var m = new HuntAnnounceMsg(HuntAnnounceFlags.PlayerKill, view);
        Assert.Equal(m, HuntEvents.DecodeAnnounce(HuntEvents.EncodeAnnounce(m)));
        var line = HuntLine.Of(m, 12);
        Assert.Equal((HuntLineKind.RecordPlayerKill, true, true), (line.Kind, line.Mine, line.IsRecord));
        Assert.False(HuntLine.Of(m, 5).Mine);
    }

    static IProgressionStore Create(bool sqlite) => sqlite
        ? SqliteProgressionStore.Open(Path.Combine(Path.GetTempPath(), $"shooter-test-{Guid.NewGuid():N}", "shooter.db"), Logged.Add, Curve)
        : new InMemoryProgressionStore(Curve);

    static async Task<long> Player(IProgressionStore s, string token, string name)
    {
        var p = await s.Profiles.GetOrCreateAsync(token);
        await s.Profiles.SetNameAsync(p.Id, name);
        return p.Id;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheStoreKeepsOneRowPerKillerAndOnlyALongerKill(bool sqlite)
    {
        using var store = Create(sqlite);
        var ann = await Player(store, "a", "Ann");
        var bob = await Player(store, "b", "Bob");
        await store.Hunts.SavePlayerKillAsync(Kill(ann, bob, 100, 0));
        await store.Hunts.SavePlayerKillAsync(Kill(ann, bob, 60, 1, "ak47")); // shorter: kept out
        await store.Hunts.SavePlayerKillAsync(Kill(ann, bob, 100, 2, "ak47")); // a tie: kept out
        await store.Hunts.SavePlayerKillAsync(Kill(bob, ann, 80, 3));
        var rows = await store.Hunts.GetPlayerKillsAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(new PlayerKillRow(Kill(ann, bob, 100, 0), "Ann", "Bob"), rows[0]);
        await store.Hunts.SavePlayerKillAsync(Kill(ann, bob, 100.1f, 4, "ak47"));
        var longer = (await store.Hunts.GetPlayerKillsAsync()).Single(r => r.Kill.KillerId == ann);
        Assert.Equal((100.1f, "ak47"), (longer.Kill.DistanceM, longer.Kill.Weapon));
        Assert.Equal(PlayerKillBook.From(await store.Hunts.GetPlayerKillsAsync()).Server!.Killer, "Ann");
    }

    [Fact]
    public async Task SqliteKillsSurviveReopeningTheDatabase()
    {
        var path = Path.Combine(Path.GetTempPath(), $"shooter-test-{Guid.NewGuid():N}", "shooter.db");
        long ann, bob;
        using (var store = SqliteProgressionStore.Open(path, Logged.Add, Curve))
        {
            ann = await Player(store, "a", "Ann");
            bob = await Player(store, "b", "Bob");
            await store.Hunts.SavePlayerKillAsync(Kill(ann, bob, 123.4f, 0));
        }
        using var again = SqliteProgressionStore.Open(path, Logged.Add, Curve);
        Assert.Equal(new PlayerKillRow(Kill(ann, bob, 123.4f, 0), "Ann", "Bob"), (await again.Hunts.GetPlayerKillsAsync()).Single());
    }
}
