using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// What a body a vehicle threw does once it touches the ground: it does not stop where it comes down, it keeps part of the speed it was given and rolls on along the
/// ground until the ground has taken it. The server's animal (<see cref="Animal"/>) and the driver's guessed throw (<see cref="ThrowGuess"/>) both step by this, so the
/// guess comes to rest near where the server's body does.
/// </summary>
public static class ThrownRoll
{
    /// <summary>The share of its speed along the ground a body keeps when it comes down on it.</summary>
    public const float LandKeep = 0.6f;
    /// <summary>How fast the ground slows a rolling body, m/s², and the speed (m/s) under which it lies.</summary>
    public const float Friction = 10f, RestSpeed = 0.4f;
    /// <summary>A drop of the ground under a rolling body bigger than this (m) and it is in the air again.</summary>
    public const float LedgeM = 0.3f;

    /// <summary>The velocity a body that came down with <paramref name="v"/> rolls on with: along the ground only.</summary>
    public static Vector3 Land(Vector3 v) => new(v.X * LandKeep, 0, v.Z * LandKeep);

    /// <summary>One step of the ground's drag on a rolling body's velocity. True once it lies (the velocity is then zero).</summary>
    public static bool Roll(ref Vector3 v, float dt)
    {
        var speed = MathF.Sqrt(v.X * v.X + v.Z * v.Z);
        var left = speed - Friction * dt;
        if (left <= RestSpeed) { v = Vector3.Zero; return true; }
        var k = left / speed;
        v = new Vector3(v.X * k, 0, v.Z * k);
        return false;
    }
}
