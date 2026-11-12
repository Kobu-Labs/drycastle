using System.Text.Json;
using Godot;
using Drycastle.ShooterCore;
using Num = System.Numerics;

namespace Drycastle.MapLoader;

/// <summary>A cable between two placed utility poles (objects.json "cables", or the editor's edits file): the ids of the cable and of its two poles.</summary>
public sealed record CableSpec(string Id, string From, string To);

// Cables: the wires strung between placed utility poles. Nothing of them is baked into the map; they are built here
// from the poles as they stand (position, rotation, scale) and the wire seats of their assets (assets/manifest.json
// "seats"), so moving or turning a pole only needs a rebuild. Visual only: no collision.
public sealed partial class MapLoader
{
    readonly List<CableSpec> _cables = new();
    readonly Dictionary<string, MeshInstance3D> _cableNodes = new();
    readonly Dictionary<string, List<CableWire>> _cableWires = new();
    readonly Dictionary<string, string> _objectAssets = new();
    Node3D? _cablesRoot;
    static StandardMaterial3D? _cableMaterial;
    static readonly Color LineWireColour = Color.FromHtml("#3f4246"), DarkCableColour = Color.FromHtml("#1d1d1f");

    /// <summary>The cables the map shows (also those whose poles are missing, which are not drawn).</summary>
    public IReadOnlyList<CableSpec> Cables => _cables;
    /// <summary>The mesh of each drawn cable, "Cables/cable_&lt;id&gt;".</summary>
    public IReadOnlyDictionary<string, MeshInstance3D> CableNodes => _cableNodes;
    /// <summary>The wires of a drawn cable (map space, Godot axes); empty when it is not drawn.</summary>
    public IReadOnlyList<CableWire> CableWiresOf(string id) => _cableWires.TryGetValue(id, out var w) ? w : Array.Empty<CableWire>();
    /// <summary>The asset of a placed object; null when there is no such object.</summary>
    public string? ObjectAsset(string objectId) => _objectAssets.GetValueOrDefault(objectId);

    /// <summary>True when the asset is a utility pole: it has wire seats, so cables can hang from it.</summary>
    public bool IsPoleAsset(string asset)
    {
        if (_assetManifest == null) ReadAssetManifest();
        return _assetManifest!.TryGetValue(asset, out var e) && e.Seats.Count > 0;
    }

    /// <summary>Replace the cables and draw them.</summary>
    public void SetCables(IEnumerable<CableSpec> cables)
    {
        _cables.Clear();
        _cables.AddRange(cables);
        RebuildCables();
    }

    /// <summary>The wire seats of a placed pole where it stands now; empty when the object is no pole or is not placed.</summary>
    public List<WireSeat> SeatsOf(string objectId)
    {
        var seats = new List<WireSeat>();
        if (!_objectNodes.TryGetValue(objectId, out var node) || !GodotObject.IsInstanceValid(node)) return seats;
        if (_assetManifest == null) ReadAssetManifest();
        if (!_objectAssets.TryGetValue(objectId, out var asset) || !_assetManifest!.TryGetValue(asset, out var entry)) return seats;
        var t = node.Transform; // the object's containers sit at the map origin
        foreach (var (kind, tier, local) in entry.Seats)
        {
            var p = t * local;
            seats.Add(new WireSeat(kind, tier, new Num.Vector3(p.X, p.Y, p.Z)));
        }
        return seats;
    }

    /// <summary>
    /// Draw the cables again from where their poles stand now: all of them, or only those of one pole (while it is
    /// being dragged). A cable with a missing pole, or between objects that are not poles, is not drawn.
    /// </summary>
    public void RebuildCables(string? onlyOfObject = null)
    {
        if (CollisionOnly) return;
        if (onlyOfObject == null)
        {
            foreach (var n in _cableNodes.Values) { n.GetParent()?.RemoveChild(n); n.QueueFree(); }
            _cableNodes.Clear();
            _cableWires.Clear();
        }
        if (_cables.Count == 0) return;
        if (_cablesRoot == null || !GodotObject.IsInstanceValid(_cablesRoot))
        {
            _cablesRoot = new Node3D { Name = "Cables" };
            Root.AddChild(_cablesRoot);
        }
        var seen = new HashSet<string>();
        foreach (var c in _cables)
        {
            if (onlyOfObject != null && c.From != onlyOfObject && c.To != onlyOfObject) continue;
            if (!seen.Add(c.Id)) continue;
            var wires = CableWires.Span(SeatsOf(c.From), SeatsOf(c.To), c.Id);
            if (_cableNodes.Remove(c.Id, out var old)) { old.GetParent()?.RemoveChild(old); old.QueueFree(); }
            _cableWires.Remove(c.Id);
            if (wires.Count == 0) continue;
            var mi = new MeshInstance3D
            {
                Name = $"cable_{SafeName(c.Id)}",
                Mesh = WireMesh(wires),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = _cableMaterial ??= new StandardMaterial3D
                {
                    VertexColorUseAsAlbedo = true,
                    VertexColorIsSrgb = true,
                    Roughness = 1f,
                    MetallicSpecular = 0f,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                },
            };
            _cablesRoot.AddChild(mi);
            _cableNodes[c.Id] = mi;
            _cableWires[c.Id] = wires;
        }
    }

