using System.Buffers.Binary;
using System.Numerics;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Scenery;
using Drycastle.ShooterCore.Vehicles;

namespace Drycastle.ShooterCore.Wildlife;

/// <summary>What <see cref="PhoenixSchedule.Advance"/> found on one tick: a Phoenix is due now (the spawn hour of every 5th night was crossed), or the dawn hour was crossed (a Phoenix still Flying leaves).</summary>
public readonly record struct PhoenixTick(bool Due, bool Dawn);

/// <summary>
/// The night counter of the Phoenix (GLOSSARY "Phoenix"), pure: the server feeds it the hour of its day clock (<see cref="DayClock.HourAt"/>) every tick, so the rule is tested with injected hours and no clock.
/// A night is counted each time the hour passes <see cref="PhoenixRules.SpawnHour"/> (midnight): <see cref="Nights"/> is how many have passed since the first hour it saw, and every
/// <see cref="PhoenixRules.NightsBetween"/>th one is Due, so the first Phoenix comes on the 5th night. A stopped clock never passes it (no Phoenix); a jump of the hour (an admin set the time) is not a pass,
/// except that an admin who sets the hour just before midnight and lets it run does cause one (the test route). Not reset by a round: it counts the server's whole life.
/// </summary>
public sealed class PhoenixSchedule
{
    /// <summary>The most hours one tick may move the clock and still be a pass of an hour; more is a jump (<see cref="DayClock.WithHour"/>) and passes nothing. A tick is about 0.001 game hours.</summary>
    public const double MaxStepHours = 0.5;

    readonly PhoenixRules _rules;
    double? _hour;

    public PhoenixSchedule(PhoenixRules rules) => _rules = rules;

    /// <summary>The nights counted so far: the times the clock passed <see cref="PhoenixRules.SpawnHour"/>.</summary>
    public int Nights { get; private set; }

    /// <summary>The clock now shows <paramref name="hour"/> (0..24; any value is folded in). The first call only learns the hour.</summary>
    public PhoenixTick Advance(double hour)
    {
        hour = (hour % 24 + 24) % 24;
        var before = _hour;
        _hour = hour;
        if (before is not { } from) return default;
        var step = hour - from;
        if (step < 0) step += 24;
        if (step <= 0 || step > MaxStepHours) return default; // stopped, or set by hand
        var due = false;
        if (Passed(from, step, _rules.SpawnHour)) { Nights++; due = Nights % _rules.NightsBetween == 0; }
        return new PhoenixTick(due, Passed(from, step, _rules.DawnHour));
    }

    /// <summary>Whether <paramref name="mark"/> lies in the hours after <paramref name="from"/> up to and including <paramref name="from"/> + <paramref name="step"/> (the day wraps).</summary>
    static bool Passed(double from, double step, double mark)
    {
        var d = mark - from;
        if (d <= 0) d += 24;
        return d <= step;
    }
}

/// <summary>A Phoenix's patrol and its patch of ground, planned by the keeper when it spawns: where the circle is, how big, and the rules.</summary>
public sealed record PhoenixSpawn(PhoenixRules Rules, Vector3 Centre, float RadiusM);

/// <summary>The flight maths of the Phoenix, pure.</summary>
public static class PhoenixFlight
{
    /// <summary>The yaw (0 faces -Z, the game's convention) that looks along (<paramref name="dx"/>, <paramref name="dz"/>).</summary>
    public static float YawOf(float dx, float dz) => MathF.Atan2(-dx, -dz);

    /// <summary>Where it is on its circle at <paramref name="angle"/> (radians, the centre's height kept).</summary>
    public static Vector3 OnCircle(Vector3 centre, float radius, float angle) => new(centre.X + radius * MathF.Cos(angle), centre.Y, centre.Z + radius * MathF.Sin(angle));

