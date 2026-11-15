using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The pole connector target and the electrocution (docs/events/thieves/07): the source with its cooldown and hazard roll, the Thief's Shocked, Falling, Down, GetUp and its second climb, hits during it, the fx codec. State machines run Strict.</summary>
public class PolesTests
{
    const float Dt = 1f / 30f;
    static readonly Vector3 Base = new(30, 0, 12), Top = new(30, 8, 12), Column = new(30, 0, 12.9f);

    static ThievesFile Cfg(Action<ThievesFile>? tune = null)
    {
        var f = ThievesFile.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "thieves.json")));
        f.Gang.Raid = new GangRaidCounts { GangBosses = 0, Enforcers = 0, Thieves = 1 };
        tune?.Invoke(f);
        return f;
    }

    sealed class FakeWorld : IGangWorld
    {
        public readonly List<GangPlayer> List = new();
        public IReadOnlyList<GangPlayer> Players => List;
        public void Hurt(ushort player, float damage, ushort memberHitId) { }
    }

    sealed class FakePoles : IPoles
    {
        public readonly List<PoleSpot> Spots = new() { new PoleSpot(0, Column, Base, Top) };
        public IReadOnlyList<PoleSpot> All() => Spots;
        public bool TryGet(int id, out PoleSpot pole) { pole = Spots.FirstOrDefault(p => p.Id == id); return Spots.Any(p => p.Id == id); }
    }

    sealed class Rig
    {
        public readonly ThievesFile Cfg;
        public readonly FakeWorld World = new();
        public readonly WorldEntityManager Entities;
        public readonly GangRoster Gangs;
        public readonly RaidRoster Raids;
        public readonly LootRegistry Registry = new();
        public readonly LootBook Book;
        public readonly FakePoles Poles = new();
        public readonly ConnectorSource Source;
        public readonly List<(GangMember, StealTarget)> Shocks = new();
        public float Clock;

        public Rig(Action<ThievesFile>? tune = null)
        {
            Cfg = PolesTests.Cfg(f => { f.Loot.Connector.ElectrocuteChance = 1f; f.Loot.Connector.StealSeconds = 3f; f.Loot.ClimbSpeed = 2f; tune?.Invoke(f); });
            var types = new WorldEntityRegistry();
            GangRules.Register(types);
            RaidVanRules.Register(types);
            LootRules.Register(types);
            Entities = new WorldEntityManager(types);
            Gangs = new GangRoster(Cfg.Gang, World, Entities, 7);
            Raids = new RaidRoster(Cfg, World, Entities, Gangs, 9);
            Book = new LootBook(Cfg.Loot, Registry, Entities, 3);
            Source = new ConnectorSource(Cfg.Loot.Connector, Poles, () => Clock);
            Registry.Register(Source);
            Raids.Loot = Book;
            Book.Shocked += (m, t) => Shocks.Add((m, t));
        }

        public Raid Launch()
        {
            var path = new RoadPath(new[] { new Vector2(-20, 0), new Vector2(60, 0) });
            var plan = new RaidPlan(null, new RoadPlace(0, 0), path.End, true, path.YawAt(path.Length), path, 1, 0, false);
            var crew = Cfg.Raids.CrewFor(Cfg.Gang.Raid, 1);
            return Raids.Launch(plan, new RoadPath(new[] { path.End, path.End + new Vector2(300, 0) }), new Vector3(path.Start.X, 0, path.Start.Y), crew, p => p + new Vector3(0, 0, 8), p => new Vector3(1000, 0, 0), null)!;
        }

        public void Step()
        {
            Clock += Dt;
            Gangs.Update();
            Entities.Tick(Dt);
            Raids.Tick(Dt);
            Gangs.ServeRoute((from, to) => new[] { from, to });
        }

        public void Run(float seconds) { for (var t = 0f; t < seconds; t += Dt) Step(); }

        public void RunUntil(Func<bool> done, float limit = 300) { for (var t = 0f; t < limit && !done(); t += Dt) Step(); Assert.True(done(), "gave up waiting"); }

        public GangMember ThiefOut(Raid raid)
        {
            RunUntil(() => raid.Gang.Members.Any(m => m.Kind == GangMemberKind.Thief));
            return raid.Gang.Members.First(m => m.Kind == GangMemberKind.Thief);
        }
    }

    // ---- data, wire ----

    [Fact]
    public void ShippedNumbersTheStateValuesAndTheShockEffectCodec()
    {
        var c = Cfg().Loot.Connector;
        Assert.Equal(0.05f, c.ElectrocuteChance);
        Assert.True(c.PoleCooldownS > 0 && c.ShockSeconds > 0 && c.DownSeconds > 0 && c.GetUpSeconds > 0 && c.BaseOffsetM > 0 && c.Weight > 0);
        var clips = Cfg().Gang.View.Clips;
        Assert.Equal(new[] { "", "", "", "" }, new[] { clips.KnockdownBegin, clips.KnockdownLoop, clips.KnockdownEnd, clips.StunLoop });
        Assert.Throws<DataFileException>(() => ThievesFile.Parse("""{"version":1,"loot":{"connector":{"electrocute_chance":1.5}}}"""));
        Assert.Throws<DataFileException>(() => ThievesFile.Parse("""{"version":1,"loot":{"connector":{"shock_seconds":0}}}"""));
        Assert.Throws<DataFileException>(() => ThievesFile.Parse("""{"version":1,"loot":{"connector":{"pole_cooldown_s":-1}}}"""));
        Assert.Throws<DataFileException>(() => ThievesFile.Parse("""{"version":1,"loot":{"connector":{"steal_seconds":0}}}"""));
        // the four states are values 19 to 22, at the end
        Assert.Equal(new byte[] { 19, 20, 21, 22 }, new[] { GangMemberState.Shocked, GangMemberState.Falling, GangMemberState.Down, GangMemberState.GetUp }.Select(s => (byte)s));
        Assert.Equal(GangMemberState.Down, GangRules.StateOf(21));
        Assert.Equal(GangMemberState.GetUp, GangRules.StateOf(22));
        Assert.Equal(GangMemberState.Idle, GangRules.StateOf(99));
        // the effect: the member's entity id and where it hangs, round trip
        var m = new ThievesFxMsg(ThievesFxKind.Shock, new Vector3(30.25f, 8.5f, -12f), 1234);
        var b = ThievesFxCodec.Encode(m);
        Assert.Equal(ThievesFxCodec.Bytes, b.Length);
        Assert.Equal(m, ThievesFxCodec.Decode(b));
        Assert.Equal(ThievesFxKind.Honk, ThievesFxCodec.Decode(ThievesFxCodec.Encode(new ThievesFxMsg(ThievesFxKind.Honk, Vector3.Zero, 1)))!.Value.Kind);
    }

    // ---- the source ----

    [Fact]
    public void ThePoleIsATargetWithAClimbAndAfterATakeItIsFreeAgainOnlyAfterTheCooldownWhileReturnChangesNothing()
    {
        var clock = 100f;
        var cfg = Cfg().Loot.Connector;
        cfg.PoleCooldownS = 50;
        var src = new ConnectorSource(cfg, new FakePoles(), () => clock);
        var found = new List<StealTarget>();
        src.FreeNear(Base, 20, found);
        var t = Assert.Single(found);
        Assert.Equal((LootKind.Connector, 0), (t.Kind, t.Id));
        Assert.True(t.NeedsClimb);
        Assert.Equal(Base, t.WorkSpot);
        Assert.Equal(Top, t.Top);
        found.Clear();
        src.FreeNear(new Vector3(500, 0, 0), 20, found);
        Assert.Empty(found); // out of the radius
        var places = new List<Vector2>();
        src.Places(places);
        Assert.Equal(new Vector2(Column.X, Column.Z), Assert.Single(places));
        src.Take(0); // nothing goes missing on the pole, but it is not a target for a while
        Assert.False(src.TryFind(0, out _));
        found.Clear(); places.Clear();
        src.FreeNear(Base, 20, found); src.Places(places);
        Assert.Empty(found); Assert.Empty(places);
        src.Return(0); // a connector sent back is just removed: the cooldown is not touched
        Assert.False(src.TryFind(0, out _));
        clock += 49;
        Assert.False(src.TryFind(0, out _));
        clock += 2;
        Assert.True(src.TryFind(0, out var again));
        Assert.Equal(t, again);
        src.Take(0);
        src.Reset(); // a new round clears it
        Assert.True(src.TryFind(0, out _));
        Assert.False(src.TryFind(7, out _)); // no such pole
    }

    [Fact]
    public void TheHazardRollFollowsTheChanceAndOnlyTheConnectorHasOne()
    {
        var cfg = Cfg().Loot.Connector;
        var src = new ConnectorSource(cfg, new FakePoles(), () => 0);
        var rng = new Random(5);
        cfg.ElectrocuteChance = 0;
        for (var i = 0; i < 500; i++) Assert.Null(src.AloftHazard(0, rng));
        cfg.ElectrocuteChance = 1;
        for (var i = 0; i < 500; i++) Assert.Equal(new AloftShock(cfg.ShockSeconds, cfg.DownSeconds, cfg.GetUpSeconds), src.AloftHazard(0, rng));
        cfg.ElectrocuteChance = 0.05f;
        var n = 0;
        for (var i = 0; i < 20000; i++) if (src.AloftHazard(0, rng) != null) n++;
        Assert.InRange(n, 700, 1300); // about 5 %
        // a kind that says nothing is untouched: the default
        IStealSource other = new TireSource(Cfg().Loot.Tire, new NoCars());
        Assert.Null(other.AloftHazard(1, rng));
    }

    sealed class NoCars : ITireCars
    {
        public IEnumerable<TireCar> Cars() => Array.Empty<TireCar>();
        public bool TryGet(int carId, out TireCar car) { car = default; return false; }
        public void SetWheel(int carId, int wheel, bool off) { }
        public void RestoreAll() { }
    }

    // ---- the electrocution ----

    [Fact]
    public void WithChanceOneTheThiefIsShockedFallsLiesGetsUpClimbsTheSamePoleAgainAndEndsOnTheGroundWithNoHitLost()
    {
        var rig = new Rig(f => { f.Loot.Connector.ShockSeconds = 1.5f; f.Loot.Connector.DownSeconds = 3f; f.Loot.Connector.GetUpSeconds = 1f; });
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        var states = new List<GangMemberState>();
        var fallY = new List<float>();
        float shockSeconds = 0, downSeconds = 0, getUpSeconds = 0;
        var hitsEverTaken = 0f;
        var shocksSeen = 0;
        rig.RunUntil(() =>
        {
            if (states.Count == 0 || states[^1] != thief.State) states.Add(thief.State);
            hitsEverTaken = MathF.Max(hitsEverTaken, thief.HitsTaken);
            switch (thief.State)
            {
                case GangMemberState.Shocked:
                    shockSeconds += Dt;
                    Assert.Equal(Top, thief.Position); // at the top
                    Assert.True(thief.Working && !thief.CanFight);
                    break;
                case GangMemberState.Falling: fallY.Add(thief.Position.Y); break;
                case GangMemberState.Down:
                    downSeconds += Dt;
                    Assert.Equal(0f, thief.Position.Y, 0.001f); // lying on the ground
                    Assert.Equal(Stance.Prone, thief.HitVolume.Stance);
                    break;
                case GangMemberState.GetUp: getUpSeconds += Dt; break;
                case GangMemberState.WorkAloft:
                    if (rig.Shocks.Count > shocksSeen) shocksSeen = rig.Shocks.Count;
                    break;
            }
            if (thief.State == GangMemberState.GetUp && getUpSeconds > 0.5f && rig.Cfg.Loot.Connector.ElectrocuteChance >= 1) rig.Cfg.Loot.Connector.ElectrocuteChance = 0; // once is enough: the second climb is clean
            return raid.Van.Load >= 1;
        });
        // order: up, shocked, falling, down, get up, up again, work, down, haul, load
        var core = states.Where(s => s is not (GangMemberState.Approach or GangMemberState.Seek or GangMemberState.Idle or GangMemberState.Walk or GangMemberState.Guard)).ToList();
        Assert.Equal(new[]
        {
            GangMemberState.ClimbUp, GangMemberState.Shocked, GangMemberState.Falling, GangMemberState.Down, GangMemberState.GetUp,
            GangMemberState.ClimbUp, GangMemberState.WorkAloft, GangMemberState.ClimbDown, GangMemberState.Haul, GangMemberState.Load,
        }, core);
        Assert.Equal(1.5f, shockSeconds, 0.15f);
        Assert.Equal(3f, downSeconds, 0.15f);
        Assert.Equal(1f, getUpSeconds, 0.15f);
        Assert.True(fallY.Count > 3 && fallY.Zip(fallY.Skip(1), (a, b) => b < a).All(x => x)); // falls, faster and faster
        Assert.True(fallY[0] <= 8f && fallY[^1] < 8f && fallY[^1] > 0f); // from the top towards the ground
        Assert.Equal(0f, hitsEverTaken); // costs no hits
        Assert.Equal(0f, thief.HitsTaken);
        Assert.Single(rig.Shocks); // the effect fired once, with the member and the pole
        Assert.Equal(rig.Shocks[0].Item1, thief);
        Assert.Equal(LootKind.Connector, rig.Shocks[0].Item2.Kind);
        Assert.Equal(1, raid.Van.Load);
        Assert.Equal(new LootOrigin(LootKind.Connector, 0), Assert.Single(raid.Loaded));
        Assert.Equal(0f, thief.Position.Y, 0.001f); // ends on the ground
        Assert.False(rig.Source.TryFind(0, out _)); // robbed: on cooldown
    }

    [Fact]
    public void WithChanceZeroTheThiefIsNeverShockedAndTheConnectorIsCarriedAndLoaded()
    {
        var rig = new Rig(f => f.Loot.Connector.ElectrocuteChance = 0f);
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        var seen = new HashSet<GangMemberState>();
        rig.RunUntil(() =>
        {
            seen.Add(thief.State);
            if (thief.State == GangMemberState.Haul) Assert.Equal(LootKinds.CarriedOf(LootKind.Connector), thief.Carried);
            return raid.Van.Load >= 1;
        });
        Assert.DoesNotContain(GangMemberState.Shocked, seen);
        Assert.DoesNotContain(GangMemberState.Falling, seen);
        Assert.DoesNotContain(GangMemberState.Down, seen);
        Assert.DoesNotContain(GangMemberState.GetUp, seen);
        Assert.Empty(rig.Shocks);
    }

    [Fact]
    public void AShotThiefWhileDownTakesNormalHitsAndOneKilledWhileShockedOrFallingFallsAndLiesDead()
    {
        // down: a hit counts
        var rig = new Rig(f => { f.Loot.Connector.DownSeconds = 30f; });
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        rig.RunUntil(() => thief.State == GangMemberState.Down);
        Assert.Equal(0f, thief.HitsTaken);
        Assert.False(thief.Hit(3, 1));
        Assert.Equal(1f, thief.HitsTaken);
        Assert.Equal(GangMemberState.Down, thief.State); // a hit does not end it
        Assert.Equal(1, rig.Registry.Claimed); // still its pole
        Assert.True(thief.Kill(3));
        Assert.Equal(GangMemberState.Dead, thief.State);
        Assert.Equal(0f, thief.Position.Y, 0.001f);
        Assert.Equal(0, rig.Registry.Claimed);
        Assert.True(rig.Source.TryFind(0, out _));

        // shocked: killed at the top, falls and lies at the base
        var rig2 = new Rig();
        var raid2 = rig2.Launch();
        var t2 = rig2.ThiefOut(raid2);
        rig2.RunUntil(() => t2.State == GangMemberState.Shocked);
        var killed = new List<GangKilled>();
        raid2.Gang.Killed += killed.Add;
        t2.Kill(4);
        Assert.Equal(Base, killed[0].Where);
        Assert.Equal(8f, t2.Position.Y, 0.01f);
        rig2.Run(0.2f);
        Assert.InRange(t2.Position.Y, 0.1f, 7.9f);
        rig2.Run(2f);
        Assert.Equal(0f, t2.Position.Y, 0.001f);
        Assert.Equal(GangMemberState.Dead, t2.State);

        // falling: killed on the way down it keeps falling
        var rig3 = new Rig();
        var raid3 = rig3.Launch();
        var t3 = rig3.ThiefOut(raid3);
        rig3.RunUntil(() => t3.State == GangMemberState.Falling);
        rig3.Run(0.3f);
        var y = t3.Position.Y;
        Assert.InRange(y, 0.1f, 7.9f);
        t3.Kill(4);
        rig3.Run(0.1f);
        Assert.True(t3.Position.Y < y);
        rig3.Run(2f);
        Assert.Equal(0f, t3.Position.Y, 0.001f);
    }

    [Fact]
    public void ACalledBackThiefIsOnTheGroundAtOnceWhateverTheShockStateAndAPoleThatWentSendsAGotUpThiefLooking()
    {
        var rig = new Rig();
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        rig.RunUntil(() => thief.State == GangMemberState.Shocked);
        thief.Board(raid.Van.DoorPoint);
        Assert.Equal(0f, thief.Position.Y);
        Assert.Equal(GangMemberState.Board, thief.State);
        Assert.Equal(0, rig.Registry.Claimed);

        var rig2 = new Rig(f => f.Loot.Connector.DownSeconds = 1f);
        var raid2 = rig2.Launch();
        var t2 = rig2.ThiefOut(raid2);
        rig2.RunUntil(() => t2.State == GangMemberState.Down);
        rig2.Poles.Spots.Clear(); // the pole is gone while it lies
        rig2.RunUntil(() => t2.State == GangMemberState.GetUp);
        rig2.RunUntil(() => t2.State == GangMemberState.Seek);
        Assert.Equal(0, rig2.Registry.Claimed);
        Assert.Equal(0f, t2.Position.Y, 0.001f);
    }

    [Fact]
    public void ARobbedPoleIsRobbedAgainAfterTheCooldown()
    {
        var rig = new Rig(f => { f.Loot.Connector.ElectrocuteChance = 0f; f.Loot.Connector.PoleCooldownS = 40f; f.Loot.Connector.StealSeconds = 1f; });
        var raid = rig.Launch();
        var thief = rig.ThiefOut(raid);
        rig.RunUntil(() => raid.Van.Load >= 1);
        var robbedAt = rig.Clock;
        Assert.False(rig.Source.TryFind(0, out _));
        rig.Run(20);
        Assert.Equal(1, raid.Van.Load); // nothing else to take meanwhile
        rig.RunUntil(() => raid.Van.Load >= 2, 120);
        Assert.InRange(rig.Clock - robbedAt, 38f, 60f); // not before the cooldown
        Assert.Equal(new LootOrigin(LootKind.Connector, 0), raid.Loaded[1]);
    }
}
