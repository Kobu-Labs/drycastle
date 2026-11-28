using System.Numerics;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Thieves;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Cartel;

/// <summary>An Assault van (docs/cartel.md "Assaults"): its shipped numbers against vehicles.json, who sits where, when it is stalled, and where it is put down.</summary>
public class AssaultCarTests
{
    const float Dt = 1f / 30f;
    static readonly GameData Game = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data"));
    static readonly AssaultCarData A = new();

    public AssaultCarTests() => StateMachineDiagnostics.Strict = true;

    [Fact]
    public void TheShippedAssaultCarIsTheDefaultsAndFitsTheCartelVanAndBothHeistsCrews()
    {
        var shipped = Game.Cartel.AssaultCar;
        Assert.Equal((A.VehicleType, A.Skin, A.GiveUpM, A.StallProgressM, A.StallSeconds, A.RemoveFarM), (shipped.VehicleType, shipped.Skin, shipped.GiveUpM, shipped.StallProgressM, shipped.StallSeconds, shipped.RemoveFarM));
        Assert.Equal(A.SpawnBandsM, shipped.SpawnBandsM);
        Assert.Equal(A.BayPosts, shipped.BayPosts);
        Assert.Equal(new[] { 250f, 400f }, shipped.SpawnBandsM[0]);
        var van = Game.Vehicles.Vehicles.Single(v => v.Id == shipped.VehicleType);
        Assert.True(shipped.Skin > 0 && shipped.Skin < van.Skins.Count, "a skin of its own besides the player's white one");
        Assert.EndsWith("_dark", van.SkinModel(shipped.Skin));
        Assert.EndsWith("_white", van.SkinModel(0));
        var bay = van.CargoVolume!;
        foreach (var p in shipped.BayPosts) // each post is on the bay's floor, inside it
        {
            Assert.InRange(p[0], bay.Centre[0] - bay.Size[0] / 2, bay.Centre[0] + bay.Size[0] / 2);
            Assert.InRange(p[2], bay.Centre[2] - bay.Size[2] / 2, bay.Centre[2] + bay.Size[2] / 2);
            Assert.Equal(bay.Centre[1] - bay.Size[1] / 2, p[1], 0.01f);
        }
        Assert.True(shipped.BayExitZ > van.HalfLength, "the exit is behind the van");
        foreach (var kind in new[] { HarvestData.KindId, TrapHouseHeistData.KindId })
        {
            var heist = DataFile.Section<HeistData>(Game.Cartel.JobSection(kind), $"jobs.{kind}");
            Assert.True(heist.MaxCrew <= van.Seats.Count + shipped.BayPosts.Length, $"{kind}: the crew fits the cab and the bay");
            Assert.Equal(new[] { 40f, 36f, 32f, 28f, 24f }, heist.AssaultIntervalSeconds); // 40% of the first 100 to 60
            for (var tier = 1; tier <= 5; tier++)
                foreach (var size in new[] { 1, 4 })
                {
                    var (g, e) = HeistRules.AssaultCrew(heist, tier, size);
                    Assert.True(g + e <= van.Seats.Count + shipped.BayPosts.Length);
                }
        }
        Assert.True(shipped.GiveUpM > shipped.SpawnBandsM[0][1], "the give-up distance is past the wanted spawn band (the chase car's 250 would fire at once)");
        Assert.Throws<DataFileException>(() => new AssaultCarData { SpawnBandsM = new[] { new[] { 250f, 400f }, new[] { 300f, 500f } } }.Validate("assault_car")); // a later band must be wider
        Assert.Throws<DataFileException>(() => new AssaultCarData { BayPosts = new[] { new[] { 0f, 1f } } }.Validate("assault_car"));
    }

