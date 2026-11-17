namespace Drycastle.ShooterCore;

/// <summary>
/// The maths of the radar screen (GLOSSARY "Radar unit"; ADR 0037), pure: the sweep arm carried on between two messages, the trail of light behind it, how a blip fades over one revolution, and the bearing
/// as a direction on the north-up sheet. Bearings are radians, 0 along -Z (north, up on the sheet), turning clockwise (+X is a quarter turn).
/// </summary>
public static class RadarScreenMath
{
    /// <summary>How far behind the arm its light trails, radians (a little over a quarter turn): at the arm it is full, at the far end of the trail gone.</summary>
    public const float TrailRadians = 1.6f;

    /// <summary>Wraps an angle into [0, 2 pi).</summary>
    public static float Wrap(float a)
    {
        a %= MathF.Tau;
        if (a < 0) a += MathF.Tau;
        return a >= MathF.Tau ? 0f : a;
    }

    /// <summary>The arm's bearing <paramref name="seconds"/> after it was <paramref name="angle"/>: it turns clockwise at 2 pi per <paramref name="revolution"/> seconds.</summary>
    public static float Advance(float angle, float revolution, float seconds) =>
        revolution > 0f && float.IsFinite(revolution) ? Wrap(angle + MathF.Tau / revolution * MathF.Max(0f, seconds)) : Wrap(angle);

    /// <summary>The bearing of a ground offset (<paramref name="dx"/> east, <paramref name="dz"/> south, the game's axes) from the unit, in [0, 2 pi).</summary>
    public static float Bearing(float dx, float dz) => Wrap(MathF.Atan2(dx, -dz));

    /// <summary>How far the arm at <paramref name="arm"/> has turned past <paramref name="bearing"/>, in [0, 2 pi): 0 when it is on it, just under a full turn when it is about to arrive.</summary>
    public static float Behind(float arm, float bearing) => Wrap(arm - bearing);

    /// <summary>
    /// The light at a point <paramref name="behind"/> radians behind the arm: 1 on the arm, falling off quadratically to 0 at <paramref name="trail"/>; 0 beyond it (and ahead of the arm, which the wrap
    /// puts at nearly a full turn behind).
    /// </summary>
    public static float TrailAlpha(float behind, float trail = TrailRadians)
    {
        if (!(trail > 0f) || behind < 0f || behind >= trail) return 0f;
        var k = 1f - behind / trail;
        return k * k;
    }

    /// <summary>A blip's opacity: full when the sweep has just found it, gone one revolution later (<paramref name="age"/> seconds since the arm passed).</summary>
    public static float Fade(float age, float revolution) => revolution > 0f ? Math.Clamp(1f - age / revolution, 0f, 1f) : 0f;

    /// <summary>The unit vector of a bearing on the sheet (x right, y down): north is (0, -1), east (1, 0).</summary>
    public static (float X, float Y) Direction(float bearing) => (MathF.Sin(bearing), -MathF.Cos(bearing));

