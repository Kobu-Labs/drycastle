using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>
/// The vehicle hit volume (ticket #144): the box from the ground to the top of the visible body, per type, and the ray test the server and
/// the client share. A bullet over the roof misses; one into the body hits on the side it enters.
/// </summary>
public class VehicleHitVolumeTests
{
    static VehiclesFile Shipped() => VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json"));
    static VehicleDef Type(string id) => Shipped().Vehicles.Single(v => v.Id == id);

    // a car at the origin facing -Z: the sedan's footprint and the height it is drawn at
    const float Hw = 1.28f, Hl = 2.18f, H = 2.2f;

    static void Near(Vector3 want, Vector3 got) => Assert.True(Vector3.Distance(want, got) < 1e-3f, $"want {want}, got {got}");

    static VehicleBoxHit? Car(Vector3 origin, Vector3 dir, float yaw = 0, float max = 1000f) =>
        VehicleBox.Ray(origin, dir, max, Vector3.Zero, yaw, Hw, Hl, H);

    [Fact]
    public void ARayFromAboveHitsTheRoof()
    {
        var hit = Car(new Vector3(0, 6, 0), new Vector3(0, -1, 0));
        Assert.NotNull(hit);
        Assert.Equal(6 - H, hit!.Value.Distance, 1e-3f);
        Near(new Vector3(0, H, 0), hit.Value.Local);
        Near(new Vector3(0, 1, 0), hit.Value.LocalNormal);
    }

    [Fact]
    public void ARayAimedAtTheCabinFromTheSideHitsTheDoor()
    {
        var hit = Car(new Vector3(6, 1.8f, 0), new Vector3(-1, 0, 0));
        Assert.NotNull(hit);
        Assert.Equal(6 - Hw, hit!.Value.Distance, 1e-3f);
        Near(new Vector3(Hw, 1.8f, 0), hit.Value.Local);
        Near(new Vector3(1, 0, 0), hit.Value.LocalNormal);
    }

    [Fact]
    public void ARayFromTheFrontHitsTheNoseAtItsOwnHeight()
    {
        // forward is -Z at yaw 0: a shot from in front enters the front face
        var hit = Car(new Vector3(0, 1.1f, -7), new Vector3(0, 0, 1));
        Assert.NotNull(hit);
        Assert.Equal(7 - Hl, hit!.Value.Distance, 1e-3f);
        Near(new Vector3(0, 0, -1), hit.Value.LocalNormal);
    }

    [Fact]
    public void ARayJustOverTheRoofMissesAndOneJustUnderItHits()
    {
        Assert.Null(Car(new Vector3(0, H + 0.01f, -7), new Vector3(0, 0, 1)));
        Assert.NotNull(Car(new Vector3(0, H - 0.01f, -7), new Vector3(0, 0, 1)));
    }

    [Fact]
    public void ACabinRoofShotHitsOnlyWhenTheBoxIsTallEnough()
    {
        // the server used to stop at 1.2 m: a shot at the cabin roof (1.8 m) passed through
        Assert.NotNull(VehicleBox.Ray(new Vector3(0, 1.8f, -7), new Vector3(0, 0, 1), 1000, Vector3.Zero, 0, Hw, Hl, 2.2f));
        Assert.Null(VehicleBox.Ray(new Vector3(0, 1.8f, -7), new Vector3(0, 0, 1), 1000, Vector3.Zero, 0, Hw, Hl, 1.2f));
    }

    [Fact]
    public void TheBoxFollowsTheYawAndTheFeetPosition()
    {
        // turned round: the side that faced the front now faces the back
        var turned = Car(new Vector3(0, 1.1f, 7), new Vector3(0, 0, -1), MathF.PI);
        Assert.NotNull(turned);
        Assert.Equal(7 - Hl, turned!.Value.Distance, 1e-3f);
        Near(new Vector3(0, 0, -1), turned.Value.LocalNormal);

        // a car standing at (10, 0, 10): the same shot from the front, moved with it
        var moved = VehicleBox.Ray(new Vector3(10, 1.1f, 3), new Vector3(0, 0, 1), 1000, new Vector3(10, 0, 10), 0, Hw, Hl, H);
        Assert.NotNull(moved);
        Assert.Equal((10 - Hl) - 3, moved!.Value.Distance, 1e-3f);
    }

