using System.Numerics;
using Drycastle.ShooterCore.Buildings;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.RozhlasFeed;
using Drycastle.ShooterCore.Weapons;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Buildings;

/// <summary>The Obecny Urad: urad.json's doorway, volume and spots, who is shown out when the door shuts, and the workers that sit in it while a rozhlas detour is open.</summary>
public class UradTests
{
    const float Dt = 0.1f;
    const double IdleClock = 1e12; // a unix second at which the schedule has nothing on air

    /// <summary>A small map: the doorway on the east wall at the origin (outward is east), an entry room east of an office, seats 0.45 m up, the desk in the office, a cctv that is ignored.</summary>
    const string Sample = """
        {
          "version": 1, "axes": "x east, y north, z up",
          "id": "w1", "parcel": "P-1",
          "doorway": { "pos": [0, 0, 100], "yaw": 90, "width": 1.2, "height": 2.1 },
          "volume": [
            { "id": "entry",  "floor_z": 100, "ceiling_z": 103, "footprint": [[-4, -2], [0, -2], [0, 2], [-4, 2]] },
            { "id": "office", "floor_z": 100, "ceiling_z": 103, "footprint": [[-9, -2], [-4, -2], [-4, 2], [-9, 2]] }
          ],
          "workers": [
            { "role": "mayor", "pos": [-8, 1, 100.45], "yaw": 90, "female": false },
            { "role": "clerk", "pos": [-8, -1, 100.45], "yaw": 90, "female": false },
            { "role": "clerk", "pos": [-6, -1, 100.45], "yaw": 90, "female": true },
            { "role": "announcer", "pos": [-6, 1, 100.45], "yaw": 90, "female": true }
          ],
          "table": { "pos": [-7, 0, 100], "yaw": 270 },
          "cctv": { "pos": [-5, 0, 102.5], "yaw": 90 }
        }
        """;

    static readonly Urad Shipped = Urad.Parse(Sample);

    /// <summary>A map point [x east, y north, z up] as Godot axes.</summary>
    static Vector3 G(float x, float north, float up) => new(x, up, -north);

    static RozhlasBroadcast Detour() => new(new RozhlasFile { Files = new() { "a.ogg" }, LengthsS = new() { 100 }, PeriodS = 600 });

    static (WorkerCrowd Workers, RozhlasBroadcast Rozhlas, WorldEntityManager Entities) Make()
    {
        var types = new WorldEntityRegistry();
        CitizenRules.Register(types);
        var entities = new WorldEntityManager(types);
        var rozhlas = Detour();
        var workers = new WorkerCrowd(new CitizensConfig { CorpseSeconds = 2 }, entities, 1);
        workers.Attach(rozhlas, () => Shipped.Workers);
        return (workers, rozhlas, entities);
    }

    static void Run(WorkerCrowd w, RozhlasBroadcast r, WorldEntityManager e, float seconds)
    {
        for (var t = 0f; t < seconds; t += Dt) { r.Update(Dt, IdleClock); e.Tick(Dt); }
    }

    // ---- urad.json ----

    [Fact]
    public void TheVolumeHoldsAPointInEachRoomAndNothingOutsideIt()
    {
        Assert.Equal(2, Shipped.Parts.Count);
        foreach (var part in Shipped.Parts)
        {
            var c = new Vector2(part.Footprint.Average(p => p.X), part.Footprint.Average(p => p.Y));
            var mid = (part.FloorZ + part.CeilingZ) / 2;
            Assert.True(Shipped.Contains(G(c.X, c.Y, mid)), $"{part.Id}: the middle of the room is inside");
            Assert.False(Shipped.Contains(G(c.X, c.Y, part.CeilingZ + 1)), $"{part.Id}: above the ceiling is outside");
            Assert.False(Shipped.Contains(G(c.X, c.Y, part.FloorZ - 1)), $"{part.Id}: below the floor is outside");
        }
        Assert.False(Shipped.Contains(G(3, 0, 101)));   // outside the door
        Assert.False(Shipped.Contains(G(0, 50, 101)));  // far away
        // the map's y north is Godot's -z: the same point with its z not negated is somewhere else
        Assert.True(Shipped.Contains(new Vector3(-2, 101, -1)));
        Assert.False(Shipped.Contains(new Vector3(-2, 101, 1)) && Shipped.Contains(new Vector3(-2, 101, 5)));
    }

