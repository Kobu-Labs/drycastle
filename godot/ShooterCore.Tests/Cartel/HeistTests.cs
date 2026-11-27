using System.Numerics;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Jobs;
using Drycastle.ShooterCore.Plantation;
using Drycastle.ShooterCore.Scenery;
using Drycastle.ShooterCore.Thieves;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Cartel;

/// <summary>The Heist template (#495, docs/cartel.md "Heists", "Harvest"): its phase, the Assault clock, the counts and bonus, the drop pick, the plantation's area, and cartel.json jobs.harvest.</summary>
public class HeistTests
{
    public HeistTests() => StateMachineDiagnostics.Strict = true;

    static readonly CartelFile Shipped = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data")).Cartel;
    static HarvestData ShippedHarvest() => DataFile.Section<HarvestData>(Shipped.JobSection("harvest"), "jobs.harvest");
    const float Dt = 1f / 30;

    [Fact]
    public void TheShippedHarvestSectionIsTheDefaultsAndChecksOut()
    {
        var shipped = ShippedHarvest();
        shipped.Validate("jobs.harvest", Shipped.Tiers.Count);
        var d = new HarvestData();
        Assert.Equal("Harvest", shipped.Name);
        Assert.Equal(d.Seconds, shipped.Seconds);
        Assert.Equal(d.MinDuffels, shipped.MinDuffels);
        Assert.Equal(d.MaxDuffels, shipped.MaxDuffels);
        Assert.Equal(d.GraceSeconds, shipped.GraceSeconds);
        Assert.Equal(d.AssaultIntervalSeconds, shipped.AssaultIntervalSeconds);
        Assert.Equal(d.AssaultGunmen, shipped.AssaultGunmen);
        Assert.Equal(d.AssaultEnforcers, shipped.AssaultEnforcers);
        Assert.Equal((d.BonusRespectPerDuffel, d.MaxCrew, d.MaxAssaultsAlive, d.VanType), (shipped.BonusRespectPerDuffel, shipped.MaxCrew, shipped.MaxAssaultsAlive, shipped.VanType));
        Assert.Equal((d.FillHoldSeconds, d.AreaMarginM, d.DropMinM, d.DropGraceSeconds), (shipped.FillHoldSeconds, shipped.AreaMarginM, shipped.DropMinM, shipped.DropGraceSeconds));
    }

    [Fact]
    public void ABadSectionIsRefusedWithTheFieldNamed()
    {
        var d = new HarvestData { MinDuffels = new[] { 1, 2 } };
        Assert.Contains("min_duffels", Assert.Throws<DataFileException>(() => d.Validate("jobs.harvest", 5)).Message);
        d = new HarvestData { MaxDuffels = new[] { 1, 1, 1, 1, 1 } };
        Assert.Contains("max_duffels is below", Assert.Throws<DataFileException>(() => d.Validate("jobs.harvest", 5)).Message);
        d = new HarvestData { MaxCrew = 16 };
        Assert.Contains("max_crew", Assert.Throws<DataFileException>(() => d.Validate("jobs.harvest", 5)).Message);
        d = new HarvestData { FillHoldSeconds = 0 };
        Assert.Contains("fill_hold_seconds", Assert.Throws<DataFileException>(() => d.Validate("jobs.harvest", 5)).Message);
        d = new HarvestData { AreaMarginM = float.NaN };
        Assert.Contains("area_margin_m", Assert.Throws<DataFileException>(() => d.Validate("jobs.harvest", 5)).Message);
    }

    [Fact]
    public void CountsGrowWithTheTierAndTheSquadAndStayWithinTheVan()
    {
        var d = new HarvestData();
        Assert.Equal(2, HeistRules.MinDuffels(d, 1, 1));
        Assert.Equal(5, HeistRules.MaxDuffels(d, 1, 1));
        Assert.Equal(4 + 3, HeistRules.MinDuffels(d, 5, 4)); // three extra members
        Assert.Equal(9 + 3, HeistRules.MaxDuffels(d, 5, 4));
        Assert.Equal(10, HeistRules.MaxDuffels(d, 5, 4, capacity: 10)); // never more than the bay takes
        Assert.Equal(4, HeistRules.MinDuffels(d, 5, 4, capacity: 4));
        Assert.Equal(2, HeistRules.MinDuffels(d, 0, 0)); // out of range is clamped
    }

