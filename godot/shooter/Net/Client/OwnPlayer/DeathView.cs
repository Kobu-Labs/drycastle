using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Client.Gear;
using Drycastle.Shooter.Net.Client.Parish;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Match;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client.OwnPlayer;

/// <summary>
/// The local player's death screen (#283). The pure half is <see cref="DeathPresentation"/> (Buckling, Falling, then Lying, and how each kind of death looks: it falls forward, back, left or right); here it is fed a death and a clock and
/// drives the three things that show it: the first-person camera (the knees give way, then it goes over to a side and lands about 0.2 m above the ground with a bounce, and holds until the spawn), the blood overlay and the respawn scrim
/// (<see cref="Hud.SetBlood"/>, <see cref="Hud.SetRespawnScrim"/>). The camera goes through one composer (<see cref="ComposedCamera"/>) with the demon's pounce camera: a death while pinned
/// starts from the pounce camera's pose of that frame, so nothing snaps. The own corpse (a server entity, drawn on this client too) is hidden while the camera lies where it fell.
/// </summary>
internal sealed class DeathView
{
    readonly ClientContext _ctx;
    readonly PounceClient _pounce;
    readonly CorpsesClient _corpses;
    /// <summary>How many times the local player died on this connection (the blood on the screen is a different one each time).</summary>
    int _count;

    public DeathView(ClientContext ctx, PounceClient pounce, CorpsesClient corpses) { _ctx = ctx; _pounce = pounce; _corpses = corpses; }

    readonly DeathPresentation _death = new();
    /// <summary>The camera's pose at the first frame of a fall (the pounce camera's, when pinned) and the ground height below it; null until that frame.</summary>
    Transform3D? _deathFrom;
    float _deathTargetY, _deathPushScale = 1f;
    Vector3 _deathForward;
    Change<bool> _deathHidesCorpse;
    /// <summary>A thrown death (#416): where the camera looks now, turning towards the own corpse; null until the first frame.</summary>
    Quaternion? _deathLook;

    /// <summary>The death of the local player: choose how it looks from what the player was doing until now, and start it.</summary>
    public void Begin(float respawnDelay, DeathFlight flight = DeathFlight.None)
    {
        _count++;
        var before = (_ctx.View.Mode with { Dead = false }).Resolve();
        var underwater = false;
        if (_ctx.Me != null && GodotObject.IsInstanceValid(_ctx.Me) && _ctx.Water.Bodies.Count > 0)
        {
            var eye = _ctx.Me.Camera.GlobalPosition;
            underwater = _ctx.Water.DepthAt(new NVec3(eye.X, eye.Y, eye.Z)) > 0f;
        }
        _death.Begin(DeathPresentation.Plan(before, underwater, drowned: false, GameSettings.Current.ReduceMotion, respawnDelay, _count, flight));
        _deathFrom = null;
        _deathLook = null;
    }

    /// <summary>The spawn: the death screen is over, the camera, the corpse and the overlays go back at once (the HUD hides its scrim itself).</summary>
    public void End()
    {
        _death.End();
        _deathFrom = null;
        _deathLook = null;
        _ctx.Hud?.SetBlood(0);
    }

    /// <summary>Every frame: the clock, the overlays, the own corpse. Runs before <see cref="UpdatePounce"/>.</summary>
    public void Update(double delta)
    {
        if (_ctx.Me != null && GodotObject.IsInstanceValid(_ctx.Me)) _ctx.Me.CutCamera ??= ComposedCamera;
        _death.Tick((float)delta);
        if (_death.Active)
        {
            _ctx.Hud?.SetBlood(_death.BloodNow, _count);
            _ctx.Hud?.SetRespawnScrim(_death.ScrimNow);
        }
        var hide = _death.HidesOwnCorpse;
        if (_deathHidesCorpse.Update(hide) || hide) _corpses.ShowOwn(!hide);
    }

    /// <summary>
    /// The one <see cref="Player.CutCamera"/>: the pounce camera's pose (null when not pounced), and a death's fall on top of it. The fall starts from whatever pose the camera had in its first frame,
    /// so a death during a pounce continues from the pinned view and never snaps back to the eye.
    /// </summary>
    Transform3D? ComposedCamera(Transform3D eye)
    {
        var pounce = _pounce.Camera(eye);
        return _death.OwnsCamera ? DeathCamera(pounce ?? eye) : pounce;
    }

