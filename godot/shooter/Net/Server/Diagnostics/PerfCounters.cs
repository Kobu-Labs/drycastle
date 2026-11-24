using Godot;

namespace Drycastle.Shooter.Net.Server.Diagnostics;

/// <summary>Counters bumped from hot paths while <see cref="ServerConfig.PerfOut"/> is set (perf-harness only; plain static adds, no allocation).</summary>
public static class PerfCounters
{
    public static long Rays, LiveTargetsCalls, LiveTargetsElems;

    /// <summary>A physics ray through the space, counted. Same result as <c>space.IntersectRay(q)</c>.</summary>
    public static Godot.Collections.Dictionary PerfRay(this PhysicsDirectSpaceState3D space, PhysicsRayQueryParameters3D q)
    {
        Rays++;
        return space.IntersectRay(q);
    }
}
