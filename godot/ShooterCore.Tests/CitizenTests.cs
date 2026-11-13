using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Citizens of the Holy Mass: church.json, the walk to a seat, one-hit death, the crowd's seat and stagger rules, the chat command and the data file.</summary>
public class CitizenTests
{
    const float Dt = 0.05f;

    static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "fixtures");
    static readonly Church Church = Church.Load(Path.Combine(FixtureDir, "church.json"));
    static readonly HolyMassFile Shipped = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data")).HolyMass;

    static CitizensConfig Cfg(int max = 60) => new() { MaxCount = max, WalkSpeed = 2f, SpeedJitter = 0, CorpseSeconds = 5, SeatHeightM = 0.45f };

    sealed class FakeWorld : ICitizenWorld
    {
        public List<Vector3> Doors = new() { new Vector3(-30, 0, 20), new Vector3(40, 0, 10) };
        public bool NoRoute;
        public float? LastRadius;
        public IReadOnlyList<Vector3> HouseDoors(Vector3 around, float minM, float radiusM) { LastRadius = radiusM; return Doors; }
        public IReadOnlyList<Vector3> Route(Vector3 from, Vector3 to) => NoRoute ? Array.Empty<Vector3>() : new[] { from, to };
    }

    static (CitizenCrowd Crowd, WorldEntityManager Entities, FakeWorld World) Make(int max = 60)
    {
        var types = new WorldEntityRegistry();
        CitizenRules.Register(types);
        var entities = new WorldEntityManager(types);
        var world = new FakeWorld();
        return (new CitizenCrowd(Cfg(max), Church, world, entities, 1), entities, world);
    }

    static void Run(CitizenCrowd crowd, WorldEntityManager entities, float seconds)
    {
        for (var t = 0f; t < seconds; t += Dt) { crowd.Update(Dt); entities.Tick(Dt); }
    }

    // ---- church.json ----

    [Fact]
    public void ChurchConvertsMapMetresToGodotAxes()
    {
        var seat = Church.Seat(0)!;
        Assert.Equal(new Vector3(-2, 0.45f, -8), seat.Pos); // x east, y north -> -z, z up -> y
        Assert.Equal(new Vector3(0, 0, -4), seat.Path[0]);
        Assert.Equal(MathF.PI * -1f, Church.Seat(3)!.Yaw, 4); // bearing 180 = facing south = Godot yaw -pi
        Assert.Equal(1, Church.Seat(3)!.Floor);
    }

    [Fact]
    public void ASeatWithoutAPathWalksTheAisle()
    {
        var route = Church.RouteTo(Church.Seat(2)!, 0.45f);
        Assert.Equal(Church.DoorPos, route[0]);
        Assert.Equal(Church.Aisle, route.Skip(1).Take(2));
        Assert.Equal(0f, route[^1].Y, 4); // ends on the floor under the seat
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"door":{"x":0,"y":0,"z":0},"seats":[]}""")]
    [InlineData("""{"door":{"x":0,"y":0,"z":0},"seats":[{"id":1,"pos":[0,0,0]},{"id":1,"pos":[1,0,0]}]}""")]
    [InlineData("""{"door":{"x":0,"y":0,"z":0},"seats":[{"id":1,"pos":[0,0]}]}""")]
    [InlineData("""{"door":{"x":0,"y":0,"z":0},"seats":[{"id":1,"pos":[0,0,0],"floor":2}]}""")]
    public void ABrokenChurchIsRejected(string json) => Assert.Throws<DataFileException>(() => Church.Parse(json));

    // ---- the state machine ----

    static Citizen Walker(Vector3? from = null) =>
        new(Cfg(), 0, 1f, new[] { from ?? new Vector3(0, 0, 20), Church.DoorPos }, Church.RouteTo(Church.Seat(0)!, 0.45f), 7);

    [Fact]
    public void WalksToTheDoorThenEntersThenSits()
    {
        var c = Walker();
        Assert.Equal(CitizenState.Walking, c.State);
        var seen = new List<CitizenState> { c.State };
        for (var t = 0f; t < 60 && !c.Seated; t += Dt) { c.Tick(Dt); if (seen[^1] != c.State) seen.Add(c.State); }
        Assert.Equal(new[] { CitizenState.Walking, CitizenState.Entering, CitizenState.Seated }, seen);
        Assert.Equal(Church.RouteTo(Church.Seat(0)!, 0.45f)[^1], c.Position);
        Assert.Equal(1f, c.Yaw);
        Assert.Equal(Stance.Crouch, c.HitStance);
        c.Tick(10);
        Assert.Equal(CitizenState.Seated, c.State); // stays seated
    }

    [Fact]
    public void WalkingSpeedIsTheConfigured()
    {
        var c = Walker(new Vector3(0, 0, 50));
        c.Tick(1f);
        Assert.Equal(2f, Vector3.Distance(c.Position, new Vector3(0, 0, 50)), 3);
        Assert.Equal(0f, c.Yaw, 3); // faces -Z, towards the door at z = 0
    }

    [Theory]
    [InlineData(0f)]    // walking
    [InlineData(10f)]   // entering
    [InlineData(100f)]  // seated
    public void AnyDamageKillsInOneHit(float walkedFor)
    {
        var c = Walker();
        for (var t = 0f; t < walkedFor; t += Dt) c.Tick(Dt);
        ushort? who = null;
        c.Died += (_, k) => who = k;
        c.OnDamage(0.5f, 9);
        Assert.Equal(CitizenState.Dead, c.State);
        Assert.Equal((ushort)9, who);
        Assert.Equal(0, c.Serialize().Hp);
        var at = c.Position;
        c.Tick(1f);
        Assert.Equal(at, c.Position); // the body stays
        c.OnDamage(5, 3);
        Assert.Equal((ushort)9, c.Killer); // a second hit changes nothing
    }

    [Fact]
    public void ACorpseIsRemovedAfterItsTime()
    {
        var c = Walker();
        c.OnDamage(1, 1);
        c.Tick(4.9f);
        Assert.Null(c.Ended);
        c.Tick(0.2f);
        Assert.Equal(WorldDespawnReason.Expired, c.Ended);
    }

    [Fact]
    public void TheWireStateCarriesLookStateAndPosition()
    {
        var c = Walker();
        var s = c.Serialize();
        Assert.Equal(CitizenRules.TypeId, c.TypeId);
        Assert.Equal((ushort)7, s.Owner);
        Assert.Equal((byte)CitizenState.Walking, s.State);
        Assert.Equal(1, s.Hp);
        Assert.Equal(CitizenState.Dead, CitizenRules.StateOf(200));
    }

    // ---- the crowd ----

    [Fact]
    public void ACrowdStaggersItsLeavingAndFillsOneSeatEach()
    {
        var (crowd, entities, _) = Make();
        Assert.Equal(3, crowd.Spawn(new[] { 0, 1, 3 }, 30, 100));
        Assert.Equal(3, crowd.Waiting);
        Run(crowd, entities, 0.1f);
        Assert.Equal(1, crowd.Count);             // the first leaves at once
        Run(crowd, entities, 10.2f);
        Assert.Equal(2, crowd.Count);             // the second at a third of the time
        Run(crowd, entities, 10);
        Assert.Equal(3, crowd.Count);
        Run(crowd, entities, 120);
        Assert.Equal(3, crowd.SeatedCount);
        Assert.Equal(3, entities.Count);
    }

    [Fact]
    public void ASeatTakesAtMostOneCitizen()
    {
        var (crowd, entities, _) = Make();
        Assert.Equal(2, crowd.Spawn(new[] { 0, 0, 1, 99 }, 0, 100)); // a repeat and an unknown seat are skipped
        Assert.Equal(0, crowd.Spawn(new[] { 0, 1 }, 0, 100));        // both are taken
        Run(crowd, entities, 1);
        Assert.Equal(0, crowd.Spawn(new[] { 0 }, 0, 100));           // and while they live
        Assert.Equal(new[] { 2, 3 }, crowd.FreeSeats.OrderBy(i => i));
    }

    [Fact]
    public void TheCapLimitsCitizensAliveOrWaiting()
    {
        var (crowd, _, _) = Make(max: 2);
        Assert.Equal(2, crowd.Spawn(4, 0, 100));
        Assert.Equal(0, crowd.Spawn(1, 0, 100));
    }

    [Fact]
    public void CountSpawnPicksFreeSeatsAndUsesTheGivenRadius()
    {
        var (crowd, _, world) = Make();
        Assert.Equal(4, crowd.Spawn(10, 5, 77));
        Assert.Equal(77f, world.LastRadius);
        Assert.Empty(crowd.FreeSeats);
    }

    [Fact]
    public void NoHouseOrNoRouteMeansNobodyLeavesAndTheSeatIsFree()
    {
        var (crowd, entities, world) = Make();
        world.Doors.Clear();
        Assert.Equal(0, crowd.Spawn(new[] { 0 }, 0, 100));
        world.Doors.Add(Vector3.Zero);
        world.NoRoute = true;
        Assert.Equal(1, crowd.Spawn(new[] { 0 }, 0, 100));
        Run(crowd, entities, 1);
        Assert.Equal(0, crowd.Count);
        Assert.Contains(0, crowd.FreeSeats);
    }

    [Fact]
    public void ClearRemovesEveryoneAndTheOnesStillWaiting()
    {
        var (crowd, entities, _) = Make();
        crowd.Spawn(4, 40, 100);
        Run(crowd, entities, 15);
        Assert.True(crowd.Count > 0 && crowd.Waiting > 0);
        crowd.Clear();
        Run(crowd, entities, 60);
        Assert.Equal(0, crowd.Count + crowd.Waiting + entities.Count);
        Assert.Equal(4, crowd.FreeSeats.Count());
    }

    [Fact]
    public void AHitKillsTellsWhoAndFreesTheSeatWhenTheBodyGoes()
    {
        var (crowd, entities, _) = Make();
        var killed = new List<CitizenKilled>();
        crowd.Killed += killed.Add;
        crowd.Spawn(new[] { 1 }, 0, 100);
        Run(crowd, entities, 2);
        var targets = new List<HitTarget>();
        crowd.AddTargets(targets);
        var t = Assert.Single(targets);
        Assert.True(CitizenRules.IsHitId(t.Id));
        Assert.Equal(DamageResult.Killed, crowd.Hit(t.Id, 5, 1));
        var k = Assert.Single(killed);
        Assert.Equal((ushort)5, k.Killer);
        Assert.Equal(1, k.SeatId);
        Assert.False(k.WasSeated);
        Assert.Equal(DamageResult.Ignored, crowd.Hit(t.Id, 5, 1));    // already dead
        Assert.Equal(DamageResult.Ignored, crowd.Hit(40001, 5, 1));    // not a citizen
        targets.Clear();
        crowd.AddTargets(targets);
        Assert.Empty(targets);                                          // the dead are not targets
        Assert.Equal(1, crowd.Count);                                   // but the body lies
        Assert.DoesNotContain(1, crowd.FreeSeats);
        Run(crowd, entities, 6);
        Assert.Equal(0, crowd.Count);
        Assert.Contains(1, crowd.FreeSeats);                            // no replacement; the seat is free for a later call
    }

    [Fact]
    public void AHitOnACitizenIsAbsorbedWhenNoRoundRuns()
    {
        var (crowd, entities, _) = Make();
        crowd.Spawn(new[] { 0 }, 0, 100);
        Run(crowd, entities, 1);
        var targets = new List<HitTarget>();
        crowd.AddTargets(targets);
        var match = new MatchSim(new MatchConfig(), () => 0, new List<Vector3> { Vector3.Zero }) { Citizens = crowd };
        Assert.Equal(DamageResult.Ignored, match.ApplyDamage(targets[0].Id, 1, 0, 50)); // no round running
    }

    // ---- chat ----

    [Fact]
    public void CitizensCommandTakesACountOrClear()
    {
        var admin = new ChatContext(true, Array.Empty<string>());
        Assert.Equal("20", ChatCommands.Parse("/citizens 20", admin).Args[0]);
        Assert.Equal("clear", ChatCommands.Parse("/citizens CLEAR", admin).Args[0]);
        Assert.False(ChatCommands.Parse("/citizens 0", admin).Ok);
        Assert.False(ChatCommands.Parse("/citizens 999", admin).Ok);
        Assert.False(ChatCommands.Parse("/citizens many", admin).Ok);
        Assert.False(ChatCommands.Parse("/citizens", admin).Ok);
        Assert.False(ChatCommands.Parse("/citizens 3", new ChatContext(false, Array.Empty<string>())).Ok);
        var hint = ChatCommands.Hint("/citizens ", admin);
        Assert.Equal("/citizens <count>", hint.Usage);
        Assert.Equal("clear", Assert.Single(hint.Suggestions).Insert);
    }

    // ---- data ----

    [Fact]
    public void TheShippedDataFileIsValidAndItsModelsAndClipsExist()
    {
        var c = Shipped.Citizens;
        Assert.True(c.MaxCount > 0 && c.WalkSpeed > 0 && c.StaggerSeconds > 0 && c.HouseRadiusM > c.HouseMinM && c.CorpseSeconds > 0);
        var assets = AssetsDir();
        foreach (var m in c.Models) Assert.True(File.Exists(Path.Combine(assets, m.File)), m.File);
    }

    [Fact]
    public void ABadDataFileIsRejected()
    {
        Assert.Throws<DataFileException>(() => HolyMassFile.Parse("""{"version":1,"citizens":{"max_count":0}}"""));
        Assert.Throws<DataFileException>(() => HolyMassFile.Parse("""{"version":1,"citizens":{"palette":["red"],"skin_tones":["#ffffff"],"models":[{"file":"a.glb","import_height":1}]}}"""));
        Assert.Throws<DataFileException>(() => HolyMassFile.Parse("""{"version":1,"citizens":{}}""")); // no models
    }

    static string AssetsDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "assets");
            if (File.Exists(Path.Combine(candidate, "manifest.json"))) return candidate;
        }
        throw new InvalidOperationException("assets/manifest.json not found above the test output folder");
    }
}
