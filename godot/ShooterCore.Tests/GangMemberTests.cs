using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The Gang members of the Thieves event (docs/events/thieves/02): the Hit count per kind, aggro, the melee windup, fleeing, the route ration, hit routing and the entity layout. State machines run Strict.</summary>
public class GangMemberTests
{
    const float Dt = 1f / 30f;
    const byte Bullet = 0, Rocket = 1, MountedGun = 2, Fall = 255; // weapon bytes of the fake source below

    static readonly ThievesFile Shipped = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data")).Thieves;

    sealed class FakeWorld : IGangWorld
    {
        public readonly List<GangPlayer> List = new();
        public readonly List<(ushort Player, float Damage, ushort Member)> Hurts = new();
        public IReadOnlyList<GangPlayer> Players => List;
        public void Hurt(ushort player, float damage, ushort memberHitId) => Hurts.Add((player, damage, memberHitId));
        public FakeWorld Player(ushort id, float x, float z, bool onFoot = true) { List.Add(new GangPlayer(id, new Vector3(x, 0, z), onFoot)); return this; }
        public void Move(ushort id, float x, float z) { var i = List.FindIndex(p => p.Id == id); List[i] = List[i] with { Feet = new Vector3(x, 0, z) }; }
        public void Remove(ushort id) => List.RemoveAll(p => p.Id == id);
    }

    sealed class Rig
    {
        public readonly GangConfig Cfg;
        public readonly FakeWorld World = new();
        public readonly WorldEntityManager Entities;
        public readonly GangRoster Roster;
        public readonly List<GangKilled> Kills = new();
        public bool NoRoutes;
        public int RoutesServed;

