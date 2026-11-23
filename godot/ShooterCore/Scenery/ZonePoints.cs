using System.Numerics;
using System.Text.Json;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Waters;

namespace Drycastle.ShooterCore.Scenery;

/// <summary>
/// The zones of the map as the server needs them (zone_points.json, ADR 0022): for each zone kind, a pool of candidate points the
/// pipeline baked inside every ground area and drawn zone of that kind. The game never sees the areas or zones, so "a random spot
/// in a Bigfoot zone" is a random pick from the pool. Scene-free, like <see cref="Water"/> (ADR 0004): the server owns the pick and
/// its random generator.
/// <para>
/// Axes: Godot ground axes, as everywhere in the game: x east, y up (the terrain height at the point, as spawns), z = -north.
/// </para>
/// <para>
/// A map without the file, or a kind with no zone, has no points: <see cref="Pick"/> returns null and the caller must handle it
/// (do not start the event, try later).
/// </para>
/// </summary>
public sealed class ZonePoints
{
    /// <summary>The newest format of the file this reader understands; an older or newer one is refused, which counts as no points.</summary>
    public const int SupportedVersion = 2;

    public static readonly ZonePoints Empty = new(new Dictionary<string, Vector3[]>());

    readonly Dictionary<string, Vector3[]> _pools;

    ZonePoints(Dictionary<string, Vector3[]> pools) => _pools = pools;

    /// <summary>The candidate points of a zone kind (<c>bigfoot</c>, <c>deer</c>); empty when no zone of it has any.</summary>
    public IReadOnlyList<Vector3> Points(string kind)
        => _pools.TryGetValue(kind, out var p) ? p : Array.Empty<Vector3>();

    /// <summary>How many candidate points a zone kind has.</summary>
    public int Count(string kind) => Points(kind).Count;

    /// <summary>
    /// A random candidate point of a zone kind, drawn with the caller's generator (the server's). Null when it has none (the map has
    /// no file, or no zone of the kind, or nothing usable was left of its areas).
    /// </summary>
    public Vector3? Pick(string kind, Random rng)
    {
        var points = Points(kind);
        return points.Count == 0 ? null : points[rng.Next(points.Count)];
    }

    /// <summary>Parses the file contents; an empty string is the map without the file (no points).</summary>
    public static ZonePoints FromJson(string? json) => string.IsNullOrEmpty(json) ? Empty : Parse(json);

    /// <summary>Like <see cref="FromJson"/>, but a broken file is reported to <paramref name="onError"/> and counts as no points.</summary>
    public static ZonePoints FromJsonOrEmpty(string? json, Action<string> onError)
    {
        try { return FromJson(json); }
        catch (DataFileException e) { onError(e.Message); return Empty; }
    }

    /// <summary>
    /// Parses zone_points.json: <c>{"version": 2, "zones": [{"kind", "areas", "area_m2", "points": [[x, y, z], ...]}]}</c> with
    /// points in local map metres (x east, y north, z the terrain height); they are converted to Godot ground axes here. Unknown
    /// fields are ignored; any version but <see cref="SupportedVersion"/> is refused.
    /// </summary>
    public static ZonePoints Parse(string json, string name = "zone_points.json")
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException e) { throw new DataFileException($"{name}: not JSON: {e.Message}", e); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new DataFileException($"{name}: not an object");
            if (!root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version) || version < 1)
                throw new DataFileException($"{name}: missing or invalid version");
            if (version > SupportedVersion) throw new DataFileException($"{name}: version {version} is newer than supported version {SupportedVersion}");
            if (version < SupportedVersion) throw new DataFileException($"{name}: version {version} is older than supported version {SupportedVersion}: rebuild the map");
            if (!root.TryGetProperty("zones", out var zones) || zones.ValueKind != JsonValueKind.Array)
                throw new DataFileException($"{name}: missing zones array");
            var pools = new Dictionary<string, Vector3[]>();
            foreach (var z in zones.EnumerateArray())
            {
                var kind = StringOf(z, "kind");
                if (string.IsNullOrEmpty(kind)) throw new DataFileException($"{name}: an entry without kind");
                if (pools.ContainsKey(kind)) throw new DataFileException($"{name}: {kind} is listed twice");
                if (!z.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array)
                    throw new DataFileException($"{name}: {kind} has no points array");
                var list = new List<Vector3>(points.GetArrayLength());
                foreach (var p in points.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 3 || !Number(p[0], out var x) || !Number(p[1], out var y) || !Number(p[2], out var h))
                        throw new DataFileException($"{name}: {kind} has a point that is not [x, y, z]");
                    list.Add(new Vector3(x, h, -y));
                }
                pools[kind] = list.ToArray();
            }
            return new ZonePoints(pools);
        }
    }

    static string StringOf(JsonElement e, string property)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";

    static bool Number(JsonElement e, out float f)
    {
        f = 0;
        return e.ValueKind == JsonValueKind.Number && e.TryGetSingle(out f) && float.IsFinite(f);
    }
}
