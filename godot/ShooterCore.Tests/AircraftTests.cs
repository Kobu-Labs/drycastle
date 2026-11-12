using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>Ticket #125: the aircraft world entity (circle, heading, bank, lifetime), the gunship turret aim, and its control.</summary>
public class AircraftTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static KillstreakFile Shipping => GameData.Load(DataDir).Killstreaks;

    static AircraftKindDef Heli => Shipping.AircraftKinds[0];
    static GunshipDef Gun => Shipping.Gunship!;

    static Aircraft Make(AircraftKindDef? def = null, bool clockwise = false, float phase = 0, ushort owner = 3, float? life = null) =>
        new(def ?? Heli, 0, new Vector3(100, 5, -40), phase, clockwise, owner, life);


    [Fact]
    public void Shipped_data_has_two_kinds_and_a_gunship_with_two_weapons()
    {
        Assert.Equal(new[] { "helicopter", "gunship" }, Shipping.AircraftKinds.Select(k => k.Id));
        Assert.NotNull(Shipping.Gunship);
        Assert.Equal(new[] { "cannon", "rockets" }, Gun.Weapons.Select(w => w.Id));
        Assert.Contains(Gun.Weapons, w => w.Kind == GunshipWeaponKind.Rocket);
        // every weapon the gunship names exists in weapons.json (the shot index and explosion numbers come from it)
        var weapons = GameData.Load(DataDir).Weapons.Weapons.Select(w => w.Id).ToHashSet();
        Assert.All(Gun.Weapons, w => Assert.Contains(w.WeaponId, weapons));
    }

    [Fact]
    public void Circle_keeps_its_radius_and_altitude()
    {
        var a = Make();
        for (var i = 0; i < 50; i++)
        {
            a.Tick(0.7f);
            var p = a.Position;
            var d = new Vector2(p.X - 100, p.Z + 40).Length();
            Assert.Equal(Heli.RadiusM, d, 2);
            Assert.Equal(5 + Heli.AltitudeM, p.Y, 3);
        }
    }

    [Fact]
    public void One_lap_returns_to_the_start()
    {
        var a = Make();
        var start = a.Position;
        a.Tick(2 * MathF.PI * Heli.RadiusM / Heli.SpeedMps);
        Assert.InRange(Vector3.Distance(start, a.Position), 0, 0.05f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Heading_is_along_the_circle(bool clockwise)
    {
        var a = Make(clockwise: clockwise);
        for (var i = 0; i < 12; i++)
        {
            var before = a.Position;
            a.Tick(0.4f);
            var move = Vector3.Normalize(a.Position - before);
            var f = HitMath.Forward(a.YawRad, 0);
            // the nose points where the aircraft moves (the chord between two close samples)
            Assert.True(Vector3.Dot(new Vector3(f.X, 0, f.Z), new Vector3(move.X, 0, move.Z)) > 0.999f);
        }
    }

    [Fact]
    public void Bank_is_clamped_and_packs_to_the_wire_and_back()
    {
        var a = Make();
        var (kind, bank) = AircraftState.Unpack(a.Serialize().State);
        Assert.Equal(0, kind);
        Assert.InRange(MathF.Abs(bank), 0, Heli.MaxBankDeg * MathF.PI / 180f + 0.02f);
        Assert.Equal(MathF.Abs(a.BankRad), MathF.Abs(bank), 0.02f);

        var (k2, b2) = AircraftState.Unpack(AircraftState.Pack(1, 2f));
        Assert.Equal(1, k2);
        Assert.Equal(AircraftState.MaxBankDeg * MathF.PI / 180f, b2, 0.02f); // clamped to the wire range
    }

    [Fact]
    public void Kind_index_sits_in_the_state_bit()
    {
        var gunship = new Aircraft(Shipping.AircraftKinds[1], 1, Vector3.Zero, 0, true, 3);
        Assert.Equal(1, AircraftState.Unpack(gunship.Serialize().State).Kind);
    }

    [Fact]
    public void Aircraft_take_damage_and_are_shot_down_at_zero_hp()
    {
        var a = Make();
        Assert.Equal(Heli.Hp, a.Hp);
        a.OnDamage(Heli.Hp / 2, 9);
        Assert.Null(a.Ended);
        Assert.Equal(Heli.Hp / 2, a.Hp, 3);
        Assert.Equal((ushort)9, a.LastAttacker);
        a.OnDamage(Heli.Hp, 7);
        Assert.Equal(WorldDespawnReason.Destroyed, a.Ended);
        Assert.Equal(0f, a.Hp);
        Assert.Equal((ushort)7, a.LastAttacker);
    }

    [Fact]
    public void A_shot_down_aircraft_is_not_overwritten_by_its_lifetime_ending()
    {
        var a = Make(life: 1);
        a.OnDamage(1_000_000, 9);
        a.Tick(5);
        Assert.Equal(WorldDespawnReason.Destroyed, a.Ended);
    }

    [Fact]
    public void Hit_points_travel_as_a_percentage_and_the_type_sends_them()
    {
        var reg = new WorldEntityRegistry();
        Aircraft.Register(reg);
        Assert.True(reg.Find(Aircraft.WireType)!.HasHp);
        var a = Make();
        Assert.Equal(100, a.Serialize().Hp);
        a.OnDamage(Heli.Hp * 0.5f, 9);
        Assert.Equal(50, a.Serialize().Hp);
        a.OnDamage(Heli.Hp * 0.499f, 9);
        Assert.Equal(1, a.Serialize().Hp); // alive never reads 0
    }

    [Fact]
    public void Position_after_follows_the_orbit_ahead_of_the_aircraft()
    {
        var a = Make();
        var ahead = a.PositionAfter(2f);
        a.Tick(2f);
        Assert.True(Vector3.Distance(ahead, a.Position) < 0.01f);
        Assert.True(Vector3.Distance(a.PositionAfter(0), a.Position) < 0.001f);
    }

    [Fact]
    public void Shipped_aircraft_kinds_have_hit_points()
    {
        Assert.All(Shipping.AircraftKinds, k => Assert.True(k.Hp > 0));
        Assert.True(Shipping.AircraftKinds.Single(k => k.Id == "gunship").Hp > Shipping.AircraftKinds.Single(k => k.Id == "helicopter").Hp);
    }

    [Fact]
    public void Lifetime_ends_the_entity_as_expired()
    {
        var a = Make(life: 5);
        a.Tick(4.9f);
        Assert.Null(a.Ended);
        a.Tick(0.2f);
        Assert.Equal(WorldDespawnReason.Expired, a.Ended);
    }

    [Fact]
    public void Registered_type_spawns_through_the_world_manager_and_replicates_owner()
    {
        var reg = new WorldEntityRegistry();
        Aircraft.Register(reg);
        Aircraft.Register(reg); // idempotent
        var m = new WorldEntityManager(reg);
        var seen = new List<WorldSpawned>();
        m.Spawned += seen.Add;
        var id = m.Spawn(Make(owner: 7));
        Assert.NotNull(id);
        Assert.Equal(Aircraft.WireType, seen[0].Type);
        Assert.Equal(7, seen[0].State.Owner);
        Assert.True(reg.Find(Aircraft.WireType)!.HasHp); // aircraft can be shot down now (ADR 0005), so their hp is sent
    }

    // The turret aim is ground-stabilised and world-space now (#160): its limits and continuity are in GunshipGroundAimTests.

    static NetInput Input(GunshipControl c, float yaw, float pitch, MoveButtons buttons) => GunnerInputs.Look(c, yaw, pitch, buttons);

    sealed class Gunner
    {
        public readonly List<GunshipShot> Shots = new();
        public readonly List<GunshipStatus> Status = new();
        public readonly GunshipControl Control;
        public readonly Aircraft Air;

        public Gunner(Aircraft? air = null)
        {
            Air = air ?? new Aircraft(Heli, 1, Vector3.Zero, 0, true, 3);
            Control = new GunshipControl(Gun, Air, Shots.Add, Status.Add, new Random(1));
        }
    }

    [Fact]
    public void Trigger_fires_the_cannon_from_under_the_airframe_with_the_clamped_aim()
    {
        var g = new Gunner(new Aircraft(Shipping.AircraftKinds[1], 1, Vector3.Zero, 0, true, 3));
        g.Control.OnInput(Input(g.Control, 0, -0.5f, MoveButtons.Fire), 1.0);
        Assert.Single(g.Shots);
        var s = g.Shots[0];
        Assert.Equal(GunshipWeaponKind.Cannon, s.Kind);
        Assert.Equal(g.Air.Position.Y - GunshipControl.TurretDropM, s.Origin.Y, 0.001f);
    }

    [Fact]
    public void Cannon_respects_its_interval()
    {
        var g = new Gunner();
        g.Control.OnInput(Input(g.Control, 0, -0.5f, MoveButtons.Fire), 1.0);
        g.Control.OnInput(Input(g.Control, 0, -0.5f, MoveButtons.Fire), 1.05); // inside the 0.15 s interval
        Assert.Single(g.Shots);
        g.Control.OnInput(Input(g.Control, 0, -0.5f, MoveButtons.Fire), 1.2);
        Assert.Equal(2, g.Shots.Count);
    }

    [Fact]
    public void Unlimited_ammo_and_no_heat_lock_the_time_limit_is_the_only_limit()
    {
        var g = new Gunner();
        var cannon = Gun.Weapons[0];
        var t = 1.0;
        var last = t;
        // 300 inputs held on fire: far past any old ammo count or heat lock, and none of it stops the trigger
        for (var i = 0; i < 300; i++, t += cannon.IntervalS + 0.01)
        {
            g.Control.OnInput(Input(g.Control, 0, -0.5f, MoveButtons.Fire), t);
            last = t;
        }
        // the rate is the interval's: rounds due from the first press to the last input (the schedule catches up when an input is late)
        Assert.Equal((int)Math.Floor((last - 1.0) / cannon.IntervalS) + 1, g.Shots.Count);
        Assert.True(g.Shots.Count > 300);
    }

    [Fact]
    public void A_short_interval_fires_every_round_due_within_one_input_up_to_the_cap()
    {
        // the AC-130's 25 mm: 0.15 s between rounds, so an input 0.8 s after the last one has five rounds due (1.15 .. 1.75)
        var shots = new List<GunshipShot>();
        var control = new GunshipControl(Shipping.Ac130!, new Aircraft(Shipping.AircraftKinds[1], 1, Vector3.Zero, 0, true, 3), shots.Add, _ => { }, new Random(1));
        var fire25 = Input(control, 0, -0.5f, MoveButtons.Fire) with { Slot = 1 }; // key 1: the AC-130's 25 mm
        control.OnInput(fire25, 1.0);
        Assert.Single(shots);
        control.OnInput(fire25, 1.8);
        Assert.Equal(6, shots.Count);
        Assert.True(GunshipControl.MaxRoundsPerInput >= 5);
    }

    [Fact]
    public void Reload_steps_to_the_rockets_and_aim_steps_the_zoom()
    {
        var g = new Gunner();
        g.Control.OnInput(Input(g.Control, 0, -0.5f, MoveButtons.Reload), 1.0);
        Assert.Equal(1, g.Control.Weapon);
        g.Control.OnInput(Input(g.Control, 0, -0.5f, MoveButtons.Fire), 1.5);
        Assert.Single(g.Shots);
        Assert.Equal(GunshipWeaponKind.Rocket, g.Shots[0].Kind);
        Assert.Equal(1, g.Shots[0].WeaponIndex);

        g.Control.OnInput(Input(g.Control, 0, -0.5f, MoveButtons.Aim), 2.0);
        Assert.Equal(1, g.Control.Zoom);
        Assert.Equal(2.5f, g.Control.Magnification);
        // a held button steps once, not every input
        g.Control.OnInput(Input(g.Control, 0, -0.5f, MoveButtons.Aim), 2.05);
        Assert.Equal(1, g.Control.Zoom);
    }

    [Fact]
    public void Ended_aircraft_takes_no_input()
    {
        var g = new Gunner(new Aircraft(Heli, 0, Vector3.Zero, 0, true, 3, lifetimeS: 1));
        g.Air.Tick(2);
        g.Control.OnInput(Input(g.Control, 0, -0.5f, MoveButtons.Fire), 1.0);
        Assert.Empty(g.Shots);
        Assert.True(g.Control.Ended);
    }

    [Fact]
    public void Status_round_trips_through_the_codec()
    {
        var s = new GunshipStatus(1, 2);
        Assert.Equal(s, GunshipCodec.Decode(GunshipCodec.Encode(s)));
        Assert.Equal(GunshipCodec.Bytes, GunshipCodec.Encode(s).Length);
        Assert.Null(GunshipCodec.Decode(new byte[3]));
    }

    static string Edited(Func<string, string> edit) => edit(File.ReadAllText(Path.Combine(DataDir, "killstreaks.json")));

    [Fact]
    public void Gunship_without_weapons_is_rejected()
    {
        var json = Edited(j => System.Text.RegularExpressions.Regex.Replace(j, "\"weapons\":\\s*\\[[\\s\\S]*?\\]\\s*\\}", "\"weapons\": [] }"));
        Assert.Throws<DataFileException>(() => KillstreakFile.Parse(json));
    }

    [Fact]
    public void Three_aircraft_kinds_are_rejected()
    {
        var extra = "{ \"id\": \"third\", \"name\": \"Third\", \"radius_m\": 100, \"altitude_m\": 10, \"speed_mps\": 10, \"max_bank_deg\": 10, \"lifetime_s\": 10, \"hp\": 100 }";
        var json = Edited(j => j.Replace("\"aircraft\": [", "\"aircraft\": [" + extra + ","));
        Assert.Throws<DataFileException>(() => KillstreakFile.Parse(json));
    }

    [Fact]
    public void Bank_above_the_wire_range_is_rejected()
    {
        var json = Edited(j => j.Replace("\"max_bank_deg\": 25", "\"max_bank_deg\": 80"));
        Assert.Throws<DataFileException>(() => KillstreakFile.Parse(json));
    }
}
