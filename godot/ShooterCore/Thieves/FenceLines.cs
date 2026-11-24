using System.Numerics;
using System.Text.Json;

namespace Drycastle.ShooterCore.Thieves;

/// <summary>One standing section of a property group's fence (<c>fences.json</c>): two ground points (x, z in Godot axes) and the ground height under each, and how high it is.</summary>
public readonly record struct FenceSection(Vector2 A, Vector2 B, float ZA, float ZB, float HeightM)
{
    /// <summary>The point of the section nearest to <paramref name="p"/> (flat) and its distance.</summary>
    public (Vector2 Point, float Distance, float T) Nearest(Vector2 p)
    {
        var ab = B - A;
        var len2 = ab.LengthSquared();
        var t = len2 < 1e-9f ? 0f : Math.Clamp(Vector2.Dot(p - A, ab) / len2, 0f, 1f);
        var q = A + ab * t;
        return (q, Vector2.Distance(p, q), t);
    }

    /// <summary>The ground height along the section (0 at A, 1 at B).</summary>
    public float GroundAt(float t) => ZA + (ZB - ZA) * t;
}

/// <summary>
/// The fences of the map by property group, from <c>fences.json</c> (version 1, docs/pipeline.md "Fence sections"): local metres (x east, y north, z up) become Godot axes (x, z = -y, height = z). Only sections of a
/// property group are kept (a pet lives in a group); the gate gaps are simply absent. A map without the file, or one this build does not know the version of, has an empty book: nobody hops, they walk.
/// </summary>
public sealed class FenceBook
{
    public static readonly FenceBook Empty = new(new Dictionary<string, List<FenceSection>>());
    static readonly IReadOnlyList<FenceSection> None = Array.Empty<FenceSection>();

    readonly Dictionary<string, List<FenceSection>> _byGroup;

    public FenceBook(Dictionary<string, List<FenceSection>> byGroup) { _byGroup = byGroup; }

    /// <summary>The sections that stand round this property group (empty: no fence, a plain walk).</summary>
    public IReadOnlyList<FenceSection> Of(string group) => _byGroup.TryGetValue(group, out var l) ? l : None;
    public int Groups => _byGroup.Count;
    public int Sections { get { var n = 0; foreach (var l in _byGroup.Values) n += l.Count; return n; } }

    /// <summary>Parses fences.json. Malformed JSON throws <see cref="JsonException"/>; an unknown version gives the empty book.</summary>
    public static FenceBook Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1) return Empty;
        if (!root.TryGetProperty("fences", out var list) || list.ValueKind != JsonValueKind.Array) return Empty;
        var by = new Dictionary<string, List<FenceSection>>(StringComparer.Ordinal);
        foreach (var f in list.EnumerateArray())
        {
            if (f.ValueKind != JsonValueKind.Object || !f.TryGetProperty("group", out var g) || g.ValueKind != JsonValueKind.String) continue;
            if (!f.TryGetProperty("a", out var a) || !f.TryGetProperty("b", out var b) || a.GetArrayLength() < 2 || b.GetArrayLength() < 2) continue;
            var za = f.TryGetProperty("za", out var zaE) && zaE.ValueKind == JsonValueKind.Number ? zaE.GetSingle() : 0f;
            var zb = f.TryGetProperty("zb", out var zbE) && zbE.ValueKind == JsonValueKind.Number ? zbE.GetSingle() : za;
            var h = f.TryGetProperty("height_m", out var hE) && hE.ValueKind == JsonValueKind.Number ? hE.GetSingle() : 1.2f;
            var group = g.GetString()!;
            if (!by.TryGetValue(group, out var l)) by[group] = l = new List<FenceSection>();
            l.Add(new FenceSection(new Vector2(a[0].GetSingle(), -a[1].GetSingle()), new Vector2(b[0].GetSingle(), -b[1].GetSingle()), za, zb, h));
        }
        return new FenceBook(by);
    }
}
