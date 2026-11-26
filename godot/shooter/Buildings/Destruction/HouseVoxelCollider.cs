using Godot;
using Drycastle.ShooterCore.Buildings.Destruction;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Buildings.Destruction;

/// <summary>
/// The collider of a carved house, one class for the server (HouseDestructionServer) and every client (<see cref="HouseWorldNode"/>), so predicted movement
/// meets the same walls as the server's. Once the house is damaged its exact shell (the map's collision node) is disabled and the live cells' faces
/// (<see cref="HouseMesher.Surface(HouseCells, CellBox, List{Quad})"/>, <see cref="HouseFrame.Triangles"/>) take its place as trimeshes, one per column of
/// <see cref="ChunkCells"/> x <see cref="ChunkCells"/> cells (sibling shapes of the node, in its frame, backfaces solid). Only the columns whose cells changed
/// (<see cref="Mark"/>) are rebuilt, so a second hole costs its own columns, not the whole house. Whole again: the shell is back, the columns off.
/// </summary>
public sealed class HouseVoxelCollider
{
    /// <summary>A column's width in cells (4 m).</summary>
    public const int ChunkCells = 16;

    readonly HouseGrid _g;
    readonly int _cx, _cy;
    readonly CollisionShape3D?[] _shapes;
    readonly bool[] _dirty;
    bool _anyDirty, _voxel;
    readonly List<Quad> _quads = new();
    readonly List<NVec3> _pos = new();

    public HouseVoxelCollider(HouseGrid g, CollisionShape3D node)
    {
        _g = g;
        Node = node;
        _cx = (g.Nx + ChunkCells - 1) / ChunkCells;
        _cy = (g.Ny + ChunkCells - 1) / ChunkCells;
        _shapes = new CollisionShape3D?[_cx * _cy];
        _dirty = new bool[_cx * _cy];
    }

    /// <summary>The map's collision node of the house (its shell).</summary>
    public CollisionShape3D Node { get; }

    /// <summary>Every column to rebuild (a catch-up, a resync).</summary>
    public void MarkAll()
    {
        Array.Fill(_dirty, true);
        _anyDirty = true;
    }

    /// <summary>The columns of <paramref name="cells"/> and of their side neighbours (a removed cell opens its neighbours' faces) are rebuilt at the next <see cref="Update"/>.</summary>
    public void Mark(ReadOnlySpan<int> cells)
    {
        foreach (var i in cells)
        {
            var (x, y, _) = _g.Coords(i);
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                int ax = x + dx, ay = y + dy;
                if ((uint)ax >= (uint)_g.Nx || (uint)ay >= (uint)_g.Ny) continue;
                _dirty[ax / ChunkCells + _cx * (ay / ChunkCells)] = true;
                _anyDirty = true;
            }
        }
    }

    /// <summary>What a carve changed: its carved, crumbled and fallen cells.</summary>
    public void Mark(CarveResult r)
    {
        Mark(r.Carved);
        Mark(r.Crumbled);
        foreach (var f in r.Fallen) Mark(f);
    }

    /// <summary>
    /// Damaged: the shell off and the marked columns rebuilt (all of them the first time). Whole: the shell back, the columns off. Returns whether the
    /// collision changed. <paramref name="mapToWorld"/> is the map root's global transform (map local, Godot frame, to world).
    /// </summary>
    public bool Update(HouseCells cells, Transform3D mapToWorld)
    {
        if (!GodotObject.IsInstanceValid(Node)) return false;
        if (!cells.Damaged)
        {
            _anyDirty = false;
            Array.Clear(_dirty);
            if (!_voxel) return false;
            _voxel = false;
            Node.Disabled = false;
            foreach (var s in _shapes) if (s != null && GodotObject.IsInstanceValid(s)) s.Disabled = true;
            return true;
        }
        if (!_voxel) { _voxel = true; Node.Disabled = true; MarkAll(); }
        if (!_anyDirty) return false;
        _anyDirty = false;
        var parent = Node.GetParent();
        var toLocal = (Node.IsInsideTree() ? Node.GlobalTransform : Node.Transform).AffineInverse() * mapToWorld;
        for (var k = 0; k < _dirty.Length; k++)
        {
            if (!_dirty[k]) continue;
            _dirty[k] = false;
            int x0 = k % _cx * ChunkCells, y0 = k / _cx * ChunkCells;
            _quads.Clear(); _pos.Clear();
            HouseMesher.Surface(cells, new CellBox(x0, y0, 0, Math.Min(x0 + ChunkCells, _g.Nx), Math.Min(y0 + ChunkCells, _g.Ny), _g.Nz), _quads);
            HouseFrame.Triangles(_g, _quads, _pos);
            var s = _shapes[k];
            if (s == null || !GodotObject.IsInstanceValid(s))
            {
                if (_pos.Count == 0) continue;
                s = _shapes[k] = new CollisionShape3D { Name = $"{Node.Name}_voxel{k}", Transform = Node.Transform, Shape = new ConcavePolygonShape3D { BackfaceCollision = true } };
                parent.AddChild(s);
            }
            if (_pos.Count == 0) { s.Disabled = true; continue; }
            var faces = new Vector3[_pos.Count];
            for (var i = 0; i < faces.Length; i++) faces[i] = toLocal * new Vector3(_pos[i].X, _pos[i].Y, _pos[i].Z);
            ((ConcavePolygonShape3D)s.Shape).SetFaces(faces);
            s.Disabled = false;
        }
        return true;
    }

    /// <summary>The map root's global transform (identity off the tree).</summary>
    public static Transform3D MapToWorld(Node3D mapRoot) => mapRoot.IsInsideTree() ? mapRoot.GlobalTransform : Transform3D.Identity;
}
