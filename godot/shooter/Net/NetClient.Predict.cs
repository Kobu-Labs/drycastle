using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// Immediate feedback for the own weapon. The server stays authoritative, but sound, muzzle flash and tracer must
/// not wait a round trip, so the client predicts the trigger pull from the last ammo message, the weapon's
/// fire_interval, and a locally counted magazine. Each predicted shot is remembered; the matching
/// server Shot/Launch event (same owner) is then swallowed instead of playing everything twice. Own reload and
/// dry-fire clicks use the same prediction.
/// </summary>
public partial class NetClient
{
    readonly Queue<double> _predShots = new();
    double _pNextFire, _pReloadUntil, _pDryAt;
    int _pMag = -1, _pMagAfter, _pSlot;
    double _shakeAmp, _shakeUntil;
    RecoilState _recoil = new(0);
    BreathState _breath = new(0);

    bool PredictionOn => !Bot && ShotFx.Enabled && Weapons != null;

    /// <summary>Called for every ammo message: re-syncs the predicted magazine and starts the switch delay on a slot change.</summary>
    void PredictOnAmmo(AmmoMsg a)
    {
        if (!PredictionOn || a.Slot < 1 || a.Slot > Weapons!.Weapons.Count) return;
        if (a.Slot != _pSlot) _pSlot = a.Slot;
        while (_predShots.Count > 0 && Now - _predShots.Peek() > 0.6) _predShots.Dequeue();
        var inFlight = _predShots.Count(t => Now - t < 0.25); // shots sent but not yet counted by this message
        _pMag = Math.Max(0, a.Slots[a.Slot - 1].Mag - inFlight);
    }

    /// <summary>Recovers the recoil offset for this tick and shows it on the camera (the server fires with the same offset).</summary>
    void TickRecoil(MoveInput mi)
    {
        if (_me == null || CurrentWeapon is not { } w) return;
        _recoil.Tick(Now, mi.Has(MoveButtons.Fire), w.Recoil);
        _me.AimKick = new Vector2(_recoil.PitchRad, _recoil.YawRad);
    }

    /// <summary>Advances the sniper breath (the server runs the same state from the same input) and shows the cosmetic sway on the camera.</summary>
    void TickBreath(MoveInput mi)
    {
        if (_me == null || CurrentWeapon is not { } w) return;
        var scoped = mi.Has(MoveButtons.Aim) && w.ZoomFov > 0;
        _breath.Tick(Now, scoped, mi.Has(MoveButtons.Sprint), w, Perks.HoldBreathTime);
        var v = BreathState.VisualSway(Now, _breath.SwayDegrees(w));
        _me.ViewSway = GameSettings.Current.ReduceMotion ? Vector2.Zero : new Vector2(v.X, v.Y);
    }