    [Fact]
    public void TheDoorwayTheSeatsAndTheTableAreInGodotAxes()
    {
        var d = Shipped.Doorway;
        Assert.Equal(new Vector3(0, 100, 0), d.Pos);
        Assert.Equal(1.2f, d.Width); Assert.Equal(2.1f, d.Height);
        Near(new Vector3(1, 0, 0), d.Out); // a compass bearing east points out along +x
        Assert.Equal(new Vector3(-7, 100, 0), Shipped.Table.Pos);
        Assert.Equal(new[] { CitizenRole.Mayor, CitizenRole.Clerk, CitizenRole.Clerk, CitizenRole.Announcer }, Shipped.Workers.Select(w => w.Role));
        Assert.Equal(new[] { false, false, true, true }, Shipped.Workers.Select(w => w.Female));
        Assert.All(Shipped.Workers, w => Assert.True(Shipped.Contains(w.Pos), "a seat is inside the rooms"));
    }

    static void Near(Vector3 expected, Vector3? actual) => Assert.True(actual is { } a && (a - expected).Length() < 1e-4f, $"expected {expected}, got {actual}");

    [Fact]
    public void ShutBodiesInTheRoomsOrTheDoorwayAreShownOutAndTheRestStayPut()
    {
        var outside = Shipped.Doorway.Outside(Urad.EvictOutsideM);
        Near(new Vector3(1.5f, 100, 0), outside); // 1.5 m east of the doorway, at its floor
        const float R = 0.35f;
        foreach (var feet in new[] { G(-2, 1, 100), G(-8, -1, 100), G(-0.05f, 0, 100.02f), G(-4, 0, 100) }) // the entry, the office, the doorway's inner edge, the room line
            Near(outside, Shipped.EvictionPoint(feet, R));
        Near(outside, Shipped.EvictionPoint(G(0.1f, 0, 100), R)); // standing in the doorway itself
        Assert.Null(Shipped.EvictionPoint(G(1.5f, 0, 100), R));   // already outside
        Assert.Null(Shipped.EvictionPoint(G(30, 30, 100), R));    // far off
        Assert.Null(Shipped.EvictionPoint(G(-2, 1, 103.5f), R));  // on the roof above the entry
        Assert.Null(Shipped.EvictionPoint(G(-2, 1, 90), R));      // below the floor
        Assert.Null(Shipped.EvictionPoint(G(0.1f, 3, 100), R));   // beside the doorway, outside the wall
    }

