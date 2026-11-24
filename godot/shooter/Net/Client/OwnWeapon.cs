using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Vehicles;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// Immediate feedback for the own weapon. The server stays authoritative, but sound, muzzle flash and tracer must
/// not wait a round trip, so the client predicts the trigger pull from the last ammo message, the weapon's
/// fire_interval, and a locally counted magazine. Each predicted shot is remembered; the matching
/// server Shot/Launch event (same owner) is then swallowed instead of playing everything twice (<see cref="EchoOfPredicted"/>). Own reload and
/// dry-fire clicks use the same prediction. The features that draw the server's shots, launches and pellet fans ask it for the echo and the muzzle.
/// </summary>
internal sealed class OwnWeapon
{
    readonly ClientContext _ctx;
    readonly OwnAim _aim;
    readonly VehicleHulls _hulls;
    readonly FxDemoClient _fxDemo;
    readonly Func<bool> _lockComplete;
    readonly Func<bool> _ownFlung;
    readonly Queue<double> _predShots = new();
    double _pNextFire, _pReloadUntil, _pDryAt;
    int _pMag = -1, _pMagAfter, _pSlot;
    bool _pHeld;

    /// <summary>The spread the next shot would get, estimated as the server does (the crosshair gap and the shots' bookkeeping).</summary>
    public SpreadEstimator Spread { get; } = new();
    /// <summary>The aim button of the last input tick; the crosshair spread uses it as the server uses the input's Aim bit.</summary>
    public bool AimHeld;

    /// <param name="lockComplete">The own Lock-on launcher has a complete Lock (it may fire).</param>
    /// <param name="ownFlung">The own body is in the Bigfoot's fist or flying from it: nothing is fired.</param>
    public OwnWeapon(ClientContext ctx, OwnAim aim, VehicleHulls hulls, FxDemoClient fxDemo, Func<bool> lockComplete, Func<bool> ownFlung)
    {
        _ctx = ctx; _aim = aim; _hulls = hulls; _fxDemo = fxDemo; _lockComplete = lockComplete; _ownFlung = ownFlung;
    }

    /// <summary>When the predicted reload ends (0: none), for the reload sound the ammo message plays.</summary>
    public double ReloadUntil => _pReloadUntil;

    /// <summary>A spawn: the spread, the aim and the predicted magazine start afresh.</summary>
    public void OnSpawn()
    {
        Spread.Reset(); _aim.Reset(); ClearReload(); _pNextFire = 0;
    }

    bool PredictionOn => !_ctx.Bot && ShotFx.Enabled && _ctx.Weapons != null;

    /// <summary>Called for every ammo message: re-syncs the predicted magazine and starts the switch delay on a slot change.</summary>
    public void OnAmmo(AmmoMsg a)
    {
        if (!PredictionOn || a.Slot < 1 || a.Slot > _ctx.Weapons!.Weapons.Count) return;
        if (a.Slot != _pSlot) { _pSlot = a.Slot; ClearReload(); } // a reload belongs to the weapon it started on
        while (_predShots.Count > 0 && _ctx.Now - _predShots.Peek() > 0.6) _predShots.Dequeue();
        var inFlight = _predShots.Count(t => _ctx.Now - t < 0.25); // shots sent but not yet counted by this message
        _pMag = Math.Max(0, a.Slots[a.Slot - 1].Mag - inFlight);
    }

