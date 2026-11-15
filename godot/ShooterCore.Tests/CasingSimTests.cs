using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class CasingSimTests
{
    static bool Flat(Vector3 from, float depth, out float y, out bool onVehicle) { onVehicle = false; y = 0f; return from.Y >= 0 && from.Y - depth <= 0; }
    static bool Roof(Vector3 from, float depth, out float y, out bool onVehicle) { onVehicle = true; y = 0f; return from.Y >= 0 && from.Y - depth <= 0; }
    static bool Void(Vector3 from, float depth, out float y, out bool onVehicle) { onVehicle = false; y = 0f; return false; }

    static void Run(CasingSim sim, float seconds, CasingFloor f, List<CasingSim.Impact>? log = null)
    {
        for (var t = 0f; t < seconds; t += 1f / 60f) { sim.Step(1f / 60f, f); log?.AddRange(sim.Impacts); }
    }

    [Fact]
    public void ACasingFallsBouncesAtMostThreeTimesAndLiesFlat()
    {
        var sim = new CasingSim();
        var s = sim.Spawn(new Vector3(0, 1.5f, 0), new Vector3(2f, 2f, 0), 0, 12f, CasingKind.Heavy, Flat);
        var hits = new List<CasingSim.Impact>();
        Run(sim, 4f, Flat, hits);
        Assert.Equal(CasingPhase.Resting, s.Phase);
        Assert.InRange(hits.Count, 1, 4); // up to three bounces and the settle
        Assert.Equal(CasingProfiles.Of(CasingKind.Heavy).Diameter * 0.5f, s.Pos.Y, 3); // lying flat: its radius above the floor
        Assert.Equal(0f, MathF.Sin(s.Tumble), 3);
        Assert.True(s.Pos.X > 0.3f); // it travelled
    }

    [Fact]
    public void ARestingCasingStaysTwentySecondsThenFadesAndIsFree()
    {
        var sim = new CasingSim();
        var s = sim.Spawn(new Vector3(0, 0.3f, 0), Vector3.Zero, 0, 0, CasingKind.Rifle, Flat);
        Run(sim, 3f, Flat);
        Assert.Equal(CasingPhase.Resting, s.Phase);
        Run(sim, CasingSim.Stay - 3f - 0.5f, Flat);
        Assert.Equal(CasingPhase.Resting, s.Phase);
        Run(sim, 1f, Flat);
        Assert.Equal(CasingPhase.Fading, s.Phase);
        Assert.InRange(s.Scale, 0f, 1f);
        Run(sim, CasingSim.FadeSeconds + 0.2f, Flat);
        Assert.Equal(CasingPhase.Free, s.Phase);
        Assert.Equal(0, sim.Live);
    }

    [Fact]
    public void ACasingThatLandsOnAVehicleGoesAfterAboutASecond()
    {
        var sim = new CasingSim();
        var s = sim.Spawn(new Vector3(0, 0.3f, 0), Vector3.Zero, 0, 0, CasingKind.Rifle, Roof);
        Run(sim, 0.7f, Roof);
        Assert.Equal(CasingPhase.Resting, s.Phase);
        Run(sim, CasingSim.VehicleStay + 0.2f, Roof);
        Assert.Equal(CasingPhase.Fading, s.Phase);
    }

    [Fact]
    public void ACasingWithNoFloorIsDroppedAfterEightMetres()
    {
        var sim = new CasingSim();
        var s = sim.Spawn(new Vector3(0, 50, 0), Vector3.Zero, 0, 3, CasingKind.Shell, Void);
        Run(sim, 2f, Void);
        Assert.Equal(CasingPhase.Free, s.Phase);
    }

    [Fact]
    public void AFullRingTakesTheOldestSlot()
    {
        var sim = new CasingSim();
        for (var i = 0; i < CasingSim.Capacity; i++) sim.Spawn(new Vector3(i, 5, 0), Vector3.Zero, 0, 0, CasingKind.Pistol, Flat);
        Assert.Equal(CasingSim.Capacity, sim.Live);
        var first = sim.Slots[0];
        first.Pos = new Vector3(-99, 5, 0);
        sim.Spawn(new Vector3(7, 5, 7), Vector3.Zero, 0, 0, CasingKind.Pistol, Flat);
        Assert.Equal(7f, first.Pos.X);
        Assert.Equal(CasingSim.Capacity, sim.Live);
    }

    [Fact]
    public void ARestingCasingDoesNotTouchItsInstance()
    {
        var sim = new CasingSim();
        var s = sim.Spawn(new Vector3(0, 0.3f, 0), Vector3.Zero, 0, 0, CasingKind.Rifle, Flat);
        Run(sim, 3f, Flat);
        s.Dirty = false;
        Run(sim, 5f, Flat);
        Assert.False(s.Dirty);
    }

    [Fact]
    public void OnlyGunCategoriesEjectCasings()
    {
        Assert.Equal(CasingKind.Pistol, CasingProfiles.Of(WeaponCategory.Pistol));
        Assert.Equal(CasingKind.Shotgun, CasingProfiles.Of(WeaponCategory.Shotgun));
        Assert.Equal(CasingKind.Rifle, CasingProfiles.Of(WeaponCategory.MachineGun));
        Assert.Null(CasingProfiles.Of(WeaponCategory.Melee));
        Assert.Null(CasingProfiles.Of(WeaponCategory.Launcher));
    }
}
