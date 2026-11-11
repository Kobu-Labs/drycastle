using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>Recoil runtime (RecoilState): climb, cap, recovery, burst pattern, determinism, ADS and crouch scaling.</summary>
public class RecoilTests
{
    static float Deg(float d) => d * MathF.PI / 180f;

    static RecoilDef Climb(float perShot = 1, float maxClimb = 0, float recovery = 0) =>
        new() { VerticalPerShot = perShot, MaxClimb = maxClimb, RecoveryRate = recovery };

    [Fact]
    public void ClimbAddsPerShotAndTheFirstShotIsUnkicked()
    {
        var def = Climb(perShot: 1);
        var r = new RecoilState(1);
        r.Tick(0, true, def);
        var fired = new List<float>();
        for (var i = 0; i < 5; i++) { fired.Add(r.Shot(def, 1).PitchRad); r.Tick(0.1 * (i + 1), true, def); }
        for (var i = 0; i < 5; i++) Assert.Equal(Deg(i), fired[i], 4);
        Assert.Equal(Deg(5), r.PitchRad, 4);
    }

    [Fact]
    public void MaxClimbCapsSustainedFire()
    {
        var def = Climb(perShot: 1, maxClimb: 3);
        var r = new RecoilState(1);
        for (var i = 0; i < 10; i++) r.Shot(def, 1);
        Assert.Equal(Deg(3), r.PitchRad, 4);
    }

    [Fact]
    public void ReleasingTheTriggerSettlesAtTheRecoveryRateAndStops()
    {
        var def = Climb(perShot: 1, recovery: 2);
        var r = new RecoilState(1);
        r.Tick(0, false, def);
        for (var i = 0; i < 3; i++) r.Shot(def, 1);
        r.Tick(0.5, false, def); // 2 deg/s for 0.5 s
        Assert.Equal(Deg(2), r.PitchRad, 4);
        r.Tick(5, false, def);
        Assert.Equal(0, r.PitchRad, 4);
        Assert.Equal(0, r.YawRad, 4);
    }

    [Fact]
    public void HeldTriggerDoesNotRecover()
    {
        var def = Climb(perShot: 1, recovery: 2);
        var r = new RecoilState(1);
        r.Tick(0, true, def);
        r.Shot(def, 1); r.Shot(def, 1);
        r.Tick(3, true, def);
        Assert.Equal(Deg(2), r.PitchRad, 4);
    }

    [Fact]
    public void PatternCyclesWithinABurstAndRestartsOnTheNextBurst()
    {
        var def = new RecoilDef { Pattern = new() { 2, 0, -2 }, RecoveryRate = 0 };
        var r = new RecoilState(1);
        // burst 1: fired offsets 0, +2 (after pattern[0]); the second shot adds pattern[1] = 0
        Assert.Equal(0, r.Shot(def, 1).YawRad, 4);
        Assert.Equal(Deg(2), r.Shot(def, 1).YawRad, 4);
        Assert.Equal(Deg(2), r.YawRad, 4);

        // release: no recovery rate, so the offset stays; the next burst restarts at pattern[0] (+2), not at pattern[2] (-2)
        r.Tick(1, false, def);
        Assert.Equal(Deg(2), r.Shot(def, 1).YawRad, 4);
        Assert.Equal(Deg(4), r.YawRad, 4);
    }

    [Fact]
    public void SwayIsDeterministicForTheSameSeedAndWandersSideways()
    {
        var def = new RecoilDef { HorizontalSway = 1 };
        float[] Run(ulong seed)
        {
            var r = new RecoilState(seed);
            var v = new float[40];
            for (var i = 0; i < v.Length; i++) v[i] = r.Shot(def, 1).YawRad;
            return v;
        }
        var a = Run(42);
        var b = Run(42);
        var c = Run(7);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Contains(a, x => x > 0);
        Assert.Contains(a, x => x < 0);
    }

    [Fact]
    public void AdsAndCrouchScaleTheKick()
    {
        var def = new RecoilDef { VerticalPerShot = 2, AdsMultiplier = 0.5f, CrouchMultiplier = 0.25f };
        Assert.Equal(1f, RecoilState.Multiplier(def, false, false));
        Assert.Equal(0.5f, RecoilState.Multiplier(def, true, false));
        Assert.Equal(0.25f, RecoilState.Multiplier(def, false, true));
        Assert.Equal(0.125f, RecoilState.Multiplier(def, true, true));

        var r = new RecoilState(1);
        var m = RecoilState.Multiplier(def, true, false);
        r.Shot(def, m);
        r.Shot(def, m);
        Assert.Equal(Deg(2), r.PitchRad, 4); // two ADS shots of 2 deg * 0.5
    }

    [Fact]
    public void ClampPitchKeepsTheLookRange()
    {
        Assert.Equal(RecoilState.MaxPitchRad, RecoilState.ClampPitch(2f));
        Assert.Equal(-RecoilState.MaxPitchRad, RecoilState.ClampPitch(-2f));
        Assert.Equal(0.3f, RecoilState.ClampPitch(0.3f));
    }

    [Fact]
    public void ResetClearsTheOffsetAndTheBurst()
    {
        var def = new RecoilDef { VerticalPerShot = 1, Pattern = new() { 1 } };
        var r = new RecoilState(1);
        r.Shot(def, 1); r.Shot(def, 1);
        r.Reset();
        Assert.Equal(0, r.PitchRad);
        Assert.Equal(0, r.YawRad);
        Assert.Equal(0, r.Shot(def, 1).PitchRad);
    }

    [Fact]
    public void ShippedWeaponsDefaultToNoRecoilAndNoStanceScaling()
    {
        var file = WeaponsFile.Load(Path.Combine(AppContext.BaseDirectory, "../../../../shooter/data/weapons.json"));
        foreach (var w in file.Weapons)
        {
            Assert.Equal(1f, w.Recoil.AdsMultiplier);
            Assert.Equal(1f, w.Recoil.CrouchMultiplier);
        }
    }
}
