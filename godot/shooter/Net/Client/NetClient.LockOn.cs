using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.HeadsUp;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Effects;
using Drycastle.ShooterCore.Flight;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The Lock-on launcher on the client (issue #347, ADR 0031). The server decides the Lock; this draws what it sends. For the shooter: the sight
/// (<see cref="LockOnOverlay"/>) while the launcher is in the hand, and the seeker's beeps (slow while it searches, faster as the Lock builds, one
/// solid tone when it is complete). For an Aircraft's owner: the warning icon (<see cref="LockAlertOverlay"/>) and its beeps, quicker at each
/// level. For everyone: the homing missile, drawn closing on its Aircraft so that it arrives when the server's hit does.
/// </summary>
public partial class NetClient
{
    LockOnOverlay _lockSight = null!;
    LockAlertOverlay _lockAlert = null!;
    /// <summary>The shooter's own Lock as the server last sent it, and when (its progress runs on from there while tracking).</summary>
    LockStateMsg _lock;
    double _lockAt;
    /// <summary>The worst alert on each of the local player's Aircraft (by entity id); empty when nobody aims at them.</summary>
    readonly Dictionary<ushort, LockAlert> _lockAlerts = new();
    /// <summary>When the next seeker beep (the shooter's) and the next warning beep (the owner's) are due.</summary>
    double _lockBeepAt, _lockWarnAt;
    /// <summary>Where and when a homing missile last shot an Aircraft down, so the Aircraft's own crash blast is not drawn on top of the missile's.</summary>
    (double T, Vector3 At) _homingKill = (double.NegativeInfinity, Vector3.Zero);

    /// <summary>Seconds between seeker beeps: searching, at the start of a Lock, at its end; and between the solid tone's plays (the sample is 0.5 s).</summary>
    const double BeepSearchS = 1.0, BeepSlowS = 0.5, BeepFastS = 0.12, ToneEveryS = 0.45;

    /// <summary>Called once from <see cref="_Ready"/>.</summary>
    void InitLockOn()
    {
        _lockSight = new LockOnOverlay();
        AddChild(_lockSight);
        _lockAlert = new LockAlertOverlay();
        AddChild(_lockAlert);
        EventReceived += OnLockEvent;
        WorldEntities.Despawned += id => _lockAlerts.Remove(id);
    }

    void OnLockEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.LockState when LockCodec.DecodeState(payload) is { } s:
                _lock = s; _lockAt = Ctx.Now;
                break;
            case EventKind.LockAlert when LockCodec.DecodeAlert(payload) is { } a:
                if (a.Level == LockAlert.None) _lockAlerts.Remove(a.Aircraft); else _lockAlerts[a.Aircraft] = a.Level;
                break;
            case EventKind.HomingLaunch when LockCodec.DecodeLaunch(payload) is { } l:
                OnHomingLaunch(l);
                break;
        }
    }

    /// <summary>The share of the own Lock built by now.</summary>
    float LockProgress => _lock.Phase switch
    {
        LockPhase.Locked => 1f,
        LockPhase.Locking => Math.Clamp(_lock.Progress + (_lock.Tracking && _lock.Seconds > 0 ? (float)(Ctx.Now - _lockAt) / _lock.Seconds : 0f), 0f, 1f),
        _ => 0f,
    };

    /// <summary>Whether the own launcher may fire now (the server's word; the trigger's dry click and the predicted launch follow it).</summary>
    bool LockComplete => _lock.Phase == LockPhase.Locked;

    /// <summary>The Lock of the weapon in the local player's hand, when its sight should be up.</summary>
    LockDef? LauncherHeld() =>
        !Dead && Ctx.Me != null && _gunship == null && !InUnarmedSeat && Weapons != null && Ammo is { } a && a.Slot >= 1 && a.Slot <= Weapons.Weapons.Count ? Weapons.Weapons[a.Slot - 1].Lock : null;

    /// <summary>Every frame: the shooter's sight and beeps, the owner's warning and beeps.</summary>
    void RenderLockOn()
    {
        if (LauncherHeld() is { } def && Ammo is { } a)
        {
            Ctx.Hud?.SetLauncherHidesCrosshair(true);
            Vector3? target = null; var range = 0f; var unlimited = false;
            if (_lock.Phase != LockPhase.Idle && LockTargetPosition(_lock.Target) is { } at)
            {
                target = at;
                range = Ctx.Me!.Camera.GlobalPosition.DistanceTo(at) / def.RangeM;
                unlimited = def.UnlimitedRange.Contains(AircraftKindId(_lock.Target));
            }
            var (mag, reserve) = a.Slots[a.Slot - 1];
            var progress = LockProgress;
            _lockSight.Present(new LockSight(def.ConeDeg, _lock.Phase, _lock.Tracking, progress, target, range, unlimited, mag, reserve, a.ReloadRemaining > 0));
            SeekerBeep(progress);
        }
        else if (_lockSight.Visible)
        {
            _lockSight.Dismiss();
            Ctx.Hud?.SetLauncherHidesCrosshair(false);
        }

        var worst = LockAlert.None;
        foreach (var level in _lockAlerts.Values) if (level > worst) worst = level;
        _lockAlert.Present(Dead ? LockAlert.None : worst);
        if (worst != LockAlert.None && Sfx.Enabled && Ctx.Now >= _lockWarnAt)
        {
            Sfx.Play2D("lock.warn");
            _lockWarnAt = Ctx.Now + worst switch { LockAlert.Launched => 0.18, LockAlert.Locked => 0.4, _ => 0.9 };
        }
    }

    void SeekerBeep(float progress)
    {
        if (!Sfx.Enabled || Ctx.Now < _lockBeepAt) return;
        if (_lock.Phase == LockPhase.Locked) { Sfx.Play2D("lock.tone"); _lockBeepAt = Ctx.Now + ToneEveryS; return; }
        Sfx.Play2D("lock.beep", _lock.Phase == LockPhase.Idle ? -8f : 0f); // quieter while it only searches
        _lockBeepAt = Ctx.Now + (_lock.Phase == LockPhase.Idle ? BeepSearchS : Mathf.Lerp((float)BeepSlowS, (float)BeepFastS, progress));
    }

    /// <summary>Where a lock target is drawn: an Aircraft's model, or a flown helicopter's vehicle node. Null when it is not in view.</summary>
    Vector3? LockTargetPosition(ushort id) =>
        _aircraft.TryGetValue(id, out var view) ? view.Model.GlobalPosition
        : Ctx.Cars.TryGet(id, out var car) && car.Node != null ? car.Node.GlobalPosition : null;

    string AircraftKindId(ushort id)
    {
        if (Ctx.Cars.Has(id)) return "";
        if (!WorldEntities.Entities.TryGetValue(id, out var e) || Killstreaks?.AircraftKinds is not { } kinds) return "";
        var index = AircraftState.Unpack(e.State.State).Kind;
        return index < kinds.Count ? kinds[index].Id : "";
    }

    /// <summary>A homing missile leaves a launcher: drawn from the launch, flown by <see cref="RenderRockets"/> after its Aircraft, ended by the Explosion with its id.</summary>
    void OnHomingLaunch(HomingLaunchMsg l)
    {
        if (Ctx.Remotes.TryGetValue(l.Owner, out var launcher)) launcher.FireUntil = Ctx.Now + 0.45;
        if (!EchoOfPredicted(l.Owner))
        {
            if (Sfx.Enabled) { var snd = SoundRules.Event("launch", WeaponId(l.Weapon)); if (l.Owner == Ctx.MyId) Sfx.Play2D(snd); else Sfx.Play3D(snd, G(l.Origin)); }
            if (l.Owner == Ctx.MyId) _viewmodel?.Flash(0.5f); else if (Ctx.Remotes.TryGetValue(l.Owner, out var lf)) lf.View?.Flash(0.6f);
        }
        if (_deadRockets.Contains(l.Id) || !ShotFx.Enabled || Weapons == null || _rockets.ContainsKey(l.Id)) return;
        var speed = l.Weapon < Weapons.Weapons.Count ? Weapons.Weapons[l.Weapon].ProjectileSpeed : 160f;
        var to = LockTargetPosition(l.Target) is { } tp ? new NVec3(tp.X, tp.Y, tp.Z) - l.Origin : NVec3.UnitY;
        _rockets[l.Id] = new Rocket { Node = RocketFx.Make(Ctx.Owned), Born = Ctx.Now, Pos = l.Origin, Dir = NVec3.Normalize(to), At = Ctx.Now, Speed = speed, Homing = true, Target = l.Target, Arrive = l.Seconds > 0 ? Ctx.Now + l.Seconds : 0 };
    }

    /// <summary>One frame of a homing missile: it closes on where its Aircraft is drawn at the rate that lands it there on time; with the Aircraft gone it flies straight on.</summary>
    void StepHoming(Rocket r)
    {
        var dt = (float)(Ctx.Now - r.At);
        r.At = Ctx.Now;
        if (dt <= 0) return;
        if (LockTargetPosition(r.Target) is { } p)
        {
            // an Aircraft: arrives when the server's hit does; a helicopter (Arrive 0, Seconds 0): flown as the server does, at its speed, straight at the vehicle
            var next = r.Arrive <= 0 ? LockRules.Pursue(r.Pos, new NVec3(p.X, p.Y, p.Z), r.Speed, 0f, dt).Next : LockRules.Chase(r.Pos, new NVec3(p.X, p.Y, p.Z), (float)(r.Arrive - Ctx.Now) + dt, dt);
            var step = next - r.Pos;
            if (step.LengthSquared() > 1e-8f) r.Dir = NVec3.Normalize(step);
            r.Pos = next;
        }
        else r.Pos += r.Dir * (r.Speed * dt);
    }

    /// <summary>The blast of a homing missile's hit; a big one (the kill's) is remembered so the Aircraft's own crash blast is skipped.</summary>
    void NoteHomingBlast(Vector3 at, float radius)
    {
        if (radius >= DustRules.FrontMinRadius) _homingKill = (Ctx.Now, at);
    }

    /// <summary>Whether a shot-down Aircraft at <paramref name="at"/> has just had a homing missile's kill blast drawn where it flew.</summary>
    bool HomingKillShown(Vector3 at) => Ctx.Now - _homingKill.T < 0.75 && _homingKill.At.DistanceTo(at) < 60f;
}