    [Fact]
    public void TheFileIsChecked()
    {
        const string Door = """ "doorway": { "pos": [1, 2, 0], "yaw": 90, "width": 1.2, "height": 2.1 } """;
        const string Vol = """ "volume": [{ "id": "a", "floor_z": 0, "ceiling_z": 3, "footprint": [[0,0],[4,0],[4,4],[0,4]] }] """;
        const string Mayor = """{ "role": "mayor", "pos": [1,1,0], "yaw": 0 }""", Announcer = """{ "role": "announcer", "pos": [2,1,0], "yaw": 90, "female": true }""";
        string File(string workers, string table = """ "table": { "pos": [3,1,0], "yaw": 0 } """, string door = Door) => $$"""{ "version": 1, {{door}}, {{Vol}}, "workers": [{{workers}}], {{table}} }""";

        var ok = Urad.Parse(File($"{Mayor}, {Announcer}"));
        Assert.Equal(new Vector3(3, 0, -1), ok.Table.Pos);                 // y north -> -z
        Assert.Equal(new Vector3(1, 0, -2), ok.Doorway.Pos);
        Assert.Equal(-MathF.PI / 2, ok.Workers[1].Yaw, 4);                  // a compass bearing east is a Godot yaw of -90 degrees
        Assert.True(ok.Contains(G(1, 1, 1)));
        Assert.Throws<DataFileException>(() => Urad.Parse(File(Mayor)));                          // no announcer
        Assert.Throws<DataFileException>(() => Urad.Parse(File($"{Mayor}, {Announcer}, {Mayor}"))); // two mayors
        Assert.Throws<DataFileException>(() => Urad.Parse(File($"{Mayor}, {Announcer}", "")));      // no table
        Assert.Throws<DataFileException>(() => Urad.Parse(File($"{Mayor}, {Announcer}", door: """ "cctv": null """))); // no doorway
        Assert.Throws<DataFileException>(() => Urad.Parse(File($"{Mayor}, {Announcer}", door: Door.Replace("\"width\": 1.2", "\"width\": 0")))); // a doorway with no width
        Assert.Throws<DataFileException>(() => Urad.Parse(File($"{Mayor}, {Announcer}", door: Door.Replace("[1, 2, 0]", "[1, 2]")))); // a doorway pos is [x, y, z]
        Assert.Throws<DataFileException>(() => Urad.Parse(File($"{Mayor}, {Announcer.Replace("announcer", "janitor")}")));
        Assert.Throws<DataFileException>(() => Urad.Parse(File($"{Mayor}, {Announcer}").Replace("\"ceiling_z\": 3", "\"ceiling_z\": 0")));
        Assert.Null(Urad.FromJson(""));
        var errors = new List<string>();
        Assert.Null(Urad.FromJsonOrNull("{ not json", errors.Add));
        Assert.Single(errors);
    }

    [Fact]
    public void TheRolesFitTheHitPointByte()
    {
        foreach (var role in Enum.GetValues<CitizenRole>())
        {
            var c = new Citizen(new CitizensConfig(), -1, 0, new[] { Vector3.Zero }, new[] { Vector3.Zero }, 0) { Role = role };
            Assert.Equal(role, Citizen.RoleOf(c.Serialize().Hp));
        }
        Assert.True(CitizenRules.IsWorker(CitizenRole.Mayor) && CitizenRules.IsWorker(CitizenRole.Clerk) && CitizenRules.IsWorker(CitizenRole.Announcer));
        Assert.False(CitizenRules.IsWorker(CitizenRole.Pastor) || CitizenRules.IsWorker(CitizenRole.Monk) || CitizenRules.IsWorker(CitizenRole.Citizen));
    }

    // ---- the workers follow the detour ----

    [Fact]
    public void WorkersSitDownWhenTheDetourOpensAndAreAllGoneWhenItEnds()
    {
        var (w, r, e) = Make();
        Assert.Equal(0, w.Count);                       // none outside a detour
        Assert.True(r.StartNow(0, 0));
        Assert.Equal(4, w.Count);
        Assert.All(w.Workers, c => Assert.Equal(CitizenState.Seated, c.State));
        var feet = new Vector3(0, new CitizensConfig().SeatHeightM, 0); // the spot is on the seat, the body's feet on the floor below it
        Assert.Equal(Shipped.Workers.Select(s => s.Pos - feet).OrderBy(p => p.X), w.Workers.Select(c => c.Position).OrderBy(p => p.X));
        Assert.True(CitizenRules.IsHitId(w.Workers.First().HitId) && w.Workers.All(c => c.HitId >= CitizenRules.FirstWorkerHitId));
        Assert.Equal(2, w.Workers.Count(c => c.Role == CitizenRole.Clerk));
        Assert.Equal(2, w.Workers.Count(c => (c.Look & 1) == 1)); // the look carries the female body

        Run(w, r, e, 30); // passive: they sit there
        Assert.All(w.Workers, c => Assert.Equal(CitizenState.Seated, c.State));
        Assert.Equal(Shipped.Workers[0].Pos - feet, w.AliveOfRole(CitizenRole.Mayor).Single().Position);

        w.Hit(w.AliveOfRole(CitizenRole.Clerk).First().HitId, 7, 1); // a corpse lies for the rest of the broadcast, however long the corpse time
        Run(w, r, e, 10);
        Assert.Equal(4, w.Count);
        Assert.Equal(3, w.AliveCount);

        Run(w, r, e, 100); // the broadcast ends: everyone goes, the dead included
        Assert.Equal(RozhlasPhase.Idle, r.Phase);
        Assert.Equal(0, w.Count);
        Assert.Equal(0, e.Count);

        Assert.True(r.StartNow(0, 0)); // fresh every broadcast
        Assert.Equal(4, w.AliveCount);
    }

