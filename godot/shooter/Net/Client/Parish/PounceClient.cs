using Godot;
using Drycastle.Shooter.Demon;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Client.OwnPlayer;
using Drycastle.Shooter.Net.Client.Peers;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Demon;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client.Parish;

/// <summary>
/// The demon's pounce on the client (docs/events/README.md, "Pounce"). The server marks the victim (<see cref="EventKind.DemonPounce"/> wind), flies the demon at them, pins them flat on the floor
/// and holds their body, then lets go. Here: the victim's camera turns to the demon and the mouse look is locked from the wind (they can still move); on the pin the camera drops to the floor
/// looking up at the demon's muzzle (<see cref="DemonView.MuzzleWorld"/>) with a jolt, a tremble, and a red flash and shake on each bite; the weapon and crosshair are hidden and the body is not
/// predicted (the server holds it, like a seat). On the release it rises back to standing height while the red fades. Everyone else sees the victim's character lie on its back under the demon.
/// The demon's head is placed by its view (<see cref="DemonView.PinMuzzleAt"/>) from the point worked out here, the same for everyone. Reduce motion: no shake, no jolt, a softer flash.
/// </summary>
internal sealed class PounceClient
{
    readonly ClientContext _ctx;
    readonly OwnPrediction _prediction;
    readonly DemonFile _data;
    readonly Func<DemonView?> _demonView;

    /// <param name="demonView">The demon's view, or null when there is none (the camera turns to its muzzle).</param>
    public PounceClient(ClientContext ctx, OwnPrediction prediction, DemonFile data, Func<DemonView?> demonView)
    {
        _ctx = ctx; _prediction = prediction; _data = data; _demonView = demonView;
    }

    /// <summary>The pounce as the client sees it (ShooterCore): who is marked or pinned, and the stage. This file keeps the clocks, the camera and the screen.</summary>
    readonly PounceView _pv = new();
    float _pLie, _pFlash;
    double _pT; // seconds since the wind or the pin began
    bool _pLookSet;
    float _pRise;
    Transform3D _pFrom, _pLast;
    Vector3 _pLook;

    /// <summary>Pinned under the demon: the server holds the body, so it is not predicted, not moved by input, and has no weapon.</summary>
    bool _pinned => _pv.PinnedMe(_ctx.MyId);

    /// <summary>The camera is rising from the floor after a pin of mine.</summary>
    bool _pRising => _pv.Rising;

    /// <summary>Pinned under the demon (mine): the view's flag for the mode.</summary>
    public bool Pinned => _pinned;

    /// <summary>Getting up from a pin (mine).</summary>
    public bool Rising => _pRising;

    /// <summary>The local player is marked by the demon's wind (the world state can come before the pin event).</summary>
    public bool MarkedMe => _pv.MarkedMe(_ctx.MyId);

    static float Smooth(float x) { x = Mathf.Clamp(x, 0f, 1f); return x * x * (3 - 2 * x); }

    public void OnMessage(DemonPounceMsg m)
    {
        if (m.Stage is PounceStage.Wind or PounceStage.Pinned) _pT = 0;
        switch (_pv.OnMessage(m, _ctx.MyId))
        {
            case PounceViewEffect.PinnedMine: BeginPinMine(); break;
            case PounceViewEffect.RiseAfterRelease: BeginRise(true); break;
            case PounceViewEffect.RiseAfterAbort: BeginRise(false); break;
            case PounceViewEffect.PinSuperseded: EndPinMineAtOnce(); break;
        }
    }

    /// <summary>My pin was replaced by another pounce's message (the server does not do this): no rise to play, the screen and the pitch go back at once.</summary>
    void EndPinMineAtOnce()
    {
        _pFlash = 0f;
        if (_ctx.Me != null) _ctx.Me.Pitch = 0;
    }

    /// <summary>The landing on me: the body is the server's, the camera starts to drop, with a jolt and a flash.</summary>
    void BeginPinMine()
    {
        if (_ctx.Me == null) return;
        _prediction.Drop();
        _pFrom = _ctx.Me.Camera.GlobalTransform;
        _pRise = 0;
        _pLookSet = false;
        var calm = GameSettings.Current.ReduceMotion;
        _pFlash = calm ? 0.15f : 0.5f;
        PounceShake(0.16);
    }

    /// <summary>Let go: the camera rises from the floor to standing height and the red fades. After an abort (the demon died, they left) the same, without the fade.</summary>
    void BeginRise(bool released)
    {
        _pRise = 0;
        _pFlash = released ? _pFlash : 0f;
        if (_ctx.Me != null) _ctx.Me.Pitch = 0;
    }

    void PounceShake(double amp)
    {
        if (!ShotFx.Enabled || GameSettings.Current.ReduceMotion) return;
        _ctx.Shake.Raise(amp);
    }

    /// <summary>A bite snapped shut (the demon's view tells): red pulse and a shake spike on the victim's screen. Nobody else's screen changes.</summary>
    public void OnBite()
    {
        if (!_pinned) return;
        _pFlash = Math.Max(_pFlash, GameSettings.Current.ReduceMotion ? 0.12f : 0.38f);
        PounceShake(0.05);
    }

    /// <summary>The demon's state is gone (died, the event ended): nobody is marked or held in anyone's view.</summary>
    public void Reset()
    {
        _pv.Reset(); _pLookSet = false; _pFlash = 0;
        if (_ctx.Me != null && GodotObject.IsInstanceValid(_ctx.Me)) { _ctx.Me.LookLocked = false; _ctx.Me.Camera.Near = 0.05f; }
        _ctx.Hud?.SetPounceWash(0); _ctx.Hud?.SetPounceHidesCrosshair(false);
    }

