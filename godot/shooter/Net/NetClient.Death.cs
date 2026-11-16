using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The local player's death screen (#283). The pure half is <see cref="DeathPresentation"/> (Falling, then Lying, and how each kind of death looks); here it is fed a death and a clock and
/// drives the three things that show it: the first-person camera (falls to about 0.2 m above the ground and rolls, then holds until the spawn), the blood overlay and the respawn scrim
/// . The camera goes through one composer (<see cref="ComposedCamera"/>) with the demon's pounce camera: a death while pinned
/// starts from the pounce camera's pose of that frame, so nothing snaps. The own corpse (a server entity, drawn on this client too) is hidden while the camera lies where it fell.
/// </summary>
public partial class NetClient
{
    readonly DeathPresentation _death = new();
    /// <summary>The camera's pose at the first frame of a fall (the pounce camera's, when pinned) and the ground height below it; null until that frame.</summary>
    Transform3D? _deathFrom;
    float _deathTargetY;
    Change<bool> _deathHidesCorpse;

    /// <summary>The death of the local player: choose how it looks from what the player was doing until now, and start it.</summary>
    void BeginDeathView(float respawnDelay)
    {
        var before = (Mode with { Dead = false }).Resolve();
        var underwater = false;
        if (_me != null && GodotObject.IsInstanceValid(_me) && Water.Bodies.Count > 0)
        {
            var eye = _me.Camera.GlobalPosition;
            underwater = Water.DepthAt(new NVec3(eye.X, eye.Y, eye.Z)) > 0f;
        }
        _death.Begin(DeathPresentation.Plan(before, underwater, drowned: false, GameSettings.Current.ReduceMotion, respawnDelay, _deaths));
        _deathFrom = null;
    }

    /// <summary>The spawn: the death screen is over, the camera, the corpse and the overlays go back at once (the HUD hides its scrim itself).</summary>
    void EndDeathView()
    {
        _death.End();
        _deathFrom = null;
    }

    /// <summary>Every frame: the clock, the overlays, the own corpse. Runs before <see cref="UpdatePounce"/>.</summary>
    void UpdateDeath(double delta)
    {
        if (_me != null && GodotObject.IsInstanceValid(_me)) _me.CutCamera ??= ComposedCamera;
        _death.Tick((float)delta);
        var hide = _death.OwnsCamera;
        if (_deathHidesCorpse.Update(hide) || hide) ShowOwnCorpse(!hide);
    }

    void ShowOwnCorpse(bool visible)
    {
        foreach (var (id, v) in _corpses)
        {
            if (v.Giant || !WorldEntities.Entities.TryGetValue(id, out var e) || e.State.Owner != _myId) continue;
            if (v.Node.Visible != visible) v.Node.Visible = visible;
        }
    }

    /// <summary>
    /// The one <see cref="Player.CutCamera"/>: the pounce camera's pose (null when not pounced), and a death's fall on top of it. The fall starts from whatever pose the camera had in its first frame,
    /// so a death during a pounce continues from the pinned view and never snaps back to the eye.
    /// </summary>
    Transform3D? ComposedCamera(Transform3D eye)
    {
        var pounce = PounceCamera(eye);
        return _death.OwnsCamera ? DeathCamera(pounce ?? eye) : pounce;
    }

    Transform3D DeathCamera(Transform3D pose)
    {
        if (_deathFrom is not { } from)
        {
            from = pose;
            _deathFrom = from;
            _deathTargetY = GroundBelow(from.Origin) + DeathPresentation.GroundClearanceM;
        }
        var k = _death.Drop;
        var pos = from.Origin;
        pos.Y = Mathf.Lerp(from.Origin.Y, Math.Min(from.Origin.Y, _deathTargetY), k); // never rises: a pinned camera is already low
        var roll = new Basis(Vector3.Back, _death.Current.RollRadians * k); // about the view axis
        return new Transform3D(from.Basis * roll, pos);
    }

    /// <summary>The height of the ground (or a vehicle's roof) below <paramref name="from"/>: a ray down, so the camera never ends inside the floor. Without a hit, an eye's height lower.</summary>
    float GroundBelow(Vector3 from)
    {
        if (_me == null || !GodotObject.IsInstanceValid(_me) || !_me.IsInsideTree()) return from.Y - 1.5f;
        var q = PhysicsRayQueryParameters3D.Create(from, from + Vector3.Down * 6f);
        q.CollisionMask = CollisionLayers.OnFoot;
        q.Exclude = new Godot.Collections.Array<Rid> { _me.GetRid() };
        var hit = _me.GetWorld3D().DirectSpaceState.IntersectRay(q);
        return hit.Count > 0 ? hit["position"].AsVector3().Y : from.Y - 1.5f;
    }
}
