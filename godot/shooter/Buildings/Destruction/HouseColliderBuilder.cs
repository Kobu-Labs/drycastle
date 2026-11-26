using Godot;
using Drycastle.ShooterCore.Buildings.Destruction;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Buildings.Destruction;

/// <summary>
/// The collider of a carved house, one builder for the server (HouseDestructionServer) and every client (<see cref="HouseWorldNode"/>), so predicted movement
/// meets the same walls as the server's: one trimesh of every face of the live cells (<see cref="MeshMode.AllFaces"/>, <see cref="HouseFrame.Triangles"/>),
/// in the frame of the house's collision node, backfaces solid. Scratch lists are reused; the face array is new per build.
/// </summary>
public sealed class HouseColliderBuilder
{
    readonly List<Quad> _quads = new();
    readonly List<NVec3> _pos = new();

    /// <summary>
    /// Fills <paramref name="into"/> (a new shape when null) with the faces of <paramref name="cells"/> in <paramref name="node"/>'s frame.
    /// <paramref name="mapToWorld"/> is the map root's global transform (map local, Godot frame, to world).
    /// </summary>
    public ConcavePolygonShape3D Build(HouseCells cells, CollisionShape3D node, Transform3D mapToWorld, ConcavePolygonShape3D? into = null)
    {
        _quads.Clear(); _pos.Clear();
        HouseMesher.Surface(cells, MeshMode.AllFaces, _quads);
        HouseFrame.Triangles(cells.Grid, _quads, _pos);
        var toLocal = (node.IsInsideTree() ? node.GlobalTransform : node.Transform).AffineInverse() * mapToWorld;
        var faces = new Vector3[_pos.Count];
        for (var i = 0; i < faces.Length; i++) faces[i] = toLocal * new Vector3(_pos[i].X, _pos[i].Y, _pos[i].Z);
        var shape = into ?? new ConcavePolygonShape3D();
        shape.BackfaceCollision = true;
        shape.SetFaces(faces);
        return shape;
    }

    /// <summary>The map root's global transform (identity off the tree).</summary>
    public static Transform3D MapToWorld(Node3D mapRoot) => mapRoot.IsInsideTree() ? mapRoot.GlobalTransform : Transform3D.Identity;
}