    [Fact]
    public void TheBonusPaysEachDuffelAboveTheMinimumTimesTheTier()
    {
        var d = new HarvestData { BonusRespectPerDuffel = 15 };
        Assert.Equal(0, HeistRules.Bonus(d, 3, 4, 4));
        Assert.Equal(0, HeistRules.Bonus(d, 3, 2, 4));
        Assert.Equal(15 * 3 * 3, HeistRules.Bonus(d, 3, 7, 4));
    }

    [Fact]
    public void AnAssaultsCrewHasGunmenPerExtraMemberWithinTheMostAVanTakes()
    {
        var d = new HarvestData { AssaultGunmen = new[] { 1, 2, 2, 3, 3 }, AssaultEnforcers = new[] { 2, 2, 3, 3, 4 }, GunmenPerExtraMember = 1, MaxCrew = 8 };
        Assert.Equal((1, 2), HeistRules.AssaultCrew(d, 1, 1));
        Assert.Equal((5, 3), HeistRules.AssaultCrew(d, 4, 3));
        Assert.Equal((6, 2), HeistRules.AssaultCrew(d, 5, 4)); // 6 Gunmen, room for 2 of the 4 Enforcers
        var order = CrewRules.Order(2, 3);
        Assert.Equal(new[] { GangMemberKind.Gunman, GangMemberKind.Enforcer, GangMemberKind.Gunman, GangMemberKind.Enforcer, GangMemberKind.Enforcer }, order);
    }

    [Fact]
    public void OneCrewFormulaAndOneOrderServeEveryKind()
    {
        // the per-member terms both count: a Hit scales its Enforcers per mate too, an Assault and a house do not (0)
        Assert.Equal((4, 5), CrewRules.Split(1, 1, 2, 1, 4, 10));
        Assert.Equal((4, 2), CrewRules.Split(1, 1, 2, 0, 4, 10));
        Assert.Equal((3, 0), CrewRules.Split(5, 0, 2, 0, 1, 3)); // Gunmen first within the cap
        Assert.Equal((0, 0), CrewRules.Split(2, 1, 2, 1, 3, -1)); // no room at all
        // the ring of a Rival dealer's guards used its own interleave: the one order gives the same for every split
        for (var g = 0; g <= 6; g++)
            for (var e = 0; e <= 6; e++)
            {
                var ring = new List<GangMemberKind>();
                int gs = 0, es = 0;
                for (var i = 0; i < g + e; i++)
                {
                    var k = gs < g && (es >= e || gs <= es) ? GangMemberKind.Gunman : GangMemberKind.Enforcer;
                    if (k == GangMemberKind.Gunman) gs++; else es++;
                    ring.Add(k);
                }
                Assert.Equal(ring, CrewRules.Order(g, e));
            }
    }

    [Fact]
    public void ATiersNumberIsItsEntryAndTheListsAreChecked()
    {
        var a = new[] { 10, 20, 30 };
        Assert.Equal(10, CartelTiers.Of(a, 1));
        Assert.Equal(30, CartelTiers.Of(a, 3));
        Assert.Equal(30, CartelTiers.Of(a, 9)); // past the list: the last
        Assert.Equal(10, CartelTiers.Of(a, 0));
        CartelTiers.Check(a, 3, "x", CartelTiers.Count, "0 or more");
        Assert.Contains("one number per tier (5)", Assert.Throws<DataFileException>(() => CartelTiers.Check(a, 5, "x", CartelTiers.Count, "0 or more")).Message);
        Assert.Contains("every number must be more than 0", Assert.Throws<DataFileException>(() => CartelTiers.Check(new[] { 1f, 0f }, 2, "x", CartelTiers.Positive, "more than 0")).Message);
    }

