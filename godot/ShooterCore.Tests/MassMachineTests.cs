using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>
/// The Holy Mass machines (phase, pastor, citizen state, seat state) pinned to the hand-written timing they replaced: the same tick counts, found by
/// replaying the old float arithmetic here. A refactor that changes a sum or the order of a check changes a tick count and fails these.
/// </summary>
public class MassMachineTests
{
    const float Dt = 0.1f;

    const string ChurchJson = """
    {"door":{"x":0,"y":0,"z":0,"yaw":0}, "exit":{"pos":[0,3,0],"yaw":0},
     "pastor":{"pos":[0,-10,0],"yaw":180}, "monks":[{"pos":[-1,-10,0],"yaw":180},{"pos":[1,-10,0],"yaw":180}],
     "sacristy_door":{"pos":[4,-10,0],"yaw":90},
     "seats":[{"id":0,"pos":[-2,-8,0.45]},{"id":1,"pos":[0,-8,0.45]},{"id":2,"pos":[2,-8,0.45]},{"id":3,"pos":[4,-8,0.45]},{"id":4,"pos":[6,-8,0],"stand":true}]}
    """;

    sealed class FakeWorld : ICitizenWorld
    {
        public IReadOnlyList<Vector3> HouseDoors(Vector3 around, float minM, float radiusM) => new[] { new Vector3(-8, 0, 3) };
        public IReadOnlyList<Vector3> Route(Vector3 from, Vector3 to) => new[] { from, to };
    }

    /// <summary>Takes an entity slot and never ends: fills the cap so a spawn is refused.</summary>
    sealed class Filler : IWorldEntity
    {
        public byte TypeId => 99;
        public ushort Owner => 0;
        public WorldDespawnReason? Ended => null;
        public void Tick(float dt) { }
        public void OnDamage(float amount, ushort attacker) { }
        public WorldState Serialize() => WorldState.Make(Vector3.Zero, 0, 0, 0, 0, 1);
    }

    static MassConfig Cfg() => new() { GatherSeconds = 20, MassSeconds = 10, CompletedSeconds = 3, StaggerSeconds = 2, PlayerSeats = 1, PastorReplaceSeconds = 2, ResumeSeconds = 1, PastorKillsToTurn = 3, SitReachM = 1.5f, StartDelaySeconds = 1000 };

    static (HolyMassEvent Mass, CitizenCrowd Crowd, WorldEntityManager Entities) Make(MassConfig? cfg = null)
    {
        var types = new WorldEntityRegistry();
        CitizenRules.Register(types);
        types.Register(new WorldEntityDef(99, "filler", HasHp: false));
        var entities = new WorldEntityManager(types);
        var church = Church.Parse(ChurchJson);
        var crowd = new CitizenCrowd(new CitizensConfig { MaxCount = 60, WalkSpeed = 5, SpeedJitter = 0, CorpseSeconds = 2 }, church, new FakeWorld(), entities, 1);
        return (new HolyMassEvent(cfg ?? Cfg(), church, crowd), crowd, entities);
    }

    static void Tick(HolyMassEvent mass, CitizenCrowd crowd, WorldEntityManager entities)
    {
        mass.Update(Dt); crowd.Update(Dt); entities.Tick(Dt);
    }

    /// <summary>How many ticks the hand-written "left -= dt; if (left &lt;= 0)" took to fire.</summary>
    static int TicksFor(float seconds)
    {
        var left = seconds; var n = 0;
        do { left -= Dt; n++; } while (left > 0);
        return n;
    }

    /// <summary>How many ticks "progress += dt; if (progress &gt;= limit)" took to fire.</summary>
    static int TicksToReach(float limit, float dt = Dt)
    {
        var p = 0f; var n = 0;
        do { p += dt; n++; } while (!(p >= limit));
        return n;
    }

    [Fact]
    public void TheGatherEndsOnTheTickItsTimerRunsOut()
    {
        var (mass, crowd, entities) = Make();
        mass.Start();
        var n = 0;
        while (mass.Phase == MassPhase.Gather && n < 1000) { Tick(mass, crowd, entities); n++; }
        Assert.Equal(MassPhase.Mass, mass.Phase);
        Assert.Equal(TicksFor(20f), n);
    }

