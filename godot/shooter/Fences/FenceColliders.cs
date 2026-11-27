using Godot;
using Drycastle.ShooterCore.Fences;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Fences;

/// <summary>
/// The collision of the thin fences follows what is standing (docs/fence-destruction.md F7), the same on the server and on every client. A thin section's closed box lives in a map
/// collision node (<see cref="FenceSectionInfo.Collider"/>: a parcel's <c>parcel_&lt;ID&gt;_props_collision</c> or a chunk's <c>props_collision</c>), where the parcel's trees are too.
/// <see cref="Mark"/> a section that changed; <see cref="Flush"/> (once a tick) rebuilds each marked node once: its original triangles, less the boxes of its thin sections, plus the standing
/// runs of those sections (<see cref="FenceGeometry.ColliderTriangles"/>). A node whose sections are all whole gets its original shape back. Shapes are in map-root local axes.
/// </summary>
public sealed class FenceColliders
{
    /// <summary>How far outside a section's box a triangle's corners may lie and the triangle still be that box's (the pipeline's rounding), flat and in height, metres.</summary>
    const float FlatSlop = 0.05f, HeightSlop = 0.1f;

    /// <summary>One collision node with thin sections in it.</summary>
    sealed class Holder
    {
        public CollisionShape3D Node = null!;
        public Shape3D Original = null!;
        /// <summary>The original triangles, three corners each.</summary>
        public Vector3[] Faces = [];
        public Aabb Bounds;
        public readonly List<int> Sections = new();
        public bool Marked, Swapped;
    }

    readonly Drycastle.MapLoader.MapLoader _map;
    readonly IReadOnlyList<FenceSectionInfo> _sections;
    readonly Dictionary<int, Holder> _holderOf = new();
    readonly Dictionary<CollisionShape3D, Holder> _holders = new();
    readonly List<Holder> _marked = new();
    readonly HashSet<string> _warned = new();
    readonly List<NVec3> _boxes = new();
    readonly List<Vector3> _faces = new();
    bool _resolved;

    public FenceColliders(Drycastle.MapLoader.MapLoader map, IReadOnlyList<FenceSectionInfo> sections)
    {
        _map = map; _sections = sections;
    }

    /// <summary>Section <paramref name="section"/> changed: its node is rebuilt at the next <see cref="Flush"/>.</summary>
    public void Mark(int section)
    {
        Resolve();
        if (!_holderOf.TryGetValue(section, out var h) || h.Marked) return;
        h.Marked = true;
        _marked.Add(h);
    }

    /// <summary>Rebuilds every marked node once from <paramref name="state"/>, then tells the map its physics changed.</summary>
    public void Flush(FenceState state)
    {
        if (_marked.Count == 0) return;
        var changed = false;
        foreach (var h in _marked)
        {
            h.Marked = false;
            if (GodotObject.IsInstanceValid(h.Node)) changed |= Rebuild(h, state);
        }
        _marked.Clear();
        if (changed) _map.HouseGeometryChanged();
    }

    /// <summary>Every node back to its original shape (a new round).</summary>
    public void RestoreAll()
    {
        foreach (var h in _marked) h.Marked = false;
        _marked.Clear();
        var changed = false;
        foreach (var h in _holders.Values)
        {
            if (!h.Swapped || !GodotObject.IsInstanceValid(h.Node)) continue;
            h.Node.Shape = h.Original;
            h.Swapped = false;
            changed = true;
        }
        if (changed) _map.HouseGeometryChanged();
    }

    bool Rebuild(Holder h, FenceState state)
    {
        var broken = false;
        foreach (var i in h.Sections) broken |= state.Broken(i) != 0;
        if (!broken)
        {
            if (!h.Swapped) return false;
            h.Node.Shape = h.Original;
            h.Swapped = false;
            return true;
        }
        _boxes.Clear();
        foreach (var i in h.Sections) FenceGeometry.ColliderTriangles(_sections[i], state.Broken(i), _boxes);
        _faces.Clear();
        for (var t = 0; t + 2 < h.Faces.Length; t += 3)
        {
            if (InsideAnyBox(h, h.Faces[t], h.Faces[t + 1], h.Faces[t + 2])) continue;
            _faces.Add(h.Faces[t]); _faces.Add(h.Faces[t + 1]); _faces.Add(h.Faces[t + 2]);
        }
        foreach (var v in _boxes) _faces.Add(new Vector3(v.X, v.Y, v.Z));
        var shape = new ConcavePolygonShape3D { BackfaceCollision = true };
        shape.SetFaces(_faces.ToArray());
        h.Node.Shape = shape;
        h.Swapped = true;
        return true;
    }