    [Fact]
    public void TheAssaultClockStartsAtTheObjectiveThenComesEveryIntervalPausesAwayAndStops()
    {
        var c = new AssaultClock(10, 20);
        for (var i = 0; i < 300; i++) Assert.False(c.Tick(Dt, false)); // not at the objective yet
        Assert.Equal(AssaultClockState.Idle, c.State);
        var due = new List<float>();
        var t = 0f;
        for (; t < 55; t += Dt) if (c.Tick(Dt, true)) due.Add(t);
        Assert.Equal(new[] { 10f, 30f, 50f }, due.Select(x => MathF.Round(x)).ToArray());
        Assert.Equal(AssaultClockState.Waiting, c.State);
        var left = c.Left;
        for (var i = 0; i < 3000; i++) Assert.False(c.Tick(Dt, false)); // away: no Assault, the time left kept
        Assert.Equal(AssaultClockState.Paused, c.State);
        c.Tick(Dt, true);
        Assert.Equal(AssaultClockState.Waiting, c.State);
        Assert.Equal(left, c.Left, 0.01f);
        c.Stop();
        for (var i = 0; i < 3000; i++) Assert.False(c.Tick(Dt, true));
        Assert.Equal(3, c.Due);
    }

    static HeistTick Step(Heist h, bool inArea, bool inDrop, int loaded, bool destroyed = false, int received = 0, bool sent = false)
        => h.Step(new HeistFacts(destroyed, inArea, inDrop, loaded, received, sent), Dt);

    [Fact]
    public void AHeistGoesToTheObjectiveLoadsLeavesWithTheMinimumUnloadsAtTheDropAndIsDeliveredWhenTheReceiverIsSentOff()
    {
        var h = new Heist(min: 3, max: 6, graceSeconds: 1, intervalSeconds: 2, dropGraceSeconds: 1.5f) { Drop = ReceiverRules.Text(ReceiverKind.Helicopter) };
        Assert.Equal(JobStep.Running, Step(h, false, false, 0).Step);
        Assert.Equal(HeistPhase.ToObjective, h.Phase);
        Assert.Equal(HeistMarker.Objective, h.Marker);
        Assert.Equal("Drive the Cartel van to the plantation", h.Line("the plantation", "Fill"));
        Step(h, true, false, 0);
        Assert.Equal(HeistPhase.AtObjective, h.Phase);
        Assert.Equal(HeistMarker.Van, h.Marker);
        Assert.Equal("Fill: 0/3 loaded", h.Line("the plantation", "Fill"));
        var due = 0;
        for (var i = 0; i < 100; i++) if (Step(h, true, false, 1).AssaultDue) due++; // 3.3 s: at 1 s and 3 s
        Assert.Equal(2, due);
        Step(h, false, false, 2); // away short of the minimum: still at the objective, the Assaults go on
        Assert.Equal(HeistPhase.AtObjective, h.Phase);
        Assert.Equal(HeistMarker.Objective, h.Marker);
        Assert.StartsWith("Bring the van back", h.Line("the plantation", "Fill"));
        Assert.Equal(JobStep.Running, Step(h, false, true, 2).Step); // at the drop short of the minimum: nothing
        Assert.Equal(HeistPhase.AtObjective, h.Phase);
        Step(h, true, false, 3);
        Step(h, false, false, 4);
        Assert.Equal(HeistPhase.ToDrop, h.Phase);
        Assert.Equal(HeistMarker.Drop, h.Marker);
        Assert.Equal("Drive the van to the helicopter: 4 loaded", h.Line("the plantation", "Fill"));
        Assert.Equal(AssaultClockState.Paused, h.Assaults.State);
        for (var i = 0; i < 300; i++) Assert.False(Step(h, false, false, 4).AssaultDue); // no chase on the road
        Step(h, true, false, 4); // back at the objective: Assaults again
        Assert.Equal(HeistPhase.AtObjective, h.Phase);
        Step(h, false, false, 5);
        Assert.Equal(HeistPhase.ToDrop, h.Phase);

        Step(h, false, true, 5); // the van is in the drop's area
        Assert.Equal(HeistPhase.AtDrop, h.Phase);
        Assert.Equal(HeistMarker.Drop, h.Marker);
        Assert.Equal("Unload the duffels into the helicopter: 0/3", h.Line("the plantation", "Fill"));
        due = 0;
        for (var i = 0; i < 156; i++) if (Step(h, false, true, 5).AssaultDue) due++; // 5.2 s at the drop: 1.5 s grace, then every 2 s: 1.5 and 3.5
        Assert.Equal(2, due);
        Assert.Equal(AssaultClockState.DropWaiting, h.Assaults.State);
        Step(h, false, true, 2, received: 3);
        Assert.Equal("3 on board (up to 6): unload the rest, then tell the pilot to leave", h.Line("the plantation", "Fill"));
        Assert.Equal(JobStep.Running, Step(h, false, true, 1, received: 4).Step); // received, but not sent off yet: the heist goes on
        Assert.Equal(HeistPhase.AtDrop, h.Phase);
        Assert.Equal(JobStep.Succeeded, Step(h, false, true, 1, received: 4, sent: true).Step);
        Assert.Equal(HeistPhase.Delivered, h.Phase);
        Assert.Equal(4, h.Received); // what Respect counts
        Assert.Equal(JobStep.Succeeded, Step(h, false, false, 0, received: 0, sent: true).Step); // stays delivered, the count frozen
        Assert.Equal(4, h.Received);
        Assert.Equal("Delivered: 4 duffels", h.Line("the plantation", "Fill"));
        Assert.Equal(AssaultClockState.Over, h.Assaults.State);
    }

