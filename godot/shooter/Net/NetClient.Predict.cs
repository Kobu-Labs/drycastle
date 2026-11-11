using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// Immediate feedback for the own weapon. The server stays authoritative, but sound, muzzle flash and tracer must
/// not wait a round trip, so the client predicts the trigger pull from the last ammo message, the weapon's
/// fire_interval and switch_time, and a locally counted magazine. Each predicted shot is remembered; the matching
/// server Shot/Launch event (same owner) is then swallowed instead of playing everything twice. Own reload and
/// dry-fire clicks use the same prediction.
/// </summary>
public partial class NetClient
{
    readonly Queue<double> _predShots = new();
    double _pNextFire, _pReadyAt, _pReloadUntil, _pDryAt;
    int _pMag = -1, _pMagAfter, _pSlot;
    double _shakeAmp, _shakeUntil;

    bool PredictionOn => !Bot && ShotFx.Enabled && Weapons != null;

    /// <summary>Called for every ammo message: re-syncs the predicted magazine and starts the switch delay on a slot change.</summary>
    void PredictOnAmmo(AmmoMsg a)
    {
        if (!PredictionOn || a.Slot < 1 || a.Slot > Weapons!.Weapons.Count) return;
        if (a.Slot != _pSlot) { _pSlot = a.Slot; _pReadyAt = Now + Weapons.Weapons[a.Slot - 1].SwitchTime; }
        while (_predShots.Count > 0 && Now - _predShots.Peek() > 0.6) _predShots.Dequeue();
        var inFlight = _predShots.Count(t => Now - t < 0.25); // shots sent but not yet counted by this message
        _pMag = Math.Max(0, a.Slots[a.Slot - 1].Mag - inFlight);
    }

    /// <summary>Predicts this tick's trigger pull / reload key; plays sounds and own-shot effects at once.</summary>
    void PredictFire(MoveInput mi)
    {
        if (!PredictionOn || Dead || _driving || Ammo is not { } a || a.Slot < 1 || a.Slot > Weapons!.Weapons.Count || a.Slot != _wantSlot) return;
        var w = Weapons.Weapons[a.Slot - 1];
        var now = Now;
        if (_pReloadUntil > 0 && now >= _pReloadUntil) { _pMag = _pMagAfter; _pReloadUntil = 0; }
        var reloading = a.ReloadRemaining > 0 || now < _pReloadUntil;
        var (srvMag, reserve) = a.Slots[a.Slot - 1];
        if (_pMag < 0) _pMag = srvMag;
        var ranged = w.Kind != WeaponKind.Melee;

        if (!reloading && ranged && mi.Has(MoveButtons.Reload) && w.MagazineSize > 0 && _pMag < w.MagazineSize && reserve > 0) { StartPredictedReload(w, reserve); return; }
        if (!mi.Has(MoveButtons.Fire)) { if (!w.FullAuto) _pHeld = false; return; }
        var pull = w.FullAuto || !_pHeld;
        _pHeld = true;
        if (!pull || reloading || now < _pReadyAt || now < _pNextFire - 0.0033) return;

        if (ranged && _pMag <= 0)
        {
            // empty: the server starts a reload when it has reserve ammo, otherwise it is a dry click
            if (reserve > 0 && w.MagazineSize > 0) StartPredictedReload(w, reserve);
            else if (now - _pDryAt > 0.45) { _pDryAt = now; Sfx.Play2D(SoundRules.Event("dry", w.Id)); }
            return;
        }
        _pNextFire = now + w.FireInterval;
        if (ranged) _pMag--;
        _predShots.Enqueue(now);
        if (FxDemo != "") GD.Print($"fx-demo: predicted {w.Id} shot, mag {_pMag}");
        OwnShotFx(w);
    }

    bool _pHeld;

    void StartPredictedReload(WeaponDef w, int reserve)
    {
        if (w.ReloadTime <= 0 || Now < _pReloadUntil) return;
        _pReloadUntil = Now + w.ReloadTime;
        _pMagAfter = Math.Min(w.MagazineSize, Math.Max(0, _pMag) + reserve);
        Sfx.Play2D(SoundRules.Event("reload", w.Id));
    }

    /// <summary>Sound, muzzle flash and tracer for the own shot, from the camera aim (spread is not predicted).</summary>
    void OwnShotFx(WeaponDef w)
    {
        if (_me == null) return;
        var melee = w.Kind == WeaponKind.Melee;
        var projectile = w.Kind == WeaponKind.Projectile;
        Sfx.Play2D(SoundRules.Event(projectile ? "launch" : "shot", w.Id));
        if (melee) return;
        var cam = _me.Camera;
        var from = cam.GlobalPosition;
        var dir = -cam.GlobalTransform.Basis.Z;
        var muzzle = _viewmodel?.MuzzlePosition ?? from + dir * 0.8f;
        if (projectile) { ShotFx.Spawn(World, muzzle, muzzle + dir * 0.4f, false, impact: false, flashScale: 0.5f); return; } // flash only; the rocket itself comes from the server
        var q = PhysicsRayQueryParameters3D.Create(from, from + dir * 300f);
        q.Exclude = new Godot.Collections.Array<Rid> { _me.GetRid() };
        var hit = World.GetWorld3D().DirectSpaceState.IntersectRay(q);
        var end = hit.Count > 0 ? hit["position"].AsVector3() : from + dir * 300f;
        ShotFx.Spawn(World, muzzle, end, false, flashScale: 0.4f);
        if (hit.Count > 0) Sfx.Play3D("impact", end);
    }

