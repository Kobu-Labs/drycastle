using System.Numerics;
using System.Text.Json;
using Drycastle.ShooterCore.Data;

namespace Drycastle.ShooterCore.Mining;

/// <summary>One static mineral node of the map: its id in the edits (MN-0001), the mineral and its place in Godot ground axes (x east, y up, z = -north).</summary>
public sealed record MineralNodeSpec(string Id, string MineralId, Vector3 Position);

/// <summary>
/// The static mineral nodes of the map (mineral_nodes.json, GLOSSARY "Mineral node"): the places that hold a crystal at a fixed spot, set in the editor's edits
/// (<c>mineral_nodes</c>) and baked by the pipeline. Scene-free, like <see cref="Scenery.ZonePoints"/> (ADR 0004). A map without the file has none.
/// </summary>
public static class MineralNodeFile
{
    /// <summary>The newest format of the file this reader understands; an older or newer one is refused, which counts as no nodes.</summary>
    public const int SupportedVersion = 1;

    /// <summary>Parses the file contents; an empty string is the map without the file (no nodes).</summary>
    public static IReadOnlyList<MineralNodeSpec> FromJson(string? json) => string.IsNullOrEmpty(json) ? Array.Empty<MineralNodeSpec>() : Parse(json);

    /// <summary>Like <see cref="FromJson"/>, but a broken file is reported to <paramref name="onError"/> and counts as no nodes.</summary>
    public static IReadOnlyList<MineralNodeSpec> FromJsonOrEmpty(string? json, Action<string> onError)
    {
        try { return FromJson(json); }
        catch (DataFileException e) { onError(e.Message); return Array.Empty<MineralNodeSpec>(); }
    }

    /// <summary>
    /// Parses <c>{"version": 1, "nodes": [{"id", "mineral", "x", "y", "z"}]}</c>, x and y in local map metres (east, north), z the height; they are converted to Godot ground axes here.
    /// Unknown fields are ignored; any version but <see cref="SupportedVersion"/> is refused. The order is the file's (the pipeline sorts by id).
    /// </summary>
    public static IReadOnlyList<MineralNodeSpec> Parse(string json, string name = "mineral_nodes.json")
    {
        using var doc = DataFile.Document(json, name);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new DataFileException($"{name}: not an object");
        if (!root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version) || version < 1)
            throw new DataFileException($"{name}: missing or invalid version");
        if (version != SupportedVersion) throw new DataFileException($"{name}: version {version} is not the supported version {SupportedVersion}");
        if (!root.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array) throw new DataFileException($"{name}: missing nodes array");
        var list = new List<MineralNodeSpec>(nodes.GetArrayLength());
        foreach (var n in nodes.EnumerateArray())
        {
            var id = Text(n, "id");
            var mineral = Text(n, "mineral");
            if (mineral.Length == 0 || !Number(n, "x", out var x) || !Number(n, "y", out var y) || !Number(n, "z", out var z))
                throw new DataFileException($"{name}: node {(id.Length == 0 ? "?" : id)} needs mineral, x, y and z");
            list.Add(new MineralNodeSpec(id, mineral, new Vector3(x, z, -y)));
        }
        return list;
    }

    /// <summary>
    /// Makes a static node of each spec in <paramref name="set"/>, in order. A spec whose mineral is not in <see cref="Minerals.All"/>, or that finds no free static id, is skipped and
    /// reported to <paramref name="onSkip"/>. Returns how many were added.
    /// </summary>
    public static int AddTo(MineralNodeSet set, IEnumerable<MineralNodeSpec> specs, Action<string> onSkip)
    {
        var added = 0;
        foreach (var s in specs)
        {
            if (Minerals.Find(s.MineralId) == null) { onSkip($"{s.Id}: unknown mineral {s.MineralId}"); continue; }
            if (set.AddStatic(s.MineralId, s.Position) == null) { onSkip($"{s.Id}: no free static node id"); continue; }
            added++;
        }
        return added;
    }

    static string Text(JsonElement e, string property) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";

    static bool Number(JsonElement e, string property, out float f)
    {
        f = 0;
        return e.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetSingle(out f) && float.IsFinite(f);
    }
}