    [Fact]
    public void TheMassCompletesOnTheTickItsProgressReachesTheLimit()
    {
        var (mass, crowd, entities) = Make();
        mass.Start();
        while (mass.Phase == MassPhase.Gather) Tick(mass, crowd, entities);
        Assert.True(mass.Chanting);
        var n = 0;
        while (mass.Phase == MassPhase.Mass && n < 1000) { Tick(mass, crowd, entities); n++; }
        Assert.Equal(MassPhase.Completed, mass.Phase);
        Assert.Equal(TicksToReach(10f), n);
    }

    [Fact]
    public void TheCompletedMassLingersExactlyItsTimeThenEnds()
    {
        var (mass, crowd, entities) = Make();
        mass.Start();
        mass.Conclude();
        Assert.Equal(MassPhase.Completed, mass.Phase);
        var n = 0;
        while (mass.Phase == MassPhase.Completed && n < 1000) { Tick(mass, crowd, entities); n++; }
        Assert.Equal(MassPhase.Idle, mass.Phase);
        Assert.Equal(TicksFor(3f), n);
    }

    [Fact]
    public void TheSettleDelayRestartsEveryTimeSomeoneIsStillComingAndCountsOnceNobodyIs()
    {
        var cfg = Cfg();
        cfg.StartDelaySeconds = 3;
        var (mass, crowd, entities) = Make(cfg);
        mass.Start();
        var arrivedAt = -1; var n = 0;
        while (mass.Phase == MassPhase.Gather && n < 1000)
        {
            Tick(mass, crowd, entities); n++;
            if (arrivedAt < 0 && !crowd.Arriving) arrivedAt = n;
        }
        Assert.Equal(MassPhase.Mass, mass.Phase);
        Assert.True(arrivedAt > 0);
        // everyone is in place after tick arrivedAt; the next tick's Update is the first to count the delay down
        Assert.Equal(arrivedAt + TicksFor(3f), n);
    }

    [Fact]
    public void AKilledPastorIsReplacedAfterThePauseAndTheMassResumesAfterTheArrivalDelay()
    {
        var (mass, crowd, entities) = Make();
        mass.Start();
        while (mass.Phase == MassPhase.Gather) Tick(mass, crowd, entities);
        var old = mass.Pastor!;
        crowd.Hit(old.HitId, 7, 100);
        Assert.False(mass.PastorReady);
        var n = 0; var spawnedAt = -1; var arrivedAt = -1; var readyAt = -1;
        while (readyAt < 0 && n < 500)
        {
            // the arrival is seen by the Update that follows the entity tick in which he stood
            var standing = mass.Pastor is { Alive: true } p && !ReferenceEquals(p, old) && p.State is CitizenState.Standing or CitizenState.Clapping;
            mass.Update(Dt); n++;
            if (spawnedAt < 0 && !ReferenceEquals(mass.Pastor, old)) spawnedAt = n;
            if (arrivedAt < 0 && standing && spawnedAt > 0 && n > spawnedAt) arrivedAt = n;
            if (mass.PastorReady) readyAt = n;
            crowd.Update(Dt); entities.Tick(Dt);
        }
        Assert.Equal(TicksFor(2f), spawnedAt);
        Assert.True(arrivedAt > spawnedAt);
        Assert.Equal(TicksFor(1f), readyAt - arrivedAt + 1);
    }

    [Fact]
    public void AFailedReplacementSpawnIsRetriedEverySecondAndThePastorIsNullMeanwhile()
    {
        var (mass, crowd, entities) = Make();
        mass.Start();
        while (mass.Phase == MassPhase.Gather) Tick(mass, crowd, entities);
        crowd.Hit(mass.Pastor!.HitId, 7, 100);
        var fillers = new List<ushort>();
        while (entities.Spawn(new Filler()) is { } id) fillers.Add(id);
        var n = 0;
        while (mass.Pastor != null && n < 200) { Tick(mass, crowd, entities); n++; }
        Assert.Null(mass.Pastor); // the failed attempt set it null
        Assert.Equal(TicksFor(2f), n);
        Assert.False(mass.PastorReady);
        Assert.NotEmpty(fillers);
        foreach (var f in fillers) entities.Despawn(f, WorldDespawnReason.Removed);
        var m = 0;
        while (mass.Pastor == null && m < 200) { Tick(mass, crowd, entities); m++; }
        Assert.NotNull(mass.Pastor);
        Assert.Equal(TicksFor(1f), m); // one more second after the first attempt failed
    }

