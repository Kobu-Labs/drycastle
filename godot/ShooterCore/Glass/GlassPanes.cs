using System.Numerics;
using System.Text.Json;

namespace Suchohrad.ShooterCore;

/// <summary>
/// The flat rectangle of a glass pane in some frame (the world for a building's pane, the vehicle's node frame for a vehicle's):
/// its centre, the outward normal, and the two in-plane axes. <see cref="Right"/> is horizontal, <see cref="Up"/> points up the pane,
/// and Right x Up = Normal, so the three are the columns of the pane's basis (a quad facing +Z maps onto the pane).
/// <see cref="Outline"/> is the pane's real shape when it is not the whole rectangle (a triangle, a trapezoid): a convex polygon, counter-clockwise
/// seen from outside, as (along Right, along Up) metres from the centre, inside the Width x Height box. Null: the rectangle itself.
/// </summary>
public readonly record struct GlassPaneRect(Vector3 Centre, Vector3 Normal, Vector3 Right, Vector3 Up, float Width, float Height, IReadOnlyList<Vector2>? Outline = null)
{
    public static GlassPaneRect Make(Vector3 centre, Vector3 normal, float width, float height, IReadOnlyList<Vector2>? outline = null)
    {
        normal = Vector3.Normalize(normal);
        // a pane lying flat (a roof light) has no horizontal direction of its own: its width runs along the frame's X
        var right = MathF.Abs(normal.Y) > 0.999f ? Vector3.UnitX : Vector3.Normalize(Vector3.Cross(Vector3.UnitY, normal));
        var up = Vector3.Normalize(Vector3.Cross(normal, right));
        return new(centre, normal, right, up, width, height, outline is { Count: >= 3 } ? outline : null);
    }

    /// <summary>Whether the point (<paramref name="u"/> along Right, <paramref name="v"/> along Up, metres from the centre) is on the pane: in the rectangle and, when there is an outline, inside it.</summary>
    public bool Contains(float u, float v)
    {
        if (MathF.Abs(u) > Width / 2 || MathF.Abs(v) > Height / 2) return false;
        if (Outline == null) return true;
        for (var i = 0; i < Outline.Count; i++)
        {
            Vector2 a = Outline[i], b = Outline[(i + 1) % Outline.Count];
            if ((b.X - a.X) * (v - a.Y) - (b.Y - a.Y) * (u - a.X) < -1e-5f) return false; // right of an edge of a counter-clockwise polygon
        }
        return true;
    }

    /// <summary>Metres along the ray to where it crosses the pane from the inside (a ray leaving the vehicle), or null. <paramref name="dir"/> is a unit vector.</summary>
    public float? CrossFromInside(Vector3 origin, Vector3 dir)
    {
        var facing = Vector3.Dot(dir, Normal);
        if (facing < 1e-4f) return null;
        var t = Vector3.Dot(Centre - origin, Normal) / facing;
        if (t < 0) return null;
        var p = origin + dir * t - Centre;
        return Contains(Vector3.Dot(p, Right), Vector3.Dot(p, Up)) ? t : null;
    }

    /// <summary>
    /// Metres along the ray to where it crosses the pane from outside, or null: it misses the pane (its outline, else its rectangle), runs along it, starts behind it
    /// or comes from the inside. <paramref name="dir"/> is a unit vector.
    /// </summary>
    public float? Cross(Vector3 origin, Vector3 dir)
    {
        var facing = Vector3.Dot(dir, Normal);
        if (facing > -1e-4f) return null;
        var t = Vector3.Dot(Centre - origin, Normal) / facing;
        if (t < 0) return null;
        var p = origin + dir * t - Centre;
        return Contains(Vector3.Dot(p, Right), Vector3.Dot(p, Up)) ? t : null;
    }
}

/// <summary>
/// Which pane: <see cref="Owner"/> is 0 for the base map's building panes (then <see cref="Index"/> is the pane's place in
/// <see cref="MapGlass"/>), else a vehicle's entity id (then it is the place in that type's vehicles.json "glass" list).
/// </summary>
public readonly record struct GlassPaneKey(ushort Owner, ushort Index)
{
    /// <summary>The owner of the building panes.</summary>
    public const ushort Map = 0;
}

/// <summary>A pane's hit points on the wire: 255 whole, 0 shattered, between them the fraction left (never 255 once damaged, never 0 while it stands).</summary>
public readonly record struct GlassPaneState(GlassPaneKey Key, byte Hp)
{
    public float Fraction => Hp / 255f;
    public bool Shattered => Hp == 0;
}

/// <summary>The rules of glass panes (ADR 0007). Pure, so the tests run the server's own arithmetic.</summary>
public static class GlassRules
{
    public const byte Whole = 255;

