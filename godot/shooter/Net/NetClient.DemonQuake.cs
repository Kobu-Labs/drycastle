using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The pastor turning into the demon (<see cref="DemonState.Transforming"/>) shakes the scene for everyone in or near the church: the camera shake of the roars
/// (<c>_shakeAmp</c>, see <c>ShakeFor</c>) is fed every frame, building up to the swap (<see cref="DemonFightDef.TransformSwapS"/>), then dying down to a rumble by the end of the turning;
/// the nearer to the pastor, the stronger. The church's lights follow the same clock (<see cref="ChurchDecor.SetTransform"/>). The clock starts when the state is first seen, as the demon view's does.
/// </summary>
public partial class NetClient
{
    /// <summary>Peak of the shake at the swap, at the pastor's feet (the roar's is 0.22), and the distances it is full and gone at.</summary>
    const double QuakePeak = 0.11, QuakeBase = 0.004;
    const float QuakeFullM = 10f, QuakeGoneM = 35f;

    readonly Dictionary<ushort, double> _quakeT = new();

    void UpdateDemonQuake(double delta)
    {
        if (_demons.Count == 0 && _quakeT.Count == 0) return;
        var fight = DemonData.Fight;
        var swap = Math.Max(0.1f, fight.TransformSwapS);
        var end = Math.Max(swap + 0.1f, fight.TransformS);
        foreach (var (id, d) in _demons)
        {
            var turning = WorldEntities.Entities.TryGetValue(id, out var e) && DemonRules.StateOf(e.State.State) == DemonState.Transforming;
            if (!turning)
            {
                if (_quakeT.Remove(id)) ChurchDecor.Current?.SetTransform(-1f);
                continue;
            }
            _quakeT.TryGetValue(id, out var t);
            t += delta;
            _quakeT[id] = t;
            ChurchDecor.Current?.SetTransform((float)(t / swap));
            if (_me == null || !GodotObject.IsInstanceValid(_me) || !ShotFx.Enabled || GameSettings.Current.ReduceMotion) continue;
            // builds to the swap, then eases to a rumble
            var build = t < swap ? t * t / (swap * swap) : 1.0 - 0.75 * Math.Min(1.0, (t - swap) / (end - swap));
            var dist = _me.Camera.GlobalPosition.DistanceTo(d.View.GlobalPosition);
            var f = Mathf.Clamp((dist - QuakeFullM) / (QuakeGoneM - QuakeFullM), 0f, 1f);
            var near = 1f - f * f * (3f - 2f * f);
            if (near <= 0) continue;
            _shakeAmp = Math.Max(_shakeAmp, (QuakeBase + QuakePeak * build) * near);
        }
        if (_quakeT.Count > _demons.Count) foreach (var id in _quakeT.Keys.Where(k => !_demons.ContainsKey(k)).ToList()) { _quakeT.Remove(id); ChurchDecor.Current?.SetTransform(-1f); }
    }
}
