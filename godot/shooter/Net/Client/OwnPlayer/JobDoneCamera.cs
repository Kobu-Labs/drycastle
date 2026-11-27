using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Colliders;

namespace Drycastle.Shooter.Net.Client.OwnPlayer;

/// <summary>
/// The camera's pull-back when a Cartel Job succeeds (docs/cartel.md "Job done"): for the banner's seconds (<see cref="JobDone"/>) the eye backs off from the body and rises, as far as <see cref="JobDone.Pull"/>
/// says (out, hold, and back), and the own figure is drawn so the player sees themselves (<see cref="OwnFigure.Seen"/>; the weapon in the hand is hidden meanwhile). Only the camera's place changes: the look,
/// the controls and the aim stay the player's (the crosshair is where the eye would aim, so the pull-back is for looking, not for shooting). On foot only, never with reduce motion, never while dead (the death camera
/// comes first). It is a part of the one camera composer (<see cref="DeathView"/>): a wall behind the player shortens the pull so the camera is never inside it.
/// </summary>
internal sealed class JobDoneCamera
{
    /// <summary>How far back and up the eye goes at full pull, metres.</summary>
    const float BackM = 4.5f, UpM = 1.8f;
    /// <summary>Clearance kept from a wall, metres.</summary>
    const float WallM = 0.3f;

    readonly ClientContext _ctx;
    readonly JobDone _done;

    public JobDoneCamera(ClientContext ctx, JobDone done) { _ctx = ctx; _done = done; }

    /// <summary>The own figure is to be drawn and the viewmodel hidden: the pull-back is out.</summary>
    public bool Active => _done.Pull > 0.01f && Allowed;

    bool Allowed => !GameSettings.Current.ReduceMotion && !_ctx.Dead && _ctx.View.Mode.OnFoot && _ctx.Me != null && GodotObject.IsInstanceValid(_ctx.Me);

    /// <summary>The camera hook: the eye's transform backed off and raised, or null (the eye).</summary>
    public Transform3D? Camera(Transform3D eye)
    {
        if (!Active || _ctx.Me is not { } me || !me.IsInsideTree()) return null;
        var offset = eye.Basis.Z * BackM + Vector3.Up * UpM; // the view looks along -Z: +Z is behind it
        var q = PhysicsRayQueryParameters3D.Create(eye.Origin, eye.Origin + offset + offset.Normalized() * WallM);
        q.CollisionMask = CollisionLayers.OnFoot;
        q.Exclude = new Godot.Collections.Array<Rid> { me.GetRid() };
        var hit = me.GetWorld3D().DirectSpaceState.IntersectRay(q);
        var room = hit.Count == 0 ? 1f : Mathf.Clamp((eye.Origin.DistanceTo(hit["position"].AsVector3()) - WallM) / offset.Length(), 0f, 1f);
        return new Transform3D(eye.Basis, eye.Origin + offset * (room * _done.Pull));
    }
}
