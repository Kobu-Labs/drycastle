using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>
/// Ticket #160: the gunship aim is world space and ground-stabilised. The aim point stays on its ground point while the platform
/// orbits; the mouse moves it, scaled by zoom; the pitch is the only limit; the camera's yaw is continuous through every heading
/// and through +-pi; the server fires along the view. Also the tracer data (#160) and its codec.
/// </summary>
public class GunshipGroundAimTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static KillstreakFile Shipping => GameData.Load(DataDir).Killstreaks;
    static GunshipDef Gun => Shipping.Gunship!;
    static AircraftKindDef Kind => Shipping.AircraftKinds.Single(k => k.Id == "gunship");
    static Vector3 Centre => new(100, 0, -40);

    static Aircraft Orbit(float phase = 0, float? life = null) => new(Kind, 1, Centre, phase, false, 3, life);

    static NetInput Look(GunshipControl c, float yaw, float pitch, MoveButtons b = MoveButtons.None) => GunnerInputs.Look(c, yaw, pitch, b);

    static Vector3 Dir(float yaw, float pitch) => HitMath.Forward(yaw, pitch);

    static float Angle(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f));

    [Fact]
    public void Aim_point_stays_on_its_ground_point_while_the_platform_orbits_through_360_degrees()
    {
        var a = Orbit(0, life: 200);
        var control = new GunshipControl(Gun, a, _ => { }, _ => { }, new Random(1));
        var input = Look(control, 0.7f, -0.45f);
        control.OnInput(input, 0);
        var anchor = control.AimPoint;
        Assert.InRange(anchor.Y, -0.01f, 0.01f); // on the ground plane (the look reaches it inside the range)

        var minYaw = float.MaxValue; var maxYaw = float.MinValue;
        var t = 0.0;
        // a full orbit is 2 pi * 420 / 70 = 37.7 s; sweep 40 s
        for (var step = 0; step < 800; step++, t += 0.05)
        {
            a.Tick(0.05f);
            control.OnInput(input, t);
            Assert.True(Vector3.Distance(control.AimPoint, anchor) < 1e-3f, $"aim point drifted at {t:F2} s");
            minYaw = MathF.Min(minYaw, control.TurretYaw); maxYaw = MathF.Max(maxYaw, control.TurretYaw);
        }
        // the platform really turned (the view moved a lot), so the stillness is the aim's, not the airframe's
        Assert.True(maxYaw - minYaw > MathF.PI, "the view should have swung with the orbit");
    }

    [Fact]
    public void The_view_points_from_the_moving_muzzle_at_the_aim_point()
    {
        var a = Orbit(0, life: 200);
        var control = new GunshipControl(Gun, a, _ => { }, _ => { }, new Random(1));
        control.OnInput(Look(control, -2.0f, -0.3f), 0);
        var t = 0.0;
        for (var step = 0; step < 400; step++, t += 0.1)
        {
            a.Tick(0.1f);
            control.OnInput(Look(control, -2.0f, -0.3f), t);
            var toAim = control.AimPoint - control.Muzzle;
            Assert.True(Angle(Dir(control.TurretYaw, control.TurretPitch), toAim) < 1e-3f);
        }
    }

    [Fact]
    public void Mouse_moves_the_aim_point_and_zoom_divides_the_move()
    {
        var yaw = 0.3f; var pitch = -0.5f;
        var (y1, p1) = GunshipAim.Turn(yaw, pitch, 0.04f, 0f, 1f, Gun);
        Assert.Equal(yaw + 0.04f, y1, 5);
        Assert.Equal(pitch, p1, 5);
        // x4 zoom: a quarter of the angular speed
        var (y4, _) = GunshipAim.Turn(yaw, pitch, 0.04f, 0f, 4f, Gun);
        Assert.Equal(yaw + 0.01f, y4, 5);
        // invert-Y is the player's sign: the pitch move is the mouse's, divided by zoom the same way
        var (_, p25) = GunshipAim.Turn(yaw, pitch, 0f, -0.05f, 2.5f, Gun);
        Assert.Equal(pitch - 0.02f, p25, 5);
    }

    [Fact]
    public void A_mouse_move_moves_the_aim_point_and_a_still_mouse_keeps_it()
    {
        var a = Orbit(0, life: 200);
        var control = new GunshipControl(Gun, a, _ => { }, _ => { }, new Random(1));
        control.OnInput(Look(control, 0.2f, -0.4f), 0);
        var before = control.AimPoint;
        control.OnInput(Look(control, 0.2f, -0.4f), 0.1); // still
        Assert.Equal(before, control.AimPoint);
        control.OnInput(Look(control, 0.4f, -0.4f), 0.2); // moved
        Assert.True(Vector3.Distance(before, control.AimPoint) > 1f);
    }

    [Fact]
    public void Pitch_is_clamped_to_its_range_and_nothing_else_is()
    {
        var up = GunshipAim.ClampPitch(0.5f, Gun);
        Assert.Equal(Gun.PitchMaxDeg * MathF.PI / 180f, up, 4);
        var down = GunshipAim.ClampPitch(-3f, Gun);
        Assert.Equal(Gun.PitchMinDeg * MathF.PI / 180f, down, 4);

        var control = new GunshipControl(Gun, Orbit(0), _ => { }, _ => { }, new Random(1));
        control.OnInput(Look(control, 0.1f, 0.5f), 0);
        Assert.Equal(Gun.PitchMaxDeg * MathF.PI / 180f, control.TurretPitch, 0.002f);
    }

    [Fact]
    public void No_yaw_limit_the_view_faces_straight_back_at_the_airframe()
    {
        var a = Orbit(0, life: 200);
        var control = new GunshipControl(Gun, a, _ => { }, _ => { }, new Random(1));
        var back = a.YawRad + MathF.PI;
        control.OnInput(Look(control, back, -0.4f), 0);
        Assert.True(Angle(Dir(control.TurretYaw, control.TurretPitch), Dir(back, -0.4f)) < 1e-3f);
    }

    [Fact]
    public void Unwrap_takes_the_short_way_across_the_pi_cut()
    {
        var u = GunshipAim.Unwrap(3.1f, -3.1f);
        Assert.Equal(3.1f + (2 * MathF.PI - 6.2f), u, 4); // 3.1 + 0.0832, not -3.1 (the long way)
        Assert.Equal(0.5f, GunshipAim.Unwrap(-0.3f, 0.5f), 4); // a plain step: unchanged
    }

    [Fact]
    public void Camera_yaw_is_continuous_as_the_heading_sweeps_0_to_720_degrees()
    {
        // heading = phase + rate * age; the orbit is 37.7 s, so 720 degrees is 75.4 s
        var a = Orbit(0, life: 200);
        var control = new GunshipControl(Gun, a, _ => { }, _ => { }, new Random(1));
        var input = Look(control, -1.2f, -0.35f);
        control.OnInput(input, 0);
        var prevDir = Dir(control.TurretYaw, control.TurretPitch);
        var unwrapped = control.TurretYaw;
        var prevUnwrapped = unwrapped;
        var t = 0.0;
        var maxStep = 0f; var maxDirStep = 0f;
        for (var step = 0; step < 1600; step++, t += 0.05)
        {
            a.Tick(0.05f);
            control.OnInput(input, t);
            var d = Dir(control.TurretYaw, control.TurretPitch);
            unwrapped = GunshipAim.Unwrap(prevUnwrapped, control.TurretYaw);
            maxStep = MathF.Max(maxStep, MathF.Abs(unwrapped - prevUnwrapped));
            maxDirStep = MathF.Max(maxDirStep, Angle(prevDir, d));
            prevDir = d; prevUnwrapped = unwrapped;
        }
        Assert.True(a.Age >= 75f, "the sweep covers 720 degrees of heading");
        Assert.True(maxStep < 0.02f, $"camera yaw jumped by {maxStep} rad in one step");
        Assert.True(maxDirStep < 0.02f, $"camera direction jumped by {maxDirStep} rad in one step");
    }

    [Fact]
    public void Camera_is_continuous_as_the_aim_yaw_sweeps_through_plus_and_minus_pi()
    {
        var a = Orbit(0, life: 200);
        var control = new GunshipControl(Gun, a, _ => { }, _ => { }, new Random(1));
        var prevDir = Vector3.Zero;
        var prevUnwrapped = 0f; var first = true;
        var maxDirStep = 0f; var maxStep = 0f;
        var t = 0.0;
        // the look yaw goes from -pi - 0.6 to pi + 0.6 in small mouse steps: across the cut at +-pi (and wrapped by Quant)
        for (var yaw = -MathF.PI - 0.6f; yaw <= MathF.PI + 0.6f; yaw += 0.004f, t += 0.03)
        {
            control.OnInput(Look(control, yaw, -0.4f), t);
            var d = Dir(control.TurretYaw, control.TurretPitch);
            if (first) { prevDir = d; prevUnwrapped = control.TurretYaw; first = false; continue; }
            var u = GunshipAim.Unwrap(prevUnwrapped, control.TurretYaw);
            maxStep = MathF.Max(maxStep, MathF.Abs(u - prevUnwrapped));
            maxDirStep = MathF.Max(maxDirStep, Angle(prevDir, d));
            prevDir = d; prevUnwrapped = u;
        }
        Assert.True(maxDirStep < 0.02f, $"aim direction jumped by {maxDirStep} rad");
        Assert.True(maxStep < 0.02f, $"unwrapped camera yaw jumped by {maxStep} rad");
    }

    [Fact]
    public void Server_fire_direction_is_the_view_within_the_weapon_spread()
    {
        var a = Orbit(0, life: 200);
        var shots = new List<GunshipShot>();
        var control = new GunshipControl(Gun, a, shots.Add, _ => { }, new Random(7));
        control.OnInput(Look(control, 0.9f, -0.5f), 0);
        var view = Dir(control.TurretYaw, control.TurretPitch);
        // one weapon at a time: the hotbar key picks it (key 1 is the turret's first weapon)
        for (var w = 0; w < Gun.Weapons.Count; w++)
        {
            var input = Look(control, 0.9f, -0.5f, MoveButtons.Fire);
            input.Slot = (byte)(w + 1);
            control.OnInput(input, 100.0 + w * 10);
            control.OnInput(input, 100.0 + w * 10 + 0.5); // a second press, past every interval, fires again
        }
        Assert.NotEmpty(shots);
        foreach (var s in shots)
        {
            var spread = Gun.Weapons[s.WeaponIndex].SpreadDeg * MathF.PI / 180f + 1e-3f;
            Assert.True(Angle(s.Direction, view) <= spread, $"round at {Angle(s.Direction, view)} rad, spread {spread}");
            Assert.Equal(control.Muzzle, s.Origin);
        }
    }

    [Fact]
    public void Gunship_tracers_carry_their_weapons_speed_and_length_from_data()
    {
        var cannon = Gun.Weapons.Single(w => w.Kind == GunshipWeaponKind.Cannon);
        Assert.Equal(1500f, cannon.TracerSpeedMps);
        Assert.Equal(14f, cannon.TracerLengthM);
        var rockets = Gun.Weapons.Single(w => w.Kind == GunshipWeaponKind.Rocket);
        Assert.Null(rockets.TracerSpeedMps); // rockets fly in the rocket sim, with their own look
        var shell = Shipping.Ac130!.Weapons.Single(w => w.Kind == GunshipWeaponKind.Shell);
        Assert.Equal(1500f, shell.TracerSpeedMps); // as fast as the cannons: a shell is a fast round
        // the 105 mm shell reaches its 3000 m range in about 2 s (its visual only; the burst is at the server's resolution)
        Assert.True((shell.RangeM - shell.TracerLengthM!.Value) / shell.TracerSpeedMps!.Value < 2.1f);
    }

    [Fact]
    public void Tracer_speed_and_length_round_trip_through_the_codec()
    {
        var t = new GunshipTracer(4, new Vector3(1, 2, 3), new Vector3(40, 2, 3), 1500f, 14f);
        var bytes = GunshipFxCodec.EncodeShots(new GunshipShotsMsg(9, new[] { t }));
        var back = GunshipFxCodec.DecodeShots(bytes);
        Assert.NotNull(back);
        var r = Assert.Single(back!.Tracers);
        Assert.Equal(1500f, r.SpeedMps);
        Assert.Equal(14f, r.LengthM, 2);
        Assert.Equal(t.Origin, r.Origin);
    }
}
