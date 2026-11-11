using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>Sniper breath hold (#113): the budget meter, forced exhale, sway phases, the deterministic aim error and Steady Lung.</summary>
public class BreathTests
{
    static WeaponsFile Weapons => WeaponsFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "weapons.json"));
    static PerksFile Perks => PerksFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "perks.json"));

    /// <summary>A sniper with a breath budget, recovery and idle sway set in-test so the numbers are fixed.</summary>
    static WeaponDef Rifle(float hold = 4f, float recover = 1f, float sway = 0.5f) => new()
    {
        Id = "t", Category = WeaponCategory.Sniper, Kind = WeaponKind.Hitscan, Slot = WeaponSlot.Primary,
        ZoomFov = 20, BreathHoldSeconds = hold, BreathRecoverSeconds = recover, SwayAmount = sway,
    };

    /// <summary>Ticks at 1/30 s from the current time <paramref name="from"/> for <paramref name="seconds"/>; returns the new time.</summary>
    static double Run(BreathState b, WeaponDef w, double from, double seconds, bool key, float mult = 1f, bool scoped = true)
    {
        var end = from + seconds;
        var t = from;
        for (; t <= end + 1e-9; t += 1.0 / 30) b.Tick(t, scoped, key, w, mult);
        return t - 1.0 / 30;
    }

    [Fact]
    public void HoldingDrainsTheMeterAndEnablesSteadying()
    {
        var b = new BreathState(1);
        var w = Rifle(hold: 4f);
        Run(b, w, 0, 2, key: true);
        Assert.Equal(BreathState.BreathPhase.Holding, b.Phase);
        Assert.InRange(b.Meter, 0.48f, 0.52f);
    }

    [Fact]
    public void ExhaustingTheBudgetForcesAnExhaleThatBlocksHoldingUntilRecovered()
    {
        var b = new BreathState(1);
        var w = Rifle(hold: 2f, recover: 1f);
        var t = Run(b, w, 0, 2.05, key: true);
        Assert.Equal(BreathState.BreathPhase.Exhaled, b.Phase);
        Assert.Equal(0f, b.Meter);
        // still holding the key during the recovery: no hold
        t = Run(b, w, t, 0.5, key: true);
        Assert.Equal(BreathState.BreathPhase.Exhaled, b.Phase);
        // recovery over: ready, and the meter refills, but holding through the recovery does not re-engage the breath
        t = Run(b, w, t, 0.8, key: true);
        Assert.Equal(BreathState.BreathPhase.Ready, b.Phase);
        Assert.True(b.Meter > 0);
        // a fresh press (after a release) does hold again
        t = Run(b, w, t, 0.2, key: false);
        t = Run(b, w, t, 0.1, key: true);
        Assert.Equal(BreathState.BreathPhase.Holding, b.Phase);
    }

    [Fact]
    public void ReleasingEarlyCostsNothingAndTheMeterRefills()
    {
        var b = new BreathState(1);
        var w = Rifle(hold: 4f);
        var t = Run(b, w, 0, 2, key: true);
        Run(b, w, t, 1, key: false);
        Assert.Equal(BreathState.BreathPhase.Ready, b.Phase);
        // refills at 1/budget per second: half the budget used, 1 s released = a quarter of the meter back
        Assert.InRange(b.Meter, 0.72f, 0.78f);
    }

    [Fact]
    public void NotScopedMeansNoHoldAndAFullRefill()
    {
        var b = new BreathState(1);
        var w = Rifle(hold: 4f);
        var t = Run(b, w, 0, 2, key: true);
        Run(b, w, t, 4, key: true, scoped: false);
        Assert.Equal(BreathState.BreathPhase.Ready, b.Phase);
        Assert.Equal(1f, b.Meter);
    }

    [Fact]
    public void SteadyLungScalesTheBudgetByItsModifier()
    {
        var mult = PerkModifiers.Of(Perks.Find("steady_lung")!).HoldBreathTime;
        var sniper = Weapons.Find("sniper")!;
        Assert.Equal(4f, BreathState.BudgetSeconds(sniper, 1f));
        Assert.Equal(6f, BreathState.BudgetSeconds(sniper, mult), 4);
        // with the perk, the same 4 s of hold leaves about a third of the meter, not empty
        var b = new BreathState(1);
        Run(b, sniper, 0, 4, key: true, mult: mult);
        Assert.InRange(b.Meter, 0.32f, 0.35f);
    }

    [Fact]
    public void WeaponWithoutABreathBudgetNeverHolds()
    {
        var b = new BreathState(1);
        Run(b, Rifle(hold: 0f), 0, 3, key: true);
        Assert.Equal(BreathState.BreathPhase.Ready, b.Phase);
    }

    [Fact]
    public void SwayIsSteadiestWhileHeldAndHeaviestAfterAForcedExhale()
    {
        var w = Rifle(hold: 2f, recover: 5f, sway: 1f);
        var held = new BreathState(1);
        Run(held, w, 0, 0.5, key: true);
        Assert.Equal(BreathState.SteadyFactor, held.SwayDegrees(w), 4);
        var ready = new BreathState(1);
        Run(ready, w, 0, 0.5, key: false);
        Assert.Equal(1f, ready.SwayDegrees(w), 4);
        Run(held, w, 0.5, 2.1, key: true);
        Assert.Equal(BreathState.ExhaledFactor, held.SwayDegrees(w), 4);
        Assert.Equal(0f, new BreathState(1).SwayDegrees(Rifle(sway: 0)), 4);
    }

    [Fact]
    public void AimErrorIsDeterministicPerSeedAndShotAndBoundedByTheSway()
    {
        var a = new BreathState(42);
        var b = new BreathState(42);
        var amp = 0.5f * MathF.PI / 180f;
        for (var n = 0; n < 50; n++)
        {
            var ea = a.AimError(0.5f);
            Assert.Equal(ea, b.AimError(0.5f));
            Assert.True(MathF.Abs(ea.PitchRad) <= amp + 1e-6f && MathF.Abs(ea.YawRad) <= amp + 1e-6f);
        }
        // a different player's stream differs; the error moves shot to shot
        Assert.NotEqual(new BreathState(42).AimError(0.5f), new BreathState(43).AimError(0.5f));
        var second = new BreathState(42);
        second.AimError(0.5f);
        Assert.NotEqual(new BreathState(42).AimError(0.5f), second.AimError(0.5f));
    }

    [Fact]
    public void ZeroSwayGivesNoAimError()
    {
        var e = new BreathState(7).AimError(0f);
        Assert.Equal(0f, e.PitchRad);
        Assert.Equal(0f, e.YawRad);
    }

    [Fact]
    public void VisualSwayIsCosmeticAndZeroWithoutSway()
    {
        var none = BreathState.VisualSway(1.25, 0f);
        Assert.Equal(0f, none.X);
        Assert.Equal(0f, none.Y);
        var some = BreathState.VisualSway(1.25, 1f);
        Assert.True(MathF.Abs(some.X) <= 0.5f * MathF.PI / 180f + 1e-6f);
    }
}
