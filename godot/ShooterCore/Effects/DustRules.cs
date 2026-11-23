using System.Numerics;
using static Drycastle.ShooterCore.Movement.Planar;

namespace Drycastle.ShooterCore.Effects;

/// <summary>
/// One blast as the dust knows it: its centre, its radius in metres, the client time (seconds) it went off and the speed its Blast wave leaves at
/// (metres a second; <see cref="DustRules.FrontSpeedMps"/> unless the blast says otherwise, as /explode can).
/// </summary>
public readonly record struct DustBlast(Vector3 At, float Radius, double Born, float Speed = DustRules.FrontSpeedMps);

/// <summary>
/// The dust of a blast (#345), as pure numbers: no Godot, no state. A blast leaves a Dust cloud at its centre (dense, low, it swells,
/// rises and thins), and a large one (<see cref="HasFront"/>) also sends a Blast wave along the ground: a wall of dust that slows down
/// as it runs out to <see cref="FrontReach"/>. Everything is a function of the blast (centre, radius, start) and the time, so every
/// client that got the blast event shows the same cloud and no net state is added. The particles (<c>DustFx</c>), the screen veil
/// (<c>Hud.Dust</c>) and the late sound and shake (<c>NetClient.Dust</c>) all read these numbers, so they cannot drift apart.
/// Visual only: no shot, blast or bot's sight is stopped by dust.
/// The tuning is the constants at the top.
/// </summary>
public static class DustRules
{
    // ---- tuning ----

    /// <summary>Blasts of at least this radius send a Blast wave (a vehicle's is 9 m, a shot-down aircraft's 12 m; a rocket's 6 m and the gunship's 8 m do not).</summary>
    public const float FrontMinRadius = 9f;
    /// <summary>The wave's speed as it leaves the blast, metres a second, for a blast that names none. It slows evenly to a stop at its reach. Just under the speed of sound.</summary>
    public const float FrontSpeedMps = 300f;
    /// <summary>How far the wave runs, in blast radii.</summary>
    public const float FrontReachPerRadius = 6f;
    /// <summary>
    /// The wave's height over the ground, metres: the same for every blast (a larger blast sends its wave further, it does not make it taller). It hugs the
    /// ground and covers a standing player's eyes; a camera above it is not veiled.
    /// </summary>
    public const float FrontHeightM = 3f;
    /// <summary>The wave's veil is at its strongest this long after it arrives.</summary>
    public const float FrontRiseSeconds = 0.12f;
    /// <summary>How long the wave's dust hangs where it passed (the time for the veil to fall to 37 %): the same for every blast.</summary>
    public const float FrontHangSeconds = 1.3f;
    /// <summary>Past the wave's reach the sound runs on at this speed (the real speed of sound).</summary>
    public const float SoundSpeedMps = 340f;

    /// <summary>The cloud's life in seconds: this plus a part for each metre of radius, within the bounds (a 6 m rocket: 4 s).</summary>
    public const float CloudBaseSeconds = 2.5f, CloudSecondsPerRadius = 0.25f, CloudMinSeconds = 3f, CloudMaxSeconds = 14f;
    /// <summary>The cloud's half width at the start, in blast radii, and how much wider it is at the end of its life.</summary>
    public const float CloudRadiusPerRadius = 1.3f, CloudGrowth = 0.6f;
    /// <summary>The cloud's height at the start, in blast radii (never below <see cref="CloudMinHeightM"/>), and how much taller it is at the end.</summary>
    public const float CloudHeightPerRadius = 0.8f, CloudMinHeightM = 2.5f, CloudRise = 0.8f;
    /// <summary>The cloud is at full density this long after the blast, stays there for this share of its life, then thins to nothing.</summary>
    public const float CloudRiseSeconds = 0.2f, CloudHoldShare = 0.45f;