    [Fact]
    public void AWallOrARangeLimitBeforeTheBoxStopsTheRay()
    {
        Assert.Null(Car(new Vector3(0, 1.1f, -7), new Vector3(0, 0, 1), 0, 3));   // the box is 4.8 m away
        Assert.Null(Car(new Vector3(0, 1.1f, 7), new Vector3(0, 0, 1)));          // pointing away
    }

    [Fact]
    public void AnOriginInsideTheBoxReportsNothing()
    {
        // a physics ray does not report a shape it starts in; the client does the same
        Assert.Null(Car(new Vector3(0, 1, 0), new Vector3(0, 0, 1)));
    }

    [Fact]
    public void TheHitIsTheDistanceAlongAnyLengthOfDirection()
    {
        var unit = Car(new Vector3(6, 1, 0), new Vector3(-1, 0, 0));
        var scaled = Car(new Vector3(6, 1, 0), new Vector3(-5, 0, 0));
        Assert.NotNull(unit);
        Assert.NotNull(scaled);
        Assert.Equal(unit!.Value.Distance, scaled!.Value.Distance, 1e-4f);
    }

    [Fact]
    public void EveryShippedVehicleHasAHitVolumeAtLeastAsTallAndWideAsItsModel()
    {
        // sedan.glb and suv.glb are 1.3 m tall and 1.5 m wide, both drawn at 1.7; the motorbike and bicycle tops are 0.89 m and 0.98 m
        var f = Shipped();
        Assert.True(f.Vehicles.Single(v => v.Id == "car").Height >= 1.3f * 1.7f - 0.01f);
        Assert.True(f.Vehicles.Single(v => v.Id == "jeep").Height >= 1.3f * 1.7f - 0.01f);
        Assert.True(f.Vehicles.Single(v => v.Id == "car").HalfWidth >= 1.5f * 1.7f / 2 - 0.01f);
        Assert.True(f.Vehicles.Single(v => v.Id == "jeep").HalfWidth >= 1.5f * 1.7f / 2 - 0.01f);
        Assert.True(f.Vehicles.Single(v => v.Id == "jeep").HalfLength >= 2.7f * 1.7f / 2 - 0.01f);
        Assert.True(f.Vehicles.Single(v => v.Id == "car").HalfLength >= 2.55f * 1.7f / 2 - 0.01f);
        Assert.True(f.Vehicles.Single(v => v.Id == "motorcycle").Height >= 0.89f);
        Assert.True(f.Vehicles.Single(v => v.Id == "bicycle").Height >= 0.98f);
        Assert.True(f.Vehicles.Single(v => v.Id == "boat").Height >= 1.45f); // the cabin's top
        foreach (var v in f.Vehicles) Assert.True(v.Height > 0);
    }

    [Fact]
    public void TheHitHeightIsDataDrivenAndValidated()
    {
        var f = Shipped();
        f.Vehicles.Single(v => v.Id == "car").Height = 0;
        Assert.Throws<DataFileException>(() => VehiclesFile.Validate(f));
    }

    [Fact]
    public void TheBlastFxIsFreedWithinAFewSecondsAndOutlivesItsOwnColumn()
    {
        // the column stops emitting, its last puff dies, and only then is the whole effect node freed
        Assert.True(VehicleBlastFx.EmitSeconds + VehicleBlastFx.ColumnLife <= VehicleBlastFx.FreeAfter);
        Assert.True(VehicleBlastFx.FreeAfter <= 4f);
    }
}
