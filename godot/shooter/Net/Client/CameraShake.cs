using Godot;
using Drycastle.Shooter.Hunts;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Vehicles;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Companions;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Hunts;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Scenery;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.Shooter.Net.Client;

/// <summary>The camera shake of the local player: blasts and roars raise the amplitude, <see cref="Update"/> applies it to the camera once a frame and lets it decay.</summary>
public sealed class CameraShake
{
    readonly ClientContext _ctx;
    double _amp;

    public CameraShake(ClientContext ctx) { _ctx = ctx; }

    /// <summary>Shake from a blast: stronger the closer it is, scaled by its radius (none with reduce motion).</summary>
    public void At(Vector3 at, float radius)
    {
        if (_ctx.Me == null || !ShotFx.Enabled || GameSettings.Current.ReduceMotion) return;
        var d = _ctx.Me.Camera.GlobalPosition.DistanceTo(at);
        var k = Mathf.Clamp(1f - d / (radius * 5f), 0f, 1f);
        if (k <= 0) return;
        Raise(0.02 + 0.2 * k * k);
    }

    /// <summary>Shake of at least this amplitude this frame (a shake already stronger stays).</summary>
    public void Raise(double amp) => _amp = Math.Max(_amp, amp);

    public void Update(double delta)
    {
        if (_ctx.Me == null || !GodotObject.IsInstanceValid(_ctx.Me)) return;
        var cam = _ctx.Me.Camera;
        if (_amp <= 0.0005) { if (cam.HOffset != 0 || cam.VOffset != 0) { cam.HOffset = 0; cam.VOffset = 0; } _amp = 0; return; }
        cam.HOffset = (float)((GD.Randf() * 2 - 1) * _amp);
        cam.VOffset = (float)((GD.Randf() * 2 - 1) * _amp);
        _amp *= Math.Exp(-8.0 * delta); // decays over ~0.4 s at any frame rate (0.88 per frame at 60 fps)
    }
}