    /// <summary>The yaw along the circle at <paramref name="angle"/>, going round the way of <paramref name="direction"/> (+1 / -1).</summary>
    public static float CircleYaw(float angle, int direction) => YawOf(-MathF.Sin(angle) * direction, MathF.Cos(angle) * direction);

    /// <summary>
    /// The circle that fits the map: <paramref name="wanted"/> metres of radius, cut to 45% of the map's smaller side, centred at a random spot (<paramref name="rng"/> draws two values) where the whole circle is
    /// inside the box. A box with no size (the world has none) gives null.
    /// </summary>
    public static (float X, float Z, float Radius)? Circle(FlightBounds b, float wanted, Random rng)
    {
        if (!float.IsFinite(b.MinX + b.MaxX + b.MinZ + b.MaxZ) || !(b.MaxX > b.MinX) || !(b.MaxZ > b.MinZ)) return null;
        var radius = MathF.Min(wanted, 0.45f * MathF.Min(b.MaxX - b.MinX, b.MaxZ - b.MinZ));
        var x = b.MinX + radius + (float)rng.NextDouble() * (b.MaxX - b.MinX - 2 * radius);
        var z = b.MinZ + radius + (float)rng.NextDouble() * (b.MaxZ - b.MinZ - 2 * radius);
        return (x, z, radius);
    }

    /// <summary>
    /// The dive's position at <paramref name="u"/> (0 at the hit, 1 at the crash site) on the straight line between them: it speeds up all the way (30% steady, 70% accelerating), so it hits the ground fast.
    /// </summary>
    public static Vector3 Dive(Vector3 from, Vector3 to, float u)
    {
        u = Math.Clamp(u, 0f, 1f);
        return Vector3.Lerp(from, to, 0.3f * u + 0.7f * u * u);
    }

    /// <summary>The pitch of the dive line, radians (negative: nose down): what the wire's pitch carries while it falls.</summary>
    public static float DivePitch(Vector3 from, Vector3 to)
    {
        var flat = MathF.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Z - from.Z) * (to.Z - from.Z));
        return MathF.Atan2(to.Y - from.Y, flat);
    }
}

/// <summary>Finds where a shot-down Phoenix crashes: a random-ish stretch of clear ground near the dive that needs little room.</summary>
public static class PhoenixCrash
{
    /// <summary>Tries of the first search (ahead of it) and of the second (all round) before the last resort.</summary>
    public const int AheadTries = 60, AroundTries = 60;

    /// <summary>The half-angle of the cone ahead of it the first search looks in, radians (about 40 degrees).</summary>
    public const float AheadHalfAngle = 0.7f;

    /// <summary>
    /// The crash site for a Phoenix hit at <paramref name="from"/> heading <paramref name="yaw"/>: ground (not deep water, not steep) all over a circle of <see cref="PhoenixRules.CrashClearM"/>, nothing solid in it
    /// (<see cref="IAnimalWorld.Fits"/>), at least <see cref="PhoenixRules.CrashBuildingM"/> from every building (<see cref="IAnimalWorld.NearestBuildingM"/>, the same footprints the Unicorn keeps away from).
    /// First <see cref="AheadTries"/> tries ahead of it at <see cref="PhoenixRules.CrashMinM"/>..<see cref="PhoenixRules.CrashMaxM"/>, then <see cref="AroundTries"/> all round out to twice the maximum. None
    /// clear: the nearest point with ground at all (Clear false), so it always crashes somewhere. The site's Y is the ground.
    /// </summary>
    public static (Vector3 Site, bool Clear) FindSite(IAnimalWorld world, Vector3 from, float yaw, PhoenixRules rules, Random rng)
    {
        for (var i = 0; i < AheadTries + AroundTries; i++)
        {
            var ahead = i < AheadTries;
            var angle = ahead ? yaw + (float)(rng.NextDouble() * 2 - 1) * AheadHalfAngle : (float)rng.NextDouble() * MathF.Tau;
            var distance = ahead ? rules.CrashMinM + (float)rng.NextDouble() * (rules.CrashMaxM - rules.CrashMinM) : (float)rng.NextDouble() * rules.CrashMaxM * 2;
            // the yaw convention: 0 faces -Z, so the heading's unit vector is (-sin, -cos)
            var x = from.X - MathF.Sin(angle) * distance;
            var z = from.Z - MathF.Cos(angle) * distance;
            if (IsClear(world, x, z, rules) is { } y) return (new Vector3(x, y, z), true);
        }
        for (var ring = 0f; ring <= rules.CrashMaxM * 2; ring += 5f)
            for (var k = 0; k < 8; k++)
            {
                var a = k * MathF.Tau / 8;
                var x = from.X + MathF.Cos(a) * ring;
                var z = from.Z + MathF.Sin(a) * ring;
                if (world.GroundY(x, z) is { } y) return (new Vector3(x, y, z), false);
            }
        return (new Vector3(from.X, 0, from.Z), false); // a world with no ground at all (a test)
    }