    [Fact]
    public void ASendOffWithFewerThanTheMinimumDoesNotDeliverAndOneAwayFromTheDropStillDoes()
    {
        var h = new Heist(3, 6, 1, 2);
        Step(h, true, false, 0);
        Assert.Equal(JobStep.Running, Step(h, true, false, 0, received: 2, sent: true).Step); // sent with 2 of 3: the Receiver never lets that happen, and the template does not believe it
        Assert.Equal(HeistPhase.AtObjective, h.Phase);
        // the leader walks the bags to the receiver while the van is still at the objective: the Receiver left with enough, the heist is done wherever the van is
        Assert.Equal(JobStep.Succeeded, Step(h, true, false, 0, received: 3, sent: true).Step);
        Assert.Equal(HeistPhase.Delivered, h.Phase);
        Assert.Equal(3, h.Received);
    }

    [Fact]
    public void TheVanLeavingTheDropAreaIsToDropAgainAndItsAssaultsPauseAndGoOnWhereTheyWere()
    {
        var h = new Heist(1, 4, 1, 2, dropGraceSeconds: 10);
        Step(h, true, false, 1); Step(h, false, false, 1); Step(h, false, true, 1);
        Assert.Equal(HeistPhase.AtDrop, h.Phase);
        for (var i = 0; i < 150; i++) Step(h, false, true, 1); // 5 s of the 10 s grace
        Assert.Equal(AssaultClockState.DropGrace, h.Assaults.State);
        var left = h.Assaults.Left;
        Step(h, false, false, 1);
        Assert.Equal(HeistPhase.ToDrop, h.Phase);
        Assert.Equal(AssaultClockState.Paused, h.Assaults.State);
        for (var i = 0; i < 300; i++) Assert.False(Step(h, false, false, 1).AssaultDue);
        Step(h, false, true, 1); // back
        Assert.Equal((HeistPhase.AtDrop, AssaultClockState.DropGrace), (h.Phase, h.Assaults.State));
        Assert.Equal(left, h.Assaults.Left, 0.1f); // the grace goes on with what it had
        var due = 0;
        for (var i = 0; i < 180; i++) if (Step(h, false, true, 1).AssaultDue) due++;
        Assert.Equal(1, due); // 6 s: the 5 left of the grace, then one more second of the interval
        Step(h, true, false, 1); // the van is back at the objective (the areas overlap, or it drove back): the objective's clock
        Assert.Equal(HeistPhase.AtObjective, h.Phase);
    }

