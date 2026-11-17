using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>Where a Scud missile is in its flight: the vertical boost, then the arc over and down onto the target.</summary>
public enum ScudPhase : byte { Boost = 0, Arc = 1, Dive = 2 }

/// <summary>
/// The whole flight of one Scud missile, worked out at the launch (as ADR 0031 does for the homing missile): a vertical boost to <see cref="ScudMissileData.BoostAltitudeM"/>
/// over <see cref="ScudMissileData.BoostSeconds"/>, then one smooth curve that pitches over and comes down onto the target at <see cref="ScudMissileData.DiveDeg"/>. The server
/// samples <see cref="At"/> every tick for the world entity; a client could rebuild the same path from the launch event, so the picture and the blast agree by construction.
/// <para>
/// The curve after the boost is a cubic Bezier: it leaves the top of the boost straight up at the speed the boost reached (so the missile never stalls), and arrives
/// at the target along the dive direction. Its parameter runs at a constant rate against time. It is an arc, not a simulation: nothing in the world is swept, the missile
/// cannot be hit and the terrain it passes over does not matter.
/// </para>
/// </summary>
public sealed class ScudFlightPlan
{
    readonly Vector3 _launch, _target, _b1, _b2;
    readonly float _boostSeconds, _boostHeight, _arcSeconds;
    readonly float _apexT;

    public ScudFlightPlan(Vector3 launch, Vector3 target, ScudMissileData t, float minRangeM)
    {
        _launch = launch; _target = target;
        _boostSeconds = (float)t.BoostSeconds;
        _boostHeight = (float)t.BoostAltitudeM;
        var range = ScudRules.Range(launch, target);
        // the flight is as long as the range asks, from the shortest at the minimum range to the longest at full range
        var k = Math.Clamp((range - minRangeM) / MathF.Max(1f, (float)t.FullRangeM - minRangeM), 0f, 1f);
        Seconds = (float)(t.FlightMinSeconds + (t.FlightMaxSeconds - t.FlightMinSeconds) * k);
        _arcSeconds = Seconds - _boostSeconds;

        var top = launch + new Vector3(0, _boostHeight, 0);
        var boostSpeed = 2f * _boostHeight / _boostSeconds; // y = h (t / tb)^2: the speed at the end of the boost
        var up = boostSpeed * _arcSeconds / 3f;             // Bezier: the first leg's length, so the arc's first speed is the boost's last
        var flat = new Vector3(target.X - launch.X, 0, target.Z - launch.Z);
        var dir = flat.LengthSquared() > 1e-6f ? Vector3.Normalize(flat) : new Vector3(0, 0, -1);
        var dive = (float)(t.DiveDeg * Math.PI / 180.0);
        var end = new Vector3(dir.X * MathF.Cos(dive), -MathF.Sin(dive), dir.Z * MathF.Cos(dive)); // the direction it arrives in
        var legIn = MathF.Max(boostSpeed, 1.2f * Vector3.Distance(top, target) / _arcSeconds) * _arcSeconds / 3f;
        _b1 = top + new Vector3(0, up, 0);
        _b2 = target - end * legIn;

        // the apex: the highest point of the curve, found by sampling (the curve is smooth and has one maximum)
        var best = float.NegativeInfinity;
        for (var i = 0; i <= 200; i++)
        {
            var tt = _boostSeconds + _arcSeconds * i / 200f;
            var y = At(tt).Y;
            if (y > best) { best = y; _apexT = tt; }
        }
        Apex = At(_apexT);
        ApexSeconds = _apexT;
    }

    /// <summary>The whole flight, seconds.</summary>
    public float Seconds { get; }
    public Vector3 Launch => _launch;
    public Vector3 Target => _target;
    /// <summary>The highest point, and the time the missile is there.</summary>
    public Vector3 Apex { get; }
    public float ApexSeconds { get; }
    /// <summary>The time the boost ends.</summary>
    public float BoostSeconds => _boostSeconds;
    static Vector3 Bezier(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float u)
    {
        var v = 1f - u;
        return a * (v * v * v) + b * (3f * v * v * u) + c * (3f * v * u * u) + d * (u * u * u);
    }

    /// <summary>Where the missile is <paramref name="t"/> seconds after the launch (clamped to the flight).</summary>
    public Vector3 At(float t)
    {
        t = Math.Clamp(t, 0f, Seconds);
        if (t <= _boostSeconds)
        {
            var f = t / _boostSeconds;
            return _launch + new Vector3(0, _boostHeight * f * f, 0);
        }
        var u = (t - _boostSeconds) / _arcSeconds;
        var top = _launch + new Vector3(0, _boostHeight, 0);
        return Bezier(top, _b1, _b2, _target, u);
    }

    /// <summary>The way the nose points at <paramref name="t"/>: the direction of travel, as the yaw and pitch of <see cref="HitMath.Forward"/> (pitch up is positive; straight up is pi/2).</summary>
    public (float Yaw, float Pitch) HeadingAt(float t)
    {
        var step = 0.05f;
        var a = At(MathF.Max(0, t - step)); var b = At(MathF.Min(Seconds, t + step));
        var d = b - a;
        var h = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
        var pitch = MathF.Atan2(d.Y, h);
        var yaw = h > 1e-4f ? MathF.Atan2(-d.X, -d.Z) : 0f; // forward is -Z: HitMath.Forward(yaw, pitch) = (-sin yaw cos pitch, sin pitch, -cos yaw cos pitch)
        return (yaw, pitch);
    }

    /// <summary>The phase at <paramref name="t"/>: the boost, the climb of the arc up to its apex, the fall after it.</summary>
    public ScudPhase PhaseAt(float t) => t < _boostSeconds ? ScudPhase.Boost : t < _apexT ? ScudPhase.Arc : ScudPhase.Dive;

    /// <summary>Seconds left to the impact at <paramref name="t"/>.</summary>
    public float LeftAt(float t) => MathF.Max(0, Seconds - t);
}
