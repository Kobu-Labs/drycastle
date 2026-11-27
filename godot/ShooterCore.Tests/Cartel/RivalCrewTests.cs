using System.Numerics;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Weapons;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Cartel;

/// <summary>The Rival crew (#495, #496): the Gunman (aim, fire, line of sight, aim error), hostility on sight to a given set, the posted member, rival_crew.json and the crew split from the Thieves' Raids. Strict.</summary>
public class RivalCrewTests
{
    const float Dt = 1f / 30f;

    static readonly GameData Shipped = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data"));

    public RivalCrewTests() => StateMachineDiagnostics.Strict = true;

    sealed class FakeWorld : IGangWorld
    {
        public readonly List<GangPlayer> List = new();
        public readonly List<(ushort Player, float Damage, ushort Member)> Hurts = new();
        public readonly List<GangShot> Shots = new();
        public Func<Vector3, Vector3, bool> Sees = (_, _) => true;
        public IReadOnlyList<GangPlayer> Players => List;
        public void Hurt(ushort player, float damage, ushort memberHitId) => Hurts.Add((player, damage, memberHitId));
        public bool Clear(Vector3 from, Vector3 to) => Sees(from, to);
        public void Shoot(GangShot shot) => Shots.Add(shot);
        public void Player(ushort id, float x, float z) => List.Add(new GangPlayer(id, new Vector3(x, 0, z), true));
    }

    sealed class Rig
    {
        public readonly FakeWorld World = new();
        public readonly WorldEntityManager Entities;
        public readonly GangRoster Roster;
        public readonly GangConfig Rival = Shipped.Thieves.RivalCrew.GangOf(Shipped.Thieves.Gang);
        public int Routes;

        public Rig()
        {
            var types = new WorldEntityRegistry();
            GangRules.Register(types);
            Entities = new WorldEntityManager(types);
            Roster = new GangRoster(Shipped.Thieves.Gang, World, Entities, 3);
        }

        public ThiefGang Crew(Func<ushort, bool> hostile)
        {
            var g = Roster.NewGang(Vector3.Zero, Rival, GangCrew.RivalCrew);
            g.HostileTo = hostile;
            return g;
        }

        public void Run(float seconds)
        {
            for (var t = 0f; t < seconds; t += Dt)
            {
                Roster.Update(Dt);
                Entities.Tick(Dt);
                Roster.ServeRoute((from, to) => { Routes++; return new[] { from, to }; });
            }
        }
    }

