using Xunit;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.ShooterCore.Tests.Weapons;

/// <summary>BreathState is stepped by BOTH the client prediction and the server and must stay in lockstep: the golden trace pins every phase, meter and recovery bit.</summary>
public class BreathMachineTests
{
    static WeaponDef Rifle(float hold, float recover) => new()
    {
        Id = "t", Category = WeaponCategory.Sniper, Kind = WeaponKind.Hitscan, Slot = WeaponSlot.Primary,
        ZoomFov = 20, BreathHoldSeconds = hold, BreathRecoverSeconds = recover, SwayAmount = 0.5f,
    };

    static ulong Trace(int seed, float hold, float recover)
    {
        var rng = new Random(seed);
        var b = new BreathState((ulong)seed);
        var w = Rifle(hold, recover);
        ulong h = 1469598103934665603UL;
        void Mix(ulong v) { h ^= v; h *= 1099511628211UL; }
        double now = 100;
        var scoped = true; var key = false;
        for (var i = 0; i < 6000; i++)
        {
            now += 0.01 + rng.NextDouble() * 0.05;
            if (rng.Next(40) == 0) scoped = !scoped;
            if (rng.Next(25) == 0) key = !key;
            if (i == 3000) b.Reset();
            b.Tick(now, scoped, key, w, rng.Next(5) == 0 ? 1.5f : 1f);
            Mix((ulong)b.Phase);
            Mix((uint)BitConverter.SingleToInt32Bits(b.Meter));
            Mix((uint)BitConverter.SingleToInt32Bits(b.RecoverRemaining));
        }
        return h;
    }

    [Theory]
    [InlineData(1, 2f, 1f, 9357770843235693701UL)]
    [InlineData(2, 1f, 0.5f, 17713912348744829626UL)]
    [InlineData(3, 1f, 0f, 17924535984634001970UL)]
    public void TheMeterPhaseAndRecoveryTraceIsUnchanged(int seed, float hold, float recover, ulong golden) => Assert.Equal(golden, Trace(seed, hold, recover));

    [Fact]
    public void ARandomDriveOnlyTakesDeclaredTransitions()
    {
        foreach (var (hold, recover) in new[] { (2f, 1f), (1f, 0f), (0f, 1f) })
            Trace(7, hold, recover); // Strict mode throws on an undeclared edge
    }
}