    [Fact]
    public void ShotWorkersDieInOneHitAndTheOthersStaySeated()
    {
        var (w, r, e) = Make();
        r.StartNow(0, 0);
        var killed = new List<CitizenKilled>();
        w.Killed += killed.Add;
        var mayor = w.AliveOfRole(CitizenRole.Mayor).Single();
        Assert.Equal(DamageResult.Killed, w.Hit(mayor.HitId, 3, 0.1f));
        Assert.Equal(DamageResult.Ignored, w.Hit(mayor.HitId, 3, 100)); // dead: the shot is absorbed
        Assert.Equal(CitizenState.Dead, mayor.State);
        var k = Assert.Single(killed);
        Assert.Equal((CitizenRole.Mayor, (ushort)3), (k.Role, k.Killer));
        Assert.All(w.AliveOfRole(CitizenRole.Clerk).Concat(w.AliveOfRole(CitizenRole.Announcer)), c => Assert.Equal(CitizenState.Seated, c.State));
        Assert.Equal(RozhlasPhase.OnAir, r.Phase); // only the announcer ends the broadcast's announcement
        var targets = new List<HitTarget>();
        w.AddTargets(targets);
        Assert.Equal(3, targets.Count); // the dead are no target
    }

    [Fact]
    public void KillingTheAnnouncerStartsTheHijackOnceAnyTimeTheDetourIsOpen()
    {
        var (w, r, e) = Make();
        var seen = new List<RozhlasPhase>();
        r.PhaseChanged += (_, to) => seen.Add(to);
        r.StartNow(0, 0);
        Run(w, r, e, 5); // before or during her announcement: not at the end
        Assert.Equal(RozhlasPhase.OnAir, r.Phase);
        w.Hit(w.AliveOfRole(CitizenRole.Clerk).First().HitId, 7, 1); // not the announcer: nothing
        Assert.Equal(RozhlasPhase.OnAir, r.Phase);
        w.Hit(w.AliveOfRole(CitizenRole.Announcer).Single().HitId, 7, 1);
        Assert.Equal(RozhlasPhase.Hijacked, r.Phase);
        Assert.Equal(RozhlasBroadcast.HijackSeconds, r.LeftS, 1);
        Assert.Equal(new[] { RozhlasPhase.OnAir, RozhlasPhase.Hijacked }, seen);
        Assert.Equal(2, w.AliveCount); // the survivors stay through the hijack
        Run(w, r, e, 60);
        Assert.Equal(2, w.AliveCount);
        Assert.InRange(r.LeftS, 100, 130);              // not refreshed by anything
        Run(w, r, e, 125);
        Assert.Equal(RozhlasPhase.Idle, r.Phase);
        Assert.Equal(0, w.Count);                       // all stop together
    }