    /// <summary>The hp byte of <see cref="GlassPaneState"/> for a pane with <paramref name="hp"/> of <paramref name="maxHp"/> left.</summary>
    public static byte Quantise(float hp, float maxHp)
    {
        if (hp <= 0) return 0;
        if (hp >= maxHp) return Whole;
        return (byte)Math.Clamp((int)MathF.Ceiling(hp / maxHp * 255f), 1, 254);
    }

    /// <summary>
    /// The crack stage of a standing pane from the fraction of its HP left: 0 at full HP (no crack), 1 as soon as it is damaged, one more for
    /// each of <paramref name="stages"/> (fractions, highest first) it has fallen to. A shattered pane (fraction 0) has no crack stage; ask
    /// <see cref="GlassPaneState.Shattered"/> first.
    /// </summary>
    public static int CrackStage(float fraction, IReadOnlyList<float> stages)
    {
        if (fraction >= 1) return 0;
        var stage = 1;
        foreach (var s in stages) if (fraction <= s) stage++;
        return stage;
    }

    /// <summary>The highest crack stage there is with <paramref name="stages"/>.</summary>
    public static int MaxCrackStage(IReadOnlyList<float> stages) => stages.Count + 1;

    /// <summary>
    /// The pane a ray crosses first between <paramref name="from"/> and <paramref name="to"/> metres along it, or -1. The ray and the panes
    /// are in the same frame; <paramref name="dir"/> is a unit vector. <paramref name="skip"/> leaves panes out (unbreakable ones).
    /// </summary>
    public static int FirstCrossed(IReadOnlyList<GlassPaneRect> panes, Vector3 origin, Vector3 dir, float from, float to, Func<int, bool>? skip = null)
    {
        var best = -1; var bestT = float.PositiveInfinity;
        for (var i = 0; i < panes.Count; i++)
        {
            if (panes[i].Cross(origin, dir) is not { } t || t < from || t > to || t >= bestT) continue;
            if (skip != null && skip(i)) continue;
            best = i; bestT = t;
        }
        return best;
    }

    /// <summary>Damage of a blast to a pane <paramref name="distance"/> metres from its centre: <paramref name="damage"/> at the centre, nothing at <paramref name="radius"/>.</summary>
    public static float BlastDamage(float distance, float radius, float damage) =>
        radius <= 0 || distance >= radius ? 0 : damage * (1 - MathF.Max(0, distance) / radius);

    /// <summary>A world point in a vehicle's node frame (origin on the ground under its centre, x right, y up, z back), as <see cref="VehicleBox"/> has it.</summary>
    public static Vector3 ToVehicle(Vector3 world, Vector3 feet, float yaw) => DirToVehicle(world - feet, yaw);

    /// <summary>A world direction in a vehicle's node frame.</summary>
    public static Vector3 DirToVehicle(Vector3 dir, float yaw)
    {
        var s = MathF.Sin(yaw); var c = MathF.Cos(yaw);
        return new Vector3(dir.X * c - dir.Z * s, dir.Y, dir.X * s + dir.Z * c);
    }

    /// <summary>A point of a vehicle's node frame in the world (the inverse of <see cref="ToVehicle"/>).</summary>
    public static Vector3 FromVehicle(Vector3 local, Vector3 feet, float yaw)
    {
        var s = MathF.Sin(yaw); var c = MathF.Cos(yaw);
        return feet + new Vector3(local.X * c + local.Z * s, local.Y, -local.X * s + local.Z * c);
    }
}

/// <summary>
/// The server's glass: the hit points of every pane that is not whole (a pane it has never heard of is whole). Changes are collected and
/// handed out once a tick (<see cref="TakeChanges"/>), so a shotgun's pellets on one pane make one message.
/// </summary>
public sealed class GlassPanes
{
    readonly Dictionary<GlassPaneKey, (float Hp, float Max)> _damaged = new();
    readonly HashSet<GlassPaneKey> _changed = new();

    /// <summary>Panes that are not whole.</summary>
    public int DamagedCount => _damaged.Count;

    public bool IsShattered(GlassPaneKey key) => _damaged.TryGetValue(key, out var p) && p.Hp <= 0;

    /// <summary>Hit points left; <paramref name="maxHp"/> for a whole pane.</summary>
    public float Hp(GlassPaneKey key, float maxHp) => _damaged.TryGetValue(key, out var p) ? p.Hp : maxHp;

    /// <summary>
    /// Takes <paramref name="amount"/> from the pane (<paramref name="maxHp"/> is what it has when whole). True when its hit points changed:
    /// a shattered pane and a non-positive amount change nothing.
    /// </summary>
    public bool Damage(GlassPaneKey key, float amount, float maxHp)
    {
        if (!(amount > 0) || !(maxHp > 0)) return false;
        var hp = Hp(key, maxHp);
        if (hp <= 0) return false;
        _damaged[key] = (MathF.Max(0, hp - amount), maxHp);
        _changed.Add(key);
        return true;
    }