    [Fact]
    public void ThePhaseChangedEventIsTheSeamForTheVanLeavingTheObjectiveAndReachingTheDrop()
    {
        var h = new Heist(2, 4, 1, 2);
        var seen = new List<(HeistPhase From, HeistPhase To)>();
        h.PhaseChanged += (a, b) => seen.Add((a, b));
        Step(h, true, false, 0);
        Step(h, false, false, 2);
        Step(h, false, true, 2);
        Step(h, false, true, 2, received: 2, sent: true);
        Assert.Equal(new[]
        {
            (HeistPhase.ToObjective, HeistPhase.AtObjective), (HeistPhase.AtObjective, HeistPhase.ToDrop), (HeistPhase.ToDrop, HeistPhase.AtDrop), (HeistPhase.AtDrop, HeistPhase.Delivered),
        }, seen);
        Assert.Equal(HeistPhase.AtDrop, h.Previous);
    }

    [Fact]
    public void TheAssaultClockComesToTheDropAfterTheDropGraceThenEveryIntervalPausesAwayAndStops()
    {
        var c = new AssaultClock(10, 20, dropGraceSeconds: 5);
        for (var i = 0; i < 60; i++) Assert.False(c.Tick(Dt, AssaultSite.Away)); // nothing before the van has come
        Assert.Equal(AssaultClockState.Idle, c.State);
        var t = 0f;
        for (; t < 12; t += Dt) c.Tick(Dt, AssaultSite.Objective); // the first at the objective, 10 s in
        Assert.Equal(1, c.Due);
        Assert.Equal(AssaultClockState.Waiting, c.State);
        c.Tick(Dt, AssaultSite.Away);
        Assert.Equal(AssaultClockState.Paused, c.State);
        c.Tick(Dt, AssaultSite.Drop); // from the road to the drop: its own clock, from the start
        Assert.Equal(AssaultClockState.DropGrace, c.State);
        Assert.Equal(5, c.Left, 0.1f);
        var due = new List<float>();
        for (t = 0; t < 52; t += Dt) if (c.Tick(Dt, AssaultSite.Drop)) due.Add(t);
        Assert.Equal(new[] { 5f, 25f, 45f }, due.Select(x => MathF.Round(x)).ToArray()); // 5 s grace, then every 20 s
        Assert.Equal(AssaultClockState.DropWaiting, c.State);
        var left = c.Left;
        for (var i = 0; i < 3000; i++) Assert.False(c.Tick(Dt, AssaultSite.Away));
        c.Tick(Dt, AssaultSite.Drop);
        Assert.Equal((AssaultClockState.DropWaiting, left), (c.State, c.Left));
        c.Tick(Dt, AssaultSite.Away);
        c.Tick(Dt, AssaultSite.Objective); // from the drop back to the objective: the interval, fresh
        Assert.Equal((AssaultClockState.Waiting, 20f), (c.State, MathF.Round(c.Left)));
        c.Stop();
        for (var i = 0; i < 3000; i++) Assert.False(c.Tick(Dt, AssaultSite.Drop));
        Assert.Equal(AssaultClockState.Over, c.State);
        Assert.Equal(4, c.Due); // one at the objective, three at the drop
    }

    [Fact]
    public void TheVanDestroyedLosesTheHeistFromAnyPhase()
    {
        var h = new Heist(1, 2, 1, 1);
        Assert.Equal(JobStep.Failed, Step(h, false, false, 0, destroyed: true).Step);
        Assert.Equal(HeistPhase.Lost, h.Phase);
        Assert.Equal(HeistMarker.None, h.Marker);
        Assert.Equal(AssaultClockState.Over, h.Assaults.State);
        h = new Heist(1, 2, 1, 1);
        Step(h, true, false, 0);
        Step(h, false, false, 1);
        Assert.Equal(HeistPhase.ToDrop, h.Phase);
        Assert.Equal(JobStep.Failed, Step(h, false, true, 1, destroyed: true).Step);
    }

    static CartelMap MapWithDrops(params Vector3[] drops)
    {
        var json = "{\"version\": 2, \"zones\": [{\"kind\": \"cartel_drop\", \"areas\": [\"Z-0001\"], \"area_m2\": 1000, \"points\": ["
            + string.Join(",", drops.Select(d => $"[{d.X},{-d.Z},{d.Y}]")) + "]}]}"; // the file's points are [x, north, height]
        return CartelMap.From(ZonePoints.Parse(json), null);
    }

