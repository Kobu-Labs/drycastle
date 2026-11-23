using System.Numerics;
using Xunit;
using Drycastle.ShooterCore.Killstreaks.Helicopter;
using Drycastle.ShooterCore.Movement;

namespace Drycastle.ShooterCore.Tests.Killstreaks.Helicopter;

/// <summary>
/// The gunner's phase machine keeps a carry-over clock (<c>_timer += x</c> inside a while loop), so a long step runs several phases in one Tick. The golden trace pins
/// that exactly, with big and small steps, targets that die or hide mid-burst, and a seeded roll.
/// </summary>
public class HelicopterGunnerMachineTests
{
    static readonly Vector3 Muzzle = new(0, 45, 0);

    static HelicopterHelperTuning Tuning() => new()
    {
        CycleMinS = 1, CycleMaxS = 2.5f, WarningS = 0.7f, Rounds = 4, RoundIntervalS = 0.2f, Damage = 25, Accuracy = 0.6f,
        MissRadiusM = 2, SightRangeM = 400, WeaponId = "m82", ExposureStand = 1, ExposureCrouch = 0.7f, ExposureProne = 0.4f,
    };

    static ulong Trace(int seed, bool bigSteps)
    {
        var rng = new Random(seed);
        var g = new HelicopterGunner(Tuning(), rng.NextDouble);
        var step = new Random(seed + 100);
        var buf = new List<HelperAction>();
        ulong h = 1469598103934665603UL;
        void Mix(ulong v) { h ^= v; h *= 1099511628211UL; }
        void MixF(float f) => Mix((uint)BitConverter.SingleToInt32Bits(f));
        var t = 0f;
        for (var i = 0; i < 3000; i++)
        {
            var dt = bigSteps ? 0.05f + (float)step.NextDouble() * 4f : 0.016f + (float)step.NextDouble() * 0.05f;
            t += dt;
            var enemies = new List<HelperCandidate>();
            if (MathF.Sin(t * 0.31f) > -0.6f) enemies.Add(new HelperCandidate(2, Muzzle + new Vector3(0, -40, -60), Stance.Stand));
            if (MathF.Sin(t * 0.17f) > 0f) enemies.Add(new HelperCandidate(5, Muzzle + new Vector3(30, -40, -20), Stance.Prone));
            var hidden = MathF.Sin(t * 0.9f) > 0.8f;
            buf.Clear();
            g.Tick(dt, Muzzle, enemies, (_, _) => !hidden, buf);
            Mix(g.Target ?? 0xFFFF);
            Mix(g.Firing ? 1UL : 0UL);
            foreach (var a in buf)
            {
                switch (a)
                {
                    case HelperWarnAction w: Mix(1); Mix(w.Target); MixF(w.Seconds); break;
                    case HelperRoundAction r: Mix(2); Mix(r.Target); Mix(r.Hit ? 1UL : 0UL); MixF(r.End.X); MixF(r.End.Z); break;
                }
            }
        }
        return h;
    }

    [Theory]
    [InlineData(1, false, 8572409274014596700UL)]
    [InlineData(2, false, 12841671921327996736UL)]
    [InlineData(1, true, 8484278765853536182UL)]
    [InlineData(2, true, 1541094022057339417UL)]
    public void TheActionsAndPhasesOfASeededGunnerAreUnchanged(int seed, bool bigSteps, ulong golden) => Assert.Equal(golden, Trace(seed, bigSteps));
}