    /// <summary>The distance from (<paramref name="ox"/>, <paramref name="oy"/>) to the farthest corner of the rectangle: a ray this long from the origin leaves the rectangle whatever its bearing.</summary>
    public static float Reach(float ox, float oy, float minX, float minY, float maxX, float maxY)
    {
        var dx = MathF.Max(MathF.Abs(ox - minX), MathF.Abs(ox - maxX));
        var dy = MathF.Max(MathF.Abs(oy - minY), MathF.Abs(oy - maxY));
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>
/// What the operator's radar screen shows (GLOSSARY "Radar unit"), as the client keeps it: the last <see cref="RadarSweepMsg"/> (the arm's bearing and the contacts) with when it came, so the arm is carried
/// on at its own speed and every blip fades with the time since the arm passed it, plus the unit's place. A swept contact stays where the sweep stamped it; the friendlies are live. Each blip carries its grid
/// reference (<c>Label</c>), made by the caller and re-made only when the blip moved, so a frame allocates nothing. Pure: the client feeds it, the tactical map draws it.
/// </summary>
public sealed class RadarDisplay
{
    /// <summary>A sweep older than this (client seconds) is not carried on: the unit stopped telling (unhitched, or the link is down).</summary>
    public const float StaleSeconds = 1.5f;

    /// <summary>An enemy the sweep found: where, how old it was on arrival, and its grid reference.</summary>
    public readonly record struct Contact(ushort Id, RadarKind Kind, float X, float Z, float AgeAtReceipt, string Label);

    /// <summary>A teammate, live: where and its grid reference.</summary>
    public readonly record struct Friend(ushort Id, RadarKind Kind, float X, float Z, string Label);

    readonly List<Contact> _contacts = new();
    readonly List<Friend> _friends = new();
    readonly Dictionary<ushort, (float X, float Z, string Label)> _labels = new();
    float _angle, _revolution;
    double _at;
    bool _has;

    /// <summary>The local player sits at the unit's screen: the tactical map draws the radar.</summary>
    public bool Seated { get; set; }
    /// <summary>The unit's id and ground place (x, z), set every frame by the client from the vehicle as it is drawn.</summary>
    public ushort Unit { get; private set; }
    public float UnitX { get; private set; }
    public float UnitZ { get; private set; }

    public IReadOnlyList<Contact> Contacts => _contacts;
    public IReadOnlyList<Friend> Friends => _friends;

    /// <summary>Seconds per turn of the arm, as the last message said (0 before the first).</summary>
    public float Revolution => _revolution;

    public void SetUnit(ushort id, float x, float z) { Unit = id; UnitX = x; UnitZ = z; }

    /// <summary>The unit is telling us now: a message arrived within <see cref="StaleSeconds"/>.</summary>
    public bool Live(double now) => _has && now - _at <= StaleSeconds;

    /// <summary>The arm's bearing at <paramref name="now"/>, carried on from the last message at the revolution's speed (held at the last carried bearing once stale).</summary>
    public float Arm(double now) => _has ? RadarScreenMath.Advance(_angle, _revolution, (float)Math.Min(now - _at, StaleSeconds)) : 0f;

    /// <summary>A contact's opacity now: its age on arrival plus the time since, over one revolution.</summary>
    public float Alpha(in Contact c, double now) => RadarScreenMath.Fade(c.AgeAtReceipt + (float)Math.Max(0, now - _at), _revolution);

    /// <summary>
    /// Takes a sweep message that arrived at <paramref name="now"/>. <paramref name="reference"/> makes the grid reference of a ground point; it is called only for a blip that is new or has moved since the last
    /// message (a swept contact stays where it was stamped, so most calls are skipped).
    /// </summary>
    public void Apply(RadarSweepMsg m, double now, Func<float, float, string> reference)
    {
        _angle = m.Angle; _revolution = m.Revolution; _at = now; _has = m.Revolution > 0f;
        _contacts.Clear(); _friends.Clear();
        foreach (var c in m.Swept) _contacts.Add(new Contact(c.Id, c.Kind, c.X, c.Z, c.Age, Label(c.Id, c.X, c.Z, reference)));
        foreach (var f in m.Friendlies) _friends.Add(new Friend(f.Id, f.Kind, f.X, f.Z, Label(f.Id, f.X, f.Z, reference)));
        if (_labels.Count > 2 * (_contacts.Count + _friends.Count) + 16) Prune();
    }

    string Label(ushort id, float x, float z, Func<float, float, string> reference)
    {
        if (_labels.TryGetValue(id, out var l) && l.X == x && l.Z == z) return l.Label;
        var text = reference(x, z);
        _labels[id] = (x, z, text);
        return text;
    }

    void Prune()
    {
        var live = new HashSet<ushort>();
        foreach (var c in _contacts) live.Add(c.Id);
        foreach (var f in _friends) live.Add(f.Id);
        foreach (var id in _labels.Keys.Where(k => !live.Contains(k)).ToList()) _labels.Remove(id);
    }

    /// <summary>Forgets everything the screen knew: the next sweep starts a blank screen (the operator left, the unit was unhitched).</summary>
    public void Clear()
    {
        _contacts.Clear(); _friends.Clear(); _labels.Clear();
        _has = false; _angle = 0f; _revolution = 0f; _at = 0;
    }
}
