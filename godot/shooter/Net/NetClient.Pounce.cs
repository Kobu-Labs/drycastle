using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The demon's pounce on the client (docs/events/README.md, "Pounce"). The server marks the victim (<see cref="EventKind.DemonPounce"/> wind), flies the demon at them, pins them flat on the floor
/// and holds their body, then lets go. Here: the victim's camera turns to the demon and the mouse look is locked from the wind (they can still move); on the pin the camera drops to the floor
/// looking up at the demon's muzzle (<see cref="DemonView.MuzzleWorld"/>) with a jolt, a tremble, and a red flash and shake on each bite; the weapon and crosshair are hidden and the body is not
/// predicted (the server holds it, like a seat). On the release it rises back to standing height while the red fades. Everyone else sees the victim's character lie on its back under the demon.
/// The demon's head is placed by its view (<see cref="DemonView.PinMuzzleAt"/>) from the point worked out here, the same for everyone. Reduce motion: no shake, no jolt, a softer flash.
/// </summary>
public partial class NetClient
{
    PounceStage? _pStage; // null: no pounce
    ushort _pVictim, _pLieId;
    float _pYaw, _pLie, _pFlash;
    double _pT;
    bool _pRising, _pLookSet;
    float _pRise;
    Transform3D _pFrom, _pLast;
    Vector3 _pLook;

    /// <summary>Pinned under the demon: the server holds the body, so it is not predicted, not moved by input, and has no weapon.</summary>
    bool _pinned => _pStage == PounceStage.Pinned && _pVictim == _myId;

    static float Smooth(float x) { x = Mathf.Clamp(x, 0f, 1f); return x * x * (3 - 2 * x); }

    void OnPounce(DemonPounceMsg m)
    {
        switch (m.Stage)
        {
            case PounceStage.Wind:
                _pStage = PounceStage.Wind; _pVictim = m.Victim; _pT = 0;
                break;
            case PounceStage.Pinned:
                _pStage = PounceStage.Pinned; _pVictim = m.Victim; _pYaw = m.Yaw; _pT = 0; _pLieId = m.Victim;
                if (m.Victim == _myId) BeginPinMine();
                break;
            default: // let go (released) or ended (aborted)
                if (_pStage == null || m.Victim != _pVictim) break;
                var wasPinned = _pStage == PounceStage.Pinned;
                _pStage = null;
                if (m.Victim == _myId && wasPinned) BeginRise(m.Stage == PounceStage.Released);
                break;
        }
    }

    /// <summary>The landing on me: the body is the server's, the camera starts to drop, with a jolt and a flash.</summary>
    void BeginPinMine()
    {
        if (_me == null) return;
        _pending.Clear(); _reconcile = null;
        _pFrom = _me.Camera.GlobalTransform;
        _pRising = false; _pRise = 0;
        _pLookSet = false;
        var calm = GameSettings.Current.ReduceMotion;
        _pFlash = calm ? 0.15f : 0.5f;
        PounceShake(0.16);
    }

    /// <summary>Let go: the camera rises from the floor to standing height and the red fades. After an abort (the demon died, they left) the same, without the fade.</summary>
    void BeginRise(bool released)
    {
        _pRising = true; _pRise = 0;
        _pFlash = released ? _pFlash : 0f;
        if (_me != null) _me.Pitch = 0;
    }

    void PounceShake(double amp)
    {
        if (!ShotFx.Enabled || GameSettings.Current.ReduceMotion) return;
        _shakeAmp = Math.Max(_shakeAmp, amp);
    }

    /// <summary>A bite snapped shut (the demon's view tells): red pulse and a shake spike on the victim's screen. Nobody else's screen changes.</summary>
    void OnPounceBite()
    {
        if (!_pinned) return;
        _pFlash = Math.Max(_pFlash, GameSettings.Current.ReduceMotion ? 0.12f : 0.38f);
        PounceShake(0.05);
    }

    /// <summary>The demon's state is gone (died, the event ended): nobody is marked or held in anyone's view.</summary>
    void PounceReset()
    {
        _pStage = null; _pRising = false; _pLookSet = false; _pFlash = 0;
        if (_me != null && GodotObject.IsInstanceValid(_me)) { _me.LookLocked = false; _me.Camera.Near = 0.05f; }
        _hud?.SetPounceWash(0); _hud?.SetPounceHidesCrosshair(false);
    }

    DemonView? PounceView()
    {
        foreach (var d in _demons.Values) return d.View;
        return null;
    }

    /// <summary>Where the victim's body is (the server's held place, the middle of their lying body), or null when they are not here.</summary>
    Vector3? PounceBase()
    {
        if (_pVictim == _myId) return _me != null && GodotObject.IsInstanceValid(_me) ? _me.GlobalPosition : null;
        return _remotes.TryGetValue(_pVictim, out var r) && GodotObject.IsInstanceValid(r.Node) && r.LastT > 0 ? r.LastPos : null;
    }

    Vector3 PounceHeadDir() => new(MathF.Sin(_pYaw), 0, MathF.Cos(_pYaw));

    /// <summary>The victim's eyes lying on their back: at the head end of the body, <see cref="DemonPounceDef.CameraHeightM"/> off the floor.</summary>
    Vector3 PounceEye(Vector3 body) => new Vector3(body.X, body.Y - 0.02f + DemonData.Pounce.CameraHeightM, body.Z) + PounceHeadDir() * (DemonBrain.LyingHalfM - 0.07f);

    /// <summary>Where the demon's muzzle belongs while it pins: <see cref="DemonPounceDef.FaceDistanceM"/> straight above the victim's eyes. Null unless someone is pinned.</summary>
    Vector3? PounceFacePoint() => _pStage == PounceStage.Pinned && PounceBase() is { } b ? PounceEye(b) + Vector3.Up * DemonData.Pounce.FaceDistanceM : null;