    /// <summary>The veil never fully blinds: the HUD stays on top of it and a little of the world shows through.</summary>
    public const float MaxVeil = 0.96f;
    /// <summary>
    /// The cloud's part of the veil: full for the blast's first moment (<see cref="CloudRiseSeconds"/>), then down to <see cref="CloudVeilShare"/> of the
    /// cloud's density over <see cref="CloudVeilPulseSeconds"/>. The drawn cloud already hides the world by distance for a camera inside it; a full veil
    /// on top of it hid the near things too, for the cloud's whole life. The wave's part is not cut.
    /// </summary>
    public const float CloudVeilShare = 0.25f, CloudVeilPulseSeconds = 0.5f;
    /// <summary>A sight line with at least this much dust on it hides what is at its end (a name tag).</summary>
    public const float SightBlocked = 0.6f;

    // ---- the Blast wave ----

    public static bool HasFront(float radius) => radius >= FrontMinRadius;

    public static float FrontReach(float radius) => radius * FrontReachPerRadius;

    /// <summary>Seconds the wave takes to run out to its reach (twice the time it would take at its first speed, since it slows evenly).</summary>
    public static float FrontSeconds(float radius, float speed = FrontSpeedMps) => 2f * FrontReach(radius) / speed;

    /// <summary>The speed a blast's wave leaves at: its own when it names one (above zero), else <see cref="FrontSpeedMps"/>.</summary>
    public static float SpeedOr(float speed) => speed > 0f ? speed : FrontSpeedMps;

    /// <summary>The wave's even slowing, metres a second squared: what the particles are damped by, so the drawn wall is where <see cref="FrontRadius"/> says.</summary>
    public static float FrontDeceleration(float radius, float speed = FrontSpeedMps) => speed * speed / (2f * FrontReach(radius));

    public static float FrontHeight(float radius) => FrontHeightM;

    /// <summary>How far from the blast the wave is, <paramref name="age"/> seconds after it.</summary>
    public static float FrontRadius(float radius, double age, float speed = FrontSpeedMps)
    {
        if (age <= 0) return 0f;
        var t = FrontSeconds(radius, speed);
        if (age >= t) return FrontReach(radius);
        var left = 1f - (float)age / t;
        return FrontReach(radius) * (1f - left * left);
    }

    /// <summary>Seconds after the blast at which the wave reaches a point <paramref name="distance"/> metres away along the ground; null beyond its reach.</summary>
    public static float? FrontArrival(float radius, float distance, float speed = FrontSpeedMps)
    {
        var reach = FrontReach(radius);
        if (!HasFront(radius) || distance >= reach) return null;
        return FrontSeconds(radius, speed) * (1f - MathF.Sqrt(1f - MathF.Max(0f, distance) / reach));
    }

    /// <summary>How thick the wave still is <paramref name="distance"/> metres out: 1 at the blast, 0 at its reach.</summary>
    public static float FrontStrength(float radius, float distance)
    {
        var reach = FrontReach(radius);
        return !HasFront(radius) || distance >= reach ? 0f : MathF.Sqrt(1f - MathF.Max(0f, distance) / reach);
    }

    public static float FrontHang(float radius) => FrontHangSeconds;

    /// <summary>
    /// Seconds after the blast at which it is heard and felt <paramref name="distance"/> metres away: with the wave where the wave
    /// reaches, later still beyond it (the sound runs on at its own speed); 0 for a blast with no wave.
    /// </summary>
    public static float FeltDelay(float radius, float distance, float speed = FrontSpeedMps)
    {
        if (!HasFront(radius)) return 0f;
        return FrontArrival(radius, distance, speed) ?? FrontSeconds(radius, speed) + (distance - FrontReach(radius)) / SoundSpeedMps;
    }

    // ---- the Dust cloud ----

    public static float CloudSeconds(float radius) => Math.Clamp(CloudBaseSeconds + CloudSecondsPerRadius * radius, CloudMinSeconds, CloudMaxSeconds);

    static float Life(float radius, double age) => Math.Clamp((float)age / CloudSeconds(radius), 0f, 1f);

    public static float CloudRadius(float radius, double age) => radius * CloudRadiusPerRadius * (1f + CloudGrowth * Life(radius, age));