    /// <summary>One mesh for the wires of a cable: a thin tube along each, three sides for a line wire, four for the thicker ones.</summary>
    static ArrayMesh WireMesh(List<CableWire> wires)
    {
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var colours = new List<Color>();
        var idx = new List<int>();
        foreach (var w in wires)
        {
            var sides = w.Kind == "line" ? 3 : 4;
            var colour = w.Kind == "line" ? LineWireColour : DarkCableColour;
            var first = verts.Count;
            for (var i = 0; i < w.Points.Length; i++)
            {
                var a = w.Points[Math.Max(i - 1, 0)];
                var b = w.Points[Math.Min(i + 1, w.Points.Length - 1)];
                var t = new Vector3(b.X - a.X, b.Y - a.Y, b.Z - a.Z).Normalized();
                var u = t.Cross(Vector3.Up);
                u = u.LengthSquared() < 1e-6f ? Vector3.Right : u.Normalized();
                var v = t.Cross(u);
                var p = new Vector3(w.Points[i].X, w.Points[i].Y, w.Points[i].Z);
                for (var k = 0; k < sides; k++)
                {
                    var ang = Mathf.Tau * k / sides;
                    var n = u * Mathf.Cos(ang) + v * Mathf.Sin(ang);
                    verts.Add(p + n * w.Radius);
                    normals.Add(n);
                    colours.Add(colour);
                }
            }
            for (var i = 0; i < w.Points.Length - 1; i++)
                for (var k = 0; k < sides; k++)
                {
                    int a = first + i * sides + k, b = first + i * sides + (k + 1) % sides;
                    int c = b + sides, d = a + sides;
                    idx.AddRange(new[] { a, c, b, a, d, c });
                }
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colours.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>The "cables" list of objects.json (maps exported with cables); empty when it has none.</summary>
    static List<CableSpec> ParseCables(JsonElement root)
    {
        var list = new List<CableSpec>();
        if (!root.TryGetProperty("cables", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var c in arr.EnumerateArray())
            if (c.TryGetProperty("id", out var id) && c.TryGetProperty("from", out var f) && c.TryGetProperty("to", out var t)
                && id.GetString() is { } i && f.GetString() is { } a && t.GetString() is { } b)
                list.Add(new CableSpec(i, a, b));
        return list;
    }

    /// <summary>The lamp of a placed object (a utility pole with a luminaire): where its light is in map space, and the way the object is turned.</summary>
    public readonly record struct ObjectLight(string ObjectId, Vector3 Position, float YawDeg);

    /// <summary>
    /// The lamps of the placed objects as they stand now: one per light point of each object's asset (assets/manifest.json
    /// "lights"). Street lamps of lamps.json are not among them (see <see cref="Lamps"/>); the shooter lights both.
    /// </summary>
    public List<ObjectLight> ObjectLights()
    {
        var lights = new List<ObjectLight>();
        if (_assetManifest == null) ReadAssetManifest();
        foreach (var (id, node) in _objectNodes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!GodotObject.IsInstanceValid(node) || !_objectAssets.TryGetValue(id, out var asset)
                || !_assetManifest!.TryGetValue(asset, out var entry)) continue;
            foreach (var local in entry.Lights)
                lights.Add(new ObjectLight(id, node.Transform * local, node.RotationDegrees.Y));
        }
        return lights;
    }

    /// <summary>The light points of a manifest asset in the object's own frame (Godot axes), from its "lights" list (pipeline axes, z up).</summary>
    static List<Vector3> ParseLights(JsonElement asset)
    {
        var lights = new List<Vector3>();
        if (!asset.TryGetProperty("lights", out var arr) || arr.ValueKind != JsonValueKind.Array) return lights;
        foreach (var p in arr.EnumerateArray())
            if (p.ValueKind == JsonValueKind.Array && p.GetArrayLength() == 3)
                lights.Add(new Vector3(p[0].GetSingle(), p[2].GetSingle(), -p[1].GetSingle()));
        return lights;
    }

    /// <summary>The wire seats of a manifest asset in the object's own frame (Godot axes), from its "seats" list (pipeline axes: x east, y north, z up).</summary>
    static List<(string kind, int tier, Vector3 local)> ParseSeats(JsonElement asset)
    {
        var seats = new List<(string, int, Vector3)>();
        if (!asset.TryGetProperty("seats", out var arr) || arr.ValueKind != JsonValueKind.Array) return seats;
        foreach (var s in arr.EnumerateArray())
        {
            if (!s.TryGetProperty("pos", out var p) || p.ValueKind != JsonValueKind.Array || p.GetArrayLength() != 3) continue;
            var kind = s.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString()! : "line";
            var tier = s.TryGetProperty("tier", out var ti) && ti.ValueKind == JsonValueKind.Number ? ti.GetInt32() : -1;
            seats.Add((kind, tier, new Vector3(p[0].GetSingle(), p[2].GetSingle(), -p[1].GetSingle())));
        }
        return seats;
    }
}