    /// <summary>Whether all three corners lie in the box of one of the node's thin sections (a triangle of the section's own original box).</summary>
    bool InsideAnyBox(Holder h, Vector3 a, Vector3 b, Vector3 c)
    {
        foreach (var i in h.Sections)
        {
            var s = _sections[i];
            if (In(s, a) && In(s, b) && In(s, c)) return true;
        }
        return false;
    }

    static bool In(FenceSectionInfo s, Vector3 p)
    {
        var flat = new System.Numerics.Vector2(p.X, p.Z);
        var along = s.AlongOf(flat);
        var d = flat - s.A;
        var across = MathF.Abs(d.X * s.Dir.Y - d.Y * s.Dir.X);
        return along >= -FlatSlop && along <= s.Length + FlatSlop && across <= s.ThicknessM / 2 + FlatSlop && p.Y >= s.Bottom - HeightSlop && p.Y <= s.Top + HeightSlop;
    }

    // ---- finding the nodes ----

    /// <summary>Finds the collision node of every thin section, once (the map is loaded by now). A node the map lacks is logged once and skipped.</summary>
    void Resolve()
    {
        if (_resolved) return;
        _resolved = true;
        foreach (var s in _sections)
        {
            if (!s.Thin || s.Collider == null) continue;
            var h = HolderOf(s);
            if (h == null) continue;
            h.Sections.Add(s.Index);
            _holderOf[s.Index] = h;
        }
    }

    Holder? HolderOf(FenceSectionInfo s)
    {
        var list = Candidates(s.Collider!);
        if (list == null || list.Count == 0) { Warn(s.Collider!, "has no such collision node in the map"); return null; }
        var mid = s.PieceCentre(s.Pieces / 2);
        Holder? first = null;
        foreach (var cs in list)
        {
            var h = Known(cs);
            if (h == null) continue;
            first ??= h;
            if (list.Count == 1 || h.Bounds.Grow(0.2f).HasPoint(new Vector3(mid.X, mid.Y, mid.Z))) return h;
        }
        if (first == null) Warn(s.Collider!, "is not a triangle mesh; its fences keep their whole collider");
        return first;
    }

    Holder? Known(CollisionShape3D cs)
    {
        if (_holders.TryGetValue(cs, out var h)) return h;
        if (!GodotObject.IsInstanceValid(cs) || cs.Shape is not ConcavePolygonShape3D shape) return null;
        var faces = shape.GetFaces();
        var box = faces.Length == 0 ? new Aabb() : new Aabb(faces[0], Vector3.Zero);
        foreach (var f in faces) box = box.Expand(f);
        return _holders[cs] = new Holder { Node = cs, Original = shape, Faces = faces, Bounds = box };
    }

    List<CollisionShape3D>? Candidates(string collider)
    {
        const string chunkMark = "props_collision@";
        if (collider.StartsWith(chunkMark, StringComparison.Ordinal))
        {
            var xy = collider[chunkMark.Length..].Split(',');
            return xy.Length == 2 && int.TryParse(xy[0], out var x) && int.TryParse(xy[1], out var y) ? _map.ChunkPropsShapes.GetValueOrDefault((x, y)) : null;
        }
        const string pre = "parcel_", post = "_props_collision";
        return collider.StartsWith(pre, StringComparison.Ordinal) && collider.EndsWith(post, StringComparison.Ordinal)
            ? _map.PropsShapes.GetValueOrDefault(collider[pre.Length..^post.Length]) : null;
    }

    void Warn(string collider, string what)
    {
        if (_warned.Add(collider)) GD.PrintErr($"fences: {collider} {what}");
    }
}
