using System.Numerics;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The turret's armed behaviour (idle, acquiring, aiming, firing) was split across a target, an acquire timer, a cooldown and a flash timer. Golden trace pins the timing through the machine refactor.</summary>
public class TurretMachineTests
{
    static TurretDef Def(float acquire) => new()
    {
        Hp = 100, LifetimeSeconds = 20, PlacementRangeM = 4, PlacementSlopeDeg = 30, PlacementStepM = 1, PlacementProbeM = 10, SightRangeM = 40,
        TurnRateDegPerS = 120, AcquireDelayS = acquire, AimToleranceDeg = 4, FireIntervalS = 0.35f, DamagePerShot = 10, KnifeHitsToDestroy = 3,
    };

    sealed class World : ITurretWorld
    {
        public IReadOnlyList<TurretCandidate> Foes = Array.Empty<TurretCandidate>();
        public bool Blocked;
        public readonly List<ushort> Shots = new();
        public (Vector3 Feet, Vector3 Eye)? Pose(ushort owner) => (Vector3.Zero, new Vector3(0, 1.6f, 0));
        public GroundHit? WorldRay(Vector3 from, Vector3 dir, float maxDistance)
        {
            if (Blocked && dir.Y > -0.5f) return new GroundHit(from + dir * 0.5f, Vector3.UnitY); // a wall right in front of every sight ray
            if (dir.Y >= -1e-6f) return null;
            var t = -from.Y / dir.Y;
            return t <= 0 || t > maxDistance ? null : new GroundHit(from + dir * t, Vector3.UnitY);
        }
        public IReadOnlyList<TurretCandidate> Enemies(ushort owner) => Foes;
        public void Fire(ushort owner, ushort target, float damage) => Shots.Add(target);
    }

    static ulong Trace(float acquire, int seed)
    {
        var w = new World();
        var t = new TurretEntity(Def(acquire), 1, w);
        t.Track(Vector3.Zero, new Vector3(0, 1.6f, 0), 0f, -0.5f);
        Assert.True(t.Place());
        var rng = new Random(seed);
        ulong h = 1469598103934665603UL;
        void Mix(ulong v) { h ^= v; h *= 1099511628211UL; }
        for (var i = 0; i < 1500; i++)
        {
            var time = i * 0.05f;
            var foes = new List<TurretCandidate>();
            if (MathF.Sin(time * 0.4f) > -0.5f) foes.Add(new TurretCandidate(3, new Vector3(10 * MathF.Sin(time * 0.3f), 1, -12)));
            if (MathF.Sin(time * 0.23f + 1) > 0f) foes.Add(new TurretCandidate(8, new Vector3(-9, 1, 6 * MathF.Cos(time * 0.2f))));
            w.Foes = foes;
            w.Blocked = MathF.Sin(time * 0.7f) > 0.9f;
            var dt = 0.016f + (float)rng.NextDouble() * 0.06f;
            t.Tick(dt);
            Mix(t.Target ?? 0xFFFF);
            Mix(t.Serialize().State);
            Mix((uint)BitConverter.SingleToInt32Bits(t.Yaw));
            Mix((uint)w.Shots.Count);
            if (t.Ended != null) { Mix(99); break; }
        }
        return h;
    }

    [Theory]
    [InlineData(0.6f, 1, 14638961553819339004UL)]
    [InlineData(0.6f, 2, 16884593293202621150UL)]
    [InlineData(0f, 1, 16868758278427724447UL)]
    public void TheTraceOfAnArmedTurretIsUnchanged(float acquire, int seed, ulong golden) => Assert.Equal(golden, Trace(acquire, seed));
}