    public static float CloudHeight(float radius, double age) => MathF.Max(CloudMinHeightM, radius * CloudHeightPerRadius) * (1f + CloudRise * Life(radius, age));

    /// <summary>The cloud's density over its life, 0..1: up at once, held, then a smooth fall to nothing at the end.</summary>
    public static float CloudOpacity(float radius, double age)
    {
        var life = CloudSeconds(radius);
        if (age <= 0 || age >= life) return 0f;
        if (age < CloudRiseSeconds) return (float)age / CloudRiseSeconds;
        var hold = life * CloudHoldShare;
        return age <= hold ? 1f : 1f - Smooth(((float)age - hold) / (life - hold));
    }

    /// <summary>How long a blast's dust lasts at all: the cloud, or the wave and what hangs behind it.</summary>
    public static float Seconds(float radius, float speed = FrontSpeedMps) =>
        HasFront(radius) ? MathF.Max(CloudSeconds(radius), FrontSeconds(radius, speed) + 5f * FrontHang(radius)) : CloudSeconds(radius);

    // ---- what a point sees ----

    static float Smooth(float x) { x = Math.Clamp(x, 0f, 1f); return x * x * (3f - 2f * x); }

    /// <summary>The cloud's density at <paramref name="p"/>, 0..1: full in its middle, falling off towards its edge and top. Under the blast there is little (it sits on the ground).</summary>
    public static float CloudDensity(in DustBlast b, Vector3 p, double now)
    {
        var age = now - b.Born;
        var opacity = CloudOpacity(b.Radius, age);
        if (opacity <= 0f) return 0f;
        var h = Flat(p, b.At) / CloudRadius(b.Radius, age);
        var height = CloudHeight(b.Radius, age);
        var dy = p.Y - b.At.Y;
        var v = dy >= 0 ? dy / height : -dy / (height * 0.4f);
        var q = MathF.Sqrt(h * h + v * v);
        return opacity * (1f - Smooth((q - 0.55f) / 0.45f));
    }

    /// <summary>
    /// The Blast wave's dust at <paramref name="p"/>, 0..1: nothing until the wave arrives, then at once as thick as the wave is there, thinning over <see cref="FrontHang"/>.
    /// The wave follows the ground: <paramref name="aboveGround"/> is how high <paramref name="p"/> is over the ground under it (without it, over the blast's own height).
    /// </summary>
    public static float FrontDensity(in DustBlast b, Vector3 p, double now, float? aboveGround = null)
    {
        if (FrontArrival(b.Radius, Flat(p, b.At), b.Speed) is not { } arrival) return 0f;
        var since = (float)(now - b.Born) - arrival;
        if (since < 0f) return 0f;
        var dy = MathF.Abs(aboveGround ?? p.Y - b.At.Y) / FrontHeight(b.Radius);
        var level = 1f - Smooth((dy - 0.6f) / 0.4f);
        if (level <= 0f) return 0f;
        var rise = MathF.Min(1f, since / FrontRiseSeconds);
        return FrontStrength(b.Radius, Flat(p, b.At)) * level * rise * MathF.Exp(-since / FrontHang(b.Radius));
    }

    /// <summary>All of one blast's dust at a point: the thicker of its cloud and its wave.</summary>
    public static float Density(in DustBlast b, Vector3 p, double now, float? aboveGround = null) => MathF.Max(CloudDensity(b, p, now), FrontDensity(b, p, now, aboveGround));

    /// <summary>How much of the cloud's density at the eye goes to the veil, <paramref name="age"/> seconds after the blast: 1, then down to <see cref="CloudVeilShare"/>.</summary>
    public static float CloudVeilShareAt(double age) => 1f - (1f - CloudVeilShare) * Smooth(((float)age - CloudRiseSeconds) / CloudVeilPulseSeconds);

