using System.Numerics;
using System.Text.Json;

namespace Drycastle.ShooterCore;

/// <summary>A place the demon can hold on to: on a wall or the ceiling, with the surface normal (pointing into the room) and the anchors it can crawl to. Godot axes (x east, y up, z = -north).</summary>
public sealed record Anchor(int Id, Vector3 Pos, Vector3 Normal, int[] Links);

/// <summary>
/// The anchor graph of the church (<c>anchors</c> of church.json: <c>[{"id":0,"pos":[x,y,z],"normal":[x,y,z],"links":[1,2]}]</c>). Like the rest of the
/// file, positions and normals are in map axes (x east, y north, z up) and become game axes (x, z, -y), as <see cref="Church"/> does. A link is a straight
/// crawl along the surface and counts both ways whichever end names it. Normals are normalised here. A graph that is empty, names an unknown anchor,
/// links an anchor to itself, repeats an id or is not connected is refused, since the demon would be stuck in a part of it.
/// </summary>
public sealed class AnchorGraph
{
    readonly Dictionary<int, Anchor> _byId = new();
    readonly Dictionary<int, int[]> _next = new();

    public IReadOnlyCollection<Anchor> Anchors => _byId.Values;
    public int Count => _byId.Count;

    public AnchorGraph(IEnumerable<Anchor> anchors, string name = "anchors")
    {
        foreach (var a in anchors)
        {
            if (_byId.ContainsKey(a.Id)) throw new DataFileException($"{name}: anchor {a.Id} appears twice");
            if (!float.IsFinite(a.Pos.X + a.Pos.Y + a.Pos.Z) || a.Normal.LengthSquared() < 1e-6f || !float.IsFinite(a.Normal.LengthSquared()))
                throw new DataFileException($"{name}: anchor {a.Id} has a bad position or a zero normal");
            _byId[a.Id] = a with { Normal = Vector3.Normalize(a.Normal) };
        }
        if (_byId.Count == 0) throw new DataFileException($"{name}: no anchors");
        var sets = _byId.Keys.ToDictionary(k => k, _ => new SortedSet<int>());
        foreach (var a in _byId.Values)
            foreach (var l in a.Links)
            {
                if (l == a.Id) throw new DataFileException($"{name}: anchor {a.Id} links to itself");
                if (!_byId.ContainsKey(l)) throw new DataFileException($"{name}: anchor {a.Id} links to the unknown anchor {l}");
                sets[a.Id].Add(l); sets[l].Add(a.Id);
            }
        foreach (var (id, s) in sets) _next[id] = s.ToArray();
        var seen = new HashSet<int> { _byId.Keys.Min() };
        var queue = new Queue<int>(seen);
        while (queue.Count > 0) foreach (var n in _next[queue.Dequeue()]) if (seen.Add(n)) queue.Enqueue(n);
        if (seen.Count != _byId.Count) throw new DataFileException($"{name}: the anchors are not all connected ({seen.Count} of {_byId.Count} reachable)");
    }

    public Anchor this[int id] => _byId[id];

    public bool Contains(int id) => _byId.ContainsKey(id);

    /// <summary>The anchors one crawl away from <paramref name="id"/>, in id order.</summary>
    public IReadOnlyList<int> Neighbours(int id) => _next[id];

    /// <summary>The smallest id: where a demon starts unless told otherwise.</summary>
    public int FirstId => _byId.Keys.Min();

    /// <summary>The graph of a church.json text (an object with an <c>anchors</c> array) or of a bare anchors array.</summary>
    public static AnchorGraph FromJson(string json, string name = "church.json")
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException e) { throw new DataFileException($"{name}: not JSON: {e.Message}", e); }
        using (doc)
        {
            var arr = doc.RootElement;
            if (arr.ValueKind == JsonValueKind.Object && !arr.TryGetProperty("anchors", out arr))
                throw new DataFileException($"{name}: no anchors array");
            if (arr.ValueKind != JsonValueKind.Array) throw new DataFileException($"{name}: anchors is not an array");
            var list = new List<Anchor>();
            foreach (var e in arr.EnumerateArray())
            {
                try
                {
                    var links = e.TryGetProperty("links", out var l) ? l.EnumerateArray().Select(x => x.GetInt32()).ToArray() : Array.Empty<int>();
                    list.Add(new Anchor(e.GetProperty("id").GetInt32(), Vec(e.GetProperty("pos")), Vec(e.GetProperty("normal")), links));
                }
                catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException)
                {
                    throw new DataFileException($"{name}: a malformed anchor ({e.GetRawText()})", ex);
                }
            }
            return new AnchorGraph(list, name);
        }
    }

    /// <summary>A map-axes vector (x east, y north, z up) in game axes.</summary>
    static Vector3 Vec(JsonElement a) => a.GetArrayLength() == 3 ? new Vector3(a[0].GetSingle(), a[2].GetSingle(), -a[1].GetSingle()) : throw new FormatException("a vector needs 3 numbers");
}