    /// <summary>True when this server event is the echo of a shot already shown by prediction (consumes one prediction).</summary>
    bool EchoOfPredicted(ushort owner)
    {
        if (owner != _myId || !PredictionOn) return false;
        while (_predShots.Count > 0 && Now - _predShots.Peek() > 1.0) _predShots.Dequeue(); // the server never answered: drop it
        if (_predShots.Count == 0) return false;
        _predShots.Dequeue();
        return true;
    }

    /// <summary>Where a shot of <paramref name="id"/> visibly leaves the weapon; <paramref name="fallback"/> (the eye) when nothing better is known.</summary>
    Vector3 MuzzleOf(ushort id, Vector3 fallback, Vector3 end)
    {
        if (id == _myId) return _viewmodel?.MuzzlePosition ?? fallback;
        if (!_remotes.TryGetValue(id, out var r)) return fallback;
        var chest = r.Node.GlobalPosition + Vector3.Up * 1.3f;
        var dir = (end - fallback).Normalized();
        var fwd = new Vector3(dir.X, 0, dir.Z).Normalized();
        var side = fwd.Cross(Vector3.Up);
        var guess = chest + fwd * 0.7f + side * 0.2f;
        return r.View?.MuzzlePosition is { } m && m.DistanceTo(chest) < 1.3f ? m : guess;
    }

    /// <summary>Camera shake for the local player: stronger the closer the blast, scaled by its radius.</summary>
    void ShakeFor(Vector3 at, float radius)
    {
        if (_me == null || !ShotFx.Enabled) return;
        var d = _me.Camera.GlobalPosition.DistanceTo(at);
        var k = Mathf.Clamp(1f - d / (radius * 5f), 0f, 1f);
        if (k <= 0) return;
        _shakeAmp = Math.Max(_shakeAmp, 0.02 + 0.2 * k * k); _shakeUntil = Now + 0.15 + 0.5 * k;
    }

    void UpdateShake()
    {
        if (_me == null || !GodotObject.IsInstanceValid(_me)) return;
        var cam = _me.Camera;
        if (_shakeAmp <= 0.0005) { if (cam.HOffset != 0 || cam.VOffset != 0) { cam.HOffset = 0; cam.VOffset = 0; } _shakeAmp = 0; return; }
        cam.HOffset = (float)((GD.Randf() * 2 - 1) * _shakeAmp);
        cam.VOffset = (float)((GD.Randf() * 2 - 1) * _shakeAmp);
        _shakeAmp *= 0.88; // decays over ~0.4 s
    }
}

/// <summary>Dev aid: <c>--fx-demo dir</c> fires the bazooka at the ground ahead (t=3 s) and then an AK burst (t=7 s), saving screenshots of the effects into dir.</summary>
public partial class NetClient
{
    public string FxDemo = "";
    double _demoStart;
    static readonly double[] DemoShots = { 3.1, 3.3, 3.6, 7.02, 7.06, 7.1, 7.14, 7.18, 7.22, 7.26, 7.3, 7.34 };
    int _demoNext; bool _demoTp;

    void FxDemoInput(ref MoveInput mi)
    {
        if (FxDemo == "" || _me == null) return;
        if (_demoStart == 0) { _demoStart = Now; System.IO.Directory.CreateDirectory(FxDemo); }
        var t = Now - _demoStart;
        if (!_demoTp && _peer != null)
        {
            _demoTp = true; // open ground, away from the cars that some spawns sit in
            Tx(_peer, Wire.ChanReliable, Wire.Encode(new EventMsg((byte)EventKind.Debug, WeaponCodec.EncodeDebugTeleport(_myId, new NVec3(-78f, 191.2f, 52.9f)))), (int)ENetPacketPeer.FlagReliable);
        }
        _wantSlot = t < 6 ? 3 : 2;
        _me.Pitch = t < 6 ? -0.2f : -0.05f;
        if ((t is > 3 and < 3.1) || (t is > 7 and < 8)) mi.Buttons |= MoveButtons.Fire;
        if (_demoNext < DemoShots.Length && t >= DemoShots[_demoNext]) _ = DemoShot(FxDemo, DemoShots[_demoNext++]);
        if (t > 9) GetTree().Quit();
    }

    async System.Threading.Tasks.Task DemoShot(string dir, double at)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
        var path = System.IO.Path.Combine(dir, $"fx_{at:F2}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"fx-demo: saved {path}");
    }
}