    /// <summary>What one blast's dust takes of the view of an eye at <paramref name="p"/>: its cloud's share (<see cref="CloudVeilShareAt"/>) or its wave, the larger.</summary>
    public static float VeilDensity(in DustBlast b, Vector3 p, double now, float? aboveGround = null) =>
        MathF.Max(CloudDensity(b, p, now) * CloudVeilShareAt(now - b.Born), FrontDensity(b, p, now, aboveGround));

    /// <summary>
    /// Whether <paramref name="b"/>, going off now, is covered by the dust <paramref name="a"/> already made: close to its centre, not larger, and
    /// while that cloud is still young. A burst of blasts in one spot (a gunship's fire) then keeps one cloud instead of stacking them.
    /// </summary>
    public static bool Covers(in DustBlast a, in DustBlast b)
    {
        var age = b.Born - a.Born;
        return age >= 0 && age < CloudSeconds(a.Radius) * 0.3f && b.Radius <= a.Radius * 1.25f && Vector3.Distance(a.At, b.At) <= a.Radius * 0.5f;
    }
}

/// <summary>
/// The dust in the air on one client: the live blasts, oldest first. <see cref="Add"/> takes a blast unless a young cloud already covers it,
/// <see cref="Veil"/> is how much of the view a camera loses, <see cref="Sight"/> how much dust is on a line. Bounded (<see cref="Max"/>);
/// nothing is allocated after it is made.
/// </summary>
public sealed class DustField
{
    public const int Max = 16;
    const int SightSamples = 8;

    readonly List<DustBlast> _blasts = new(Max);

    public int Count => _blasts.Count;
    public IReadOnlyList<DustBlast> Blasts => _blasts;

    public void Clear() => _blasts.Clear();

    /// <summary>Drops the blasts whose dust is gone.</summary>
    public void Prune(double now)
    {
        for (var i = _blasts.Count - 1; i >= 0; i--)
            if (now - _blasts[i].Born >= DustRules.Seconds(_blasts[i].Radius, _blasts[i].Speed)) _blasts.RemoveAt(i);
    }

    /// <summary>Takes a blast. False when a live cloud covers it (<see cref="DustRules.Covers"/>): nothing new is to be drawn for it. A full field drops its oldest.</summary>
    public bool Add(in DustBlast b)
    {
        Prune(b.Born);
        foreach (var a in _blasts) if (DustRules.Covers(a, b)) return false;
        if (_blasts.Count >= Max) _blasts.RemoveAt(0);
        _blasts.Add(b);
        return true;
    }

    /// <summary>
    /// How much of the view a camera at <paramref name="eye"/> loses to dust, 0 to <see cref="DustRules.MaxVeil"/>. <paramref name="reach"/> says how much
    /// of a blast's dust gets to the eye (1 in the open, less behind a wall); it is asked only for a blast that has dust there. <paramref name="aboveGround"/>
    /// is the eye's height over the ground under it, for the waves (they follow the ground).
    /// </summary>
    public float Veil(Vector3 eye, double now, Func<DustBlast, float>? reach = null, float? aboveGround = null)
    {
        var clear = 1f;
        foreach (var b in _blasts)
        {
            var d = DustRules.VeilDensity(b, eye, now, aboveGround);
            if (d <= 0.005f) continue;
            if (reach != null) d *= Math.Clamp(reach(b), 0f, 1f);
            clear *= 1f - d;
        }
        return MathF.Min(DustRules.MaxVeil, 1f - clear);
    }

    /// <summary>The thickest dust on the line from <paramref name="eye"/> to <paramref name="target"/>, 0..1: the line is sampled, both ends included.</summary>
    public float Sight(Vector3 eye, Vector3 target, double now)
    {
        if (_blasts.Count == 0) return 0f;
        var worst = 0f;
        for (var i = 0; i <= SightSamples; i++)
        {
            var p = Vector3.Lerp(eye, target, i / (float)SightSamples);
            var clear = 1f;
            foreach (var b in _blasts) clear *= 1f - DustRules.Density(b, p, now);
            worst = MathF.Max(worst, 1f - clear);
        }
        return worst;
    }
}
