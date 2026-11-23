using System.Numerics;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Effects;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Weapons;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Weapons;

/// <summary>The Lock-on launcher's rules (issue #347, ADR 0031): the lock circle, the Lock's phases, the owner's alert, the missile's flight, the wire.</summary>
public class LockOnTests
{
    static readonly LockDef Def = new() { Seconds = 2f, RangeM = 400, ConeDeg = 10, AlertAtFraction = 0.4f, GraceSeconds = 0.5f, UnlimitedRange = { "uav" }, HitBlastRadius = 6, KillBlastRadius = 20 };
    static readonly Vector3 Eye = new(0, 2, 0);
    static readonly Vector3 Ahead = -Vector3.UnitZ;
    const float Dt = 1f / 30f;

    static LockCandidate At(ushort id, float x, float z, string kind = "helicopter") => new(id, new Vector3(x, 2, z), kind);

    static WeaponsFile Real() => WeaponsFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "weapons.json"));

    [Fact]
    public void The_circle_holds_an_aircraft_inside_the_cone_and_the_range()
    {
        Assert.True(LockRules.Holds(Def, Eye, Ahead, At(1, 0, -300)));
        Assert.True(LockRules.Holds(Def, Eye, Ahead, At(1, 30, -300))); // about 5.7 degrees off
        Assert.False(LockRules.Holds(Def, Eye, Ahead, At(1, 80, -300))); // about 15 degrees off
        Assert.False(LockRules.Holds(Def, Eye, Ahead, At(1, 0, 300))); // behind
        Assert.False(LockRules.Holds(Def, Eye, Ahead, At(1, 0, -500))); // past the range
    }

    [Theory]
    [InlineData(false, false, true)]  // on foot
    [InlineData(true, true, true)]    // armed seat
    [InlineData(true, false, false)]  // driver / unarmed passenger: no Lock, no beeps (#373)
    public void A_seat_allows_the_launcher_only_when_it_is_armed(bool seated, bool armed, bool allowed) =>
        Assert.Equal(allowed, LockRules.SeatAllows(seated, armed));

    [Fact]
    public void A_kind_with_unlimited_range_is_held_at_any_distance()
    {
        Assert.True(LockRules.Holds(Def, Eye, Ahead, At(1, 0, -3000, "uav")));
        Assert.False(LockRules.Holds(Def, Eye, Ahead, At(1, 0, -3000, "gunship")));
    }

    [Fact]
    public void Pick_takes_the_one_nearest_the_middle_and_keeps_the_one_it_holds()
    {
        var both = new[] { At(1, 30, -300), At(2, 5, -300) };
        Assert.Equal((ushort)2, LockRules.Pick(Def, Eye, Ahead, both, 0, _ => true));
        Assert.Equal((ushort)1, LockRules.Pick(Def, Eye, Ahead, both, 1, _ => true)); // sticky: the held one stays while it is in the circle
        Assert.Equal((ushort)1, LockRules.Pick(Def, Eye, Ahead, both, 0, c => c.Id != 2)); // the nearer one is behind a wall
        Assert.Null(LockRules.Pick(Def, Eye, Ahead, both, 0, _ => false));
        Assert.Null(LockRules.Pick(Def, Eye, Ahead, Array.Empty<LockCandidate>(), 0, _ => true));
    }

    [Fact]
    public void A_lock_builds_over_its_seconds_then_is_complete()
    {
        var l = new LockOn();
        Assert.Equal(LockPhase.Idle, l.Phase);
        l.Update(Dt, Def, 7);
        Assert.Equal(LockPhase.Locking, l.Phase);
        Assert.Equal((ushort)7, l.Target);
        for (var i = 0; i < 30; i++) l.Update(Dt, Def, 7);
        Assert.Equal(LockPhase.Locking, l.Phase);
        Assert.InRange(l.Progress, 0.45f, 0.55f);
        for (var i = 0; i < 31; i++) l.Update(Dt, Def, 7);
        Assert.Equal(LockPhase.Locked, l.Phase);
        Assert.Equal(1f, l.Progress);
    }

    [Fact]
    public void A_short_loss_of_sight_waits_and_a_long_one_loses_the_lock()
    {
        var l = new LockOn();
        for (var i = 0; i < 31; i++) l.Update(Dt, Def, 7);
        var before = l.Progress;
        for (var i = 0; i < 10; i++) l.Update(Dt, Def, null); // a third of a second, inside the grace
        Assert.Equal(LockPhase.Locking, l.Phase);
        Assert.False(l.Tracking);
        Assert.Equal(before, l.Progress, 3); // the progress waited
        l.Update(Dt, Def, 7);
        Assert.True(l.Tracking);
        for (var i = 0; i < 20; i++) l.Update(Dt, Def, null); // two thirds of a second: past the grace
        Assert.Equal(LockPhase.Idle, l.Phase);
        Assert.Equal((ushort)0, l.Target);
        Assert.Equal(0f, l.Progress);
    }

    [Fact]
    public void A_complete_lock_is_lost_when_the_aircraft_leaves_and_another_starts_from_nothing()
    {
        var l = new LockOn();
        for (var i = 0; i < 70; i++) l.Update(Dt, Def, 7);
        Assert.Equal(LockPhase.Locked, l.Phase);
        for (var i = 0; i < 20; i++) l.Update(Dt, Def, 9); // the launcher now looks at another Aircraft
        Assert.Equal(LockPhase.Locking, l.Phase);
        Assert.Equal((ushort)9, l.Target);
        Assert.True(l.Progress < 0.2f);
    }

    [Fact]
    public void Reset_lets_go()
    {
        var l = new LockOn();
        for (var i = 0; i < 70; i++) l.Update(Dt, Def, 7);
        l.Reset();
        Assert.Equal(LockPhase.Idle, l.Phase);
        Assert.Equal((ushort)0, l.Target);
    }

    [Fact]
    public void The_owner_is_warned_from_the_alert_share_on()
    {
        Assert.Equal(LockAlert.None, LockRules.AlertOf(Def, LockPhase.Idle, 0));
        Assert.Equal(LockAlert.None, LockRules.AlertOf(Def, LockPhase.Locking, 0.39f));
        Assert.Equal(LockAlert.Locking, LockRules.AlertOf(Def, LockPhase.Locking, 0.4f));
        Assert.Equal(LockAlert.Locked, LockRules.AlertOf(Def, LockPhase.Locked, 1f));
        Assert.True(LockAlert.Launched > LockAlert.Locked && LockAlert.Locked > LockAlert.Locking); // the worst wins by comparison
    }

    [Fact]
    public void The_missile_meets_a_circling_aircraft_where_it_will_be()
    {
        // an Aircraft on a 220 m circle, 45 m up, at 38 m/s; the shooter stands under the circle's centre
        Vector3 After(float t) { var a = 38f / 220f * t; return new Vector3(220 * MathF.Cos(a), 45, 220 * MathF.Sin(a)); }
        var plan = LockRules.Intercept(Eye, 160, 8, After);
        Assert.NotNull(plan);
        var (seconds, at) = plan!.Value;
        Assert.InRange(seconds, 1.3f, 1.5f); // about 224 m at 160 m/s
        Assert.True(Vector3.Distance(at, After(seconds)) < 0.01f);
        Assert.True(Vector3.Distance(Eye, at) <= 160 * seconds + 0.01f);
    }

    [Fact]
    public void An_aircraft_out_of_reach_has_no_flight()
    {
        Assert.Null(LockRules.Intercept(Eye, 160, 2, _ => new Vector3(0, 2, -1000)));
        Assert.Null(LockRules.Intercept(Eye, 0, 8, _ => new Vector3(0, 2, -10)));
    }

    [Fact]
    public void The_drawn_missile_arrives_with_the_hit()
    {
        var at = Eye;
        var left = 1.5f;
        Vector3 Target(float t) => new(100 + 30 * t, 60, -200);
        for (var t = 0f; left > 0; t += Dt, left -= Dt) at = LockRules.Chase(at, Target(t), left, Dt);
        Assert.True(Vector3.Distance(at, Target(1.5f)) < 2f);
    }

    [Fact]
    public void The_launcher_fires_only_on_a_complete_lock()
    {
        var file = Real();
        var index = file.Weapons.FindIndex(w => w.Id == "strela");
        Assert.True(index >= 0);
        var w = new PlayerWeapons(file, hotbar: new[] { index });
        Assert.Null(w.Update(1.0, new WeaponInput(true, false, 0, false)));
        Assert.Equal(1, w.Mag(index + 1)); // no dumb fire: the missile stays in the tube
        Assert.Null(w.Update(1.1, new WeaponInput(false, false, 0, false)));
        var shot = w.Update(1.2, new WeaponInput(true, false, 0, false, Locked: true));
        Assert.NotNull(shot);
        Assert.Equal("strela", shot!.Value.Weapon.Id);
        Assert.Equal(0, w.Mag(index + 1));
    }

    [Fact]
    public void The_launcher_is_a_secondary_that_downs_each_aircraft_in_the_agreed_number_of_hits()
    {
        var w = Real().Find("strela")!;
        Assert.Equal(WeaponSlot.Secondary, w.Slot);
        Assert.NotNull(w.Lock);
        Assert.Equal(1, w.MagazineSize);
        Assert.Equal(2, w.ReserveAmmo);
        Assert.Contains("uav", w.Lock!.UnlimitedRange);
        Assert.True(w.Lock.KillBlastRadius >= DustRules.FrontMinRadius); // a kill raises a Blast wave
        var kinds = KillstreakFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "killstreaks.json")).AircraftKinds;
        int Hits(string kind) => (int)MathF.Ceiling(kinds.First(k => k.Id == kind).Hp / w.Damage);
        Assert.Equal(3, Hits("gunship"));
        Assert.Equal(2, Hits("helicopter"));
        Assert.Equal(1, Hits("uav"));
    }

    [Fact]
    public void A_lock_block_is_validated()
    {
        const string head = """{ "version": 2, "starting_weapon": "p", "weapons": [ { "id": "p", "category": "pistol", "slot": "secondary", "kind": "hitscan", "damage": 10 }, """;
        static string Launcher(string lockBlock) => $$"""{ "id": "l", "category": "launcher", "slot": "secondary", "kind": "projectile", "damage": 1, "projectile_speed": 100, "projectile_life": 5, "lock": {{lockBlock}} } ] }""";
        Assert.NotNull(WeaponsFile.Parse(head + Launcher("""{ "seconds": 2, "range_m": 400, "cone_deg": 9 }""")));
        Assert.Throws<DataFileException>(() => WeaponsFile.Parse(head + Launcher("""{ "seconds": 0, "range_m": 400, "cone_deg": 9 }""")));
        Assert.Throws<DataFileException>(() => WeaponsFile.Parse(head + Launcher("""{ "seconds": 2, "range_m": 400, "cone_deg": 120 }""")));
        Assert.Throws<DataFileException>(() => WeaponsFile.Parse(head + Launcher("""{ "seconds": 2, "range_m": 400, "cone_deg": 9, "alert_at_fraction": 1.5 }""")));
    }

    [Fact]
    public void The_messages_round_trip()
    {
        var state = new LockStateMsg(LockPhase.Locking, 513, true, 0.25f, 2.5f);
        Assert.Equal(state, LockCodec.DecodeState(LockCodec.Encode(state)));
        var alert = new LockAlertMsg(LockAlert.Launched, 770);
        Assert.Equal(alert, LockCodec.DecodeAlert(LockCodec.Encode(alert)));
        var launch = new HomingLaunchMsg(0x8001, 12, 25, new Vector3(1.5f, 20, -300), 513, 1.75f);
        Assert.Equal(launch, LockCodec.DecodeLaunch(LockCodec.Encode(launch)));
        Assert.Null(LockCodec.DecodeState(new byte[3]));
        Assert.Null(LockCodec.DecodeAlert(new byte[] { 9, 0, 0 })); // not a level
        Assert.Null(LockCodec.DecodeLaunch(new byte[10]));
    }

    [Fact]
    public void A_missile_pursues_a_moving_vehicle_and_hits_it()
    {
        var at = new Vector3(0, 50, 0);
        var heli = new Vector3(0, 50, -300);
        var hit = false;
        for (var t = 0; t < 300 && !hit; t++)
        {
            heli += new Vector3(20, 0, 0) * Dt; // flies off sideways at 20 m/s
            (at, hit) = LockRules.Pursue(at, heli, 160f, 2f, Dt);
        }
        Assert.True(hit);
        Assert.True(Vector3.Distance(at, heli) < 1f);
    }

    [Fact]
    public void A_missile_out_of_reach_keeps_flying()
    {
        var (next, hit) = LockRules.Pursue(Vector3.Zero, new Vector3(0, 0, -500), 160f, 2f, Dt);
        Assert.False(hit);
        Assert.InRange(next.Z, -5.4f, -5.2f);
    }
}