    /// <summary>Makes every pane of <paramref name="owner"/> whole again (a vehicle that returned). True when any was damaged.</summary>
    public bool Restore(ushort owner)
    {
        var any = false;
        foreach (var key in _damaged.Keys.Where(k => k.Owner == owner).ToList()) { _damaged.Remove(key); _changed.Remove(key); any = true; }
        return any;
    }

    /// <summary>Makes every pane whole again (a new round). True when any was damaged.</summary>
    public bool RestoreAll()
    {
        var any = _damaged.Count > 0;
        _damaged.Clear(); _changed.Clear();
        return any;
    }

    /// <summary>The panes that changed since the last call, with their state now.</summary>
    public List<GlassPaneState> TakeChanges()
    {
        var list = new List<GlassPaneState>(_changed.Count);
        foreach (var key in _changed) if (_damaged.TryGetValue(key, out var p)) list.Add(new GlassPaneState(key, GlassRules.Quantise(p.Hp, p.Max)));
        _changed.Clear();
        return list;
    }

    /// <summary>Every pane that is not whole, for a player who joins.</summary>
    public List<GlassPaneState> Damaged()
    {
        var list = new List<GlassPaneState>(_damaged.Count);
        foreach (var (key, p) in _damaged) list.Add(new GlassPaneState(key, GlassRules.Quantise(p.Hp, p.Max)));
        return list;
    }
}

/// <summary>The client's copy of the glass state: the hp byte of every pane that is not whole.</summary>
public sealed class GlassReplica
{
    readonly Dictionary<GlassPaneKey, byte> _hp = new();

    public byte Hp(GlassPaneKey key) => _hp.GetValueOrDefault(key, GlassRules.Whole);

    /// <summary>Stores a pane's state and returns the hp byte it had before.</summary>
    public byte Set(GlassPaneState s)
    {
        var before = Hp(s.Key);
        if (s.Hp == GlassRules.Whole) _hp.Remove(s.Key); else _hp[s.Key] = s.Hp;
        return before;
    }

    /// <summary>Makes the panes of <paramref name="scope"/> whole (<see cref="GlassCodec.Everything"/>: all of them) and returns the ones that were not.</summary>
    public List<GlassPaneKey> Reset(ushort scope)
    {
        var gone = _hp.Keys.Where(k => scope == GlassCodec.Everything || k.Owner == scope).ToList();
        foreach (var k in gone) _hp.Remove(k);
        return gone;
    }

    /// <summary>The panes of one owner that are not whole (a vehicle that comes into view after its glass state arrived).</summary>
    public List<GlassPaneState> Of(ushort owner)
    {
        var list = new List<GlassPaneState>();
        foreach (var (k, hp) in _hp) if (k.Owner == owner) list.Add(new GlassPaneState(k, hp));
        return list;
    }
}

/// <summary>A building's pane from the base map's windows.json: the node of its mesh, its rectangle in the game's axes (Y up), and whether it can break.</summary>
public sealed record MapGlassPane(string Node, GlassPaneRect Rect, bool Breakable);

/// <summary>
/// The building panes of a base map (windows.json, written by the map pipeline's building detail). Server and client read the same file,
/// and the order here (by node name, ordinal) is the pane's index on the wire.
/// </summary>
public static class MapGlass
{
    /// <summary>Parses windows.json. An empty string (a map without the file) gives no panes. Map axes (x east, y north, z up) become the game's (x, z, -y).</summary>
    public static List<MapGlassPane> Parse(string windowsJson)
    {
        var list = new List<MapGlassPane>();
        if (string.IsNullOrWhiteSpace(windowsJson)) return list;
        try
        {
            using var doc = JsonDocument.Parse(windowsJson);
            foreach (var w in doc.RootElement.GetProperty("windows").EnumerateArray())
            {
                var c = w.GetProperty("centre"); var n = w.GetProperty("normal");
                var centre = new Vector3(c[0].GetSingle(), c[2].GetSingle(), -c[1].GetSingle());
                var normal = new Vector3(n[0].GetSingle(), 0, -n[1].GetSingle());
                float width = w.GetProperty("width_m").GetSingle(), height = w.GetProperty("height_m").GetSingle();
                if (normal.LengthSquared() < 1e-6f || !(width > 0) || !(height > 0)) throw new DataFileException("windows.json: a pane needs a normal and a positive width_m and height_m");
                var breakable = !w.TryGetProperty("breakable", out var b) || b.ValueKind != JsonValueKind.False;
                list.Add(new MapGlassPane(w.GetProperty("node").GetString() ?? "", GlassPaneRect.Make(centre, normal, width, height), breakable));
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or FormatException)
        {
            throw new DataFileException($"windows.json: {e.Message}", e);
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Node, b.Node));
        if (list.Count > ushort.MaxValue) throw new DataFileException("windows.json: more panes than a pane index holds (65535)");
        return list;
    }
}