    /// <summary>The breath meter while scoped with a breath hold (the HUD reads it each frame).</summary>
    (bool Show, float Meter, BreathState.BreathPhase Phase) BreathView()
    {
        if (_me is not { ZoomFov: > 0 } || CurrentWeapon is not { } w || BreathState.BudgetSeconds(w, Perks.HoldBreathTime) <= 0)
            return (false, 0f, BreathState.BreathPhase.Ready);
        return (true, _breath.Meter, _breath.Phase);
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
        if (!pull || reloading || now < _pNextFire - 0.0033) return;

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
        if (w.Kind == WeaponKind.Hitscan && _me != null)
        {
            // same offset step as the server's shot; the viewmodel kicks by the same (ADS / crouch scaled) climb
            var mult = RecoilState.Multiplier(w.Recoil, mi.Has(MoveButtons.Aim), _me.State.Crouching);
            _recoil.Shot(w.Recoil, mult);
            _breath.AimError(_breath.SwayDegrees(w)); // the server's per-shot aim error; kept in step (the tracer follows the camera, like recoil)
            _viewmodel?.Kick(w.Recoil.VerticalPerShot * mult);
        }
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
        var muzzle = _me.ZoomFov > 0 ? from + dir * 0.6f : _viewmodel?.MuzzlePosition ?? from + dir * 0.8f; // scoped: the streak leaves the crosshair, the gun is hidden
        _viewmodel?.Flash(projectile ? 0.5f : 0.4f);
        if (projectile) return; // the rocket itself comes from the server
        var q = PhysicsRayQueryParameters3D.Create(from, from + dir * 300f);
        q.Exclude = new Godot.Collections.Array<Rid> { _me.GetRid() };
        var hit = World.GetWorld3D().DirectSpaceState.IntersectRay(q);
        var end = hit.Count > 0 ? hit["position"].AsVector3() : from + dir * 300f;
        ShotFx.Spawn(World, muzzle, end, false, true, hit.Count > 0 ? hit["normal"].AsVector3() : null);
        if (hit.Count > 0) { Sfx.Play3D("impact", end); if (hit["collider"].AsGodotObject() is CollisionObject3D { CollisionLayer: var l } && (l & 1) != 0) BulletHoles.Place(World, end, hit["normal"].AsVector3()); }
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
        if (id == _myId) return _me is { ZoomFov: > 0 } ? fallback + (end - fallback).Normalized() * 0.6f : _viewmodel?.MuzzlePosition ?? fallback;
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
    static readonly double[] DemoShots = { 3.1, 3.3, 3.6, 7.02, 7.06, 7.1, 7.14, 7.18, 7.22, 7.26, 7.3, 7.34, 12.7, 13.0, 13.4 };
    int _demoNext; bool _demoTp;

    // vehicle demo (<c>--fx-demo vehicle:dir</c> on a --blast-smoke server): stands 12 m in front of car 0 and hurts it through the stages and the blast, saving screenshots
    static readonly (double T, float Damage)[] VehicleDemoHits = { (1.5, 120), (5.0, 120), (9.0, 40), (12.0, 100) };
    static readonly double[] VehicleDemoShots = { 4.0, 8.0, 9.5, 10.5, 12.05, 12.15, 12.3, 12.6, 13.5, 16.0 };
    int _vdHit, _vdShot;

    void FxVehicleDemoInput(ref MoveInput mi)
    {
        if (_me == null || _peer == null || _cars.Count == 0) return;
        var dir = FxDemo["vehicle:".Length..];
        if (_demoStart == 0) { _demoStart = Now; System.IO.Directory.CreateDirectory(dir); }
        var t = Now - _demoStart;
        var c = new NVec3(NetServer.ArenaX, 0, NetServer.ArenaZ);
        if (!_demoTp) { _demoTp = true; Tx(_peer, Wire.ChanReliable, Wire.Encode(new EventMsg((byte)EventKind.Debug, WeaponCodec.EncodeDebugTeleport(_myId, c + new NVec3(2, 0, -42)))), (int)ENetPacketPeer.FlagReliable); }
        mi.Yaw = MathF.PI; _me.Pitch = -0.08f;
        if (_vdHit < VehicleDemoHits.Length && t >= VehicleDemoHits[_vdHit].T)
        {
            Tx(_peer, Wire.ChanReliable, Wire.Encode(new EventMsg((byte)EventKind.Debug, WeaponCodec.EncodeDebugVehicleDamage(VehicleRecord.FirstId, VehicleDemoHits[_vdHit++].Damage))), (int)ENetPacketPeer.FlagReliable);
            _conn.Flush();
        }
        if (_vdShot < VehicleDemoShots.Length && t >= VehicleDemoShots[_vdShot]) _ = DemoShot(dir, VehicleDemoShots[_vdShot++]);
        if (t > 17) GetTree().Quit();
    }

    // drive demo (<c>--fx-demo drive:dir</c> on a real-map server with --debug-cmds): stands 4 m from the nearest vehicle, looks at it, presses E, drives ahead with a turn, brakes and exits; screenshots + positions in the log
    static readonly double[] DriveShots = { 3.5, 6.0, 8.0, 10.5, 13.0, 16.0 };
    int _ddShot; ushort _ddCar; NVec3 _ddStart;

    void FxDriveDemoInput(ref MoveInput mi)
    {
        if (_me == null || _peer == null || _cars.Count == 0) return;
        var dir = FxDemo["drive:".Length..];
        if (_demoStart == 0) { _demoStart = Now; System.IO.Directory.CreateDirectory(dir); }
        var t = Now - _demoStart;
        if (!_demoTp)
        {
            if (t < 1.5) return; // let a few snapshots in
            _demoTp = true;
            var me0 = _me.State.Position;
            var pick = _cars.OrderBy(c => NVec3.Distance(c.Value.Pos, me0)).First();
            _ddCar = pick.Key; _ddStart = pick.Value.Pos;
            var at = pick.Value.Pos + new NVec3(0, 0.3f, 4.5f); // behind the car's rear is +Z for yaw 0; stand there, look at it
            var yaw = pick.Value.Yaw;
            var side = new NVec3(MathF.Sin(yaw), 0, MathF.Cos(yaw)) * 4.5f; // behind the car (it faces -Z at yaw 0)
            at = pick.Value.Pos + side + new NVec3(0, 0.3f, 0);
            _ddYaw = yaw;
            GD.Print($"drive-demo: nearest vehicle {pick.Key} kind {pick.Value.Kind} at {pick.Value.Pos}, teleport to {at}, distance before {NVec3.Distance(pick.Value.Pos, me0):F0} m");
            Tx(_peer, Wire.ChanReliable, Wire.Encode(new EventMsg((byte)EventKind.Debug, WeaponCodec.EncodeDebugTeleport(_myId, at))), (int)ENetPacketPeer.FlagReliable);
            _conn.Flush();
        }
        if (!_driving) mi.Yaw = _ddYaw;
        if (!_driving) _me.Pitch = -0.1f;
        if (_ddShot < DriveShots.Length && t >= DriveShots[_ddShot]) _ = DemoShot(dir, DriveShots[_ddShot++]);
        var pos = _cars.TryGetValue(_ddCar, out var cv) ? cv.Pos : _ddStart;
        if (t is > 4.5 and < 4.6) { mi.Buttons |= MoveButtons.Use; GD.Print($"drive-demo: E pressed, hint='{Strings.F("hud.vehicle.enter", InputBindings.Name("interact"))}' driving={_driving}"); }
        if (t > 5 && t < 12) { mi.MoveY = 1; mi.MoveX = t > 8 && t < 10 ? 0.6f : 0f; }
        if (t is > 12 and < 13.4) mi.Buttons |= MoveButtons.Jump; // handbrake
        if (t is > 14 and < 14.1) mi.Buttons |= MoveButtons.Use;  // exit
        if (t > 6 && (int)(t * 2) != _ddLog) { _ddLog = (int)(t * 2); GD.Print($"drive-demo: t={t:F1} driving={_driving} car {pos} moved {NVec3.Distance(pos, _ddStart):F1} m kmh {(_cars.TryGetValue(_ddCar, out var c2) ? c2.Kmh : 0):F0}"); }
        if (t > 16.5) GetTree().Quit();
    }
    float _ddYaw; int _ddLog;

    void FxDemoInput(ref MoveInput mi)
    {
        if (FxDemo.StartsWith("drive:")) { FxDriveDemoInput(ref mi); return; }
        if (FxDemo.StartsWith("vehicle:")) { FxVehicleDemoInput(ref mi); return; }
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
        if (t > 7) { mi.MoveX = t < 7.2 ? 1 : -1; mi.Yaw += (float)(t - 7) * 0.8f; } // strafe + turn: the flash must stay on the barrel
        if ((t is > 3 and < 3.1) || (t is > 7 and < 8)) mi.Buttons |= MoveButtons.Fire;
        if (_demoNext < DemoShots.Length && t >= DemoShots[_demoNext]) _ = DemoShot(FxDemo, DemoShots[_demoNext++]);
        // wall phase: face the nearest wall found by horizontal rays and fire an AK burst at it (bullet holes)
        var wallSpots = new[] { new NVec3(-1f, 192f, 14.9f), new NVec3(-1f, 192f, -14.9f), new NVec3(-1f, 192f, 0f) };
        for (var k = 0; k < wallSpots.Length && _wallYaw == null; k++)
        {
            if (_wallStep == 2 * k && t > 8 + 1.7 * k && _peer != null)
            {
                _wallStep++; GD.Print($"fx-demo: wall spot {k}");
                Tx(_peer, Wire.ChanReliable, Wire.Encode(new EventMsg((byte)EventKind.Debug, WeaponCodec.EncodeDebugTeleport(_myId, wallSpots[k]))), (int)ENetPacketPeer.FlagReliable);
            }
            else if (_wallStep == 2 * k + 1 && t > 9.2 + 1.7 * k)
            {
                _wallStep++; var best = 1e9f; var from = _me.Camera.GlobalPosition;
                for (var i = 0; i < 24; i++)
                {
                    var yaw = i * Mathf.Tau / 24;
                    var d = new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw));
                    // a real wall: rays at three heights hit the same vertical plane
                    var ok = true; var dd = 0f;
                    foreach (var dy in new[] { -0.8f, 0f, 1.2f })
                    {
                        var hit = World.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from + Vector3.Up * dy, from + Vector3.Up * dy + d * 25f, 1));
                        if (hit.Count == 0 || Mathf.Abs(hit["normal"].AsVector3().Y) > 0.15f) { ok = false; break; }
                        var hd = from.DistanceTo(hit["position"].AsVector3());
                        if (dd != 0 && Mathf.Abs(hd - dd) > 0.6f) { ok = false; break; }
                        dd = hd;
                    }
                    if (ok && dd > 3f && dd < best) { best = dd; _wallYaw = yaw; }
                }
                GD.Print($"fx-demo: wall yaw {_wallYaw} dist {best} at {from}");
            }
        }
        if (t > 8 && _wallYaw is { } wy)
        {
            mi.Yaw = wy; _me.Pitch = 0;
            var o = _me.Camera.GlobalPosition; var fw = -_me.Camera.GlobalTransform.Basis.Z;
            var ahead = World.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(o, o + fw * 40f, 1));
            if (t < 12.5 && (ahead.Count == 0 || o.DistanceTo(ahead["position"].AsVector3()) > 3f)) mi.MoveY = 1;
        }
        if (_wallYaw != null && (t is > 12.5 and < 13)) mi.Buttons |= MoveButtons.Fire;
        if (t > 15) GetTree().Quit();
    }
    float? _wallYaw; int _wallStep;

    async System.Threading.Tasks.Task DemoShot(string dir, double at)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
        var path = System.IO.Path.Combine(dir, $"fx_{at:F2}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"fx-demo: saved {path}");
    }
}