    /// <summary>Predicts this tick's trigger pull / reload key; plays sounds and own-shot effects at once.</summary>
    public void PredictFire(MoveInput mi)
    {
        if (!PredictionOn || !_ctx.View.Mode.CanPredictFire || _ownFlung() || _ctx.Ammo is not { } a || a.Slot < 1 || a.Slot > _ctx.Weapons!.Weapons.Count || a.Slot != _ctx.Hotbar.Want) return;
        var w = _ctx.Weapons.Weapons[a.Slot - 1];
        var now = _ctx.Now;
        if (_pReloadUntil > 0 && now >= _pReloadUntil) { _pMag = _pMagAfter; _pReloadUntil = 0; }
        var reloading = a.ReloadRemaining > 0 || now < _pReloadUntil;
        var (srvMag, reserve) = a.Slots[a.Slot - 1];
        if (_pMag < 0) _pMag = srvMag;
        var ranged = w.Kind != WeaponKind.Melee;

        if (!reloading && ranged && mi.Has(MoveButtons.Reload) && w.MagazineSize > 0 && _pMag < w.MagazineSize && reserve > 0) { StartPredictedReload(w, reserve); return; }
        if (!mi.Has(MoveButtons.Fire)) { if (!w.FullAuto) _pHeld = false; return; }
        var pull = w.FullAuto || !_pHeld;
        _pHeld = true;
        // firing interrupts a shell reload once a shell is loaded (the server does the same)
        if (w.ShellReload && reloading && pull && _pMag > 0 && now >= _pNextFire - 0.0033) { reloading = false; _pReloadUntil = 0; }
        if (!pull || reloading || now < _pNextFire - 0.0033) return;

        if (ranged && _pMag <= 0)
        {
            // empty: the server starts a reload when it has reserve ammo, otherwise it is a dry click
            if (reserve > 0 && w.MagazineSize > 0) StartPredictedReload(w, reserve);
            else if (now - _pDryAt > 0.45) { _pDryAt = now; Sfx.Play2D(SoundRules.Event("dry", w.Id)); }
            return;
        }
        if (w.Lock != null && !_lockComplete())
        {
            // a Lock-on launcher has no dumb fire: without a complete Lock the trigger only clicks (the server fires nothing)
            if (now - _pDryAt > 0.45) { _pDryAt = now; Sfx.Play2D("lock.deny"); }
            return;
        }
        _pNextFire = now + w.FireInterval;
        if (ranged) _pMag--;
        _predShots.Enqueue(now);
        if (w.Kind == WeaponKind.Hitscan && _ctx.Me != null)
        {
            var mult = _aim.Shot(w, mi.Has(MoveButtons.Aim)); // same offset step as the server's shot; the viewmodel kicks by the same (ADS / crouch scaled) climb
            _ctx.Viewmodel.Kick(w.Recoil.VerticalPerShot * mult);
        }
        if (_fxDemo.Mode != "") GD.Print($"fx-demo: predicted {w.Id} shot, mag {_pMag}");
        OwnShotFx(w);
    }

    /// <summary>Every frame: the rocket head sits in the held RPG's tube only while a rocket is loaded (the predicted magazine, else the server's).</summary>
    public void ShowLoaded()
    {
        if (_ctx.Viewmodel.Held is not { } held) return;
        var mag = _pMag >= 0 ? _pMag : _ctx.Ammo is { } a && a.Slot >= 1 && a.Slot <= a.Slots.Count ? a.Slots[a.Slot - 1].Mag : 1;
        WeaponFx.SetLoaded(held, mag > 0);
    }

    /// <summary>Forgets the predicted reload and magazine (weapon switch, death, spawn): the next ammo message re-syncs the magazine, and no reload keeps counting for a weapon that is gone.</summary>
    public void ClearReload()
    {
        _pReloadUntil = 0; _pMagAfter = 0; _pMag = -1;
        _predShots.Clear();
    }

    void StartPredictedReload(WeaponDef w, int reserve)
    {
        if (w.ReloadTime <= 0 || _ctx.Now < _pReloadUntil) return;
        _pReloadUntil = _ctx.Now + w.ReloadTime;
        // a shell reload predicts one shell; the server's ammo message after each shell re-syncs the rest
        _pMagAfter = Math.Min(w.MagazineSize, Math.Max(0, _pMag) + (w.ShellReload ? 1 : reserve));
        Sfx.Play2D(SoundRules.Event("reload", w.Id));
    }