        public Rig(Action<GangConfig>? tune = null)
        {
            Cfg = ThievesFile.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "thieves.json"))).Gang;
            tune?.Invoke(Cfg);
            var types = new WorldEntityRegistry();
            GangRules.Register(types);
            Entities = new WorldEntityManager(types);
            Roster = new GangRoster(Cfg, World, Entities, 7) { SourceOf = w => w switch { Rocket => HitSource.Explosive, MountedGun => HitSource.VehicleWeapon, Bullet => HitSource.Bullet, _ => HitSource.None } };
            Roster.Killed += Kills.Add;
        }

        public ThiefGang Gang(float x = 0, float z = 0) => Roster.NewGang(new Vector3(x, 0, z));

        /// <summary>One tick as the server does it: who fights whom, the entities, then one route.</summary>
        public void Step()
        {
            Roster.Update(Dt);
            Entities.Tick(Dt);
            if (Roster.ServeRoute((from, to) => { RoutesServed++; return NoRoutes ? Array.Empty<Vector3>() : new[] { from, to }; })) { }
        }

        public void Run(float seconds) { for (var t = 0f; t < seconds; t += Dt) Step(); }
    }

    static GangMember One(ThiefGang gang, GangMemberKind kind, float x = 0, float z = 0) => gang.Add(kind, new Vector3(x, 0, z), 0)!;

    // ---- data and layout ----

    [Fact]
    public void ShippedNumbersAreTheTicketsAndTheFileRejectsNonsense()
    {
        var g = Shipped.Gang;
        Assert.Equal((5f, 8.75f, 20f), (g.Thief.Hits, g.Enforcer.Hits, g.GangBoss.Hits));
        Assert.Equal((10f, 25f, 10f), (g.Thief.Melee.Damage, g.Enforcer.Melee.Damage, g.GangBoss.Melee.Damage));
        Assert.All(new[] { g.Thief, g.Enforcer, g.GangBoss }, k => { Assert.Equal(2f, k.Melee.ReachM); Assert.Equal(1f, k.Melee.IntervalSeconds); Assert.True(k.Melee.WindupSeconds < 1f); });
        Assert.Equal((1, 2, 5), (g.Raid.GangBosses, g.Raid.Enforcers, g.Raid.Thieves));
        Assert.Equal(10f, g.CorpseSeconds);
        Assert.Equal(2f, g.Hits.HeadshotWeight);
        Assert.Throws<DataFileException>(() => ThievesFile.Parse("""{"version":1,"gang":{"thief":{"hits":0}}}"""));
        Assert.Throws<DataFileException>(() => ThievesFile.Parse("""{"version":1,"gang":{"aggro":{"forget_seconds":0,"give_up_m":5}}}"""));
        Assert.Throws<DataFileException>(() => ThievesFile.Parse("""{"version":1,"gang":{"thief":{"melee":{"windup_seconds":2,"interval_seconds":1}}}}"""));
    }

    [Fact]
    public void TheEntityStateLayoutIsLookStateByteAndKindAliveCarried()
    {
        var rig = new Rig();
        var gang = rig.Gang();
        var boss = One(gang, GangMemberKind.GangBoss, 3, 4);
        boss.Carried = 5;
        var s = boss.Serialize();
        Assert.Equal(GangRules.TypeId, boss.TypeId);
        Assert.Equal(57, GangRules.TypeId);
        Assert.Equal(boss.Look, s.Owner);
        Assert.Equal((byte)GangMemberState.Guard, s.State);
        Assert.Equal(GangMemberKind.GangBoss, GangRules.KindOf(s.Hp));
        Assert.True(GangRules.AliveOf(s.Hp));
        Assert.Equal(5, GangRules.CarriedOf(s.Hp));
        Assert.Equal(new Vector3(3, 0, 4), s.Position);
        foreach (var kind in Enum.GetValues<GangMemberKind>())
            foreach (var alive in new[] { true, false })
            {
                var hp = GangRules.PackHp(kind, alive, 7);
                Assert.Equal((kind, alive, (byte)7), (GangRules.KindOf(hp), GangRules.AliveOf(hp), GangRules.CarriedOf(hp)));
            }
        boss.Kill(1);
        Assert.False(GangRules.AliveOf(boss.Serialize().Hp));
        Assert.Equal(GangMemberState.Dead, GangRules.StateOf(boss.Serialize().State));
        Assert.Equal(GangMemberState.Idle, GangRules.StateOf(200)); // a state this build does not know stands
    }

    [Fact]
    public void TheCrewIsOneBossTwoEnforcersFiveThievesAndTheHitIdsAreUniqueAndInRange()
    {
        var rig = new Rig();
        var gang = rig.Gang(10, 10);
        Assert.Equal(8, gang.SpawnCrew(new Vector3(10, 0, 10), p => p));
        Assert.Equal(new[] { 1, 2, 5 }, new[] { GangMemberKind.GangBoss, GangMemberKind.Enforcer, GangMemberKind.Thief }.Select(k => gang.Members.Count(m => m.Kind == k)));
        var ids = gang.Members.Select(m => m.HitId).ToList();
        Assert.Equal(8, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(GangRules.IsHitId(id)));
        Assert.False(GangRules.IsHitId((ushort)(GangRules.FirstHitId - 1)));
        Assert.False(GangRules.IsHitId((ushort)(GangRules.FirstHitId + GangRules.HitIdCount)));
        Assert.Equal(8, rig.Entities.Count);
        Assert.Equal(new Vector3(10, 0, 10), gang.Members[0].Position); // the Gang boss in the middle
        Assert.All(gang.Members.Skip(1), m => Assert.InRange(Vector3.Distance(m.Position, gang.Members[0].Position), 2.9f, 3.1f));
        rig.Roster.Clear();
        Assert.Equal(0, rig.Entities.Count);
        Assert.Equal(0, rig.Roster.MemberCount);
    }

    [Fact]
    public void ASnapFarFromTheCentreFallsBackToTheCentre()
    {
        var rig = new Rig();
        var gang = rig.Gang();
        gang.SpawnCrew(Vector3.Zero, p => p + new Vector3(40, 0, 0)); // the mesh is far from the ring
        Assert.All(gang.Members, m => Assert.Equal(Vector3.Zero, m.Position));
    }

    // ---- the Hit count ----

    [Theory]
    [InlineData(GangMemberKind.Thief, 5)]
    [InlineData(GangMemberKind.Enforcer, 9)] // 8.75: the ninth bullet kills
    [InlineData(GangMemberKind.GangBoss, 20)]
    public void ABulletCountsOneAndTheHitCountKills(GangMemberKind kind, int hits)
    {
        var rig = new Rig();
        var m = One(rig.Gang(), kind);
        for (var i = 1; i < hits; i++) Assert.Equal(DamageResult.Damaged, rig.Roster.Hit(m.HitId, 1, Bullet, 14, KillCause.Standard));
        Assert.True(m.Alive);
        Assert.Equal(DamageResult.Killed, rig.Roster.Hit(m.HitId, 1, Bullet, 14, KillCause.Standard));
        Assert.False(m.Alive);
        Assert.Equal(hits, m.HitsTaken);
        var k = Assert.Single(rig.Kills);
        Assert.Equal((m.HitId, (ushort)1, kind), (k.HitId, k.Killer, k.Kind));
    }

    [Fact]
    public void AHeadshotCountsTwoAnExplosiveItsWeightAndAVehicleWeaponItsOwn()
    {
        var rig = new Rig();
        var gang = rig.Gang();
        var thief = One(gang, GangMemberKind.Thief);
        for (var i = 0; i < 2; i++) rig.Roster.Hit(thief.HitId, 1, Bullet, 30, KillCause.Headshot);
        Assert.Equal(4, thief.HitsTaken);
        Assert.Equal(DamageResult.Killed, rig.Roster.Hit(thief.HitId, 1, Bullet, 30, KillCause.Headshot)); // three headshots
        var boss = One(gang, GangMemberKind.GangBoss);
        var w = rig.Cfg.Hits;
        rig.Roster.Hit(boss.HitId, 1, MountedGun, 20, KillCause.Standard);
        Assert.Equal(w.VehicleWeaponWeight, boss.HitsTaken);
        rig.Roster.Hit(boss.HitId, 1, Rocket, 90, KillCause.Standard);
        Assert.Equal(w.VehicleWeaponWeight + w.ExplosiveWeight, boss.HitsTaken);
        Assert.True(boss.Alive);
        Assert.Equal(DamageResult.Killed, rig.Roster.Hit(boss.HitId, 1, Rocket, 90, KillCause.Headshot)); // a blast has no headshot bonus: 3 + 12 + 12 >= 20
    }

    [Fact]
    public void RammingKillsOutrightAndWhatHasNoWeightIsAbsorbed()
    {
        var rig = new Rig();
        var gang = rig.Gang();
        var boss = One(gang, GangMemberKind.GangBoss);
        Assert.Equal(DamageResult.Ignored, rig.Roster.Hit(boss.HitId, 1, Fall, 50, KillCause.Standard)); // a fall or a crash does nothing
        Assert.Equal(DamageResult.Ignored, rig.Roster.Hit(boss.HitId, 1, Bullet, 0, KillCause.Standard));
        Assert.Equal(0, boss.HitsTaken);
        Assert.Equal(DamageResult.Killed, rig.Roster.Hit(boss.HitId, 1, SpecialWeapons.Vehicle, 100, KillCause.Roadkill)); // run over: dead, whatever its Hit count
        Assert.False(boss.Alive);
        Assert.Equal(DamageResult.Ignored, rig.Roster.Hit(boss.HitId, 1, Bullet, 14, KillCause.Standard)); // a dead one takes nothing
        Assert.Equal(DamageResult.Ignored, rig.Roster.Hit(0x7fff, 1, Bullet, 14, KillCause.Standard)); // not a member
    }

    [Fact]
    public void TheMatchRoutesTheIdsOfTheRangeToTheRosterAndNotOutsideARound()
    {
        var rig = new Rig();
        var m = One(rig.Gang(), GangMemberKind.Thief);
        var match = new MatchSim(new MatchConfig(), () => 0, new List<Vector3> { Vector3.Zero }) { Gang = rig.Roster };
        Assert.Equal(DamageResult.Ignored, match.ApplyDamage(m.HitId, 1, Bullet, 14)); // no round running
        Assert.Equal(0, m.HitsTaken);
        Assert.Equal(DamageResult.Ignored, new MatchSim(new MatchConfig(), () => 0, new List<Vector3> { Vector3.Zero }).ApplyDamage(m.HitId, 1, Bullet, 14)); // no roster
    }

    [Fact]
    public void TurretsAndStreaksShootThemSoTheyAreNotInTheNonEnemyIds()
    {
        var rig = new Rig();
        var m = One(rig.Gang(), GangMemberKind.Thief);
        Assert.False(NonEnemyTargets.IsNonEnemy(m.HitId));
        Assert.True(NonEnemyTargets.IsNonEnemy(CitizenRules.FirstHitId)); // the others stay as they were
    }

    // ---- hit targets ----

    [Fact]
    public void LivingMembersAreTargetsAtTheirScaleAndTheDeadAreNot()
    {
        var rig = new Rig();
        var gang = rig.Gang();
        var boss = One(gang, GangMemberKind.GangBoss, 5, 0);
        var thief = One(gang, GangMemberKind.Thief, 8, 0);
        var list = new List<HitTarget>();
        gang.AddTargets(list);
        Assert.Equal(2, list.Count);
        Assert.Equal(rig.Cfg.GangBoss.HitScale, list.Single(t => t.Id == boss.HitId).Scale);
        Assert.DoesNotContain(list, t => t.HeadGuarded); // a headshot is a head hit
        thief.Kill(1);
        list.Clear();
        gang.AddTargets(list);
        Assert.Equal(boss.HitId, Assert.Single(list).Id);
    }

    [Fact]
    public void ARewoundShotSeesAMemberWhereItWasAndFallsBackToWhereItIsNow()
    {
        var rig = new Rig();
        var gang = rig.Gang();
        var m = One(gang, GangMemberKind.Thief, 0, 0);
        gang.RecordPoses(100);
        m.SetRoute(new[] { Vector3.Zero, new Vector3(30, 0, 0) });
        m.SetTarget(new GangPlayer(1, new Vector3(30, 0, 0), true)); // it runs
        rig.World.Player(1, 30, 0);
        gang.NoteHit(1);
        for (var t = 101u; t <= 130; t++) { rig.Step(); gang.RecordPoses(t); }
        Assert.True(m.Position.X > 4);
        var list = new List<HitTarget>();
        gang.AddTargets(list, 100);
        Assert.Equal(0f, Assert.Single(list).Feet.X, 3); // where the shooter's screen showed it
        list.Clear();
        gang.AddTargets(list, 5); // before it was there: the pose it appeared with
        Assert.Equal(0f, Assert.Single(list).Feet.X, 3);
        list.Clear();
        gang.AddTargets(list, 131); // no pose recorded at that tick: where it stands
        Assert.Equal(m.Position.X, Assert.Single(list).Feet.X, 3);
        list.Clear();
        gang.AddTargets(list);
        Assert.Equal(m.Position.X, Assert.Single(list).Feet.X, 3);
    }

    // ---- aggro ----

    [Fact]
    public void APlayerWhoHitAMemberIsFoughtByTheEnforcersTheBossAndAFreeThiefButNotByAnother()
    {
        var rig = new Rig();
        var gang = rig.Gang();
        gang.SpawnCrew(Vector3.Zero, p => p);
        rig.World.Player(1, 12, 0).Player(2, -15, 0);
        var hit = gang.Members.First(m => m.Kind == GangMemberKind.Thief);
        Assert.Empty(gang.Aggro);
        rig.Roster.Hit(hit.HitId, 1, Bullet, 14, KillCause.Standard);
        Assert.Equal(new ushort[] { 1 }, gang.Aggro);
        rig.Step();
        Assert.All(gang.Members.Where(m => m.Alive), m => { Assert.Equal((ushort)1, m.Target!.Value.Id); Assert.Equal(GangMemberState.Chase, m.State); });
        rig.Run(0.5f);
        Assert.All(gang.Members, m => Assert.True(Vector3.Distance(m.Position, m.Home) > 0.5f, $"{m.Kind} runs at the player")); // everyone has left the ring
        Assert.DoesNotContain((ushort)2, gang.Aggro);
    }

    [Fact]
    public void AHitFromSomeoneWhoIsNotAPlayerOrIsDeadStartsNothing()
    {
        var rig = new Rig();
        var gang = rig.Gang();
        var m = One(gang, GangMemberKind.Enforcer);
        rig.Roster.Hit(m.HitId, 9, Bullet, 14, KillCause.Standard); // a turret's owner who is gone, or a dead player's last bullet
        Assert.Empty(gang.Aggro);
        rig.Step();
        Assert.Null(m.Target);
        Assert.Equal(GangMemberState.Guard, m.State);
    }

    [Fact]
    public void ThePlayerIsLetGoWhenTheyDieOrGetFartherThanGiveUpFromEveryLivingMember()
    {
        var rig = new Rig();
        var gang = rig.Gang();
        var enforcer = One(gang, GangMemberKind.Enforcer);
        rig.World.Player(1, 10, 0).Player(2, 10, 0);
        gang.NoteHit(1); gang.NoteHit(2);
        rig.Step();
        Assert.Equal(GangMemberState.Chase, enforcer.State);
        rig.World.Remove(2); // died
        rig.Step();
        Assert.Equal(new ushort[] { 1 }, gang.Aggro);
        rig.World.Move(1, rig.Cfg.Aggro.GiveUpM + enforcer.Position.X + 5, 0); // got away
        rig.Step();
        Assert.Empty(gang.Aggro);
        Assert.Null(enforcer.Target);
        rig.Step();
        Assert.Equal(GangMemberState.Guard, enforcer.State); // back to its post
    }

    [Fact]
    public void AMemberThatIsFarFromThePlayerButAMateIsNearKeepsTheAggro()
    {
        var rig = new Rig();
        var gang = rig.Gang();
        One(gang, GangMemberKind.Enforcer, 0, 0);
        One(gang, GangMemberKind.Thief, 100, 0);
        rig.World.Player(1, 110, 0); // 10 m from the Thief, 110 from the Enforcer
        gang.NoteHit(1);
        rig.Step();
        Assert.Equal(new ushort[] { 1 }, gang.Aggro);
    }

    [Fact]
    public void StandingNearAWorkingThiefIsNoAggroAndABusyThiefDoesNotJoinAFight()
    {
        var rig = new Rig();
        var gang = rig.Gang();
        var thief = One(gang, GangMemberKind.Thief);
        rig.World.Player(1, 2, 0);
        rig.Step();
        Assert.Empty(gang.Aggro);
        thief.Carried = 1; // hands full: a busy Thief does not join a fight
        gang.NoteHit(1);
        rig.Step();
        Assert.Null(thief.Target);
        Assert.False(thief.CanFight);
    }

    [Fact]
    public void AggroLapsesForgetSecondsAfterTheLastHitAndEachHitStartsItAgain()
    {
        var rig = new Rig(c => c.Aggro.ForgetSeconds = 4);
        var gang = rig.Gang();
        var e = One(gang, GangMemberKind.Enforcer);
        var thief = One(gang, GangMemberKind.Thief, 3, 0);
        rig.World.Player(1, 25, 0);
        gang.NoteHit(1);
        rig.Run(3);
        Assert.Equal(new ushort[] { 1 }, gang.Aggro);
        Assert.Equal(GangMemberState.Chase, e.State);
        rig.Roster.Hit(thief.HitId, 1, Bullet, 14, KillCause.Standard); // keeps hitting: the clock starts again
        rig.Run(3);
        Assert.Equal(new ushort[] { 1 }, gang.Aggro);
        rig.Run(1.5f);
        Assert.Empty(gang.Aggro);
        Assert.Null(e.Target);
        rig.Run(0.2f);
        Assert.Equal(GangMemberState.Guard, e.State); // back to its post
        Assert.NotEqual(GangMemberState.Chase, thief.State); // the Thief stands (or walks) again
    }

    [Fact]
    public void AMemberInItsWindupWhenTheAggroLapsesStopsAndGoesBack()
    {
        var rig = new Rig(c => c.Aggro.ForgetSeconds = 0.5f);
        var gang = rig.Gang();
        var e = One(gang, GangMemberKind.Enforcer);
        rig.World.Player(1, 1.5f, 0);
        gang.NoteHit(1);
        rig.Step();
        rig.Step();
        Assert.Equal(GangMemberState.Windup, e.State);
        rig.Run(1);
        Assert.Empty(gang.Aggro);
        Assert.Equal(GangMemberState.Guard, e.State);
    }

    // ---- surround and separation ----

    static float MinPairDistance(IEnumerable<GangMember> ms)
    {
        var l = ms.ToList(); var best = float.PositiveInfinity;
        for (var i = 0; i < l.Count; i++) for (var j = i + 1; j < l.Count; j++) best = MathF.Min(best, Vector3.Distance(l[i].Position, l[j].Position));
        return best;
    }

    [Fact]
    public void ChasersOfOnePlayerTakeDistinctSlotsAndDoNotStackIntoOneBlob()
    {
        var rig = new Rig(c => c.Aggro.ForgetSeconds = 60);
        var gang = rig.Gang();
        var chasers = Enumerable.Range(0, 8).Select(i => One(gang, GangMemberKind.Enforcer, -1 + i * 0.2f, 0)).ToList(); // nearly on one spot
        rig.World.Player(1, 30, 0);
        gang.NoteHit(1);
        rig.Step();
        Assert.Equal(8, chasers.Select(c => c.Slot).Distinct().Count());
        Assert.Equal(8, chasers.Select(c => c.SlotPoint!.Value).Distinct().Count());
        var slots = chasers.Select(c => c.Slot).ToList();
        rig.Run(8); // all of them arrive and strike
        Assert.Equal(slots, chasers.Select(c => c.Slot).ToList()); // stable while the fight lasts
        Assert.True(MinPairDistance(chasers) > rig.Cfg.SeparationM * 0.8f, $"min distance {MinPairDistance(chasers)}");
        Assert.All(chasers, c => Assert.True(Vector3.Distance(c.Position, new Vector3(30, 0, 0)) <= rig.Cfg.Enforcer.Melee.ReachM, "all of them are within reach"));
    }

    [Fact]
    public void ASlotThatCannotBeStruckFromFallsBackToThePlayersFeetAndSlotsAreFreedWhenTheFightEnds()
    {
        var rig = new Rig(c => c.Aggro.ForgetSeconds = 1);
        var gang = rig.Gang();
        var e = One(gang, GangMemberKind.Enforcer);
        rig.World.Player(1, 20, 0);
        gang.NoteHit(1);
        rig.Step();
        Assert.Equal(0, e.Slot);
        var onRing = e.SlotPoint!.Value;
        Assert.InRange(Vector3.Distance(onRing, new Vector3(20, 0, 0)), 1f, 1.7f);
        rig.Run(2);
        Assert.Equal(-1, e.Slot);
    }

    [Fact]
    public void MembersOfAnyRaidAreNudgedApartButADeadOneNeitherMovesNorPushes()
    {
        var rig = new Rig();
        var a = rig.Gang(); var b = rig.Gang();
        var x = One(a, GangMemberKind.Thief, 0, 0);
        var y = One(b, GangMemberKind.Enforcer, 0.1f, 0);
        rig.Run(2);
        Assert.True(Vector3.Distance(x.Position, y.Position) >= rig.Cfg.SeparationM - 0.05f);
        var dead = One(a, GangMemberKind.Thief, 10, 10);
        var stand = One(b, GangMemberKind.Thief, 10.1f, 10);
        dead.Kill(0);
        var (atDead, atStand) = (dead.Position, stand.Position);
        rig.Run(1);
        Assert.Equal((atDead, atStand), (dead.Position, stand.Position)); // a body neither is pushed nor pushes
    }

    // ---- melee ----

    [Fact]
    public void ABlowLandsAfterTheWindupOnlyIfThePlayerIsStillInReach()
    {
        var rig = new Rig();
        var gang = rig.Gang();
        var e = One(gang, GangMemberKind.Enforcer);
        rig.World.Player(1, 1.2f, 0);
        gang.NoteHit(1);
        rig.Step();
        Assert.Equal(GangMemberState.Windup, e.State);
        rig.Run(rig.Cfg.Enforcer.Melee.WindupSeconds * 0.8f);
        Assert.Empty(rig.World.Hurts); // the windup is not a hit
        rig.World.Move(1, 6, 0); // steps out of reach in the windup
        rig.Run(0.2f);
        Assert.Empty(rig.World.Hurts);
        Assert.Equal(GangMemberState.Swing, e.State);
        rig.Run(0.7f);
        Assert.Equal(GangMemberState.Chase, e.State); // the blow missed: after the swing it runs after them again
        Assert.Empty(rig.World.Hurts);
    }

    [Fact]
    public void ABlowThatLandsHurtsForTheKindsDamageOneAHitASecond()
    {
        var rig = new Rig();
        var gang = rig.Gang();
        var e = One(gang, GangMemberKind.Enforcer);
        var thief = One(gang, GangMemberKind.Thief, 50, 50);
        rig.World.Player(1, 1f, 0);
        gang.NoteHit(1);
        rig.Run(3.2f);
        var hits = rig.World.Hurts.Where(h => h.Member == e.HitId).ToList();
        Assert.InRange(hits.Count, 3, 4); // one a second (the first lands after the windup)
        Assert.All(hits, h => { Assert.Equal(1, h.Player); Assert.Equal(25f, h.Damage); });
        Assert.All(rig.World.Hurts.Where(h => h.Member == thief.HitId), h => Assert.Equal(10f, h.Damage));
    }

    [Fact]
    public void APlayerInAVehicleIsChasedButNeverStruck()
    {
        var rig = new Rig();
        var gang = rig.Gang();
        var e = One(gang, GangMemberKind.Enforcer);
        rig.World.Player(1, 0.5f, 0, onFoot: false);
        gang.NoteHit(1);
        rig.Run(3);
        Assert.Empty(rig.World.Hurts);
        Assert.Equal(GangMemberState.Chase, e.State);
    }

    [Fact]
    public void AChaseAsksForARouteAtMostEveryRepathSecondsAndRunsItAtTheRunSpeed()
    {
        var rig = new Rig(c => c.Aggro.ForgetSeconds = 60);
        var gang = rig.Gang();
        var e = One(gang, GangMemberKind.Enforcer);
        rig.World.Player(1, 25, 0);
        gang.NoteHit(1);
        rig.Run(2);
        Assert.InRange(rig.RoutesServed, 1, 2);
        Assert.InRange(e.Position.X, rig.Cfg.Enforcer.RunSpeed * 1.0f, rig.Cfg.Enforcer.RunSpeed * 2.0f);
        rig.Run(20);
        Assert.Contains(e.State, new[] { GangMemberState.Windup, GangMemberState.Swing }); // it caught up: windup and swing, over and over
        Assert.InRange(Vector3.Distance(e.Position, new Vector3(25, 0, 0)), 0f, 2.1f);
    }

    // ---- standing, walking, guarding ----

    [Fact]
    public void AThiefIdlesThenWalksToAPointNearThePostThenStandsAgain()
    {
        var rig = new Rig(c => { c.IdleMinSeconds = 2; c.IdleMaxSeconds = 2; });
        var gang = rig.Gang(100, 100);
        var t = One(gang, GangMemberKind.Thief, 100, 100);
        Assert.Equal(GangMemberState.Idle, t.State);
        rig.Run(1.9f);
        Assert.Equal(GangMemberState.Idle, t.State);
        rig.Run(0.3f);
        Assert.Equal(GangMemberState.Walk, t.State);
        var walked = 0f;
        while (t.State == GangMemberState.Walk && walked < 15) { rig.Step(); walked += Dt; }
        Assert.Equal(GangMemberState.Idle, t.State); // arrived
        Assert.InRange(walked, 0.1f, rig.Cfg.WanderM / rig.Cfg.Thief.WalkSpeed + 0.5f);
        Assert.True(Vector3.Distance(t.Position, gang.Post) <= rig.Cfg.WanderM + 0.1f);
    }

    [Fact]
    public void AWalkThatGetsNoRouteGivesUpAndStands()
    {
        var rig = new Rig(c => { c.IdleMinSeconds = 1; c.IdleMaxSeconds = 1; }) { NoRoutes = true };
        var t = One(rig.Gang(), GangMemberKind.Thief);
        rig.Run(1.5f);
        Assert.Equal(GangMemberState.Idle, t.State);
        Assert.Equal(Vector3.Zero, t.Position);
    }

    [Fact]
    public void TheBossStandsAtThePostAndAnEnforcerFollowsTheThiefItEscorts()
    {
        var rig = new Rig(c => { c.IdleMinSeconds = 1; c.IdleMaxSeconds = 1; });
        var gang = rig.Gang();
        gang.SpawnCrew(Vector3.Zero, p => p);
        var boss = gang.Members.Single(m => m.Kind == GangMemberKind.GangBoss);
        var enforcers = gang.Members.Where(m => m.Kind == GangMemberKind.Enforcer).ToList();
        rig.Step();
        Assert.All(enforcers, e => Assert.NotNull(e.Escort));
        Assert.Equal(2, enforcers.Select(e => e.Escort).Distinct().Count()); // not both on one Thief
        rig.Run(40);
        Assert.True(Vector3.Distance(Vector3.Zero, boss.Position) <= 1.5f, "the boss stays within its stop of the post");
        Assert.All(enforcers, e => Assert.True(Vector3.Distance(e.Position, e.Escort!.Position) <= rig.Cfg.GuardFollowM * 2.2f, "an escort stays by its Thief"));
        var escort = enforcers[0].Escort!;
        escort.Kill(1);
        rig.Step();
        Assert.NotSame(escort, enforcers[0].Escort);
        Assert.True(enforcers[0].Escort!.Alive);
    }

    // ---- fleeing and ending ----

    [Fact]
    public void FleeRunsForTheEdgeAndTheEntityEndsThere()
    {
        var rig = new Rig();
        var gang = rig.Gang();
        var m = One(gang, GangMemberKind.Thief);
        gang.FleeAll(new Vector3(60, 0, 0));
        Assert.Equal(GangMemberState.Flee, m.State);
        rig.Run(5);
        Assert.Equal(1, rig.Entities.Count);
        Assert.InRange(m.Position.X, 10f, 59f); // running
        Assert.Equal(GangMemberState.Flee, m.State);
        rig.Run(10);
        Assert.Equal(0, rig.Entities.Count); // gone at the edge
        Assert.Equal(WorldDespawnReason.Removed, m.Ended);
        Assert.Equal(0, gang.Count);
    }

    [Fact]
    public void AFleeingMemberIgnoresAggroAndTheDeadDoNotFlee()
    {
        var rig = new Rig();
        var gang = rig.Gang();
        var runner = One(gang, GangMemberKind.Enforcer);
        var dead = One(gang, GangMemberKind.Thief);
        dead.Kill(1);
        gang.FleeAll(new Vector3(500, 0, 0));
        Assert.Equal(GangMemberState.Dead, dead.State);
        rig.World.Player(1, 3, 0);
        gang.NoteHit(1);
        rig.Step();
        Assert.Equal(GangMemberState.Flee, runner.State);
        Assert.Null(runner.Target);
        Assert.False(runner.CanFight);
    }

    [Fact]
    public void FleeWithNoRouteEndsAtOnceAndFleeTimesOut()
    {
        var rig = new Rig { NoRoutes = true };
        var m = One(rig.Gang(), GangMemberKind.Thief);
        m.Flee(new Vector3(900, 0, 0));
        rig.Step();
        Assert.Equal(WorldDespawnReason.Removed, m.Ended);

        var slow = new Rig(c => c.FleeSeconds = 2);
        var n = One(slow.Gang(), GangMemberKind.Thief);
        n.Flee(new Vector3(9000, 0, 0));
        slow.Run(1.9f);
        Assert.Null(n.Ended);
        slow.Run(0.3f);
        Assert.Equal(WorldDespawnReason.Removed, n.Ended); // out of time: gone where it stands
    }

    [Fact]
    public void ADeadMemberLiesCorpseSecondsThenTheEntityEndsAndItsIdIsFree()
    {
        var rig = new Rig();
        var gang = rig.Gang();
        var m = One(gang, GangMemberKind.Thief);
        var id = m.HitId;
        rig.Roster.Hit(id, 1, SpecialWeapons.Vehicle, 100, KillCause.Roadkill);
        rig.Run(rig.Cfg.CorpseSeconds - 0.5f);
        Assert.Equal(1, rig.Entities.Count);
        Assert.Equal(GangMemberState.Dead, m.State);
        rig.Run(1f);
        Assert.Equal(0, rig.Entities.Count);
        Assert.Equal(WorldDespawnReason.Expired, m.Ended);
        Assert.False(gang.Owns(id));
        Assert.Equal(DamageResult.Ignored, rig.Roster.Hit(id, 1, Bullet, 14, KillCause.Standard));
    }

    [Fact]
    public void AMemberWhoseTargetDiesInTheWindupDoesNotSwing()
    {
        var rig = new Rig();
        var gang = rig.Gang();
        var e = One(gang, GangMemberKind.Enforcer);
        rig.World.Player(1, 1, 0);
        gang.NoteHit(1);
        rig.Step();
        Assert.Equal(GangMemberState.Windup, e.State);
        rig.World.Remove(1); // the player died
        rig.Run(1);
        Assert.Empty(rig.World.Hurts);
        Assert.Equal(GangMemberState.Guard, e.State);
    }

    // ---- the route ration ----

    [Fact]
    public void OneRouteIsServedPerCallTheOldestRequestFirstAndFalseWhenNoneWait()
    {
        var rig = new Rig();
        var gang = rig.Gang();
        var a = One(gang, GangMemberKind.Enforcer, 0, 0);
        var b = One(gang, GangMemberKind.Enforcer, 1, 0);
        var c = One(gang, GangMemberKind.Enforcer, 2, 0);
        Assert.False(gang.ServeRoute((f, t) => new[] { f, t }));
        var served = new List<Vector3>();
        IReadOnlyList<Vector3> Take(Vector3 f, Vector3 t) { served.Add(f); return new[] { f, t }; }
        c.Flee(new Vector3(50, 0, 0));
        b.Flee(new Vector3(50, 0, 0));
        Assert.Equal(2, gang.RoutesWaiting);
        Assert.True(gang.ServeRoute(Take)); // the first scan queues what waits, in the order of the list (b, c): b is oldest
        Assert.Equal(1, gang.RoutesWaiting);
        a.Flee(new Vector3(50, 0, 0)); // arrives later: behind b
        Assert.True(gang.ServeRoute(Take));
        Assert.True(gang.ServeRoute(Take));
        Assert.Equal(new[] { b.Position, c.Position, a.Position }, served); // a asked last: it waits behind both
        Assert.False(gang.ServeRoute((f, t) => new[] { f, t }));
    }

    [Fact]
    public void BeginRouteTakesTheOldestUnsentRequestOnceAndDeliverGivesItsAnswer()
    {
        var rig = new Rig();
        var gang = rig.Gang();
        var a = One(gang, GangMemberKind.Enforcer, 0, 0);
        var b = One(gang, GangMemberKind.Enforcer, 1, 0);
        b.Flee(new Vector3(50, 0, 0));
        Assert.True(gang.BeginRoute(10, out var from, out _));
        Assert.Equal(b.Position, from);
        Assert.Equal(10, b.RouteTicket);
        Assert.Equal(0, gang.RoutesWaiting);
        Assert.Equal(1, gang.RoutesInFlight);
        a.Flee(new Vector3(50, 0, 0));
        Assert.True(gang.BeginRoute(11, out from, out _)); // b is out already: not offered again, a goes
        Assert.Equal(a.Position, from);
        Assert.False(gang.BeginRoute(12, out _, out _));
        Assert.Equal(2, gang.RoutesInFlight);
        Assert.True(gang.DeliverRoute(10, new[] { b.Position, new Vector3(50, 0, 0) }));
        Assert.Equal(1, gang.RoutesInFlight);
        Assert.Null(b.RouteRequest);
        Assert.Equal(2, b.Route.Count);
    }

    [Fact]
    public void ADeadMemberReceivesNoRouteAndAStaleTicketIsDropped()
    {
        var rig = new Rig();
        var gang = rig.Gang();
        var e = One(gang, GangMemberKind.Enforcer);
        e.Flee(new Vector3(50, 0, 0));
        Assert.True(gang.BeginRoute(20, out _, out _));
        e.Kill(1);
        Assert.False(gang.DeliverRoute(20, new[] { e.Position, new Vector3(50, 0, 0) }));
        Assert.Empty(e.Route);
        Assert.Equal(0, gang.RoutesInFlight);
        // a request cleared and replaced while out: the old answer is not the new request's
        var t = One(gang, GangMemberKind.Thief, 5, 0);
        t.Board(new Vector3(50, 0, 0));
        Assert.True(gang.BeginRoute(21, out _, out _));
        t.Flee(new Vector3(-50, 0, 0)); // a new request (run for the edge): the ticket is forgotten
        Assert.Equal(0, t.RouteTicket);
        Assert.False(gang.DeliverRoute(21, new[] { t.Position, new Vector3(50, 0, 0) }));
        Assert.Empty(t.Route);
        Assert.NotNull(t.RouteRequest); // still waiting for its own
        Assert.True(gang.BeginRoute(22, out _, out var to));
        Assert.Equal(new Vector3(-50, 0, 0), to);
    }

    [Fact]
    public void AMemberWhoseRouteNeverComesAsksAgainAfterTheGiveUpTime()
    {
        var rig = new Rig();
        var gang = rig.Gang();
        var e = One(gang, GangMemberKind.Enforcer);
        e.Flee(new Vector3(50, 0, 0));
        Assert.True(gang.BeginRoute(30, out _, out _));
        Assert.False(gang.BeginRoute(31, out _, out _));
        for (var t = 0f; t < GangMember.RouteLostSeconds + 0.5f; t += Dt) e.Tick(Dt);
        Assert.Equal(0, e.RouteTicket);
        Assert.True(gang.BeginRoute(32, out _, out _));
        Assert.False(gang.DeliverRoute(30, new[] { e.Position, new Vector3(50, 0, 0) })); // the late answer to the first
    }

    [Fact]
    public void TheRosterBeginsRoutesOfAllRaidsInTurnAndDeliversByTicket()
    {
        var rig = new Rig();
        var g1 = rig.Gang();
        var g2 = rig.Gang(100, 0);
        var a = One(g1, GangMemberKind.Thief); a.Flee(new Vector3(50, 0, 0));
        var b = One(g2, GangMemberKind.Thief, 100, 0); b.Flee(new Vector3(150, 0, 0));
        Assert.True(rig.Roster.BeginRoute(1, out var f1, out _));
        Assert.True(rig.Roster.BeginRoute(2, out var f2, out _));
        Assert.NotEqual(f1, f2);
        Assert.Equal(2, rig.Roster.RoutesInFlight);
        Assert.False(rig.Roster.AnyRouteWaiting);
        Assert.True(rig.Roster.DeliverRoute(2, new[] { f2, f2 + new Vector3(5, 0, 0) }));
        Assert.False(rig.Roster.DeliverRoute(2, new[] { f2, f2 }));
        Assert.Equal(1, rig.Roster.RoutesInFlight);
    }

    [Fact]
    public void TheRosterTakesTurnsBetweenRaidsAndNeverServesTwoInACall()
    {
        var rig = new Rig();
        var g1 = rig.Gang();
        var g2 = rig.Gang(100, 0);
        var a = One(g1, GangMemberKind.Thief); a.Flee(new Vector3(50, 0, 0));
        var b = One(g2, GangMemberKind.Thief, 100, 0); b.Flee(new Vector3(150, 0, 0));
        Assert.True(rig.Roster.AnyRouteWaiting);
        var n = 0;
        Assert.True(rig.Roster.ServeRoute((f, t) => { n++; return new[] { f, t }; }));
        Assert.Equal(1, n);
        Assert.True(rig.Roster.AnyRouteWaiting);
        Assert.True(rig.Roster.ServeRoute((f, t) => { n++; return new[] { f, t }; }));
        Assert.False(rig.Roster.AnyRouteWaiting);
        Assert.False(rig.Roster.ServeRoute((f, t) => new[] { f, t }));
        Assert.Equal(2, n);
    }

    [Fact]
    public void ADeadMemberHasNoRequestLeftInTheQueue()
    {
        var rig = new Rig();
        var gang = rig.Gang();
        var e = One(gang, GangMemberKind.Enforcer);
        rig.World.Player(1, 25, 0);
        gang.NoteHit(1);
        rig.Roster.Update(Dt);
        rig.Entities.Tick(Dt); // the chase asks for a route; nobody serves it yet
        Assert.NotNull(e.RouteRequest);
        Assert.Equal(1, gang.RoutesWaiting);
        e.Kill(1);
        Assert.Null(e.RouteRequest);
        Assert.False(gang.ServeRoute((f, t) => new[] { f, t }));
    }

    // ---- state machine and placement ----

    [Fact]
    public void TheMachineEndsInFleeOrDeadAndEveryStateIsReachable()
    {
        var spec = GangMember.Spec;
        Assert.Equal(new[] { "Flee", "Dead" }, spec.Terminal); // Flee ends in the entity's removal at the edge
        Assert.Empty(spec.Unreachable);
        Assert.Equal("Idle", spec.InitialName);
        Assert.True(spec.Allows(GangMemberState.Windup, GangMemberState.Swing));
        Assert.False(spec.Allows(GangMemberState.Idle, GangMemberState.Windup)); // no blow without a chase first
        Assert.False(spec.Allows(GangMemberState.Dead, GangMemberState.Idle));
    }

    [Fact]
    public void RoadPointsLieOnTheRoadsInTheRingAroundThePlayerInWorldAxes()
    {
        // a road running east-west 40 m north of the origin (map y north = world -z), 200 m long
        var roads = new List<RoadLine> { new(new[] { new Vector2(-100, 40), new Vector2(100, 40) }, 6, "residential") };
        var pts = GangPlacement.RoadPoints(roads, new Vector3(0, 5, 0), 30, 60, 4, new Random(1));
        Assert.NotEmpty(pts);
        Assert.All(pts, p => { Assert.Equal(-40f, p.Z, 3); Assert.Equal(5f, p.Y); Assert.InRange(MathF.Sqrt(p.X * p.X + p.Z * p.Z), 30f, 60f); });
        Assert.Empty(GangPlacement.RoadPoints(roads, new Vector3(0, 0, 500), 30, 60, 4, new Random(1))); // nothing near
        Assert.NotEqual(pts, GangPlacement.RoadPoints(roads, new Vector3(0, 5, 0), 30, 60, 4, new Random(2))); // a different order
    }

    [Fact]
    public void TheNearestEdgePointIsOnTheBoxWhetherTheMemberIsInsideOrOut()
    {
        var min = new Vector2(-100, -50); var max = new Vector2(300, 150);
        Assert.Equal(new Vector3(-100, 2, 0), GangPlacement.NearestEdge(new Vector3(-90, 2, 0), min, max));
        Assert.Equal(new Vector3(120, 2, 150), GangPlacement.NearestEdge(new Vector3(120, 2, 140), min, max));
        Assert.Equal(new Vector3(300, 0, 20), GangPlacement.NearestEdge(new Vector3(400, 0, 20), min, max)); // outside: pulled to it
    }
}
