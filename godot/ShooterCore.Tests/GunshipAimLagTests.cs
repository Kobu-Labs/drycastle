using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>
/// Ticket #161: the gunner's aim point is sent with the input, so the rounds land where the reticle is even though the server's
/// muzzle is later along the orbit than the client's. The lag simulation is the proof: the old scheme (the server re-intersects
/// the look angles from its own muzzle) misses by the platform's travel over the lag; the new one does not.
/// </summary>
public class GunshipAimLagTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static KillstreakFile Shipping => GameData.Load(DataDir).Killstreaks;
    static GunshipDef Gun => Shipping.Gunship!;
    static AircraftKindDef Kind => Shipping.AircraftKinds.Single(k => k.Id == "gunship");
    static Vector3 Centre => new(100, 0, -40);

    /// <summary>The client draws the platform this far behind the true position (interpolation delay).</summary>
    const float ClientMountLag = 0.10f;
    /// <summary>The input reaches the server this much after the look (latency). Total lag: 0.15 s.</summary>
    const float Latency = 0.05f;

    static Aircraft Orbit(float phase, bool clockwise) => new(Kind, 1, Centre, phase, clockwise, 3, lifetimeS: 200);

    /// <summary>The client's aim: the ground point its look meets from its own (lagging) muzzle, as NetClient computes it.</summary>
    static Vector3 ClientAim(Vector3 clientPosition, float yaw, float pitch, Aircraft a) =>
        GunshipAim.AimPoint(clientPosition - Vector3.UnitY * GunshipControl.TurretDropM, yaw, pitch, a.GroundY, GunshipAim.MaxDistance(Gun));

    /// <summary>Where the ray from the control's muzzle along its view meets the ground plane (before any spread).</summary>
    static Vector3 GroundHit(GunshipControl c)
    {
        var d = HitMath.Forward(c.TurretYaw, c.TurretPitch);
        var t = (c.Muzzle.Y - c.Aircraft.GroundY) / -d.Y;
        return c.Muzzle + d * t;
    }

    static readonly (float Yaw, float Pitch)[] Looks = { (0.6f, -0.45f), (-2.1f, -0.35f), (2.8f, -0.55f) };

    [Theory]
    [InlineData(0f, true)]
    [InlineData(MathF.PI / 2, true)]
    [InlineData(MathF.PI, true)]
    [InlineData(3 * MathF.PI / 2, true)]
    [InlineData(0f, false)]
    [InlineData(MathF.PI / 2, false)]
    [InlineData(MathF.PI, false)]
    [InlineData(3 * MathF.PI / 2, false)]
    public void Old_angles_only_scheme_misses_the_reticle_by_the_platform_travel_over_the_lag(float phase, bool clockwise)
    {
        foreach (var (yaw, pitch) in Looks)
        {
            var a = Orbit(phase, clockwise);
            a.Tick(4f - ClientMountLag);
            var clientMount = a.Position;
            var reticle = ClientAim(clientMount, yaw, pitch, a);
            a.Tick(ClientMountLag + Latency);
            // the old server: the same angles, intersected from its own, later muzzle
            var oldServer = GunshipAim.AimPoint(a.Position - Vector3.UnitY * GunshipControl.TurretDropM, yaw, pitch, a.GroundY, GunshipAim.MaxDistance(Gun));
            var miss = Vector3.Distance(oldServer, reticle);
            var travel = Kind.SpeedMps * (ClientMountLag + Latency);
            Assert.InRange(miss, travel * 0.9f, travel * 1.1f); // about 10.5 m at 70 m/s: plainly visible, not 0.5 degrees
        }
    }

    [Theory]
    [InlineData(0f, true)]
    [InlineData(MathF.PI / 2, true)]
    [InlineData(MathF.PI, true)]
    [InlineData(3 * MathF.PI / 2, true)]
    [InlineData(0f, false)]
    [InlineData(MathF.PI / 2, false)]
    [InlineData(MathF.PI, false)]
    [InlineData(3 * MathF.PI / 2, false)]
    public void New_aim_point_scheme_lands_the_rounds_on_the_reticle_under_the_same_lag(float phase, bool clockwise)
    {
        foreach (var (yaw, pitch) in Looks)
        {
            var a = Orbit(phase, clockwise);
            a.Tick(4f - ClientMountLag);
            var reticle = ClientAim(a.Position, yaw, pitch, a);
            a.Tick(ClientMountLag + Latency);

            var control = new GunshipControl(Gun, a, _ => { }, _ => { }, new Random(1));
            var input = NetInput.From(1, new MoveInput { Yaw = yaw }, pitch, 0, reticle);
            control.OnInput(input, 0);

            Assert.True(Vector3.Distance(control.AimPoint, reticle) < 0.01f, "the server holds the owner's point");
            var hit = GroundHit(control);
            Assert.True(Vector3.Distance(hit, reticle) < 0.5f, $"ground impact {Vector3.Distance(hit, reticle):F3} m from the reticle");
        }
    }

    [Fact]
    public void A_held_aim_keeps_landing_on_the_reticle_while_the_platform_keeps_orbiting()
    {
        var a = Orbit(0.4f, clockwise: true);
        a.Tick(3f);
        var reticle = ClientAim(a.Position, 0.9f, -0.5f, a);
        var control = new GunshipControl(Gun, a, _ => { }, _ => { }, new Random(1));
        var input = NetInput.From(1, new MoveInput { Yaw = 0.9f }, -0.5f, 0, reticle);
        var t = 0.0;
        for (var step = 0; step < 300; step++, t += 0.1)
        {
            a.Tick(0.1f);
            control.OnInput(input, t);
            Assert.True(Vector3.Distance(GroundHit(control), reticle) < 0.01f, $"off the reticle at {t:F1} s");
        }
    }

    [Fact]
    public void Rounds_fired_leave_along_the_view_from_the_server_muzzle_to_the_owners_point()
    {
        var a = Orbit(0f, clockwise: false);
        a.Tick(4f - ClientMountLag);
        var reticle = ClientAim(a.Position, 0.6f, -0.45f, a);
        a.Tick(ClientMountLag + Latency);
        var shots = new List<GunshipShot>();
        var control = new GunshipControl(Gun, a, shots.Add, _ => { }, new Random(3), weapons: new[] { 0 });
        control.OnInput(NetInput.From(1, new MoveInput { Yaw = 0.6f, Buttons = MoveButtons.Fire }, -0.45f, 0, reticle), 1.0);
        var shot = Assert.Single(shots);
        Assert.Equal(control.Muzzle, shot.Origin);
        var t = (reticle - shot.Origin).Length();
        var spread = Gun.Weapons[0].SpreadDeg * MathF.PI / 180f + 1e-3f;
        Assert.True(Vector3.Distance(shot.Origin + shot.Direction * t, reticle) < t * MathF.Tan(spread) + 0.01f);
    }

    [Fact]
    public void A_turret_with_no_aim_yet_does_not_fire()
    {
        var shots = new List<GunshipShot>();
        var a = Orbit(0f, clockwise: false);
        var control = new GunshipControl(Gun, a, shots.Add, _ => { }, new Random(1), weapons: new[] { 0 });
        control.OnInput(new NetInput { Buttons = (ushort)MoveButtons.Fire }, 1.0);
        Assert.Empty(shots);
        control.OnInput(NetInput.From(2, new MoveInput { Buttons = MoveButtons.Fire }, -0.45f, 0, ClientAim(a.Position, 0.6f, -0.45f, a)), 1.1);
        Assert.Single(shots);
    }

    [Fact]
    public void A_far_aim_point_is_bounded_from_the_orbit_centre_and_the_direction_is_kept()
    {
        var a = Orbit(0f, clockwise: false);
        var control = new GunshipControl(Gun, a, _ => { }, _ => { }, new Random(1));
        var far = a.Centre + new Vector3(50000, 300, 0);
        control.OnInput(NetInput.From(1, new MoveInput(), 0f, 0, far), 0);
        var reach = GunshipAim.OrbitReach(Kind) + GunshipAim.MaxDistance(Gun) + GunshipAim.AimSlackM;
        Assert.InRange(Vector3.Distance(control.AimPoint, a.Centre), reach - 0.01f, reach + 0.01f);
        Assert.True(Vector3.Distance(Vector3.Normalize(control.AimPoint - a.Centre), Vector3.Normalize(far - a.Centre)) < 1e-4f);
    }

    [Fact]
    public void A_stabilised_ground_point_out_of_range_of_the_moving_muzzle_is_not_pulled_back()
    {
        // a ground point 660 m from the orbit's centre, on the near side of the orbit. Half an orbit later the muzzle is on the far
        // side and the point is about 1080 m from it: farther than the old 800 m ranges ever allowed, and within the map. Any aim point
        // on the map is valid now (the longest range is 3000 m), so the clamp must leave it alone. A clamp to the muzzle would pull it
        // off the reticle (the bug this guards); the bound is from the orbit centre, so the point stays where the owner put it.
        var a = Orbit(0f, clockwise: false);
        var outward = Vector3.Normalize(new Vector3(a.Position.X - a.Centre.X, 0, a.Position.Z - a.Centre.Z));
        var aim = new Vector3(a.Centre.X + 660f * outward.X, a.GroundY, a.Centre.Z + 660f * outward.Z);
        var control = new GunshipControl(Gun, a, _ => { }, _ => { }, new Random(1));
        control.OnInput(NetInput.From(1, new MoveInput(), 0f, 0, aim), 0);
        a.Tick(MathF.PI * Kind.RadiusM / Kind.SpeedMps); // half an orbit
        Assert.True(Vector3.Distance(control.Muzzle, aim) > 850f, "the point is beyond the old 800 m ranges from the muzzle now");
        Assert.True(Vector3.Distance(control.Muzzle, aim) < GunshipAim.MaxDistance(Gun), "and still inside the longest range");
        control.OnInput(NetInput.From(2, new MoveInput(), 0f, 0, aim), 1.0);
        Assert.True(Vector3.Distance(control.AimPoint, aim) < 0.01f, "the aim point did not move with the platform");
    }

    [Fact]
    public void The_longest_range_is_the_3000_metre_gunship_range_and_a_horizon_look_is_that_far()
    {
        Assert.Equal(3000f, GunshipAim.MaxDistance(Gun));
        var p = GunshipAim.AimPoint(new Vector3(0, 100, 0), 0f, 0f, 0f, GunshipAim.MaxDistance(Gun));
        Assert.Equal(3000f, Vector3.Distance(p, new Vector3(0, 100, 0)), 0.01f); // a horizontal look finds no ground: the full range
        var down = GunshipAim.AimPoint(new Vector3(0, 100, 0), 0f, -0.2f, 0f, GunshipAim.MaxDistance(Gun));
        Assert.Equal(0f, down.Y, 0.01f);                                          // a look down meets the ground plane first
        Assert.True(Vector3.Distance(down, new Vector3(0, 100, 0)) < 3000f);
    }

    [Fact]
    public void An_aim_below_the_ground_is_lifted_to_it()
    {
        var a = Orbit(0f, clockwise: false);
        var reach = GunshipAim.OrbitReach(Kind);
        var below = new Vector3(a.Centre.X, a.GroundY - 40f, a.Centre.Z);
        var p = GunshipAim.ClampAim(a.Centre, reach, below, a.GroundY, GunshipAim.MaxDistance(Gun));
        Assert.Equal(a.GroundY, p.Y, 4);
        var inRange = new Vector3(a.Centre.X + 100, a.GroundY, a.Centre.Z);
        Assert.Equal(inRange, GunshipAim.ClampAim(a.Centre, reach, inRange, a.GroundY, GunshipAim.MaxDistance(Gun)));
    }

    [Fact]
    public void The_aim_is_quantised_to_the_position_grid_and_round_trips_exactly()
    {
        var p = new Vector3(123.456f, -0.5f, -77.001f);
        var input = NetInput.From(9, new MoveInput(), 0f, 0, p);
        Assert.True(input.HasAim);
        Assert.True(Vector3.Distance(input.Aim, p) <= 0.5f / 128f + 1e-6f);
        var got = new List<NetInput>();
        Assert.True(Wire.TryDecodeInputs(Wire.EncodeInputs(new[] { input }), got, out _, out _));
        Assert.Equal(input, Assert.Single(got));
        Assert.Equal(input.Aim, Assert.Single(got).Aim);
    }

    [Fact]
    public void Inputs_without_an_aim_stay_eleven_bytes_plus_the_flags_byte_and_mix_with_aimed_ones()
    {
        var plain = NetInput.From(1, new MoveInput { Buttons = MoveButtons.Fire }, 0f, 2);
        var aimed = NetInput.From(2, new MoveInput(), 0.1f, 2, new Vector3(10, 0, 20));
        var pkt = Wire.EncodeInputs(new[] { plain, aimed, plain });
        Assert.Equal(Wire.InputHeaderBytes + Wire.InputBytes + (Wire.InputBytes + Wire.AimBytes) + Wire.InputBytes, pkt.Length);
        var got = new List<NetInput>();
        Assert.True(Wire.TryDecodeInputs(pkt, got, out _, out _));
        Assert.Equal(new[] { plain, aimed, plain }, got);
        Assert.False(got[0].HasAim);
        Assert.True(got[1].HasAim);
    }

    [Fact]
    public void A_truncated_aim_is_rejected()
    {
        var aimed = NetInput.From(2, new MoveInput(), 0f, 0, new Vector3(10, 0, 20));
        var pkt = Wire.EncodeInputs(new[] { aimed });
        var got = new List<NetInput>();
        Assert.False(Wire.TryDecodeInputs(pkt.AsSpan(0, pkt.Length - 1), got, out _, out _));
        Assert.False(Wire.TryDecodeInputs(pkt.AsSpan(0, Wire.InputBytes + 2), got, out _, out _));
        Assert.Empty(got);
    }
}