    Transform3D DeathCamera(Transform3D pose)
    {
        if (_death.Current.Style == DeathStyle.Thrown) return ThrownCamera(pose);
        if (_deathFrom is not { } from)
        {
            from = pose;
            _deathFrom = from;
            _deathTargetY = GroundBelow(from.Origin) + DeathPresentation.GroundClearanceM;
            _deathForward = new Vector3(-from.Basis.Z.X, 0, -from.Basis.Z.Z).Normalized();
            _deathPushScale = PushRoom(from.Origin, _deathForward, _death.Current.Kind);
        }
        var p = _death.Pose;
        var pos = from.Origin + _deathForward * (p.PushM * _deathPushScale);
        pos.Y = Mathf.Lerp(from.Origin.Y, Math.Min(from.Origin.Y, _deathTargetY), p.Drop) + p.BounceM; // never rises: a pinned camera is already low
        var turn = new Basis(Vector3.Up, p.Yaw) * new Basis(Vector3.Right, p.Pitch) * new Basis(Vector3.Back, p.Roll); // about the view's own axes
        return new Transform3D(from.Basis * turn, pos);
    }

    /// <summary>
    /// A thrown death (#416): the camera stays where the eye was in the first frame, backed off a little (<see cref="DeathPresentation.ThrownBackM"/>, shortened against a wall) and
    /// up, and only turns, smoothly, to keep the own corpse in the middle. Before the corpse is known it keeps looking the way the eye did.
    /// </summary>
    Transform3D ThrownCamera(Transform3D pose)
    {
        if (_deathFrom is not { } from)
        {
            from = pose;
            var back = from.Basis.Z; // the view looks along -Z: +Z is behind it
            var room = 1f;
            if (_ctx.Me != null && GodotObject.IsInstanceValid(_ctx.Me) && _ctx.Me.IsInsideTree())
            {
                var q = PhysicsRayQueryParameters3D.Create(from.Origin, from.Origin + back * (DeathPresentation.ThrownBackM + 0.3f));
                q.CollisionMask = CollisionLayers.OnFoot;
                q.Exclude = new Godot.Collections.Array<Rid> { _ctx.Me.GetRid() };
                var hit = _ctx.Me.GetWorld3D().DirectSpaceState.IntersectRay(q);
                if (hit.Count > 0) room = Mathf.Clamp((from.Origin.DistanceTo(hit["position"].AsVector3()) - 0.3f) / DeathPresentation.ThrownBackM, 0f, 1f);
            }
            from.Origin += back * (DeathPresentation.ThrownBackM * room) + Vector3.Up * DeathPresentation.ThrownUpM * room;
            _deathFrom = from;
            _deathLook = from.Basis.GetRotationQuaternion();
        }
        var look = _deathLook ?? from.Basis.GetRotationQuaternion();
        if (_corpses.OwnBodyAt() is { } body)
        {
            var dir = body - from.Origin;
            var yawNow = look.GetEuler(EulerOrder.Yxz).Y;
            var (yaw, pitch) = DeathPresentation.ThrownAim(dir.X, dir.Y, dir.Z, yawNow); // never LookingAt: a body straight above is collinear with Up
            var aim = Quaternion.FromEuler(new Vector3(pitch, yaw, 0));
            look = look.Slerp(aim, DeathPresentation.PanBlend((float)_ctx.Root.GetProcessDeltaTime(), look.AngleTo(aim)));
        }
        _deathLook = look;
        return new Transform3D(new Basis(look), from.Origin);
    }

    /// <summary>How much of a fall's travel (forward or back) fits before a wall: 0..1 (a body that falls against a wall does not push the view into it).</summary>
    float PushRoom(Vector3 from, Vector3 forward, FallKind kind)
    {
        if (_ctx.Me == null || !GodotObject.IsInstanceValid(_ctx.Me) || !_ctx.Me.IsInsideTree()) return 1f;
        var dir = kind == FallKind.Backward ? -forward : forward;
        var q = PhysicsRayQueryParameters3D.Create(from, from + dir * (DeathPresentation.MaxPushM + 0.3f));
        q.CollisionMask = CollisionLayers.OnFoot;
        q.Exclude = new Godot.Collections.Array<Rid> { _ctx.Me.GetRid() };
        var hit = _ctx.Me.GetWorld3D().DirectSpaceState.IntersectRay(q);
        if (hit.Count == 0) return 1f;
        return Mathf.Clamp((from.DistanceTo(hit["position"].AsVector3()) - 0.3f) / DeathPresentation.MaxPushM, 0f, 1f);
    }

    /// <summary>The height of the ground (or a vehicle's roof) below <paramref name="from"/>: a ray down, so the camera never ends inside the floor. Without a hit, an eye's height lower.</summary>
    float GroundBelow(Vector3 from)
    {
        if (_ctx.Me == null || !GodotObject.IsInstanceValid(_ctx.Me) || !_ctx.Me.IsInsideTree()) return from.Y - 1.5f;
        var q = PhysicsRayQueryParameters3D.Create(from, from + Vector3.Down * 6f);
        q.CollisionMask = CollisionLayers.OnFoot;
        q.Exclude = new Godot.Collections.Array<Rid> { _ctx.Me.GetRid() };
        var hit = _ctx.Me.GetWorld3D().DirectSpaceState.IntersectRay(q);
        return hit.Count > 0 ? hit["position"].AsVector3().Y : from.Y - 1.5f;
    }
}
