using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

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
    public void Aircraft_cannot_be_shot_down()
    {
        var a = Make();
        var before = a.Serialize();
        a.OnDamage(10000, 9);
        Assert.Null(a.Ended);
        Assert.Equal(before, a.Serialize());
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
        Assert.False(reg.Find(Aircraft.WireType)!.HasHp);
    }

    [Fact]
    public void Aim_clamp_limits_yaw_to_the_swing_and_pitch_to_its_range()
    {
        var limit = Gun.YawLimitDeg * MathF.PI / 180f;
        var (y, p) = GunshipAim.Clamp(0f, 2f, 0.5f, Gun);
        Assert.Equal(limit, y, 0.001f);
        Assert.Equal(Gun.PitchMaxDeg * MathF.PI / 180f, p, 0.001f);

        var (y2, p2) = GunshipAim.Clamp(0f, -2f, -3f, Gun);
        Assert.Equal(-limit, y2, 0.001f);
        Assert.Equal(Gun.PitchMinDeg * MathF.PI / 180f, p2, 0.001f);
    }

    [Fact]
    public void Aim_clamp_is_relative_to_the_airframe_heading_across_the_wrap()
    {
        // airframe heading just under pi, view just over -pi: a small turn, inside the swing
        var (y, _) = GunshipAim.Clamp(3.0f, -3.0f, -0.3f, Gun);
        Assert.Equal(3.0f + 0.283f, y, 0.01f);
    }

    [Fact]
    public void Aim_inside_the_limits_is_unchanged()
    {
        var (y, p) = GunshipAim.Clamp(1f, 1.2f, -0.4f, Gun);
        Assert.Equal(1.2f, y, 0.001f);
        Assert.Equal(-0.4f, p, 0.001f);
    }

    static NetInput Input(float yaw, float pitch, MoveButtons buttons) => new()
    {
        Yaw = Quant.Yaw(yaw), Pitch = Quant.Pitch(pitch), Buttons = (ushort)buttons,
    };

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
        g.Control.OnInput(Input(0, -0.5f, MoveButtons.Fire), 1.0);
        Assert.Single(g.Shots);
        var s = g.Shots[0];
        Assert.Equal(GunshipWeaponKind.Cannon, s.Kind);
        Assert.Equal(g.Air.Position.Y - GunshipControl.TurretDropM, s.Origin.Y, 0.001f);
        Assert.Equal(Gun.Weapons[0].Ammo - 1, g.Control.Ammo(0));
    }

    [Fact]
    public void Cannon_respects_its_interval()
    {
        var g = new Gunner();
        g.Control.OnInput(Input(0, -0.5f, MoveButtons.Fire), 1.0);
        g.Control.OnInput(Input(0, -0.5f, MoveButtons.Fire), 1.05); // inside the 0.15 s interval
        Assert.Single(g.Shots);
        g.Control.OnInput(Input(0, -0.5f, MoveButtons.Fire), 1.2);
        Assert.Equal(2, g.Shots.Count);
    }

    [Fact]
    public void Heat_locks_the_weapon_and_it_unlocks_at_half_after_cooling()
    {
        var g = new Gunner();
        var cannon = Gun.Weapons[0];
        var t = 1.0;
        // fire at the full rate until the cannon locks (heat rises faster than it cools at that rate)
        for (var i = 0; i < 500 && !g.Control.Overheated(0); i++, t += cannon.IntervalS + 0.01)
            g.Control.OnInput(Input(0, -0.5f, MoveButtons.Fire), t);
        Assert.True(g.Control.Overheated(0));
        var fired = g.Shots.Count;
        g.Control.OnInput(Input(0, -0.5f, MoveButtons.Fire), t + 0.1);
        Assert.Equal(fired, g.Shots.Count); // locked: no round

        // cooling from the lock level (max) down to half takes (max / 2) / cool-rate seconds
        var lockAt = t - (cannon.IntervalS + 0.01); // the last round locked it
        var toHalf = cannon.HeatMax * 0.5f / cannon.HeatCoolPerS;
        g.Control.OnInput(Input(0, -0.5f, MoveButtons.None), lockAt + toHalf - 0.2);
        Assert.True(g.Control.Overheated(0));
        g.Control.OnInput(Input(0, -0.5f, MoveButtons.None), lockAt + toHalf + 0.2);
        Assert.False(g.Control.Overheated(0));
        g.Control.OnInput(Input(0, -0.5f, MoveButtons.Fire), lockAt + toHalf + 0.3);
        Assert.Equal(fired + 1, g.Shots.Count);
    }

    [Fact]
    public void Ammo_runs_out_and_reports_empty()
    {
        var g = new Gunner();
        var cannon = Gun.Weapons[0];
        var t = 1.0;
        // one round every half second: the cannon cools more than it heats, so only the ammo limits the run
        for (var i = 0; i < cannon.Ammo + 5; i++, t += 0.5)
            g.Control.OnInput(Input(0, -0.5f, MoveButtons.Fire), t);
        Assert.Equal(cannon.Ammo, g.Shots.Count);
        Assert.Equal(0, g.Control.Ammo(0));
        Assert.True((g.Status.Last().Flags & GunshipStatus.FlagEmpty) != 0);
    }

    [Fact]
    public void Reload_steps_to_the_rockets_and_aim_steps_the_zoom()
    {
        var g = new Gunner();
        g.Control.OnInput(Input(0, -0.5f, MoveButtons.Reload), 1.0);
        Assert.Equal(1, g.Control.Weapon);
        g.Control.OnInput(Input(0, -0.5f, MoveButtons.Fire), 1.5);
        Assert.Single(g.Shots);
        Assert.Equal(GunshipWeaponKind.Rocket, g.Shots[0].Kind);
        Assert.Equal(1, g.Shots[0].WeaponIndex);

        g.Control.OnInput(Input(0, -0.5f, MoveButtons.Aim), 2.0);
        Assert.Equal(1, g.Control.Zoom);
        Assert.Equal(2.5f, g.Control.Magnification);
        // a held button steps once, not every input
        g.Control.OnInput(Input(0, -0.5f, MoveButtons.Aim), 2.05);
        Assert.Equal(1, g.Control.Zoom);
    }

    [Fact]
    public void Ended_aircraft_takes_no_input()
    {
        var g = new Gunner(new Aircraft(Heli, 0, Vector3.Zero, 0, true, 3, lifetimeS: 1));
        g.Air.Tick(2);
        g.Control.OnInput(Input(0, -0.5f, MoveButtons.Fire), 1.0);
        Assert.Empty(g.Shots);
        Assert.True(g.Control.Ended);
    }

    [Fact]
    public void Status_round_trips_through_the_codec()
    {
        var s = new GunshipStatus(1, 1234, 77, 2, GunshipStatus.FlagOverheated);
        Assert.Equal(s, GunshipCodec.Decode(GunshipCodec.Encode(s)));
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
        var extra = "{ \"id\": \"third\", \"name\": \"Third\", \"radius_m\": 100, \"altitude_m\": 10, \"speed_mps\": 10, \"max_bank_deg\": 10, \"lifetime_s\": 10 }";
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
