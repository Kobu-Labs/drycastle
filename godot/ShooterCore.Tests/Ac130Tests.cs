using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>Ticket #128: the AC-130 data (its turret, shell splash), the weapon selector, the shell's rate, and the streak.</summary>
public class Ac130Tests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static GameData Shipped => GameData.Load(DataDir);
    static KillstreakFile Shipping => Shipped.Killstreaks;
    static GunshipDef Turret => Shipping.Ac130!;

    const ushort Owner = 7, Enemy = 8;

    static string ShippedJson => File.ReadAllText(Path.Combine(DataDir, "killstreaks.json"));

    static NetInput Input(GunshipControl c, MoveButtons buttons, byte slot = 0, float yaw = 0, float pitch = -0.5f) => GunnerInputs.Look(c, yaw, pitch, buttons, slot);

    [Fact]
    public void Shipped_turret_has_a_cannon_pair_and_a_shell_with_its_own_splash()
    {
        Assert.Equal(new[] { "gun25", "gun40", "howitzer" }, Turret.Weapons.Select(w => w.Id));
        var shell = Turret.Weapons[2];
        Assert.Equal(GunshipWeaponKind.Shell, shell.Kind);
        Assert.NotNull(shell.Splash);
        Assert.True(shell.Splash!.Radius > shell.Splash.FullDamageRadius);
        // every weapon names a weapons.json entry (the shot's sound and tracer come from it)
        var weapons = Shipped.Weapons.Weapons.Select(w => w.Id).ToHashSet();
        Assert.All(Turret.Weapons, w => Assert.Contains(w.WeaponId, weapons));
    }

    [Fact]
    public void The_streak_is_a_pov_streak_of_forty_seconds_with_the_gunship_airframe()
    {
        var s = Shipping.Find(Ac130Streak.StreakId)!;
        Assert.Equal(KillstreakControl.Pov, s.Control);
        Assert.Equal(40, s.DurationSeconds);
        Assert.Equal("gunship", Shipping.AircraftKinds[1].Id);
    }

    [Fact]
    public void A_shell_without_splash_is_rejected()
    {
        // the shell is the entry of kind "shell": drop its splash block (the cannons keep theirs)
        var json = System.Text.RegularExpressions.Regex.Replace(ShippedJson, "(\"kind\": \"shell\"[^}]*?),\\s*\"splash\": \\{[^}]*\\}", "$1");
        Assert.DoesNotContain("\"splash\": { \"radius\": 14", json);
        var e = Assert.Throws<DataFileException>(() => KillstreakFile.Parse(json));
        Assert.Contains("needs a \"splash\" block", e.Message);
    }

    [Fact]
    public void Full_damage_radius_must_be_inside_the_splash_radius()
    {
        var json = ShippedJson.Replace("\"full_damage_radius\": 4", "\"full_damage_radius\": 14");
        var e = Assert.Throws<DataFileException>(() => KillstreakFile.Parse(json));
        Assert.Contains("full_damage_radius", e.Message);
    }

    [Fact]
    public void The_hotbar_key_picks_the_weapon_and_reload_still_cycles()
    {
        var shots = new List<GunshipShot>();
        var control = new GunshipControl(Turret, new Aircraft(Shipping.AircraftKinds[1], 1, Vector3.Zero, 0, true, Owner), shots.Add, _ => { }, new Random(1));
        control.OnInput(Input(control, 0, slot: 2), 1.0);
        Assert.Equal(1, control.Weapon);
        control.OnInput(Input(control, 0, slot: 9), 1.1); // no such weapon: ignored
        Assert.Equal(1, control.Weapon);
        control.OnInput(Input(control, MoveButtons.Reload, slot: 9), 1.2); // the reload key steps on; the unchanged slot does not undo it
        Assert.Equal(2, control.Weapon);
        control.OnInput(Input(control, MoveButtons.Reload, slot: 9), 1.3); // held, not a new press
        Assert.Equal(2, control.Weapon);
    }

    [Fact]
    public void The_shell_fires_once_per_its_interval_with_no_ammo_limit()
    {
        var shots = new List<GunshipShot>();
        var control = new GunshipControl(Turret, new Aircraft(Shipping.AircraftKinds[1], 1, Vector3.Zero, 0, true, Owner), shots.Add, _ => { }, new Random(1));
        control.OnInput(Input(control, MoveButtons.Fire, slot: 3), 1.0);
        control.OnInput(Input(control, MoveButtons.Fire, slot: 3), 1.5); // inside the 1 s interval
        Assert.Single(shots);
        Assert.Equal(GunshipWeaponKind.Shell, shots[0].Kind);

        control.OnInput(Input(control, MoveButtons.Fire, slot: 3), 2.1);
        Assert.Equal(2, shots.Count);
    }

    [Fact]
    public void Each_cannon_fires_at_its_own_rate_with_no_ammo_or_heat()
    {
        var shots = new List<GunshipShot>();
        var control = new GunshipControl(Turret, new Aircraft(Shipping.AircraftKinds[1], 1, Vector3.Zero, 0, true, Owner), shots.Add, _ => { }, new Random(1));
        control.OnInput(Input(control, MoveButtons.Fire, slot: 2), 1.0);
        control.OnInput(Input(control, MoveButtons.Fire, slot: 2), 1.61); // the 40 mm's 0.2 s: rounds due at 1.0, 1.2, 1.4 and 1.6
        Assert.Equal(4, shots.Count);
        Assert.All(shots, s => Assert.Equal(1, s.WeaponIndex));
    }

    [Fact]
    public void The_airframe_is_taken_from_the_ac130_streak_spawn_and_the_owner_holds_still_while_steering()
    {
        var h = new Harness();
        Assert.True(h.Service.Activate(Owner, 0, new Vector3(3, 1, -4)));
        Assert.Equal(1, h.Entities.Count);
        var session = h.Service.ControlOf(Owner);
        Assert.NotNull(session);
        Assert.True(session!.FreezesBody);
        Assert.Equal(Ac130Streak.StreakId, session!.StreakId);
        var air = Assert.Single(h.Entities.Snapshot());
        Assert.Equal(Aircraft.WireType, air.Type);
        Assert.Equal(air.Id, session.EntityId);
        Assert.Equal(Owner, air.State.Owner);
    }

    [Fact]
    public void Owner_death_removes_the_gunship()
    {
        var h = new Harness();
        Assert.True(h.Service.Activate(Owner, 0, Vector3.Zero));
        h.Service.OnDeath(Owner);
        Assert.Equal(0, h.Entities.Count);
    }

    /// <summary>The AC-130 in a service with the real table: 18 kills earn it, and the rest of the streaks log.</summary>
    sealed class Harness
    {
        public readonly WorldEntityManager Entities;
        public readonly KillstreakService Service;

        public Harness()
        {
            var reg = new WorldEntityRegistry();
            Aircraft.Register(reg);
            Entities = new WorldEntityManager(reg);
            var world = new World(Entities);
            var impl = Shipping.Streaks.Select(s => s.Id == Ac130Streak.StreakId
                ? (IKillstreak)new Ac130Streak(world, Shipping.AircraftKinds[1], 1)
                : new LoggingKillstreak(s.Id));
            Service = new KillstreakService(Shipping, impl, (_, _, _) => { }, _ => { });
            Service.Arm(Owner, new[] { Ac130Streak.StreakId, "", "" }, 0);
            for (var i = 0; i < 18; i++) Service.OnKill(Owner, Enemy, false);
        }

        sealed class World : Ac130Streak.IWorld
        {
            public World(WorldEntityManager entities) => Entities = entities;
            public WorldEntityManager Entities { get; }
            public double Clock => 0;
            public GunshipControl Turret(ushort owner, Aircraft aircraft) =>
                new(Shipping.Ac130!, aircraft, _ => { }, _ => { }, new Random(1));
        }
    }
}
