using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.HeadsUp;
using Drycastle.ShooterCore.Effects;
using Drycastle.ShooterCore.Flight;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The Lock-on launcher on the client (issue #347, ADR 0031). The server decides the Lock; this draws what it sends. For the shooter: the sight
/// (<see cref="LockOnOverlay"/>) while the launcher is in the hand, and the seeker's beeps (slow while it searches, faster as the Lock builds, one
/// solid tone when it is complete). For an Aircraft's owner: the warning icon (<see cref="LockAlertOverlay"/>) and its beeps, quicker at each
/// level. The homing missile itself is drawn by <see cref="RocketsClient"/>, closing on where <see cref="TargetPosition"/> says its Aircraft is.
/// </summary>
internal sealed class LockOnClient
{
    readonly ClientContext _ctx;
    readonly AircraftClient _aircraft;

    public LockOnClient(ClientContext ctx, AircraftClient aircraft)
    {
        _ctx = ctx; _aircraft = aircraft;
        ctx.Root.AddChild(_lockSight);
        ctx.Root.AddChild(_lockAlert);
        ctx.WorldEntities.Despawned += id => _lockAlerts.Remove(id);
    }

    readonly LockOnOverlay _lockSight = new();
    readonly LockAlertOverlay _lockAlert = new();
    /// <summary>The shooter's own Lock as the server last sent it, and when (its progress runs on from there while tracking).</summary>
    LockStateMsg _lock;
    double _lockAt;
    /// <summary>The worst alert on each of the local player's Aircraft (by entity id); empty when nobody aims at them.</summary>
    readonly Dictionary<ushort, LockAlert> _lockAlerts = new();
    /// <summary>When the next seeker beep (the shooter's) and the next warning beep (the owner's) are due.</summary>
    double _lockBeepAt, _lockWarnAt;

    /// <summary>Seconds between seeker beeps: searching, at the start of a Lock, at its end; and between the solid tone's plays (the sample is 0.5 s).</summary>
    const double BeepSearchS = 1.0, BeepSlowS = 0.5, BeepFastS = 0.12, ToneEveryS = 0.45;

    public void OnEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.LockState when LockCodec.DecodeState(payload) is { } s:
                _lock = s; _lockAt = _ctx.Now;
                break;
            case EventKind.LockAlert when LockCodec.DecodeAlert(payload) is { } a:
                if (a.Level == LockAlert.None) _lockAlerts.Remove(a.Aircraft); else _lockAlerts[a.Aircraft] = a.Level;
                break;
        }
    }

    /// <summary>The share of the own Lock built by now.</summary>
    float LockProgress => _lock.Phase switch
    {
        LockPhase.Locked => 1f,
        LockPhase.Locking => Math.Clamp(_lock.Progress + (_lock.Tracking && _lock.Seconds > 0 ? (float)(_ctx.Now - _lockAt) / _lock.Seconds : 0f), 0f, 1f),
        _ => 0f,
    };

    /// <summary>Whether the own launcher may fire now (the server's word; the trigger's dry click and the predicted launch follow it).</summary>
    public bool Complete => _lock.Phase == LockPhase.Locked;

    /// <summary>The Lock of the weapon in the local player's hand, when its sight should be up.</summary>
    LockDef? LauncherHeld() =>
        !_ctx.Dead && _ctx.Me != null && !_aircraft.AtTurret && !_ctx.View.InUnarmedSeat && _ctx.Weapons != null && _ctx.Ammo is { } a && a.Slot >= 1 && a.Slot <= _ctx.Weapons.Weapons.Count ? _ctx.Weapons.Weapons[a.Slot - 1].Lock : null;

    /// <summary>Every frame: the shooter's sight and beeps, the owner's warning and beeps.</summary>
    public void Render()
    {
        if (LauncherHeld() is { } def && _ctx.Ammo is { } a)
        {
            _ctx.Hud?.SetLauncherHidesCrosshair(true);
            Vector3? target = null; var range = 0f; var unlimited = false;
            if (_lock.Phase != LockPhase.Idle && TargetPosition(_lock.Target) is { } at)
            {
                target = at;
                range = _ctx.Me!.Camera.GlobalPosition.DistanceTo(at) / def.RangeM;
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
            _ctx.Hud?.SetLauncherHidesCrosshair(false);
        }

        var worst = LockAlert.None;
        foreach (var level in _lockAlerts.Values) if (level > worst) worst = level;
        _lockAlert.Present(_ctx.Dead ? LockAlert.None : worst);
        if (worst != LockAlert.None && Sfx.Enabled && _ctx.Now >= _lockWarnAt)
        {
            Sfx.Play2D("lock.warn");
            _lockWarnAt = _ctx.Now + worst switch { LockAlert.Launched => 0.18, LockAlert.Locked => 0.4, _ => 0.9 };
        }
    }

    void SeekerBeep(float progress)
    {
        if (!Sfx.Enabled || _ctx.Now < _lockBeepAt) return;
        if (_lock.Phase == LockPhase.Locked) { Sfx.Play2D("lock.tone"); _lockBeepAt = _ctx.Now + ToneEveryS; return; }
        Sfx.Play2D("lock.beep", _lock.Phase == LockPhase.Idle ? -8f : 0f); // quieter while it only searches
        _lockBeepAt = _ctx.Now + (_lock.Phase == LockPhase.Idle ? BeepSearchS : Mathf.Lerp((float)BeepSlowS, (float)BeepFastS, progress));
    }

    /// <summary>Where a lock target is drawn: an Aircraft's model, or a flown helicopter's vehicle node. Null when it is not in view.</summary>
    public Vector3? TargetPosition(ushort id) =>
        _aircraft.PositionOf(id) is { } aircraftAt ? aircraftAt
        : _ctx.Cars.TryGet(id, out var car) && car.Node != null ? car.Node.GlobalPosition : null;

    string AircraftKindId(ushort id)
    {
        if (_ctx.Cars.Has(id)) return "";
        if (!_ctx.WorldEntities.Entities.TryGetValue(id, out var e) || _ctx.Killstreaks?.AircraftKinds is not { } kinds) return "";
        var index = AircraftState.Unpack(e.State.State).Kind;
        return index < kinds.Count ? kinds[index].Id : "";
    }

}