    /// <summary>Sound, muzzle flash and tracer for the own shot, from the camera aim (spread is not predicted).</summary>
    void OwnShotFx(WeaponDef w)
    {
        if (_ctx.Me == null) return;
        var melee = w.Kind == WeaponKind.Melee;
        var projectile = w.Kind == WeaponKind.Projectile;
        Sfx.Play2D(SoundRules.Event(projectile ? "launch" : "shot", w.Id));
        if (melee) return;
        var cam = _ctx.Me.Camera;
        var from = cam.GlobalPosition;
        var dir = -cam.GlobalTransform.Basis.Z;
        var muzzle = _ctx.Me.ZoomFov > 0 ? from + dir * 0.6f : _ctx.Viewmodel.MuzzlePosition ?? from + dir * 0.8f; // scoped: the streak leaves the crosshair, the gun is hidden
        if (w.Grenade == null) _ctx.Viewmodel.Flash(projectile ? 0.5f : 0.4f);
        if (w.Grenade != null) { _ctx.Viewmodel.Kick(6f); return; } // a throw: the hand jumps, nothing blows back
        if (projectile)
        {
            // a launcher: the tube jumps (weapons.json recoil, the viewmodel only: a rocket has no spread to climb) and throws its backblast out of the rear
            _ctx.Viewmodel.Kick(w.Recoil.VerticalPerShot);
            RocketFx.Backblast(_ctx.Owned, (_ctx.Viewmodel.MuzzlePosition ?? from) - dir * (WeaponFx.LauncherRear + 0.2f), dir);
            return;
        }
        if (CasingFx.KindOf(w) is { } ck) CasingFx.Eject(_ctx.Owned, muzzle, dir, ck); // the rocket itself comes from the server
        var q = PhysicsRayQueryParameters3D.Create(from, from + dir * 300f, CollisionLayers.WorldShots); // not the vehicles' solid boxes: VehicleAlong finds a vehicle
        q.Exclude = new Godot.Collections.Array<Rid> { _ctx.Me.GetRid() };
        var hit = _ctx.World.GetWorld3D().DirectSpaceState.IntersectRay(q);
        var worldDist = hit.Count == 0 ? 300f : from.DistanceTo(hit["position"].AsVector3());
        if (_hulls.Along(from, dir, MathF.Min(300f, worldDist), out var car, out var vh, _ctx.Cars.MySeat()?.Car))
        {
            // the predicted shot stops on a car: the tracer ends on its hit box, the hole rides the car's node
            var vat = VehicleHulls.ToWorld(car, vh.Local.ToGodot());
            var vnormal = car.Node.GlobalTransform.Basis * vh.LocalNormal.ToGodot();
            ShotFx.Spawn(_ctx.Owned, muzzle, vat, false, true, vnormal);
            Sfx.Play3D("impact", vat);
            BulletHoles.PlaceOn(_ctx.Owned, GlassClient.HostOf(car.Node), vh.Local.ToGodot(), vh.LocalNormal.ToGodot());
            return;
        }
        var end = hit.Count > 0 ? hit["position"].AsVector3() : from + dir * 300f;
        ShotFx.Spawn(_ctx.Owned, muzzle, end, false, true, hit.Count > 0 ? hit["normal"].AsVector3() : null);
        if (hit.Count > 0) { Sfx.Play3D("impact", end); if (hit["collider"].AsGodotObject() is CollisionObject3D { CollisionLayer: var l } && (l & CollisionLayers.WorldShots) != 0) BulletHoles.Place(_ctx.Owned, end, hit["normal"].AsVector3()); }
    }

    /// <summary>True when this server event is the echo of a shot already shown by prediction (consumes one prediction).</summary>
    public bool EchoOfPredicted(ushort owner)
    {
        if (owner != _ctx.MyId || !PredictionOn) return false;
        while (_predShots.Count > 0 && _ctx.Now - _predShots.Peek() > 1.0) _predShots.Dequeue(); // the server never answered: drop it
        if (_predShots.Count == 0) return false;
        _predShots.Dequeue();
        return true;
    }

    /// <summary>Where a shot of <paramref name="id"/> visibly leaves the weapon; <paramref name="fallback"/> (the eye) when nothing better is known.</summary>
    public Vector3 MuzzleOf(ushort id, Vector3 fallback, Vector3 end)
    {
        if (id == _ctx.MyId) return _ctx.Me is { ZoomFov: > 0 } ? fallback + (end - fallback).Normalized() * 0.6f : _ctx.Viewmodel.MuzzlePosition ?? fallback;
        if (!_ctx.Remotes.TryGetValue(id, out var r)) return fallback;
        var chest = r.Node.GlobalPosition + Vector3.Up * 1.3f;
        var dir = (end - fallback).Normalized();
        var fwd = new Vector3(dir.X, 0, dir.Z).Normalized();
        var side = fwd.Cross(Vector3.Up);
        var guess = chest + fwd * 0.7f + side * 0.2f;
        return r.View?.MuzzlePosition is { } m && m.DistanceTo(chest) < 1.3f ? m : guess;
    }
}