    [Fact]
    public void ASecondKillWhileTheReplacementWalksRestartsThePause()
    {
        var (mass, crowd, entities) = Make();
        mass.Start();
        while (mass.Phase == MassPhase.Gather) Tick(mass, crowd, entities);
        crowd.Hit(mass.Pastor!.HitId, 7, 100);
        for (var i = 0; i < TicksFor(2f); i++) Tick(mass, crowd, entities); // the replacement is out
        var walker = mass.Pastor!;
        Assert.True(walker.Alive);
        Assert.False(mass.PastorReady);
        crowd.Hit(walker.HitId, 7, 100);
        Assert.Equal(2, mass.PastorKills);
        var n = 0;
        while (ReferenceEquals(mass.Pastor, walker) && n < 200) { Tick(mass, crowd, entities); n++; }
        Assert.Equal(TicksFor(2f), n); // a full new pause, not what was left
    }

    [Fact]
    public void ThePastorStartsReadyAndTheMassPausesWhileHeIsGone()
    {
        var (mass, crowd, entities) = Make();
        Assert.False(mass.PastorReady);
        mass.Start();
        Assert.True(mass.PastorReady);
        while (mass.Phase == MassPhase.Gather) Tick(mass, crowd, entities);
        Assert.True(mass.Chanting);
        crowd.Hit(mass.Pastor!.HitId, 7, 100);
        Assert.False(mass.PastorReady);
        Assert.False(mass.Chanting);
        mass.Conclude();
        Assert.False(mass.PastorReady);
        mass.End();
        Assert.False(mass.PastorReady);
    }

    [Fact]
    public void PastorKillsAreCountedEvenAfterTheMassIsOver()
    {
        var (mass, crowd, entities) = Make();
        mass.Start();
        mass.Conclude(); // Completed
        var changes = 0;
        mass.Changed += () => changes++;
        crowd.Hit(mass.Pastor!.HitId, 7, 100);
        Assert.Equal(1, mass.PastorKills);
        Assert.Equal(1, changes);
        Assert.Equal(MassPhase.Completed, mass.Phase);
        mass.ForceTurn(); // an admin can still turn a completed Mass
        Assert.Equal(MassPhase.Void, mass.Phase);
        mass.End();
        Assert.Equal(MassPhase.Idle, mass.Phase);
    }

    [Fact]
    public void ForceTurnVoidsARunningMassAndConcludeCleansTheVoidUp()
    {
        var (mass, crowd, entities) = Make();
        var phases = new List<MassPhase>();
        mass.MassPhaseChanged += phases.Add;
        mass.Start();
        mass.ForceTurn();
        mass.Conclude(); // void: cleaned up at once
        Assert.Equal(new[] { MassPhase.Gather, MassPhase.Void, MassPhase.Idle }, phases);
        Assert.False(mass.Running);
    }

    [Fact]
    public void ACitizenWalksEntersAndSitsThroughDeclaredStates()
    {
        var church = Church.Parse(ChurchJson);
        var cfg = new CitizensConfig { WalkSpeed = 5, SpeedJitter = 0, CorpseSeconds = 1, FleeSpeed = 8 };
        var seen = new List<CitizenState>();
        var c = new Citizen(cfg, 0, 0, new[] { new Vector3(-8, 0, 3), church.DoorPos }, church.RouteTo(church.Seat(0)!, 0.45f), 1);
        for (var i = 0; i < 400 && c.State != CitizenState.Seated; i++)
        {
            if (seen.Count == 0 || seen[^1] != c.State) seen.Add(c.State);
            c.Tick(0.05f);
        }
        seen.Add(c.State);
        Assert.Equal(new[] { CitizenState.Walking, CitizenState.Entering, CitizenState.Seated }, seen);
    }

    [Fact]
    public void AStandingSpotKneelsAndThePastorAndMonksStand()
    {
        var church = Church.Parse(ChurchJson);
        var cfg = new CitizensConfig { WalkSpeed = 50, SpeedJitter = 0 };
        var walk = new[] { new Vector3(-8, 0, 3), church.DoorPos };
        var kneeler = new Citizen(cfg, 4, 0, walk, church.RouteTo(church.Seat(4)!, 0.45f), 1) { Stand = true };
        var monk = new Citizen(cfg, 0, 0, walk, church.RouteTo(church.Seat(0)!, 0.45f), 1) { Role = CitizenRole.Monk };
        for (var i = 0; i < 200; i++) { kneeler.Tick(0.05f); monk.Tick(0.05f); }
        Assert.Equal(CitizenState.Kneeling, kneeler.State);
        Assert.True(kneeler.Seated);
        Assert.Equal(CitizenState.Standing, monk.State);
        monk.Finish(true);
        Assert.Equal(CitizenState.Clapping, monk.State);
        kneeler.Finish(false);
        Assert.Equal(CitizenState.Standing, kneeler.State);
    }