    [Fact]
    public void TheDriverIsAnEnforcerThenTheGunmenTakeTheCabsOtherSeatAndTheBayAndASecondEnforcerRidesLast()
    {
        Assert.Equal(new[] { GangMemberKind.Enforcer, GangMemberKind.Gunman, GangMemberKind.Gunman, GangMemberKind.Gunman, GangMemberKind.Gunman }, AssaultRules.SeatOrder(4, 1));
        Assert.Equal(new[] { GangMemberKind.Enforcer, GangMemberKind.Gunman, GangMemberKind.Enforcer }, AssaultRules.SeatOrder(1, 2));
        Assert.Equal(new[] { GangMemberKind.Gunman, GangMemberKind.Gunman }, AssaultRules.SeatOrder(2, 0)); // no melee member: a Gunman drives
    }

    static AssaultStall Watched(out float gap)
    {
        var s = new AssaultStall(A);
        gap = 300f;
        Assert.False(s.Step(true, true, gap, Dt));
        Assert.Equal(AssaultStallState.Watching, s.State);
        return s;
    }

    [Fact]
    public void AnAssaultThatGetsNoTenMetresCloserInTenSecondsIsStalledOnceAndNoLongerCounts()
    {
        var s = Watched(out var gap);
        var stalled = 0;
        for (var t = 0f; t < 9.9f; t += Dt) if (s.Step(true, true, gap, Dt)) stalled++; // a fence in the way: it stands
        Assert.Equal(0, stalled);
        Assert.True(s.Counts);
        for (var t = 0f; t < 0.5f; t += Dt) if (s.Step(true, true, gap, Dt)) stalled++;
        Assert.Equal(1, stalled);
        Assert.Equal(AssaultStallState.Stalled, s.State);
        Assert.False(s.Counts);
        for (var t = 0f; t < 20f; t += Dt) Assert.False(s.Step(true, false, gap, Dt)); // stays stalled once a newer Assault is the watched one
        Assert.Equal(AssaultStallState.Stalled, s.State);
        Assert.False(s.Step(false, false, gap, Dt)); // the crew got out: it fights on foot and counts again
        Assert.Equal((AssaultStallState.Idle, true), (s.State, s.Counts));
    }

    [Fact]
    public void HeadwayOrBeingOnTheVansTailKeepsTheWatchFromRunningOutAndOnlyTheNewestRidingOneIsWatched()
    {
        var s = Watched(out var gap);
        for (var t = 0f; t < 60f; t += Dt) { gap -= 3f * Dt; Assert.False(s.Step(true, true, gap, Dt)); } // 3 m/s: 30 m in 10 s
        Assert.Equal(AssaultStallState.Watching, s.State);
        for (var t = 0f; t < 60f; t += Dt) Assert.False(s.Step(true, true, 30f, Dt)); // within stall_near_m: close enough, whatever the van does
        Assert.Equal(AssaultStallState.Watching, s.State);
        Assert.False(s.Step(true, false, 300f, Dt)); // a newer Assault rides: this one is not watched
        Assert.Equal(AssaultStallState.Idle, s.State);
        var out_ = new AssaultStall(A);
        for (var t = 0f; t < 30f; t += Dt) Assert.False(out_.Step(false, true, 300f, Dt)); // the crew is out: never watched
        Assert.Equal(AssaultStallState.Idle, out_.State);
    }

    // ---- the spot ----

    // A straight road east along z = 0 from x = 0 to 3000 in 100 m legs.
    static RoadGraph Road()
    {
        var nodes = Enumerable.Range(0, 31).Select(i => new Vector2(i * 100f, 0)).ToArray();
        var edges = new List<(int, int, Vector2[])>();
        for (var i = 0; i < 30; i++) edges.Add((i, i + 1, new[] { nodes[i], nodes[i + 1] }));
        return RoadGraph.Make(nodes, edges);
    }

    static float? AlongTheRoad(Vector2 target, Vector2 p) => MathF.Abs(p.X - target.X) + MathF.Abs(p.Y); // the way: the road's metres plus the way off it

