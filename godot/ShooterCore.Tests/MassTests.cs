using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The Holy Mass event: timeline, the pastor's kills and replacement, seats and the blessing rule, the wire format.</summary>
public class MassTests
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

    static MassConfig Cfg(int playerSeats = 1) => new() { GatherSeconds = 20, MassSeconds = 10, CompletedSeconds = 3, StaggerSeconds = 2, PlayerSeats = playerSeats, PastorReplaceSeconds = 2, ResumeSeconds = 1, PastorKillsToTurn = 3, SitReachM = 1.5f, StartDelaySeconds = 1000 };

    static (HolyMassEvent Mass, CitizenCrowd Crowd, WorldEntityManager Entities, Church Church) Make(MassConfig? cfg = null)
    {
        var types = new WorldEntityRegistry();
        CitizenRules.Register(types);
        var entities = new WorldEntityManager(types);
        var church = Church.Parse(ChurchJson);
        var crowd = new CitizenCrowd(new CitizensConfig { MaxCount = 60, WalkSpeed = 5, SpeedJitter = 0, CorpseSeconds = 2 }, church, new FakeWorld(), entities, 1);
        return (new HolyMassEvent(cfg ?? Cfg(), church, crowd), crowd, entities, church);
    }

    static void Run(HolyMassEvent mass, CitizenCrowd crowd, WorldEntityManager entities, float seconds)
    {
        for (var t = 0f; t < seconds; t += Dt) { mass.Update(Dt); crowd.Update(Dt); entities.Tick(Dt); }
    }

    static Citizen Kill(HolyMassEvent mass, CitizenCrowd crowd, ushort killer = 7)
    {
        var p = mass.Pastor!;
        crowd.Hit(p.HitId, killer, 100);
        return p;
    }

    [Fact]
    public void ChurchFileGivesTheMassItsSpots()
    {
        var c = Church.Parse(ChurchJson);
        Assert.Equal(new Vector3(0, 0, 10), c.Pastor!.Pos); // y north -> -z
        Assert.Equal(2, c.Monks.Count);
        Assert.Equal(new Vector3(0, 0, -3), c.Exit!.Pos);
        Assert.NotNull(c.SacristyDoor);
        Assert.Null(Church.Parse("""{"door":{"x":0,"y":0,"z":0},"seats":[{"id":0,"pos":[0,0,0]}]}""").Pastor);
    }

    [Fact]
    public void StartPutsThePastorAndMonksInPlaceAndKeepsSeatsForPlayers()
    {
        var (mass, crowd, entities, _) = Make();
        var phases = new List<MassPhase>();
        mass.MassPhaseChanged += phases.Add;
        var sent = mass.Start();
        Assert.Equal(MassPhase.Gather, mass.Phase);
        Assert.Equal(4, sent); // 5 seats, one kept for players
        Assert.Single(mass.KeptSeats);
        Assert.Equal(CitizenState.Standing, mass.Pastor!.State);
        Assert.Equal(2, crowd.AliveOfRole(CitizenRole.Monk).Count());
        Assert.Equal(CitizenRole.Pastor, Citizen.RoleOf(mass.Pastor.Serialize().Hp));
        Run(mass, crowd, entities, 21);
        Assert.Equal(new[] { MassPhase.Gather, MassPhase.Mass }, phases);
    }

    [Fact]
    public void ThePastorKilledPausesTheMassAndANewOneResumesIt()
    {
        var (mass, crowd, entities, church) = Make();
        mass.Start();
        Run(mass, crowd, entities, 25); // the Mass runs for 5 s
        var before = mass.Progress;
        Assert.InRange(before, 4, 6);
        Assert.True(mass.Chanting);
        var old = Kill(mass, crowd);
        Assert.Equal(1, mass.PastorKills);
        Assert.False(mass.Chanting);
        Run(mass, crowd, entities, 1.5f);
        Assert.Equal(before, mass.Progress, 3); // paused, not reset
        Run(mass, crowd, entities, 1f); // the replacement walks out of the sacristy
        Assert.NotSame(old, mass.Pastor);
        Assert.True(mass.Pastor!.Alive);
        Assert.NotEqual(CitizenState.Standing, mass.Pastor.State);
        Assert.InRange(Vector3.Distance(church.Pastor!.Pos, mass.Pastor.Position), 0.5f, 4f); // on his way from the sacristy door
        Run(mass, crowd, entities, 3f); // arrived (fast walkers), then 1 s of pause
        Assert.True(mass.Chanting);
        Assert.Equal(church.Pastor!.Pos, mass.Pastor.Position);
        Run(mass, crowd, entities, 1f);
        Assert.True(mass.Progress > before);
    }

    [Fact]
    public void TheDemonTurnsAtOnceOnAdminCommandAndTheCongregationBurns()
    {
        var (mass, crowd, entities, _) = Make();
        var turned = 0;
        mass.PastorTurns += () => turned++;
        mass.ForceTurn(); // from idle: void at once
        Assert.Equal(MassPhase.Void, mass.Phase);
        Assert.Equal(1, turned);
        mass.ForceTurn();
        Assert.Equal(1, turned);
        mass.End();

        mass.Start();
        Run(mass, crowd, entities, 21);
        var alive = crowd.AliveCount;
        Assert.True(alive > 2);
        crowd.Remove(mass.Pastor!); // the pastor's body is taken away
        Assert.Equal(alive - 1, crowd.Count == 0 ? 0 : crowd.AliveCount);
        var died = crowd.KillAll(); // everyone else dies in place
        Assert.Equal(alive - 1, died);
        Assert.Equal(0, crowd.AliveCount);
    }

    [Fact]
    public void ThirdKillTurnsThePastorAndVoidsTheMass()
    {
        var (mass, crowd, entities, _) = Make();
        var turned = 0;
        mass.PastorTurns += () => turned++;
        mass.Start();
        Run(mass, crowd, entities, 21);
        for (var i = 0; i < 2; i++) { Kill(mass, crowd); Run(mass, crowd, entities, 6); Assert.True(mass.Chanting); }
        Assert.Equal(0, turned);
        var before = crowd.Count;
        var last = Kill(mass, crowd);
        Assert.Equal(3, mass.PastorKills);
        Assert.Equal(1, turned);
        Assert.Equal(before - 1, crowd.Count); // no corpse: the body went with the hit
        Assert.Equal(DamageResult.Ignored, crowd.Hit(last.HitId, 7, 100));
        Assert.Equal(MassPhase.Void, mass.Phase);
        Run(mass, crowd, entities, 20);
        Assert.False(mass.Chanting);
        Assert.Equal(MassPhase.Void, mass.Phase);
        mass.Conclude(); // /event current end cleans up
        Assert.Equal(MassPhase.Idle, mass.Phase);
        Assert.Equal(0, crowd.Count);
    }

    [Fact]
    public void AKilledPastorOrMonkLiesUntilTheEventIsOverAndCannotBeHitAgain()
    {
        var (mass, crowd, entities, _) = Make();
        mass.Start();
        Run(mass, crowd, entities, 21);
        var monk = crowd.AliveOfRole(CitizenRole.Monk).First();
        var corpse = Kill(mass, crowd);
        Assert.Equal(DamageResult.Killed, crowd.Hit(monk.HitId, 7, 100));
        Run(mass, crowd, entities, 8); // well beyond the corpse time (2 s)
        Assert.Equal(CitizenState.Dead, corpse.State);
        Assert.Equal(CitizenState.Dead, monk.State);
        Assert.Null(corpse.Ended);
        Assert.Null(monk.Ended);
        Assert.Equal(DamageResult.Ignored, crowd.Hit(corpse.HitId, 7, 100));
        Assert.Equal(DamageResult.Ignored, crowd.Hit(monk.HitId, 7, 100));
        Assert.Equal(1, mass.PastorKills);
        Assert.True(mass.Pastor!.Alive); // the replacement came regardless
        Assert.NotSame(corpse, mass.Pastor);
        mass.End();
        Assert.Equal(0, crowd.Count);
    }

    [Fact]
    public void TheCitizensLeaveOnlyAfterTheBellsLeadIn()
    {
        var cfg = Cfg();
        cfg.BellsLeadSeconds = 5;
        var (mass, crowd, entities, _) = Make(cfg);
        var sent = mass.Start();
        Assert.True(sent > 0);
        Run(mass, crowd, entities, 4.5f);
        Assert.Equal(0, crowd.AliveOfRole(CitizenRole.Citizen).Count());
        Run(mass, crowd, entities, 1f);
        Assert.True(crowd.AliveOfRole(CitizenRole.Citizen).Any());
        Run(mass, crowd, entities, 19f); // gather = lead 5 + 20 s
        Assert.Equal(MassPhase.Gather, mass.Phase);
        Run(mass, crowd, entities, 2f);
        Assert.Equal(MassPhase.Mass, mass.Phase);
    }

    [Fact]
    public void TheMassBeginsShortlyAfterTheLastCitizenIsInPlace()
    {
        var cfg = Cfg();
        cfg.StartDelaySeconds = 3;
        var (mass, crowd, entities, _) = Make(cfg);
        mass.Start();
        Run(mass, crowd, entities, 1f);
        Assert.True(crowd.Arriving);
        for (var t = 0f; t < 15 && crowd.Arriving; t += Dt) Run(mass, crowd, entities, Dt);
        Assert.False(crowd.Arriving);
        Assert.Equal(MassPhase.Gather, mass.Phase);
        Run(mass, crowd, entities, 2f);
        Assert.Equal(MassPhase.Gather, mass.Phase);
        Run(mass, crowd, entities, 1.5f);
        Assert.Equal(MassPhase.Mass, mass.Phase); // well before the 20 s gather timer
    }

    [Fact]
    public void ADeadCitizenOnTheWayDoesNotHoldTheMassUp()
    {
        var cfg = Cfg(0);
        cfg.StartDelaySeconds = 1;
        var (mass, crowd, entities, _) = Make(cfg);
        mass.Start();
        Run(mass, crowd, entities, 2.5f); // all have left their houses, some still walk
        Assert.True(crowd.Arriving);
        foreach (var c in crowd.AliveOfRole(CitizenRole.Citizen).Where(c => !c.Seated).ToList()) crowd.Hit(c.HitId, 7, 100);
        Run(mass, crowd, entities, 2f);
        Assert.Equal(MassPhase.Mass, mass.Phase);
    }

    [Fact]
    public void WhenThePastorTurnsEveryLivingCitizenSprintsHomeAndIsRemoved()
    {
        var (mass, crowd, entities, church) = Make(Cfg(0));
        mass.Start();
        Run(mass, crowd, entities, 0.5f);
        var walker = crowd.AliveOfRole(CitizenRole.Citizen).First();
        Assert.Equal(CitizenState.Walking, walker.State);
        Run(mass, crowd, entities, 8f);
        var seated = crowd.AliveOfRole(CitizenRole.Citizen).Where(c => c.Seated).ToList();
        Assert.NotEmpty(seated);
        var victim = seated[0];
        mass.ForceTurn();
        Assert.Equal(MassPhase.Void, mass.Phase);
        Assert.All(crowd.AliveOfRole(CitizenRole.Citizen), c => Assert.Equal(CitizenState.Fleeing, c.State));
        Assert.Equal(2, crowd.AliveOfRole(CitizenRole.Monk).Count()); // the monks stay
        Assert.Equal(CitizenState.Standing, mass.Pastor!.State);
        var before = victim.Position;
        Run(mass, crowd, entities, 1f);
        Assert.True(Vector3.Distance(before, victim.Position) > 3); // 6 m/s (the sprint, not the 5 m/s walk of the test)
        crowd.Hit(victim.HitId, 7, 100); // can be killed while fleeing
        Assert.False(victim.Alive);
        Run(mass, crowd, entities, 20f);
        Assert.Equal(0, crowd.AliveOfRole(CitizenRole.Citizen).Count()); // all home, gone
        Assert.Equal(0, crowd.Waiting);
    }

    [Fact]
    public void AFleeingCitizenWalksTheWayItCameBack()
    {
        var church = Church.Parse(ChurchJson);
        var cfg = new CitizensConfig { WalkSpeed = 2, SpeedJitter = 0, FleeSpeed = 10 };
        var home = new Vector3(-8, 0, 3); var turn = new Vector3(-8, 0, 0);
        var c = new Citizen(cfg, 0, 0, new[] { home, turn, church.DoorPos }, church.RouteTo(church.Seat(0)!, 0.45f), 1);
        for (var i = 0; i < 200 && c.State != CitizenState.Seated; i++) c.Tick(0.1f);
        Assert.Equal(CitizenState.Seated, c.State);
        c.Flee();
        Assert.Equal(CitizenState.Fleeing, c.State);
        var door = false; var corner = false;
        for (var i = 0; i < 400 && c.Ended == null; i++)
        {
            c.Tick(0.05f);
            door |= Vector3.Distance(c.Position, church.DoorPos) < 0.6f;
            corner |= Vector3.Distance(c.Position, turn) < 0.6f;
        }
        Assert.True(door && corner);
        Assert.Equal(WorldDespawnReason.Removed, c.Ended);
        Assert.Equal(home, c.Position);
    }

    [Fact]
    public void APlayerSitsOnlyInASeatNoCitizenHas()
    {
        var (mass, crowd, entities, church) = Make();
        mass.Start();
        Run(mass, crowd, entities, 1);
        var kept = church.Seat(mass.KeptSeats.Single())!;
        var taken = church.Seats.First(s => !s.Stand && s.Id != kept.Id);
        Assert.Null(mass.TrySit(1, Church.FeetOf(taken, 0.45f))); // a citizen is assigned to it
        Assert.Same(kept, mass.TrySit(1, Church.FeetOf(kept, 0.45f)));
        Assert.Equal(kept.Id, mass.SeatOf(1));
        Assert.Null(mass.TrySit(2, Church.FeetOf(kept, 0.45f))); // taken by a player
        Assert.Null(mass.TrySit(2, church.Seat(4)!.Pos)); // the standing spot is nobody's to sit
        mass.Stand(1);
        Assert.Null(mass.SeatOf(1));
        Assert.Same(kept, mass.TrySit(2, Church.FeetOf(kept, 0.45f)));
    }

    [Fact]
    public void ASeatAKilledCitizenLeftIsFreeOnceItsBodyIsGone()
    {
        var (mass, crowd, entities, church) = Make(Cfg(0));
        mass.Start();
        Run(mass, crowd, entities, 5);
        var seat = church.Seats.First(s => !s.Stand);
        Assert.Null(mass.SeatNear(Church.FeetOf(seat, 0.45f))); // every seat has a citizen
        var victim = crowd.AliveOfRole(CitizenRole.Citizen).First(c => c.SeatId == seat.Id);
        crowd.Hit(victim.HitId, 3, 10);
        Assert.Null(mass.SeatNear(Church.FeetOf(seat, 0.45f))); // the body still lies there
        Run(mass, crowd, entities, 3); // corpse_seconds 2
        Assert.Equal(seat.Id, mass.SeatNear(Church.FeetOf(seat, 0.45f)));
    }

    [Fact]
    public void BlessedAreTheSeatedWhoSatLongEnoughAndSinnedNot()
    {
        var (mass, crowd, entities, church) = Make(Cfg(3));
        IReadOnlyList<ushort>? blessed = null;
        mass.Completed += b => blessed = b;
        mass.Start();
        Run(mass, crowd, entities, 1);
        var seats = mass.KeptSeats.Select(church.Seat).ToList();
        for (ushort p = 1; p <= 3; p++) Assert.NotNull(mass.TrySit(p, Church.FeetOf(seats[p - 1]!, 0.45f)));
        mass.Stand(3); // 3 stands before the end
        Run(mass, crowd, entities, 23); // the Mass is at 4 s of 10
        mass.TrySit(3, Church.FeetOf(seats[2]!, 0.45f)); // sits again late: 6 of 10 s
        Assert.Equal(MassPhase.Mass, mass.Phase);
        var victim = crowd.AliveOfRole(CitizenRole.Monk).First();
        crowd.Hit(victim.HitId, 2, 10); // player 2 kills a monk: a sinner
        Run(mass, crowd, entities, 8);
        Assert.Equal(MassPhase.Completed, mass.Phase);
        Assert.Equal(new ushort[] { 1 }, blessed); // 2 is a sinner, 3 sat 6 of 10 s
        Assert.True(mass.IsSinner(2));
    }

    [Fact]
    public void ConcludingEarlyBlessesTheSeatedAndLeavesNobodyLocked()
    {
        var (mass, crowd, entities, church) = Make();
        IReadOnlyList<ushort>? blessed = null;
        mass.Completed += b => blessed = b;
        mass.Start();
        var kept = church.Seat(mass.KeptSeats.Single())!;
        mass.TrySit(5, Church.FeetOf(kept, 0.45f));
        mass.Conclude();
        Assert.Equal(new ushort[] { 5 }, blessed);
        Assert.Equal(MassPhase.Completed, mass.Phase);
        Assert.Null(mass.SeatOf(5)); // released: the server stands them up
        Run(mass, crowd, entities, 4); // completed_seconds 3
        Assert.Equal(MassPhase.Idle, mass.Phase);
        Assert.Equal(0, crowd.Count);
    }

    [Theory]
    [InlineData(true, false, 8f, 10f, true)]
    [InlineData(true, false, 7.9f, 10f, false)]
    [InlineData(true, true, 10f, 10f, false)]
    [InlineData(false, false, 10f, 10f, false)]
    [InlineData(true, false, 0f, 0f, true)]
    public void TheBlessingRule(bool seated, bool sinner, float seatedFor, float mass, bool blessed) =>
        Assert.Equal(blessed, HolyMassEvent.IsBlessed(seated, sinner, seatedFor, mass, 0.8f));

    [Fact]
    public void TheWireFormatRoundTrips()
    {
        var s = new MassStateMsg(MassPhase.Mass, true, 2, new Vector3(1.5f, 2f, -3.25f), true);
        Assert.Equal(s, MassCodec.DecodeState(MassCodec.EncodeState(s)));
        var seat = new MassSeatMsg(MassSeatState.Seated, 1.25f);
        Assert.Equal(seat, MassCodec.DecodeSeat(MassCodec.EncodeSeat(seat)));
        Assert.Equal(-0.5f, MassCodec.DecodeBlessing(MassCodec.EncodeBlessing(-0.5f)));
        Assert.Null(MassCodec.DecodeState(new byte[3]));
        Assert.Null(MassCodec.DecodeSeat(new byte[] { 9, 0, 0, 0, 0 }));
    }

    [Fact]
    public void ShippedMassDataIsValid()
    {
        var shipped = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data")).HolyMass;
        Assert.Equal(3, shipped.Mass.PastorKillsToTurn);
        Assert.Equal(120, shipped.Mass.MassSeconds);
        Assert.Throws<DataFileException>(() => HolyMassFile.Parse("""{"version":1,"mass":{"mass_seconds":0},"citizens":{"palette":["#000000"],"skin_tones":["#000000"],"models":[{"file":"x"}]}}"""));
    }
}