    /// <summary>The ground height at (x, z) when a Phoenix may crash there (see <see cref="FindSite"/>); null otherwise. The cheapest test first.</summary>
    public static float? IsClear(IAnimalWorld world, float x, float z, PhoenixRules rules)
    {
        if (world.GroundY(x, z) is not { } y) return null;
        if (!(world.NearestBuildingM(x, z, rules.CrashBuildingM) >= rules.CrashBuildingM)) return null;
        for (var k = 0; k < 8; k++)
        {
            var a = k * MathF.Tau / 8;
            if (world.GroundY(x + MathF.Cos(a) * rules.CrashClearM, z + MathF.Sin(a) * rules.CrashClearM) is not { } edge || MathF.Abs(edge - y) > rules.CrashMaxSlopeM) return null;
        }
        return world.Fits(x, z, rules.CrashClearM) ? y : null;
    }
}

/// <summary>
/// The event the server broadcasts when a Phoenix is first hit and starts to fall (<see cref="EventKind.PhoenixFall"/>, protocol 80). The animal's own state already says Falling and its position, yaw and pitch
/// (the pitch of the dive line) follow it down every tick; this adds what no snapshot carries: the whole line, for a trail, a shadow or a warning at the crash site.
/// Layout, little endian, 30 bytes: u16 world entity id, f32 x, y, z of where it was hit, f32 x, y, z of the crash site (on the ground), f32 seconds the fall takes.
/// </summary>
public static class PhoenixFallCodec
{
    public const int Size = 30;

    public static byte[] Encode(ushort entity, Vector3 from, Vector3 site, float seconds)
    {
        var b = new byte[Size];
        BinaryPrimitives.WriteUInt16LittleEndian(b, entity);
        var o = 2;
        foreach (var f in new[] { from.X, from.Y, from.Z, site.X, site.Y, site.Z, seconds }) { BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o), f); o += 4; }
        return b;
    }

    public static bool TryDecode(ReadOnlySpan<byte> p, out ushort entity, out Vector3 from, out Vector3 site, out float seconds)
    {
        entity = 0; from = site = default; seconds = 0;
        if (p.Length != Size) return false;
        entity = BinaryPrimitives.ReadUInt16LittleEndian(p);
        var f = new float[7];
        for (var i = 0; i < f.Length; i++) f[i] = BinaryPrimitives.ReadSingleLittleEndian(p[(2 + i * 4)..]);
        from = new Vector3(f[0], f[1], f[2]);
        site = new Vector3(f[3], f[4], f[5]);
        seconds = f[6];
        return true;
    }
}

/// <summary>A mineral a creature left in the world (a dead Phoenix's pheonixite): what, where, and the world entity of the creature that dropped it. Free for anybody to take (GLOSSARY "Mineral").</summary>
public readonly record struct MineralDrop(string MineralId, Vector3 At, ushort Entity);
