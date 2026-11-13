using System.Numerics;
using System.Text.Json;

namespace Drycastle.ShooterCore;

/// <summary>
/// The runtime attributes of the map (attribute_points.json, issue #263): for each ground attribute the server needs while it
/// runs, a pool of candidate points the pipeline baked inside every area that carries it. The game never sees the areas, so
/// "a random spot in a Bigfoot area" is a random pick from the pool. Scene-free, like <see cref="Water"/> (ADR 0004): the
/// server owns the pick and its random generator.
/// <para>
/// Axes: Godot ground axes, as everywhere in the game: x east, y up (the terrain height at the point, as spawns), z = -north.
/// </para>
/// <para>
/// A map without the file, or an attribute no area carries, has no points: <see cref="Pick"/> returns null and the caller must
/// handle it (do not start the event, try later).
/// </para>
/// </summary>
public sealed class AttributePoints
{
    /// <summary>The attribute of the events that may spawn somewhere; its values are the event names (<c>bigfoot</c>).</summary>
    public const string EventSpawn = "event_spawn";

    /// <summary>The newest format of the file this reader understands (a newer one is refused, which counts as no points).</summary>
    public const int SupportedVersion = 1;

    public static readonly AttributePoints Empty = new(new Dictionary<string, Vector3[]>());

    readonly Dictionary<string, Vector3[]> _pools;

    AttributePoints(Dictionary<string, Vector3[]> pools) => _pools = pools;

    static string Key(string attribute, string value) => attribute + ":" + value;

    /// <summary>The candidate points of an attribute value (<c>event_spawn</c>, <c>bigfoot</c>); empty when no area carries it.</summary>
    public IReadOnlyList<Vector3> Points(string attribute, string value)
        => _pools.TryGetValue(Key(attribute, value), out var p) ? p : Array.Empty<Vector3>();

    /// <summary>How many candidate points an attribute value has.</summary>
    public int Count(string attribute, string value) => Points(attribute, value).Count;

    /// <summary>
    /// A random candidate point of an attribute value, drawn with the caller's generator (the server's). Null when it has none
    /// (the map has no file, or no area carries the attribute, or nothing usable was left of its areas).
    /// </summary>
    public Vector3? Pick(string attribute, string value, Random rng)
    {
        var points = Points(attribute, value);
        return points.Count == 0 ? null : points[rng.Next(points.Count)];
    }

    /// <summary>A random point where an event may spawn (<c>event_spawn: &lt;eventName&gt;</c>); null when there is none.</summary>
    public Vector3? PickEventSpawn(string eventName, Random rng) => Pick(EventSpawn, eventName, rng);

    /// <summary>Parses the file contents; an empty string is the map without the file (no points).</summary>
    public static AttributePoints FromJson(string? json) => string.IsNullOrEmpty(json) ? Empty : Parse(json);

    /// <summary>Like <see cref="FromJson"/>, but a broken file is reported to <paramref name="onError"/> and counts as no points.</summary>
    public static AttributePoints FromJsonOrEmpty(string? json, Action<string> onError)
    {
        try { return FromJson(json); }
        catch (DataFileException e) { onError(e.Message); return Empty; }
    }

    /// <summary>
    /// Parses attribute_points.json: <c>{"version": 1, "attributes": [{"attribute", "value", "areas", "area_m2",
    /// "points": [[x, y, z], ...]}]}</c> with points in local map metres (x east, y north, z the terrain height); they are
    /// converted to Godot ground axes here. Unknown fields are ignored.
    /// </summary>
    public static AttributePoints Parse(string json, string name = "attribute_points.json")
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
            if (!root.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Array)
                throw new DataFileException($"{name}: missing attributes array");
            var pools = new Dictionary<string, Vector3[]>();
            foreach (var a in attributes.EnumerateArray())
            {
                var attribute = StringOf(a, "attribute");
                var value = StringOf(a, "value");
                if (string.IsNullOrEmpty(attribute) || string.IsNullOrEmpty(value)) throw new DataFileException($"{name}: an entry without attribute or value");
                var key = Key(attribute, value);
                if (pools.ContainsKey(key)) throw new DataFileException($"{name}: {attribute}: {value} is listed twice");
                if (!a.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array)
                    throw new DataFileException($"{name}: {attribute}: {value} has no points array");
                var list = new List<Vector3>(points.GetArrayLength());
                foreach (var p in points.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 3 || !Number(p[0], out var x) || !Number(p[1], out var y) || !Number(p[2], out var z))
                        throw new DataFileException($"{name}: {attribute}: {value} has a point that is not [x, y, z]");
                    list.Add(new Vector3(x, z, -y));
                }
                pools[key] = list.ToArray();
            }
            return new AttributePoints(pools);
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