    /// <summary>Every frame: the lock-on, the cut camera's inputs, the screen's red, the body lying down for everyone else.</summary>
    void UpdatePounce(double delta)
    {
        var dt = (float)delta;
        var cfg = DemonData.Pounce;
        var pinnedNow = _pStage == PounceStage.Pinned;
        if (cfg.PinS <= 0 && _pStage == null && _pLie <= 0 && !_pRising) return;
        _pLie = Mathf.MoveToward(_pLie, pinnedNow ? 1f : 0f, dt / Math.Max(0.05f, pinnedNow ? cfg.DropS : cfg.RiseS));
        if (_pStage != null) _pT += delta;
        if (_me == null || !GodotObject.IsInstanceValid(_me)) return;
        _me.CutCamera ??= PounceCamera;
        var view = PounceView();
        var marked = _pStage != null && _pVictim == _myId;
        _me.LookLocked = marked;
        if (marked && _pStage == PounceStage.Wind && view != null) TurnToDemon(view, dt);
        if (_pRising)
        {
            _pRise += dt / Math.Max(0.1f, cfg.RiseS);
            if (_pRise >= 1) _pRising = false;
        }
        var lying = _pinned || _pRising;
        var near = lying ? 0.02f : 0.05f; // the muzzle is a hand away: the near plane must not clip it
        if (!Mathf.IsEqualApprox(_me.Camera.Near, near)) _me.Camera.Near = near;
        _hud?.SetPounceHidesCrosshair(lying);
        if (view != null)
        {
            var m = view.MuzzleWorld;
            _pLook = _pLookSet ? _pLook.Lerp(m, 1f - MathF.Exp(-3.5f * dt)) : m;
            _pLookSet = true;
        }
        var calm = GameSettings.Current.ReduceMotion;
        if (_pinned) PounceShake(0.012); // the tremble
        _pFlash *= MathF.Exp(-dt * (calm ? 4f : 7f));
        var wash = Math.Max(_pinned ? 0.1f : 0f, _pFlash);
        if (_pRising) wash = Math.Max(wash, (calm ? 0.2f : 0.45f) * (1f - Smooth(_pRise)));
        _hud?.SetPounceWash(wash);
    }

    /// <summary>The wind: the view turns to the demon's head in about <see cref="DemonPounceDef.LockS"/>; the mouse is locked, the body moves as before.</summary>
    void TurnToDemon(DemonView view, float dt)
    {
        var eye = _me!.Camera.GlobalPosition;
        var dir = view.MuzzleWorld - eye;
        if (dir.LengthSquared() < 0.01f) return;
        dir = dir.Normalized();
        var yaw = MathF.Atan2(-dir.X, -dir.Z);
        var pitch = Mathf.Clamp(MathF.Asin(Mathf.Clamp(dir.Y, -1f, 1f)), -1.4f, 1.4f);
        var k = 1f - MathF.Exp(-3f / Math.Max(0.05f, DemonData.Pounce.LockS) * dt);
        _me.Yaw = Mathf.LerpAngle(_me.Yaw, yaw, k);
        _me.Pitch = Mathf.Lerp(_me.Pitch, pitch, k);
    }

    /// <summary>The cut camera (<see cref="Player.CutCamera"/>): the drop to the floor and the look at the demon's muzzle while pinned, the rise back to the eye after. Null: the normal eye.</summary>
    Transform3D? PounceCamera(Transform3D eye)
    {
        var cfg = DemonData.Pounce;
        if (_pinned && _me != null && PounceBase() is { } body)
        {
            var k = Smooth((float)_pT / Math.Max(0.05f, cfg.DropS));
            var pos = _pFrom.Origin.Lerp(PounceEye(body), k);
            var target = _pLookSet ? _pLook : PounceEye(body) + Vector3.Up;
            var look = (target - pos).Normalized();
            var basis = Basis.LookingAt(look, PounceHeadDir()); // lying on their back, the top of the view is towards their head
            var q = _pFrom.Basis.GetRotationQuaternion().Slerp(basis.GetRotationQuaternion(), k);
            _pLast = new Transform3D(new Basis(q), pos);
            return _pLast;
        }
        if (_pRising)
        {
            var k = Smooth(_pRise);
            var q = _pLast.Basis.GetRotationQuaternion().Slerp(eye.Basis.GetRotationQuaternion(), k);
            return new Transform3D(new Basis(q), _pLast.Origin.Lerp(eye.Origin, k));
        }
        return null;
    }

    /// <summary>Everyone else's view of the victim: their character lies on its back under the demon, head towards (sin yaw, cos yaw), then gets up. Called after the remote is placed.</summary>
    void PounceLieRemote(ushort id, Remote r, Vector3 at)
    {
        if (id != _pLieId || _pLie <= 0.001f) return;
        var k = Smooth(_pLie);
        var d = new Vector3(MathF.Sin(_pYaw), 0, MathF.Cos(_pYaw));
        var down = new Basis(Vector3.Up, _pYaw) * new Basis(Vector3.Right, Mathf.Pi / 2); // head to +d, face up
        var lie = at - d * (DemonBrain.LyingHalfM + 0.025f) + Vector3.Up * 0.13f; // the feet end, so the body is centred on where it was held; the back's thickness off the floor
        r.Node.Basis = new Basis(r.Node.Basis.GetRotationQuaternion().Slerp(down.GetRotationQuaternion(), k));
        r.Node.GlobalPosition = r.Node.GlobalPosition.Lerp(lie, k);
    }
}