    [Fact]
    public void ASpotIsTwoFiftyToFourHundredMetresOfWayFromTheVanAndFarFromEveryPlayer()
    {
        var van = new Vector2(1500, 0);
        for (var seed = 1; seed <= 20; seed++)
        {
            var spot = AssaultSpawn.Pick(Road(), van, new[] { van, new Vector2(1500, 30) }, Array.Empty<Vector2>(), A, _ => false, null, p => AlongTheRoad(van, p), new Random(seed), out _)!.Value;
            Assert.Equal(0, spot.Band);
            Assert.InRange(spot.WayM, 250f, 400f);
            Assert.True(Vector2.Distance(spot.Ground, van) >= 150f);
            Assert.Equal(0f, spot.Ground.Y, 0.01f); // on the road
            Assert.Equal(RoadPath.Yaw(van - spot.Ground), spot.Yaw, 0.01f); // along it towards the van
        }
        // no way for the sight: a player standing on the near road side does not make it need to be out of sight; only distance and vehicles count
        var crowded = new[] { van, new Vector2(1200, 0), new Vector2(1800, 0) };
        var one = AssaultSpawn.Pick(Road(), van, crowded, Array.Empty<Vector2>(), A, _ => false, null, p => AlongTheRoad(van, p), new Random(1), out _)!.Value;
        Assert.All(crowded, pl => Assert.True(Vector2.Distance(one.Ground, pl) >= 150f));
        // a vehicle beside the spot or another Rival vehicle near it rules it out
        var apart = new[] { new Vector2(1200, 0) };
        for (var seed = 1; seed <= 10; seed++) Assert.True(Vector2.Distance(AssaultSpawn.Pick(Road(), van, new[] { van }, apart, A, p => p.X > 1700f, null, p => AlongTheRoad(van, p), new Random(seed), out _)!.Value.Ground, apart[0]) >= 25f);
    }

    [Fact]
    public void WhenNothingFitsTheBandItWidensRatherThanSendNoneAndSaysWhyWhenNothingFitsAtAll()
    {
        // the van is 1400 m of way from the nearest road point that is 150 m from every player: only the wider bands reach it
        var van = new Vector2(1500, 0);
        var nearPlayers = new List<Vector2> { van, new Vector2(1500, 10) };
        // block everything within 450 m of the van with a vehicle: the first band (250 to 400) and the second (to 600) have road left only past 450
        var spot = AssaultSpawn.Pick(Road(), van, nearPlayers, Array.Empty<Vector2>(), A, p => Vector2.Distance(p, van) < 450f, null, p => AlongTheRoad(van, p), new Random(2), out _);
        Assert.NotNull(spot);
        Assert.Equal(1, spot!.Value.Band); // the second band: 200 to 600
        Assert.InRange(spot.Value.WayM, 450f, 600f);
        Assert.Null(AssaultSpawn.Pick(Road(), van, nearPlayers, Array.Empty<Vector2>(), A, _ => true, null, p => AlongTheRoad(van, p), new Random(1), out var why));
        Assert.Contains("clear of vehicles", why);
        Assert.Null(AssaultSpawn.Pick(Road(), van, nearPlayers, Array.Empty<Vector2>(), A, _ => false, null, _ => null, new Random(1), out why)); // no way from any
        Assert.Contains("none within", why);
    }

    [Fact]
    public void OpenGroundPointsAreCandidatesTooOnARingRoundTheVan()
    {
        var van = new Vector2(0, 0);
        var spot = AssaultSpawn.Pick(RoadGraph.Empty, van, new[] { van }, Array.Empty<Vector2>(), A, _ => false, _ => true, p => Vector2.Distance(p, van), new Random(4), out _)!.Value;
        Assert.Null(spot.Place);
        Assert.InRange(spot.WayM, 250f, 400f);
        Assert.Equal(RoadPath.Yaw(van - spot.Ground), spot.Yaw, 0.01f);
    }
}
