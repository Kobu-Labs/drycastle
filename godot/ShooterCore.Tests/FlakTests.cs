using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>ADR 0005: the flak truck's turret rules (aim limits, the proximity fuse, the airburst damage), its records, and ending a streak whose aircraft is shot down.</summary>
public class FlakTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly GameData Shipped = GameData.Load(DataDir);
    static VehicleFlakDef Flak => Shipped.Vehicles.Vehicles.Single(v => v.Id == "flak").Flak!;
    static AircraftKindDef Kind(string id) => Shipped.Killstreaks.AircraftKinds.Single(k => k.Id == id);

    static Aircraft Orbit(string kind, float phase = 0) => new(Kind(kind), 0, Vector3.Zero, phase, clockwise: false, owner: 3);

    static List<FlakTarget> Targets(params Aircraft[] a) => a.Select((x, i) => new FlakTarget((ushort)(i + 1), x.PositionAfter)).ToList();

    /// <summary>The direction to fire from <paramref name="origin"/> so the shell meets the aircraft: the point it will be at when the shell gets there (a few passes of the fixed-point lead).</summary>
    static Vector3 LeadDir(Aircraft a, Vector3 origin, float speed)
    {
        var t = Vector3.Distance(origin, a.Position) / speed;
        for (var i = 0; i < 6; i++) t = Vector3.Distance(origin, a.PositionAfter(t)) / speed;
        return Vector3.Normalize(a.PositionAfter(t) - origin);
    }

    // ---- data ----

    [Fact]
    public void The_shipped_flak_truck_has_a_gunner_seat_a_real_weapon_and_no_jeep_gun()
    {
        var v = Shipped.Vehicles.Vehicles.Single(x => x.Id == "flak");
        Assert.NotNull(v.Flak);
        Assert.Null(v.Gun);
        Assert.Contains(v.Seats, s => s.Role == "gunner");
        Assert.Contains(Shipped.Weapons.Weapons, w => w.Id == v.Flak!.WeaponId);
        Assert.Equal(40, v.Flak!.Magazine);
        Assert.Equal(40, v.Flak.AsGun().Magazine);
    }

    [Fact]
    public void A_flak_turret_without_a_gunner_seat_or_with_a_gun_is_rejected()
    {
        VehiclesFile Make(Action<VehicleDef> edit)
        {
            var v = new VehicleDef { Id = "t", TopSpeed = 10, Acceleration = 2, Brake = 5, TurnRadius = 5, MaxLateralAccel = 5, Mass = 1000, Flak = new VehicleFlakDef() };
            edit(v);
            return new VehiclesFile { Vehicles = { v } };
        }
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(Make(_ => { }))); // only a driver seat
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(Make(v => { v.Seats.Add(new VehicleSeatDef { Role = "gunner" }); v.Gun = new VehicleGunDef(); })));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(Make(v => { v.Seats.Add(new VehicleSeatDef { Role = "gunner" }); v.Flak!.BurstFullRadius = 20; })));
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(Make(v => { v.Seats.Add(new VehicleSeatDef { Role = "gunner" }); v.Flak!.MinElevationDeg = 90; })));
        VehiclesFile.Validate(Make(v => v.Seats.Add(new VehicleSeatDef { Role = "gunner" }))); // the defaults on a driver and a gunner seat are valid
    }

    // ---- aim ----

    [Fact]
    public void The_barrels_cannot_drop_below_the_minimum_or_rise_above_the_maximum_elevation()
    {
        var f = Flak;
        Assert.Equal(f.MinElevationDeg * MathF.PI / 180f, FlakRules.ClampPitch(f, -0.5f), 5);
        Assert.Equal(f.MaxElevationDeg * MathF.PI / 180f, FlakRules.ClampPitch(f, 1.55f), 5);
        Assert.Equal(0.7f, FlakRules.ClampPitch(f, 0.7f), 5);
    }

    [Fact]
    public void Yaw_wraps_into_minus_pi_to_pi()
    {
        Assert.Equal(-MathF.PI + 0.5f, FlakRules.Wrap(MathF.PI + 0.5f), 4);
        Assert.Equal(MathF.PI - 0.5f, FlakRules.Wrap(-MathF.PI - 0.5f), 4);
        Assert.Equal(0.25f, FlakRules.Wrap(0.25f + 4 * MathF.PI), 4);
    }

    [Fact]
    public void The_muzzle_is_a_barrel_length_from_the_pivot_and_the_barrels_alternate_sides()
    {
        var f = Flak;
        var aim = HitMath.Forward(0, 0.6f);
        var l = FlakRules.Muzzle(f, Vector3.Zero, 0, 0, aim, 0);
        var r = FlakRules.Muzzle(f, Vector3.Zero, 0, 0, aim, 1);
        var pivot = FlakRules.PivotWorld(f, Vector3.Zero, 0);
        Assert.Equal(f.BarrelSpacing * 2, Vector3.Distance(l, r), 4);
        Assert.Equal(f.MuzzleLength, Vector3.Distance(pivot, (l + r) / 2), 3);
        Assert.True(l.X < r.X); // the left barrel is on the left (-x) at yaw 0
    }

    [Fact]
    public void The_gunner_seat_turns_with_the_turret_about_its_axis()
    {
        var f = Flak;
        var seat = new[] { 0f, 1.59f, 2.37f };
        var same = FlakRules.TurretSeatOffset(f, seat, 0);
        Assert.Equal(seat[0], same[0], 4); Assert.Equal(seat[2], same[2], 4);
        var back = FlakRules.TurretSeatOffset(f, seat, MathF.PI); // turned right round: the seat is now in front of the axis
        Assert.Equal(f.Pivot[2] - (seat[2] - f.Pivot[2]), back[2], 3);
        Assert.Equal(seat[1], back[1]);
        var quarter = FlakRules.TurretSeatOffset(f, seat, MathF.PI / 2); // turned left: the rear swings to the right (+x)
        Assert.True(quarter[0] > f.Pivot[0] + 0.5f);
    }

    // ---- damage ----

    [Fact]
    public void An_airburst_does_full_damage_close_falls_to_the_edge_and_stops_at_the_burst_radius()
    {
        var f = Flak;
        Assert.Equal(f.Damage, FlakRules.BurstDamage(f, 0));
        Assert.Equal(f.Damage, FlakRules.BurstDamage(f, f.BurstFullRadius));
        Assert.Equal(0f, FlakRules.BurstDamage(f, f.BurstRadius));
        var mid = FlakRules.BurstDamage(f, (f.BurstFullRadius + f.BurstRadius) / 2);
        Assert.InRange(mid, f.EdgeDamage, f.Damage);
        Assert.True(FlakRules.BurstDamage(f, f.BurstRadius - 0.01f) >= f.EdgeDamage - 1);
        Assert.True(FlakRules.BurstDamage(f, 6) > FlakRules.BurstDamage(f, 12));
    }

    [Fact]
    public void A_helicopter_takes_a_handful_of_close_bursts_and_a_gunship_many_more()
    {
        var f = Flak;
        var heli = Kind("helicopter").Hp; var ship = Kind("gunship").Hp;
        var closeBursts = (int)MathF.Ceiling(heli / FlakRules.BurstDamage(f, 2));
        Assert.InRange(closeBursts, 3, 8);
        Assert.True(MathF.Ceiling(ship / FlakRules.BurstDamage(f, 2)) > closeBursts * 2);
        // at the fuse radius a burst is far weaker than a direct one
        Assert.True(FlakRules.BurstDamage(f, f.ProximityRadius) < f.Damage / 2);
    }

    // ---- the fuse ----

    [Fact]
    public void A_shell_led_at_an_orbiting_helicopter_bursts_beside_it()
    {
        var f = Flak;
        var heli = Orbit("helicopter", 0.3f);
        var origin = new Vector3(5, 3, 8);
        var dir = LeadDir(heli, origin, f.ShellSpeed);
        var plan = FlakRules.Plan(f, origin, dir, float.PositiveInfinity, Targets(heli));
        Assert.Equal(FlakEnd.Burst, plan.End);
        Assert.True(Vector3.Distance(plan.At, heli.PositionAfter(plan.Seconds)) <= f.ProximityRadius);
        Assert.True(Vector3.Distance(plan.At, origin + dir * plan.Distance) < 0.01f);
        Assert.True(plan.Distance < f.MaxRange);
    }

    [Fact]
    public void The_burst_is_at_the_closest_approach_not_where_the_shell_first_enters_the_fuse()
    {
        var f = Flak;
        var heli = Orbit("helicopter", 1.1f);
        var origin = new Vector3(0, 3, 0);
        var dir = LeadDir(heli, origin, f.ShellSpeed);
        var plan = FlakRules.Plan(f, origin, dir, float.PositiveInfinity, Targets(heli));
        Assert.Equal(FlakEnd.Burst, plan.End);
        var gap = Vector3.Distance(plan.At, heli.PositionAfter(plan.Seconds));
        // a step either side is no closer (to within the march's step)
        foreach (var dt in new[] { -FlakRules.FuseStepS * 2, FlakRules.FuseStepS * 2 })
        {
            var t = plan.Seconds + dt;
            var other = Vector3.Distance(origin + dir * (f.ShellSpeed * t), heli.PositionAfter(t));
            Assert.True(gap <= other + 0.5f, $"gap {gap} vs {other} at {dt}");
        }
        Assert.True(gap < f.ProximityRadius * 0.6f, $"a well-led shell bursts well inside the fuse radius, got {gap}");
    }

    [Fact]
    public void A_shell_aimed_at_where_a_fast_gunship_is_now_misses_it()
    {
        var f = Flak;
        var ship = Orbit("gunship", 0.2f);
        var origin = new Vector3(0, 3, 0);
        var dir = Vector3.Normalize(ship.Position - origin); // no lead
        var plan = FlakRules.Plan(f, origin, dir, float.PositiveInfinity, Targets(ship));
        Assert.NotEqual(FlakEnd.Burst, plan.End);
        // the same shell, led, hits
        var led = FlakRules.Plan(f, origin, LeadDir(ship, origin, f.ShellSpeed), float.PositiveInfinity, Targets(ship));
        Assert.Equal(FlakEnd.Burst, led.End);
    }

    [Fact]
    public void With_no_aircraft_a_shell_ends_on_the_ground_or_expires_at_its_range()
    {
        var f = Flak;
        var up = Vector3.Normalize(new Vector3(0.1f, 1, 0));
        var ground = FlakRules.Plan(f, Vector3.Zero, up, 100f, Array.Empty<FlakTarget>());
        Assert.Equal(FlakEnd.Ground, ground.End);
        Assert.Equal(100f / f.ShellSpeed, ground.Seconds, 4);
        var far = FlakRules.Plan(f, Vector3.Zero, up, float.PositiveInfinity, Array.Empty<FlakTarget>());
        Assert.Equal(FlakEnd.Expired, far.End);
        Assert.Equal(f.MaxRange, far.Distance);
    }

    [Fact]
    public void An_aircraft_beyond_a_wall_is_not_burst_on_the_shell_stops_at_the_wall_first()
    {
        var f = Flak;
        var heli = Orbit("helicopter", 0.3f);
        var origin = new Vector3(5, 3, 8);
        var dir = LeadDir(heli, origin, f.ShellSpeed);
        var full = FlakRules.Plan(f, origin, dir, float.PositiveInfinity, Targets(heli));
        var blocked = FlakRules.Plan(f, origin, dir, full.Distance * 0.5f, Targets(heli)); // a wall half way
        Assert.Equal(FlakEnd.Ground, blocked.End);
    }

    [Fact]
    public void Of_two_aircraft_the_shell_bursts_by_the_one_it_reaches()
    {
        var f = Flak;
        var heli = Orbit("helicopter", 0.3f);
        var ship = Orbit("gunship", 3.0f);
        var origin = new Vector3(5, 3, 8);
        var dir = LeadDir(heli, origin, f.ShellSpeed);
        var plan = FlakRules.Plan(f, origin, dir, float.PositiveInfinity, Targets(ship, heli));
        Assert.Equal(FlakEnd.Burst, plan.End);
        Assert.True(Vector3.Distance(plan.At, heli.PositionAfter(plan.Seconds)) <= f.ProximityRadius);
    }

    // ---- records ----

    [Fact]
    public void The_turret_record_carries_the_aim_and_the_gunner_and_never_moves()
    {
        const ushort car = VehicleRecord.FirstId + 7;
        var r = FlakRecord.Make(car, 0x1234, 1.2f, 0.9f);
        Assert.Equal(EntityType.Turret, r.Type);
        Assert.Equal(FlakRecord.IdOf(car), r.Id);
        Assert.Equal(car, FlakRecord.CarOf(r.Id));
        Assert.Equal((ushort)0x1234, FlakRecord.Gunner(r));
        Assert.Equal(1.2f, r.YawRad, 3);
        Assert.Equal(0.9f, r.PitchRad, 3);
        Assert.Equal(0, r.X); Assert.Equal(0, r.Y); Assert.Equal(0, r.Z);
        Assert.Equal(EntityState.Alive, FlakRecord.Make(car, 0, 0, 0).State);
        Assert.Equal((ushort)0, FlakRecord.Gunner(FlakRecord.Make(car, 0, 0, 0)));
        // the id range sits between the vehicles and the rockets
        Assert.InRange(FlakRecord.IdOf(VehicleRecord.FirstId + 1000), (ushort)(VehicleRecord.FirstId + 1000), (ushort)0x7FFF);
    }

    [Fact]
    public void The_turret_record_survives_the_snapshot_codec()
    {
        const ushort car = VehicleRecord.FirstId + 2;
        var rec = FlakRecord.Make(car, 321, 2.5f, 0.4f);
        var enc = new SnapshotEncoder();
        var dec = new SnapshotDecoder();
        foreach (var pkt in enc.Encode(5, 0, Wire.NoTick, new[] { rec })) Assert.True(dec.Apply(pkt, out _));
        var got = dec.States[rec.Id];
        Assert.Equal(EntityType.Turret, got.Type);
        Assert.Equal((ushort)321, FlakRecord.Gunner(got));
        Assert.Equal(rec.Yaw, got.Yaw);
        Assert.Equal(rec.Pitch, got.Pitch);
    }

    [Fact]
    public void The_gunner_status_round_trips_and_a_bad_one_is_refused()
    {
        var s = new FlakStatus(36, 400, 2.5f);
        Assert.Equal(s, FlakCodec.Decode(FlakCodec.Encode(s)));
        Assert.Null(FlakCodec.Decode(new byte[3]));
        Assert.Null(FlakCodec.Decode(FlakCodec.Encode(new FlakStatus(1, 1, -1f))));
    }

    // ---- the streak of a shot-down aircraft ----

    sealed class TakesControl : IKillstreak
    {
        public TakesControl(string id) => Id = id;
        public string Id { get; }
        public IReadOnlySet<string> OwnedWeapons { get; } = new HashSet<string>();
        public bool Activate(KillstreakUse use) => use.TakeControl(77, _ => { }, freezesBody: true, shieldsBody: true);
    }

    [Fact]
    public void Ending_for_a_destroyed_entity_ends_the_streak_that_flies_it_and_releases_the_owner()
    {
        const ushort owner = 4;
        var sent = new List<(ushort Owner, EventKind Kind, byte[] Payload)>();
        var impl = Shipped.Killstreaks.Streaks.Select(s => s.Id == "uav" ? (IKillstreak)new TakesControl(s.Id) : new LoggingKillstreak(s.Id));
        var service = new KillstreakService(Shipped.Killstreaks, impl, (o, k, p) => sent.Add((o, k, p)), _ => { });
        service.Arm(owner, new[] { "uav", "", "" }, 0);
        for (var i = 0; i < 4; i++) service.OnKill(owner, 9, false);
        Assert.True(service.Activate(owner, 10, Vector3.Zero));
        Assert.Equal((ushort)77, service.ControlOf(owner)!.EntityId);

        Assert.Equal(0, service.EndForEntity(5, KillstreakEndReason.Destroyed)); // another entity: nothing
        Assert.NotNull(service.ControlOf(owner));
        Assert.Equal(1, service.EndForEntity(77, KillstreakEndReason.Destroyed));
        Assert.Null(service.ControlOf(owner));
        var ended = sent.Last(x => x.Kind == EventKind.StreakEnded);
        Assert.Equal(KillstreakEndReason.Destroyed, KillstreakEvents.DecodeEnded(ended.Payload)!.Reason);
        Assert.Equal(0, service.EndForEntity(77, KillstreakEndReason.Destroyed)); // already over
    }

    [Fact]
    public void The_world_entity_manager_lists_aircraft_by_type()
    {
        var reg = new WorldEntityRegistry();
        Aircraft.Register(reg);
        var world = new WorldEntityManager(reg);
        var id = world.Spawn(Orbit("helicopter"))!.Value;
        var found = world.OfType(Aircraft.WireType);
        Assert.Single(found);
        Assert.Equal(id, found[0].Id);
        Assert.Empty(world.OfType(99));
        world.Damage(id, 1_000_000, 8);
        Assert.Null(world.StateOf(id)); // shot down: despawned as Destroyed
    }
}
