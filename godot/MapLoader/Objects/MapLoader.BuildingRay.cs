using Godot;

namespace Drycastle.MapLoader;

/// <summary>Where a ray meets a building: the point, the surface normal there (facing the ray's origin) and the parcel the building is on. Map space, Godot axes.</summary>
public readonly record struct BuildingHit(Vector3 Point, Vector3 Normal, string ParcelId, float Distance);

// A ray against the buildings as they are drawn (walls with their openings, the real roof), not their collision
// blocks: the editor uses it to fix things to a wall or a roof (house connectors).
public sealed partial class MapLoader
{
    readonly Dictionary<ulong, Vector3[]> _buildingFaces = new();

    /// <summary>
    /// The nearest building surface a ray meets within <paramref name="maxDistance"/>; null when it meets none.
    /// Hidden buildings (replaced parcels) are skipped. The triangles of a building are read once and kept.
    /// </summary>
    public BuildingHit? RayBuildings(Vector3 origin, Vector3 dir, float maxDistance = 3000f)
    {
        BuildingHit? best = null;
        var bestT = maxDistance;
        foreach (var (id, node) in _visuals)
        {
            if (node is not MeshInstance3D mi || !GodotObject.IsInstanceValid(mi) || mi.Mesh == null || !mi.IsVisibleInTree()) continue;
            var inv = mi.GlobalTransform.AffineInverse();
            var o = inv * origin;
            var d = inv.Basis * dir;
            if (!RayHitsBox(mi.GetAabb(), o, d, bestT)) continue;
            var key = mi.Mesh.GetInstanceId();
            if (!_buildingFaces.TryGetValue(key, out var faces))
            {
                if (_buildingFaces.Count > 2000) _buildingFaces.Clear(); // meshes of reloaded chunks pile up otherwise
                _buildingFaces[key] = faces = mi.Mesh.GetFaces();
            }
            for (var i = 0; i + 2 < faces.Length; i += 3)
            {
                Vector3 a = faces[i], e1 = faces[i + 1] - a, e2 = faces[i + 2] - a;
                var p = d.Cross(e2);
                var det = e1.Dot(p);
                if (Mathf.Abs(det) < 1e-9f) continue;
                var s = o - a;
                var u = s.Dot(p) / det;
                if (u < 0f || u > 1f) continue;
                var q = s.Cross(e1);
                var v = d.Dot(q) / det;
                if (v < 0f || u + v > 1f) continue;
                var t = e2.Dot(q) / det;
                if (t <= 1e-4f || t >= bestT) continue;
                var n = e1.Cross(e2).Normalized();
                if (n.Dot(d) > 0) n = -n;
                bestT = t;
                best = new BuildingHit(mi.GlobalTransform * (o + d * t), (mi.GlobalTransform.Basis * n).Normalized(), id, t);
            }
        }
        return best;
    }

    /// <summary>Slab test: does the ray enter the box before <paramref name="maxT"/>?</summary>
    static bool RayHitsBox(Aabb box, Vector3 from, Vector3 dir, float maxT)
    {
        float tmin = 0f, tmax = maxT;
        for (var i = 0; i < 3; i++)
        {
            float o = from[i], d = dir[i], lo = box.Position[i], hi = lo + box.Size[i];
            if (Mathf.Abs(d) < 1e-9f) { if (o < lo || o > hi) return false; continue; }
            float t1 = (lo - o) / d, t2 = (hi - o) / d;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = Mathf.Max(tmin, t1);
            tmax = Mathf.Min(tmax, t2);
            if (tmin > tmax) return false;
        }
        return true;
    }
}
