using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Server;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client;

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
    RecoilState _recoil = new(0);
    BreathState _breath = new(0);
    readonly BreathCues _breathCues = new();

    readonly ZeroDial _zero = new();

    /// <summary>The scope that is up, for the HUD's mil-dots and zero: its FOV and the zero dialled on the weapon (0: no bullet drop).</summary>
    (float FovDeg, float ZeroM) ScopeView() =>
        Ctx.Me is { ZoomFov: > 0 } me && !_binoculars && Ammo is { } a && CurrentWeapon is { } w && BulletDrop.HasDrop(w) ? (me.ZoomFov, _zero.Of(a.Slot - 1, w)) : (0f, 0f);

    /// <summary>
    /// The mouse wheel while a scope with bullet drop is up: one click of the zero dial (up: farther). The server is told the new zero and resolves the
    /// next shots with it. False when the wheel is not the dial's (no such scope up), so it cycles the weapons as ever.
    /// </summary>
    bool DialZero(int clicks)
    {
        if (Ctx.Me is not { ZoomFov: > 0 } || _binoculars || Ammo is not { } a || CurrentWeapon is not { } w || !BulletDrop.HasDrop(w)) return false;
        var was = _zero.Of(a.Slot - 1, w);
        var now = BulletDrop.Dial(w, was, clicks);
        if (now == was) return true; // at the end of the dial
        _zero.Set(a.Slot - 1, w, now);
        Ctx.SendHuman(EventKind.SetZero, BulletDrop.EncodeZero(a.Slot - 1, now));
        Sfx.Ui("ui.hover");
        return true;
    }

    bool PredictionOn => !Bot && ShotFx.Enabled && Weapons != null;

    /// <summary>Called for every ammo message: re-syncs the predicted magazine and starts the switch delay on a slot change.</summary>
    void PredictOnAmmo(AmmoMsg a)
    {
        if (!PredictionOn || a.Slot < 1 || a.Slot > Weapons!.Weapons.Count) return;
        if (a.Slot != _pSlot) { _pSlot = a.Slot; ClearPredictedReload(); } // a reload belongs to the weapon it started on
        while (_predShots.Count > 0 && Ctx.Now - _predShots.Peek() > 0.6) _predShots.Dequeue();
        var inFlight = _predShots.Count(t => Ctx.Now - t < 0.25); // shots sent but not yet counted by this message
        _pMag = Math.Max(0, a.Slots[a.Slot - 1].Mag - inFlight);
    }

    /// <summary>Recovers the recoil offset for this tick and shows it on the camera (the server fires with the same offset).</summary>
    void TickRecoil(MoveInput mi)
    {
        if (Ctx.Me == null || CurrentWeapon is not { } w) return;
        _recoil.Tick(Ctx.Now, mi.Has(MoveButtons.Fire), w.Recoil);
        Ctx.Me.AimKick = new Vector2(_recoil.PitchRad, _recoil.YawRad);
    }

    /// <summary>Advances the sniper breath (the server runs the same state from the same input), plays its sounds (inhale, heartbeat, exhale) and shows the cosmetic sway on the camera.</summary>
    void TickBreath(MoveInput mi)
    {
        if (Ctx.Me == null || CurrentWeapon is not { } w) return;
        var scoped = mi.Has(MoveButtons.Aim) && w.ZoomFov > 0 && Mode.CanScope;
        _breath.Tick(Ctx.Now, scoped, mi.Has(MoveButtons.Sprint), w, Loadouts.Perks.HoldBreathTime);
        var cue = _breathCues.Tick(Ctx.Now, _breath.Phase, _breath.Meter);
        if (Sfx.Enabled)
        {
            if (cue.Inhale) Sfx.Play2D("breath.in");
            if (cue.Beat) Sfx.Play2D("breath.heartbeat", cue.BeatDb);
            if (cue.ForcedExhale) Sfx.Play2D("breath.out");
            else if (cue.Exhale) Sfx.Play2D("breath.out", -8f);
        }
        var v = BreathState.VisualSway(Ctx.Now, _breath.SwayDegrees(w));
        // the binoculars are held steady: the weapon's sway is not theirs (it would show at their zoom with a sniper rifle in the hotbar)
        Ctx.Me.ViewSway = GameSettings.Current.ReduceMotion || _binoculars ? Vector2.Zero : new Vector2(v.X, v.Y);
    }

    /// <summary>The breath while scoped with a breath hold (the HUD reads it each frame for the key hint).</summary>
    (bool Show, float Meter, BreathState.BreathPhase Phase) BreathView()
    {
        if (Ctx.Me is not { ZoomFov: > 0 } || _binoculars || CurrentWeapon is not { } w || BreathState.BudgetSeconds(w, Loadouts.Perks.HoldBreathTime) <= 0)
            return (false, 0f, BreathState.BreathPhase.Ready);
        return (true, _breath.Meter, _breath.Phase);
    }

    /// <summary>Predicts this tick's trigger pull / reload key; plays sounds and own-shot effects at once.</summary>
    void PredictFire(MoveInput mi)
    {
        if (!PredictionOn || !Mode.CanPredictFire || OwnBodyFlung || Ammo is not { } a || a.Slot < 1 || a.Slot > Weapons!.Weapons.Count || a.Slot != Ctx.Hotbar.Want) return;
        var w = Weapons.Weapons[a.Slot - 1];
        var now = Ctx.Now;
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
        if (w.Lock != null && !LockComplete)
        {
            // a Lock-on launcher has no dumb fire: without a complete Lock the trigger only clicks (the server fires nothing)
            if (now - _pDryAt > 0.45) { _pDryAt = now; Sfx.Play2D("lock.deny"); }
            return;
        }
        _pNextFire = now + w.FireInterval;
        if (ranged) _pMag--;
        _predShots.Enqueue(now);
        if (w.Kind == WeaponKind.Hitscan && Ctx.Me != null)
        {
            // same offset step as the server's shot; the viewmodel kicks by the same (ADS / crouch scaled) climb
            var mult = RecoilState.Multiplier(w.Recoil, mi.Has(MoveButtons.Aim), Ctx.View.InArmedSeat ? Stance.Stand : Ctx.Me.State.Stance); // a seat steadies it no more than standing does, as on the server
            _recoil.Shot(w.Recoil, mult);
            _breath.AimError(_breath.SwayDegrees(w)); // the server's per-shot aim error; kept in step (the tracer follows the camera, like recoil)
            _viewmodel.Kick(w.Recoil.VerticalPerShot * mult);
        }
        if (_fxDemo.Mode != "") GD.Print($"fx-demo: predicted {w.Id} shot, mag {_pMag}");
        OwnShotFx(w);
    }

    bool _pHeld;

    /// <summary>Every frame: the rocket head sits in the held RPG's tube only while a rocket is loaded (the predicted magazine, else the server's).</summary>
    void RenderLauncherLoaded()
    {
        if (_viewmodel.Held is not { } held) return;
        var mag = _pMag >= 0 ? _pMag : Ammo is { } a && a.Slot >= 1 && a.Slot <= a.Slots.Count ? a.Slots[a.Slot - 1].Mag : 1;
        WeaponFx.SetLoaded(held, mag > 0);
    }

    /// <summary>Forgets the predicted reload and magazine (weapon switch, death, spawn): the next ammo message re-syncs the magazine, and no reload keeps counting for a weapon that is gone.</summary>
    void ClearPredictedReload()
    {
        _pReloadUntil = 0; _pMagAfter = 0; _pMag = -1;
        _predShots.Clear();
    }

    void StartPredictedReload(WeaponDef w, int reserve)
    {
        if (w.ReloadTime <= 0 || Ctx.Now < _pReloadUntil) return;
        _pReloadUntil = Ctx.Now + w.ReloadTime;
        // a shell reload predicts one shell; the server's ammo message after each shell re-syncs the rest
        _pMagAfter = Math.Min(w.MagazineSize, Math.Max(0, _pMag) + (w.ShellReload ? 1 : reserve));
        Sfx.Play2D(SoundRules.Event("reload", w.Id));
    }

    /// <summary>Sound, muzzle flash and tracer for the own shot, from the camera aim (spread is not predicted).</summary>
    void OwnShotFx(WeaponDef w)
    {
        if (Ctx.Me == null) return;
        var melee = w.Kind == WeaponKind.Melee;
        var projectile = w.Kind == WeaponKind.Projectile;
        Sfx.Play2D(SoundRules.Event(projectile ? "launch" : "shot", w.Id));
        if (melee) return;
        var cam = Ctx.Me.Camera;
        var from = cam.GlobalPosition;
        var dir = -cam.GlobalTransform.Basis.Z;
        var muzzle = Ctx.Me.ZoomFov > 0 ? from + dir * 0.6f : _viewmodel.MuzzlePosition ?? from + dir * 0.8f; // scoped: the streak leaves the crosshair, the gun is hidden
        if (w.Grenade == null) _viewmodel.Flash(projectile ? 0.5f : 0.4f);
        if (w.Grenade != null) { _viewmodel.Kick(6f); return; } // a throw: the hand jumps, nothing blows back
        if (projectile)
        {
            // a launcher: the tube jumps (weapons.json recoil, the viewmodel only: a rocket has no spread to climb) and throws its backblast out of the rear
            _viewmodel.Kick(w.Recoil.VerticalPerShot);
            RocketFx.Backblast(Ctx.Owned, (_viewmodel.MuzzlePosition ?? from) - dir * (WeaponFx.LauncherRear + 0.2f), dir);
            return;
        }
        if (CasingFx.KindOf(w) is { } ck) CasingFx.Eject(Ctx.Owned, muzzle, dir, ck); // the rocket itself comes from the server
        var q = PhysicsRayQueryParameters3D.Create(from, from + dir * 300f, CollisionLayers.WorldShots); // not the vehicles' solid boxes: VehicleAlong finds a vehicle
        q.Exclude = new Godot.Collections.Array<Rid> { Ctx.Me.GetRid() };
        var hit = World.GetWorld3D().DirectSpaceState.IntersectRay(q);
        var worldDist = hit.Count == 0 ? 300f : from.DistanceTo(hit["position"].AsVector3());
        if (VehicleAlong(from, dir, MathF.Min(300f, worldDist), out var car, out var vh, Ctx.Cars.MySeat()?.Car))
        {
            // the predicted shot stops on a car: the tracer ends on its hit box, the hole rides the car's node
            var vat = ToWorld(car, G(vh.Local));
            var vnormal = car.Node.GlobalTransform.Basis * G(vh.LocalNormal);
            ShotFx.Spawn(Ctx.Owned, muzzle, vat, false, true, vnormal);
            Sfx.Play3D("impact", vat);
            BulletHoles.PlaceOn(Ctx.Owned, GlassClient.HostOf(car.Node), G(vh.Local), G(vh.LocalNormal));
            return;
        }
        var end = hit.Count > 0 ? hit["position"].AsVector3() : from + dir * 300f;
        ShotFx.Spawn(Ctx.Owned, muzzle, end, false, true, hit.Count > 0 ? hit["normal"].AsVector3() : null);
        if (hit.Count > 0) { Sfx.Play3D("impact", end); if (hit["collider"].AsGodotObject() is CollisionObject3D { CollisionLayer: var l } && (l & CollisionLayers.WorldShots) != 0) BulletHoles.Place(Ctx.Owned, end, hit["normal"].AsVector3()); }
    }

    /// <summary>True when this server event is the echo of a shot already shown by prediction (consumes one prediction).</summary>
    bool EchoOfPredicted(ushort owner)
    {
        if (owner != Ctx.MyId || !PredictionOn) return false;
        while (_predShots.Count > 0 && Ctx.Now - _predShots.Peek() > 1.0) _predShots.Dequeue(); // the server never answered: drop it
        if (_predShots.Count == 0) return false;
        _predShots.Dequeue();
        return true;
    }

    /// <summary>Where a shot of <paramref name="id"/> visibly leaves the weapon; <paramref name="fallback"/> (the eye) when nothing better is known.</summary>
    Vector3 MuzzleOf(ushort id, Vector3 fallback, Vector3 end)
    {
        if (id == Ctx.MyId) return Ctx.Me is { ZoomFov: > 0 } ? fallback + (end - fallback).Normalized() * 0.6f : _viewmodel.MuzzlePosition ?? fallback;
        if (!Ctx.Remotes.TryGetValue(id, out var r)) return fallback;
        var chest = r.Node.GlobalPosition + Vector3.Up * 1.3f;
        var dir = (end - fallback).Normalized();
        var fwd = new Vector3(dir.X, 0, dir.Z).Normalized();
        var side = fwd.Cross(Vector3.Up);
        var guess = chest + fwd * 0.7f + side * 0.2f;
        return r.View?.MuzzlePosition is { } m && m.DistanceTo(chest) < 1.3f ? m : guess;
    }
}
