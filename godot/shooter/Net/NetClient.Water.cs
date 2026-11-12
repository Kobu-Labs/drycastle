using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Water on the client: splashes when the local player, a remote player or a rocket goes into or out of the water,
/// the remote swim pose (lowered into the water, the slow walk, body leaned forward), and bullets that meet the surface.
/// Runs after the remotes and rockets are placed each frame. Pure rules are in <see cref="UnderwaterFx"/>.
/// </summary>
public partial class NetClient
{
    bool _meInWater;
    readonly Dictionary<ushort, bool> _remoteInWater = new();
    readonly Dictionary<ushort, bool> _rocketInWater = new();
    readonly List<ushort> _staleWater = new(); // reused each frame to drop the swimmers who left (#173, T3-033)

    void RenderWater()
    {
        if (Water.Bodies.Count == 0) return;
        if (_me != null)
        {
            var feet = _me.GlobalPosition;
            var inWater = Water.DepthAt(new NVec3(feet.X, feet.Y, feet.Z)) > 0f;
            if (UnderwaterFx.WaterCrossing(_meInWater, inWater) != UnderwaterFx.Crossing.None) SplashAt(feet);
            _meInWater = inWater;
        }
        foreach (var (id, r) in _remotes)
        {
            if (r.Node == null || !r.Node.Visible) { _remoteInWater.Remove(id); continue; }
            var feet = r.Node.GlobalPosition;
            var depth = Water.DepthAt(new NVec3(feet.X, feet.Y, feet.Z));
            var inWater = depth > 0f;
            if (_remoteInWater.TryGetValue(id, out var was)) { if (UnderwaterFx.WaterCrossing(was, inWater) != UnderwaterFx.Crossing.None) SplashAt(feet); }
            _remoteInWater[id] = inWater;
            // swimming (deep enough to swim): lowered into the water, leaned forward, the slow walk; wading stays upright
            r.View?.SetSwim(depth >= WaterConsts.SwimDepth);
        }
        if (_remoteInWater.Count > 0)
        {
            _staleWater.Clear();
            foreach (var k in _remoteInWater.Keys) if (!_remotes.ContainsKey(k)) _staleWater.Add(k);
            foreach (var k in _staleWater) _remoteInWater.Remove(k);
        }
        foreach (var (id, rk) in _rockets)
        {
            if (rk.Node == null || !GodotObject.IsInstanceValid(rk.Node)) continue;
            var at = rk.Node.GlobalPosition;
            var inWater = Water.DepthAt(new NVec3(at.X, at.Y, at.Z)) > 0f;
            if (_rocketInWater.TryGetValue(id, out var was) && UnderwaterFx.WaterCrossing(was, inWater) == UnderwaterFx.Crossing.Enter)
                SplashAt(at, 0.4f);
            _rocketInWater[id] = inWater;
        }
        foreach (var id in _rocketInWater.Keys.Where(k => !_rockets.ContainsKey(k)).ToList()) _rocketInWater.Remove(id);
    }

    /// <summary>Splash at the surface directly below <paramref name="p"/> (the body's own splash sound and droplets).</summary>
    void SplashAt(Vector3 p, float size = 1f)
    {
        var surface = Water.SurfaceY(p.X, p.Z);
        if (float.IsNaN(surface)) return;
        WaterFx.Splash(Owned, new Vector3(p.X, surface, p.Z), size);
    }

    /// <summary>A bullet that crosses a lake surface on its way to the target: a small splash where it meets the water.</summary>
    void BulletSurfaceHit(Vector3 from, Vector3 to)
    {
        if (Water.Bodies.Count == 0) return;
        if (UnderwaterFx.FirstSurfaceCrossing(Water, new NVec3(from.X, from.Y, from.Z), new NVec3(to.X, to.Y, to.Z), out var hit))
            WaterFx.Splash(Owned, new Vector3(hit.X, hit.Y, hit.Z), 0.4f);
    }
}