    [Fact]
    public void FinishAndFleeLeaveTheWrongStatesAlone()
    {
        var church = Church.Parse(ChurchJson);
        var cfg = new CitizensConfig { WalkSpeed = 1, SpeedJitter = 0 };
        var walker = new Citizen(cfg, 0, 0, new[] { new Vector3(-8, 0, 3), church.DoorPos }, church.RouteTo(church.Seat(0)!, 0.45f), 1);
        walker.Finish(true); // still walking: the Mass being done does not stop it
        Assert.Equal(CitizenState.Walking, walker.State);
        walker.Flee();
        Assert.Equal(CitizenState.Fleeing, walker.State);
        walker.Finish(true);
        Assert.Equal(CitizenState.Fleeing, walker.State);
        walker.Flee(); // already fleeing: not started over
        Assert.Equal(CitizenState.Fleeing, walker.State);
        var pastor = new Citizen(cfg, -1, 0, new[] { Vector3.Zero }, new[] { Vector3.Zero }, 1) { Role = CitizenRole.Pastor };
        pastor.Settle();
        pastor.Flee(); // the pastor stays
        Assert.Equal(CitizenState.Standing, pastor.State);
    }

    [Fact]
    public void ACorpseGoesOnTheTickItsLyingTimeIsReached()
    {
        var church = Church.Parse(ChurchJson);
        var cfg = new CitizensConfig { WalkSpeed = 1, SpeedJitter = 0, CorpseSeconds = 0.7f };
        var c = new Citizen(cfg, 0, 0, new[] { new Vector3(-8, 0, 3), church.DoorPos }, church.RouteTo(church.Seat(0)!, 0.45f), 1);
        c.OnDamage(1, 1);
        var n = 0;
        while (c.Ended == null && n < 100) { c.Tick(0.1f); n++; }
        Assert.Equal(TicksToReach(0.7f), n);
        Assert.Equal(WorldDespawnReason.Expired, c.Ended);
    }

    [Fact]
    public void ASeatIsHeldByOneSideAtATime()
    {
        var book = new SeatBook();
        Assert.Equal(SeatHolder.Free, book.HolderOf(1));
        Assert.True(book.Reserve(1));
        Assert.Equal(SeatHolder.Reserved, book.HolderOf(1));
        Assert.False(book.Reserve(1)); // already kept
        book.Assign(2);
        Assert.Equal(SeatHolder.Citizen, book.HolderOf(2));
        Assert.False(book.Reserve(2)); // a citizen has it
        Assert.False(book.IsFree(2));
        book.Release(2);
        Assert.True(book.IsFree(2));
        book.Unreserve(1);
        Assert.True(book.IsFree(1));
        book.Assign(3); book.Reserve(4);
        book.Clear();
        Assert.True(book.IsFree(3) && book.IsFree(4));
    }

    [Fact]
    public void TheSeatMessageStatesFollowTheDeclaredHints()
    {
        var spec = MassSeatRules.Spec;
        Assert.True(spec.Allows(MassSeatState.None, MassSeatState.CanSit));
        Assert.True(spec.Allows(MassSeatState.None, MassSeatState.Seated)); // interact before the hint went out
        Assert.True(spec.Allows(MassSeatState.CanSit, MassSeatState.None));
        Assert.True(spec.Allows(MassSeatState.Seated, MassSeatState.None));
        Assert.False(spec.Allows(MassSeatState.Seated, MassSeatState.CanSit)); // a stand-up goes through None
        foreach (var s in Enum.GetValues<MassSeatState>())
            Assert.Equal(s, MassCodec.DecodeSeat(MassCodec.EncodeSeat(new MassSeatMsg(s, 0)))!.Value.State);
    }

    [Fact]
    public void TheMassSpecsAreInTheCatalog()
    {
        var names = StateMachineCatalog.All.Select(x => x.Name).ToHashSet();
        Assert.Contains("Mass.Phase", names);
        Assert.Contains("Mass.Pastor", names);
        Assert.Contains("Mass.Seat", names);
        Assert.Contains("Citizen.State", names);
    }
}