    static float AngleDeg(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f)) * 180f / MathF.PI;

    // ---- data ----

    [Fact]
    public void TheFileIsShippedAndChecked()
    {
        var r = Shipped.Thieves.RivalCrew;
        Assert.Equal(3, r.Gunman.Gun!.Weapons.Count);
        Assert.All(r.Gunman.Gun.Weapons, w => Assert.Contains(Shipped.Weapons.Weapons, d => d.Id == w.Weapon && d.Kind == WeaponKind.Hitscan));
        Assert.True(r.Aggro.GiveUpM >= r.Sight.RadiusM);
        var g = r.GangOf(Shipped.Thieves.Gang);
        Assert.Equal(0, g.KillXp); // no XP for the Rival crew
        Assert.Equal(r.Enforcer.Hits, g.Enforcer.Hits);
        Assert.Same(Shipped.Thieves.Gang.Enforcer.Melee, g.Enforcer.Melee); // the Thieves' Enforcer otherwise
        Assert.Equal(5, Shipped.Thieves.Gang.KillXp); // the Thieves' own untouched
        Assert.Same(r.Gunman, Shipped.Thieves.Gang.Gunman); // attached: a client draws the server's Gunman
        Assert.Throws<DataFileException>(() => RivalCrewFile.Parse("""{"version":1,"gunman":{"gun":{"weapons":[]}}}"""));
        Assert.Throws<DataFileException>(() => RivalCrewFile.Parse("""{"version":1,"sight":{"radius_m":80}}""")); // give_up_m below the sight radius
        Assert.Throws<DataFileException>(() => RivalCrewFile.Parse("""{"version":1,"gunman":{"gun":{"weapons":[{"weapon":"nope"}]}}}""").CheckAgainst(Shipped.Weapons));
        Assert.Throws<DataFileException>(() => RivalCrewFile.Parse("""{"version":1,"gunman":{"gun":{"weapons":[{"weapon":"rpg"}]}}}""").CheckAgainst(Shipped.Weapons));
    }

    [Fact]
    public void TheGunmanIsKindThreeAndItsStatesTravel()
    {
        var hp = GangRules.PackHp(GangMemberKind.Gunman, true, 0);
        Assert.Equal((GangMemberKind.Gunman, true), (GangRules.KindOf(hp), GangRules.AliveOf(hp)));
        Assert.Equal(GangMemberState.Posted, GangRules.StateOf(25));
        Assert.Equal(GangMemberState.Idle, GangRules.StateOf(26));
        var rig = new Rig();
        var crew = rig.Crew(_ => false);
        var looks = new HashSet<string>();
        for (var i = 0; i < 12; i++)
        {
            var m = crew.Add(GangMemberKind.Gunman, new Vector3(i, 0, 0), 0)!;
            Assert.Same(GangLook.Gun(rig.Rival.Gunman.Gun!, m.Look), m.Weapon); // the client draws the weapon the server fires
            looks.Add(m.Weapon!.Weapon);
        }
        Assert.True(looks.Count > 1);
        Assert.Null(crew.Add(GangMemberKind.Enforcer, Vector3.Zero, 0)!.Weapon);
    }

    [Fact]
    public void ARivalCrewMemberCarriesTheCrewBitSoTheClientDrawsItApartFromTheThieves()
    {
        var hp = GangRules.PackHp(GangMemberKind.GangBoss, true, 5, speaker: true, rival: true);
        Assert.True(GangRules.RivalOf(hp));
        Assert.Equal((GangMemberKind.GangBoss, true, (byte)5, true), (GangRules.KindOf(hp), GangRules.AliveOf(hp), GangRules.CarriedOf(hp), GangRules.SpeakerOf(hp))); // bit 7 is free: nothing else moves
        Assert.False(GangRules.RivalOf(GangRules.PackHp(GangMemberKind.GangBoss, true, 5, speaker: true)));
        var rig = new Rig();
        var dealer = rig.Crew(_ => false).Add(GangMemberKind.GangBoss, Vector3.Zero, 0)!;
        Assert.True(GangRules.RivalOf(dealer.Serialize().Hp));
        var thief = rig.Roster.NewGang(Vector3.Zero).Add(GangMemberKind.GangBoss, new Vector3(5, 0, 0), 0)!;
        Assert.False(GangRules.RivalOf(thief.Serialize().Hp));
        Assert.Matches("^#[0-9a-fA-F]{6}$", Shipped.Thieves.RivalCrew.View.Tint);
    }

    // ---- the Gunman ----

    [Fact]
    public void AGunmanAimsThenFiresAtAHostilePlayerInSightWithinItsAimError()
    {
        var rig = new Rig();
        var crew = rig.Crew(p => p == 1);
        var m = crew.Add(GangMemberKind.Gunman, Vector3.Zero, 0)!;
        rig.World.Player(1, 0, -15);
        rig.Run(0.4f);
        Assert.Equal(GangMemberState.Aim, m.State); // the telegraph: no round yet
        Assert.Empty(rig.World.Shots);
        rig.Run(3f);
        Assert.NotEmpty(rig.World.Shots);
        var gun = rig.Rival.Gunman.Gun!;
        var chest = new Vector3(0, rig.Rival.Sight.ChestM, -15);
        Assert.All(rig.World.Shots, s =>
        {
            Assert.Equal((m.HitId, (ushort)1), (s.MemberHitId, s.Target));
            Assert.Same(m.Weapon, s.Weapon);
            Assert.True(AngleDeg(s.Dir, chest - s.From) <= gun.AimErrorDeg + 0.01f);
        });
        Assert.Equal(Vector3.Zero, m.Position); // it stood to shoot
    }

    [Fact]
    public void AWallHidesTheHostilePlayerAndAGunmanWithoutSightClosesInInsteadOfShooting()
    {
        var rig = new Rig();
        var crew = rig.Crew(p => p == 1);
        var m = crew.Add(GangMemberKind.Gunman, Vector3.Zero, 0)!;
        rig.World.Sees = (_, _) => false;
        rig.World.Player(1, 0, -15);
        rig.Run(2f);
        Assert.Empty(crew.Aggro); // never seen: not attacked
        Assert.Equal(GangMemberState.Guard, m.State);
        crew.Hit(m.HitId, 1, 0, 10, KillCause.Standard); // it was shot: the attacker is fought, seen or not
        rig.Run(1f);
        Assert.Contains((ushort)1, crew.Aggro);
        Assert.Equal(GangMemberState.Chase, m.State);
        Assert.Empty(rig.World.Shots);
        Assert.True(m.Position.Z < -0.5f); // closing in
    }

    [Fact]
    public void ACrewIgnoresPlayersOutsideItsSetUntilTheyHitAMemberAndThoseBeyondItsSightRadius()
    {
        var rig = new Rig();
        var crew = rig.Crew(p => p == 1);
        var m = crew.Add(GangMemberKind.Gunman, Vector3.Zero, 0)!;
        rig.World.Player(2, 0, -8); // not in the set, close
        rig.World.Player(1, 0, -(rig.Rival.Sight.RadiusM + 5)); // in the set, too far
        rig.Run(2f);
        Assert.Empty(crew.Aggro);
        Assert.Empty(rig.World.Shots);
        crew.Hit(m.HitId, 2, 0, 10, KillCause.Standard);
        rig.Run(2f);
        Assert.Equal(new ushort[] { 2 }, crew.Aggro.ToArray());
        Assert.All(rig.World.Shots, s => Assert.Equal(2, s.Target));
        Assert.NotEmpty(rig.World.Shots);
    }

    [Fact]
    public void ARaidAttacksNobodyOnSight()
    {
        var rig = new Rig();
        var raid = rig.Roster.NewGang(Vector3.Zero);
        raid.Add(GangMemberKind.Enforcer, Vector3.Zero, 0);
        rig.World.Player(1, 0, -5);
        rig.Run(2f);
        Assert.Empty(raid.Aggro);
        Assert.Equal(GangCrew.Raid, raid.Crew);
    }

    // ---- posted ----

    [Fact]
    public void APostedGunmanHoldsItsSpotTurnsAndShootsAndNeverWalks()
    {
        var rig = new Rig();
        var crew = rig.Crew(p => p == 1);
        var spot = new Vector3(3, 1.2f, 4); // a room's floor, off the navigation mesh
        var m = crew.AddPosted(GangMemberKind.Gunman, spot, 0)!;
        Assert.Equal(GangMemberState.Posted, m.State);
        Assert.False(m.Pushable);
        var seen = false;
        rig.World.Sees = (_, _) => seen;
        rig.World.Player(1, 13, 4); // to its right
        crew.Hit(m.HitId, 1, 0, 1, KillCause.Standard); // fought, but out of sight
        rig.Run(3f);
        Assert.Equal(GangMemberState.Posted, m.State);
        Assert.Equal(spot, m.Position);
        Assert.Equal(0, rig.Routes);
        Assert.Empty(rig.World.Shots);
        Assert.True(MathF.Abs(TurretMathYawDiff(m.Yaw, -MathF.PI / 2)) < 0.05f); // turned to the player (east: yaw -90 degrees)
        seen = true;
        rig.Run(3f);
        Assert.NotEmpty(rig.World.Shots);
        Assert.Equal(spot, m.Position);
        rig.World.List.Clear(); // the player is gone
        rig.Run(3f);
        Assert.Equal(GangMemberState.Posted, m.State);
        Assert.True(MathF.Abs(TurretMathYawDiff(m.Yaw, 0)) < 0.05f); // back to its facing
        Assert.Equal(spot, m.Position);
    }

    [Fact]
    public void APostedEnforcerStrikesOnlyWithinReach()
    {
        var rig = new Rig();
        var crew = rig.Crew(p => p == 1);
        var m = crew.AddPosted(GangMemberKind.Enforcer, Vector3.Zero, 0)!;
        rig.World.Player(1, 0, -6);
        rig.Run(3f);
        Assert.Contains((ushort)1, crew.Aggro);
        Assert.Empty(rig.World.Hurts);
        Assert.Equal(Vector3.Zero, m.Position);
        rig.World.List[0] = rig.World.List[0] with { Feet = new Vector3(0, 0, -1.2f) };
        rig.Run(2f);
        Assert.NotEmpty(rig.World.Hurts);
        Assert.Equal(Vector3.Zero, m.Position);
    }

    // ---- the crew apart from the Raids ----

    [Fact]
    public void TheThievesClearTheirRaidsOnlyAndAMemberSaysItsGang()
    {
        var rig = new Rig();
        var raid = rig.Roster.NewGang(Vector3.Zero);
        var thief = raid.Add(GangMemberKind.Thief, Vector3.Zero, 0)!;
        var crew = rig.Crew(_ => false);
        var gunman = crew.Add(GangMemberKind.Gunman, new Vector3(5, 0, 0), 0)!;
        Assert.Same(raid, rig.Roster.GangOf(thief.HitId));
        Assert.Same(crew, rig.Roster.GangOf(gunman.HitId));
        Assert.Equal(0, rig.Roster.GangOf(gunman.HitId)!.Config.KillXp);
        rig.Roster.Clear(GangCrew.Raid);
        Assert.Equal(new[] { crew }, rig.Roster.Gangs.ToArray());
        Assert.True(gunman.Alive);
    }

    [Fact]
    public void TheAimErrorStaysInItsCone()
    {
        var aim = Vector3.Normalize(new Vector3(1, 0.2f, -3));
        for (uint seed = 1; seed < 200; seed++) Assert.True(AngleDeg(GangMember.AimError(aim, 4f, seed), aim) <= 4.001f);
        Assert.Equal(aim, GangMember.AimError(aim, 0f, 7));
    }

    static float TurretMathYawDiff(float a, float b)
    {
        var d = (a - b) % MathF.Tau;
        if (d > MathF.PI) d -= MathF.Tau;
        if (d < -MathF.PI) d += MathF.Tau;
        return d;
    }
}