    [Fact]
    public void TheAdminStandInHijackStillWorksAndAnAlreadyHijackedMicIsNotRefreshed()
    {
        var (w, r, e) = Make();
        r.StartNow(0, 0);
        Assert.True(r.AnnouncerKilled());               // /rozhlas hijack
        Assert.Equal(4, w.AliveCount);                  // the stand-in does not kill her: she is still there
        Run(w, r, e, 50);
        var left = r.LeftS;
        w.Hit(w.AliveOfRole(CitizenRole.Announcer).Single().HitId, 7, 1); // her real death later changes nothing
        Assert.Equal(RozhlasPhase.Hijacked, r.Phase);
        Assert.Equal(left, r.LeftS, 3);
    }

    [Fact]
    public void TheHolyMassIsUntouchedByADetourAndTheOtherWayRound()
    {
        var (w, r, e) = Make();
        var church = Church.Parse("""
            {"door":{"x":0,"y":0,"z":0,"yaw":0}, "pastor":{"pos":[0,-10,0],"yaw":180}, "monks":[{"pos":[-1,-10,0],"yaw":180},{"pos":[1,-10,0],"yaw":180}],
             "seats":[{"id":0,"pos":[-2,-8,0.45]},{"id":1,"pos":[0,-8,0.45]}]}
            """);
        var crowd = new CitizenCrowd(new CitizensConfig { MaxCount = 60 }, church, new MassWorld(), e, 1);
        var router = new CitizenHitRouter();
        router.Add(crowd); router.Add(w);

        crowd.SpawnRole(CitizenRole.Pastor, new[] { church.Pastor!.Pos }, 0);
        foreach (var m in church.Monks) crowd.SpawnRole(CitizenRole.Monk, new[] { m.Pos }, 0);
        r.StartNow(0, 0); // a detour opens during the Mass
        Assert.Equal(3, crowd.Count);
        Assert.Equal(4, w.Count);
        Assert.Single(crowd.AliveOfRole(CitizenRole.Pastor));
        Assert.DoesNotContain(crowd.AliveOfRole(CitizenRole.Pastor).Concat(crowd.AliveOfRole(CitizenRole.Monk)), c => c.HitId >= CitizenRules.FirstWorkerHitId);

        crowd.Finish(1);        // the Mass completes: its citizens stand and clap, the workers sit on
        crowd.Flee();           // and the demon: they sprint, the workers sit on
        Assert.Equal(3, crowd.KillAll(citizens: false)); // the demon's fire: the pastor and the monks burn, not the workers
        Assert.Equal(4, w.AliveCount);
        Assert.All(w.Workers, c => Assert.Equal(CitizenState.Seated, c.State));

        crowd.Clear();          // the Mass is over: its body count goes, the detour stays
        Assert.Equal(0, crowd.Count);
        Assert.Equal(4, w.Count);
        Assert.Equal(DamageResult.Killed, router.Hit(w.AliveOfRole(CitizenRole.Mayor).Single().HitId, 1, 1)); // hits reach each crowd under its own ids
        Assert.Equal(RozhlasPhase.OnAir, r.Phase);

        crowd.SpawnRole(CitizenRole.Pastor, new[] { church.Pastor.Pos }, 0);
        var pastor = crowd.AliveOfRole(CitizenRole.Pastor).Single();
        w.Clear();              // the detour ends: the Mass's pastor stays
        Assert.Equal(0, w.Count);
        Assert.True(pastor.Alive);
        Assert.Equal(DamageResult.Killed, router.Hit(pastor.HitId, 1, 1));
        Assert.Equal(DamageResult.Ignored, router.Hit(CitizenRules.FirstWorkerHitId, 1, 1)); // a hit id nobody holds
    }

    sealed class MassWorld : ICitizenWorld
    {
        public IReadOnlyList<Vector3> HouseDoors(Vector3 around, float minM, float radiusM) => new[] { new Vector3(-8, 0, 3) };
        public IReadOnlyList<Vector3> Route(Vector3 from, Vector3 to) => new[] { from, to };
    }
}