    sealed class NoParties : IJobParties
    {
        public JobOwner OwnerOf(ushort p) => JobOwner.Solo(p);
        public ushort LeaderOf(JobOwner o) => o.Id;
        public IReadOnlyList<ushort> MembersOf(JobOwner o) => new[] { o.Id };
    }

    sealed class DropKind(CartelMap map, Vector3 from, float minM, float apartM) : IJobKind
    {
        public Vector3? Got;
        public string? Why;
        public string Id => "drop";
        public string Name => "Drop";
        public float Seconds(int tier) => 60;
        public IJobObjective? Begin(Job job, JobBook book, out string? refusal)
        {
            Got = CartelSpots.Claim(job, book, map, CartelZones.DropKind, new Random(1), from, "the objective", minM, apartM, out refusal);
            Why = refusal;
            return Got == null ? null : new Obj();
        }
        sealed class Obj : IJobObjective { public JobStep Step(Job job, float dt) => JobStep.Running; }
    }

    [Fact]
    public void ADropIsFarFromTheObjectiveApartFromOtherJobsAndNeverShared()
    {
        var map = MapWithDrops(new Vector3(10, 0, 0), new Vector3(500, 0, 0), new Vector3(520, 0, 0), new Vector3(0, 0, 900));
        var book = new JobBook();
        var kind = new DropKind(map, Vector3.Zero, 300, 40);
        book.Register(kind);
        var got = new List<Vector3>();
        for (ushort p = 1; p <= 3; p++)
        {
            var job = book.Start("cartel", kind, 1, JobOwner.Solo(p), p, new NoParties(), out _);
            if (job != null) got.Add(kind.Got!.Value);
        }
        Assert.Equal(2, got.Count); // (10,0,0) is too near, 500 and 520 are too close to share
        Assert.DoesNotContain(new Vector3(10, 0, 0), got);
        Assert.Contains(new Vector3(0, 0, 900), got);
        Assert.Equal("No drop point 300 m or more from the objective is free now: another job holds every one.", kind.Why);
        Assert.Equal(2, CartelSpots.Held(book, map).Count);
        var ended = new List<JobEnded>();
        book.FailAll(JobFailReason.RoundReset, new NoParties(), ended);
        Assert.Empty(CartelSpots.Held(book, map)); // released with the Jobs
    }

    [Fact]
    public void AHeistsDropRefusalSaysWhetherTheMapHasNoneNoneFarEnoughOrNoneFree()
    {
        string? Why(CartelMap map, float minM)
        {
            var book = new JobBook();
            var kind = new DropKind(map, Vector3.Zero, minM, 40);
            book.Register(kind);
            Assert.Null(book.Start("cartel", kind, 1, JobOwner.Solo(1), 1, new NoParties(), out var why));
            return why;
        }
        Assert.Equal("This map has no drop point.", Why(CartelMap.Empty, 300));
        Assert.Equal("No drop point is far enough from the objective on this map.", Why(MapWithDrops(new Vector3(10, 0, 0)), 300));
    }

    [Fact]
    public void ThePlantationsAreaIsItsOutlineGrownByTheMarginAndTheContactsIsTheNearest()
    {
        var square = new[] { new Vector2(0, 0), new Vector2(20, 0), new Vector2(20, 20), new Vector2(0, 20) };
        var area = new PlantationArea(square, 5);
        Assert.Equal(new Vector2(10, 10), area.Centre);
        Assert.True(area.Contains(10, 10));
        Assert.True(area.Contains(24, 10)); // within the margin
        Assert.True(area.Contains(-3, -3));
        Assert.False(area.Contains(26, 10));
        Assert.False(area.Contains(-4, -4)); // the corner's margin is round
        var far = square.Select(p => p + new Vector2(500, 0)).ToArray();
        Assert.Equal(1, PlantationArea.Nearest(new IReadOnlyList<Vector2>[] { far, square }, new Vector2(30, 0)));
        Assert.Equal(-1, PlantationArea.Nearest(Array.Empty<IReadOnlyList<Vector2>>(), Vector2.Zero));
    }
}
