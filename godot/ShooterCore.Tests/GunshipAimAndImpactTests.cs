using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>Ticket #152: the turret reaches the orbit centre, a gunship round at a dummy is a hit, impact surfaces and the impact codec.</summary>
public class GunshipAimAndImpactTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static KillstreakFile Shipping => GameData.Load(DataDir).Killstreaks;
    static GunshipDef Gun => Shipping.Gunship!;
    static AircraftKindDef Kind => Shipping.AircraftKinds.Single(k => k.Id == "gunship");

    static Vector3 Centre => new(100, 0, -40);

    static Aircraft Orbit(float phase = 0) => new(Kind, 1, Centre, phase, false, 3, null);

    /// <summary>The engine yaw that points forward (-Z at yaw 0) along a horizontal direction.</summary>
    static float YawToward(Vector3 d) => MathF.Atan2(-d.X, -d.Z);

    [Fact]
    public void Turret_reaches_the_orbit_centre_from_the_airframe_heading()
    {
        // the airframe flies tangent to its circle, so the centre is 90 degrees off its nose; the aim is world space, so it is reached
        var a = Orbit(0);
        var toCentre = new Vector3(Centre.X - a.Position.X, 0, Centre.Z - a.Position.Z);
        var yaw = YawToward(toCentre);
        var control = new GunshipControl(Gun, a, _ => { }, _ => { }, new Random(1));
        control.OnInput(new NetInput { Yaw = Quant.Yaw(yaw), Pitch = Quant.Pitch(-0.4f) }, 1.0);
        Assert.InRange(MathF.Abs(GunshipAim.Wrap(control.TurretYaw - yaw)), 0, 0.001f);
    }

    [Fact]
    public void A_cannon_round_at_a_target_dummy_position_hits_it_through_the_raycast()
    {
        // the dummy is a HitTarget with a target id (50000 and up); the gunship round is the same ray the server uses
        var a = Orbit(0);
        var shots = new List<GunshipShot>();
        var control = new GunshipControl(Gun, a, shots.Add, _ => { }, new Random(1), weapons: new[] { 0 });
        var yaw = a.YawRad;
        control.OnInput(Input(yaw, -0.4f, MoveButtons.Fire), 0.0);
        Assert.Single(shots);
        var shot = shots[0];
        var spot = shot.Origin + shot.Direction * 30f;
        var targets = new[] { new HitTarget(50000, new Vector3(spot.X, spot.Y - 1.0f, spot.Z), Stance.Stand, 0f) };
        var hit = HitMath.Raycast(shot.Origin, shot.Direction, Gun.Weapons[0].RangeM, float.PositiveInfinity, targets, 3);
        Assert.NotNull(hit);
        Assert.Equal((ushort)50000, hit!.Value.Id);
    }

    [Fact]
    public void Surface_is_a_body_then_a_vehicle_then_water_then_the_group()
    {
        Assert.Equal(SurfaceKind.Flesh, SurfaceClassifier.Classify(body: true, vehicle: true, inWater: true, "road"));
        Assert.Equal(SurfaceKind.Metal, SurfaceClassifier.Classify(body: false, vehicle: true, inWater: true, "road"));
        Assert.Equal(SurfaceKind.Water, SurfaceClassifier.Classify(body: false, vehicle: false, inWater: true, "building"));
        Assert.Equal(SurfaceKind.Road, SurfaceClassifier.Classify(false, false, false, "road"));
        Assert.Equal(SurfaceKind.Building, SurfaceClassifier.Classify(false, false, false, "building"));
        Assert.Equal(SurfaceKind.Ground, SurfaceClassifier.Classify(false, false, false, null));
        Assert.Equal(SurfaceKind.Ground, SurfaceClassifier.Classify(false, false, false, "anything"));
    }

    [Theory]
    [InlineData(GunshipWeaponKind.Cannon, 22f, 0f, ImpactSize.Puff)]
    [InlineData(GunshipWeaponKind.Cannon, 40f, 0f, ImpactSize.Burst)]
    [InlineData(GunshipWeaponKind.Cannon, 6f, 3f, ImpactSize.Puff)]     // 25 mm: a 3 m blast
    [InlineData(GunshipWeaponKind.Cannon, 36f, 6f, ImpactSize.Burst)]   // 40 mm: a 6 m blast
    [InlineData(GunshipWeaponKind.Cannon, 25f, 10f, ImpactSize.Plume)]  // a 10 m blast is a plume
    [InlineData(GunshipWeaponKind.Rocket, 0f, 0f, ImpactSize.Burst)]
    [InlineData(GunshipWeaponKind.Shell, 150f, 14f, ImpactSize.Plume)]
    public void Burst_size_follows_the_round(GunshipWeaponKind kind, float damage, float radius, ImpactSize expected)
    {
        Assert.Equal(expected, SurfaceClassifier.SizeFor(kind, damage, radius));
    }

    [Fact]
    public void Impact_codec_round_trips_and_rejects_bad_payloads()
    {
        var info = new ImpactInfo(new Vector3(1.5f, -2f, 300.25f), SurfaceKind.Water, ImpactSize.Plume);
        var bytes = ImpactCodec.Encode(info);
        Assert.Equal(ImpactCodec.Bytes, bytes.Length);
        Assert.Equal(info, ImpactCodec.Decode(bytes));
        Assert.Null(ImpactCodec.Decode(bytes.AsSpan(0, 13)));
        var bad = (byte[])bytes.Clone();
        bad[12] = 99;
        Assert.Null(ImpactCodec.Decode(bad));
    }

    static NetInput Input(float yaw, float pitch, MoveButtons buttons) => new()
    {
        Yaw = Quant.Yaw(yaw), Pitch = Quant.Pitch(pitch), Buttons = (ushort)buttons,
    };
}