    /// <summary>Where the victim's body is (the server's held place, the middle of their lying body), or null when they are not here.</summary>
    Vector3? PounceBase()
    {
        if (_pv.Victim == _ctx.MyId) return _ctx.Me != null && GodotObject.IsInstanceValid(_ctx.Me) ? _ctx.Me.GlobalPosition : null;
        return _ctx.Remotes.TryGetValue(_pv.Victim, out var r) && GodotObject.IsInstanceValid(r.Node) && r.LastT > 0 ? r.LastPos : null;
    }

    Vector3 PounceHeadDir() => new(MathF.Sin(_pv.Yaw), 0, MathF.Cos(_pv.Yaw));

    /// <summary>The victim's eyes lying on their back: at the head end of the body, <see cref="DemonPounceDef.CameraHeightM"/> off the floor.</summary>
    Vector3 PounceEye(Vector3 body) => new Vector3(body.X, body.Y - 0.02f + _data.Pounce.CameraHeightM, body.Z) + PounceHeadDir() * (DemonBrain.LyingHalfM - 0.07f);

    /// <summary>Where the demon's muzzle belongs while it pins: <see cref="DemonPounceDef.FaceDistanceM"/> straight above the victim's eyes. Null unless someone is pinned.</summary>
    public Vector3? FacePoint() => _pv.Pinned && PounceBase() is { } b ? PounceEye(b) + Vector3.Up * _data.Pounce.FaceDistanceM : null;

    /// <summary>Every frame: the lock-on, the cut camera's inputs, the screen's red, the body lying down for everyone else.</summary>
    public void Update(double delta)
    {
        var dt = (float)delta;
        var cfg = _data.Pounce;
        var pinnedNow = _pv.Pinned;
        if (cfg.PinS <= 0 && !_pv.Marked && _pLie <= 0 && !_pv.Rising) return;
        _pLie = Mathf.MoveToward(_pLie, pinnedNow ? 1f : 0f, dt / Math.Max(0.05f, pinnedNow ? cfg.DropS : cfg.RiseS));
        if (_pv.Marked) _pT += delta;
        if (_ctx.Me == null || !GodotObject.IsInstanceValid(_ctx.Me)) return;
        var view = _demonView();
        var marked = _pv.MarkedMe(_ctx.MyId);
        _ctx.Me.LookLocked = marked;
        if (marked && _pv.Stage == PounceViewStage.Wind && view != null) TurnToDemon(view, dt);
        if (_pv.Rising)
        {
            _pRise += dt / Math.Max(0.1f, cfg.RiseS);
            if (_pRise >= 1) _pv.RiseDone();
        }
        var lying = _pinned || _pRising;
        var near = lying ? 0.02f : 0.05f; // the muzzle is a hand away: the near plane must not clip it
        if (!Mathf.IsEqualApprox(_ctx.Me.Camera.Near, near)) _ctx.Me.Camera.Near = near;
        _ctx.Hud?.SetPounceHidesCrosshair(lying);
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
        _ctx.Hud?.SetPounceWash(wash);
    }

    /// <summary>The wind: the view turns to the demon's head in about <see cref="DemonPounceDef.LockS"/>; the mouse is locked, the body moves as before.</summary>
    void TurnToDemon(DemonView view, float dt)
    {
        var eye = _ctx.Me!.Camera.GlobalPosition;
        var dir = view.MuzzleWorld - eye;
        if (dir.LengthSquared() < 0.01f) return;
        dir = dir.Normalized();
        var yaw = MathF.Atan2(-dir.X, -dir.Z);
        var pitch = Mathf.Clamp(MathF.Asin(Mathf.Clamp(dir.Y, -1f, 1f)), -1.4f, 1.4f);
        var k = 1f - MathF.Exp(-3f / Math.Max(0.05f, _data.Pounce.LockS) * dt);
        _ctx.Me.Yaw = Mathf.LerpAngle(_ctx.Me.Yaw, yaw, k);
        _ctx.Me.Pitch = Mathf.Lerp(_ctx.Me.Pitch, pitch, k);
    }

    /// <summary>The pounce's part of the cut camera (<see cref="ComposedCamera"/> is the hook, shared with the death's fall): the drop to the floor and the look at the demon's muzzle while pinned, the rise back to the eye after. Null: the normal eye.</summary>
    public Transform3D? Camera(Transform3D eye)
    {
        var cfg = _data.Pounce;
        if (_pinned && _ctx.Me != null && PounceBase() is { } body)
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
    public void LieRemote(ushort id, RemotePlayer r, Vector3 at)
    {
        if (id != _pv.LieVictim || _pLie <= 0.001f) return;
        var k = Smooth(_pLie);
        var d = PounceHeadDir();
        var down = new Basis(Vector3.Up, _pv.Yaw) * new Basis(Vector3.Right, Mathf.Pi / 2); // head to +d, face up
        var lie = at - d * (DemonBrain.LyingHalfM + 0.025f) + Vector3.Up * 0.13f; // the feet end, so the body is centred on where it was held; the back's thickness off the floor
        r.Node.Basis = new Basis(r.Node.Basis.GetRotationQuaternion().Slerp(down.GetRotationQuaternion(), k));
        r.Node.GlobalPosition = r.Node.GlobalPosition.Lerp(lie, k);
    }
}
