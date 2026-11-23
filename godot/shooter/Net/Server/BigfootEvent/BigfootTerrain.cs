using Godot;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Waters;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.BigfootEvent;

/// <summary>The terrain and the solid world the Bigfoot walks in, by the physics space: where its feet stand and what a body of its size cannot walk through. It tramples trees, so its rays skip their trunks.</summary>
internal sealed class BigfootTerrain
{
    const float StepUpM = 3.5f, DropM = 12f, WadeM = 2.2f;

    /// <summary>Bigfoot is not in the rule table (ADR 0028): its rays see what they saw while player bodies shared the world layer.</summary>
    const uint RayMask = CollisionLayers.World | CollisionLayers.Players;

    readonly Node3D _world;
    readonly Water _water;

    public BigfootTerrain(Node3D world, Water water) { _world = world; _water = water; }

    /// <summary>Whether the collider a ray hit is a tree's trunk: the Bigfoot tramples those (placed trees and the baked ones carry the surface "wood").</summary>
    static bool IsTreeHit(Godot.Collections.Dictionary hit) =>
        hit["collider"].AsGodotObject() is Node n && n.HasMeta("surface") && n.GetMeta("surface").AsString() == "wood";

    /// <summary>
    /// The ground height at (x, z) for something standing near <paramref name="nearY"/>: the first surface a ray finds from a little above down to a
    /// dozen metres below, trees skipped. Null where there is none, or the water is deeper than it wades. A roof less than a few metres above counts as ground.
    /// </summary>
    public float? Ground(float x, float z, float nearY)
    {
        if (_water.Depth(x, z) > WadeM) return null;
        var space = _world.GetWorld3D().DirectSpaceState;
        var exclude = new Godot.Collections.Array<Rid>();
        for (var i = 0; i < 5; i++)
        {
            var q = PhysicsRayQueryParameters3D.Create(new Vector3(x, nearY + StepUpM, z), new Vector3(x, nearY - DropM, z), RayMask);
            q.Exclude = exclude;
            var hit = space.PerfRay(q);
            if (hit.Count == 0) return null;
            if (!IsTreeHit(hit)) return hit["position"].AsVector3().Y;
            exclude.Add(hit["rid"].AsRid());
        }
        return null;
    }

    /// <summary>True when something solid that is not a tree (a building, a wall, a post) stands between the two points, for a body of <paramref name="radius"/>.</summary>
    public bool Blocked(NVec3 from, NVec3 to, float radius)
    {
        var dir = to - from;
        var len = dir.Length();
        if (len < 1e-4f) return false;
        dir /= len;
        var space = _world.GetWorld3D().DirectSpaceState;
        foreach (var dy in new[] { 0f, 1.3f }) // a low and a high ray: a fence post and a wall
        {
            var a = new Vector3(from.X, from.Y + dy, from.Z);
            var b = a + new Vector3(dir.X, dir.Y, dir.Z) * (len + radius);
            var exclude = new Godot.Collections.Array<Rid>();
            for (var i = 0; i < 6; i++)
            {
                var q = PhysicsRayQueryParameters3D.Create(a, b, RayMask);
                q.Exclude = exclude;
                var hit = space.PerfRay(q);
                if (hit.Count == 0) break;
                if (!IsTreeHit(hit)) return true;
                exclude.Add(hit["rid"].AsRid());
            }
        }
        return false;
    }
}
