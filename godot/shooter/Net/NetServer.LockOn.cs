using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The Lock-on launcher on the server (ADR 0031, glossary "Lock-on launcher", "Lock", "Homing missile"). The server alone decides the Lock: once a
/// tick, for every player with the launcher in hand, which enemy Aircraft the lock circle holds in sight (<see cref="LockRules.Pick"/>) and how far
/// the Lock is (<see cref="LockOn"/>); the shooter is sent their Lock when it changes, and the Aircraft's owner the worst of the launchers on it
/// (<see cref="LockAlert"/>). A pull on a complete Lock fires a missile whose whole flight is known at once, because an Aircraft flies a fixed
/// circle (<see cref="LockRules.Intercept"/>, as the flak's shell): the damage lands when it arrives, through the path every anti-aircraft uses
/// (<see cref="DamageAircraft"/>, ADR 0005), and the blast is the ordinary Explosion event (a kill's is big enough for a Blast wave).
/// </summary>
public partial class NetServer
{
    /// <summary>A missile on its way: its id (the Explosion's), who fired it with which weapon, the Aircraft it homes on, where it was planned to arrive and when.</summary>
    sealed record Homing(ushort Id, ushort Owner, byte Weapon, WeaponDef Def, ushort Target, NVec3 At, double Due);

    readonly List<Homing> _homing = new();
    /// <summary>The level last sent for each Aircraft (by its entity id) and the owner it went to, so only a change is sent.</summary>
    readonly Dictionary<ushort, (LockAlert Level, ushort Owner)> _lockAlerts = new();
    readonly Dictionary<ushort, LockAlert> _lockAlertsNow = new();
    readonly List<LockCandidate> _lockCandidates = new();

    /// <summary>The Lock of the weapon in a player's hand when they can use it now: alive, not a bot, on foot or in an armed seat, not at a streak's controls.</summary>
    LockDef? LauncherInHand(Client c)
    {
        if (c.IsBot || c.W.Current.Lock is not { } def) return null;
        if (_match.Get(c.Id)?.Life != PlayerLife.Alive) return null;
        if (_streaks.ControlOf(c.Id) != null || _sitters.ContainsKey(c.Id) || _pins.ContainsKey(c.Id)) return null;
        if (!LockRules.SeatAllows(c.Car != null, c.Car != null && c.CarSeat >= 0 && c.CarSeat < c.Car.Def.Seats.Count && c.Car.Def.Seats[c.CarSeat].Armed)) return null;
        return def;
    }

    /// <summary>Whether this input's pull may fire a weapon that needs a Lock (<see cref="WeaponInput.Locked"/>).</summary>
    bool HasLock(Client c) => c.Lock.Phase == LockPhase.Locked;

    /// <summary>Once a tick, after the players moved: every Lock steps, the shooters and the Aircraft owners are told what changed.</summary>
    void StepLocks(float dt)
    {
        var candidatesBuilt = false;
        foreach (var c in _simClients)
        {
            var def = LauncherInHand(c);
            if (def == null)
            {
                if (c.Lock.Phase != LockPhase.Idle) c.Lock.Reset();
            }
            else
            {
                if (!candidatesBuilt) { BuildLockCandidates(); candidatesBuilt = true; }
                c.Lock.Update(dt, def, LockSeen(c, def));
            }
            if (!c.LockSent.Update((c.Lock.Phase, c.Lock.Target, c.Lock.Tracking))) continue;
            Send(c, new EventMsg((byte)EventKind.LockState, LockCodec.Encode(new LockStateMsg(c.Lock.Phase, c.Lock.Target, c.Lock.Tracking, c.Lock.Progress, def?.Seconds ?? 0f))));
        }
        FlushLockAlerts();
    }

    void BuildLockCandidates()
    {
        _lockCandidates.Clear();
        foreach (var (id, e) in _match.Entities.OfType(Aircraft.WireType))
            if (e is Aircraft { Ended: null } a) _lockCandidates.Add(new LockCandidate(id, a.Position, a.Def.Id));
    }

    /// <summary>The Aircraft a player's launcher looks at: an enemy's (never their own, never a teammate's), in the circle and the range, with nothing solid in between.</summary>
    ushort? LockSeen(Client c, LockDef def)
    {
        if (_lockCandidates.Count == 0) return null;
        var feet = c.Car != null ? SeatPos(c.Car, c.CarSeat) : c.Body.GlobalPosition;
        var eye = feet + Vector3.Up * (c.Car != null ? SeatAimRules.EyeHeight(c.Car.Def.Seats[c.CarSeat]) : MovementStep.EyeHeight(c.Body.State.Stance));
        var forward = HitMath.Forward(c.Body.Yaw, c.Body.Pitch);
        return LockRules.Pick(def, N(eye), forward, _lockCandidates, c.Lock.Target, cand =>
        {
            if (AircraftOwner(cand.Id) is not { } owner || !BotEnemies.IsEnemy(_match, c.Id, owner)) return false;
            var to = new Vector3(cand.Position.X, cand.Position.Y, cand.Position.Z) - eye;
            var len = to.Length();
            return WorldHit(eye, to / len, len, out _, c.Car) >= len - 0.05f;
        });
    }

    ushort? AircraftOwner(ushort id) => LiveAircraft(id)?.Owner;

    Aircraft? LiveAircraft(ushort id)
    {
        foreach (var (eid, e) in _match.Entities.OfType(Aircraft.WireType))
            if (eid == id) return e is Aircraft { Ended: null } a ? a : null;
        return null;
    }

    /// <summary>
    /// A pull on a complete Lock: the missile leaves, the Lock starts over. The flight is planned here; an Aircraft that cannot be reached (it left in
    /// this very tick) gets an ordinary rocket along the aim instead, which ends on whatever it meets.
    /// </summary>
    bool FireHoming(Client c, FireOutcome shot, Vector3 eye)
    {
        var w = shot.Weapon;
        var target = c.Lock.Target;
        c.Lock.Reset();
        if (LiveAircraft(target) is not { } aircraft || LockRules.Intercept(N(eye), w.ProjectileSpeed, w.ProjectileLife, aircraft.PositionAfter) is not { } plan) return false;
        var id = _nextRocket;
        _nextRocket = _nextRocket == ushort.MaxValue ? (ushort)0x8000 : (ushort)(_nextRocket + 1);
        _rocketsFired++;
        _homing.Add(new Homing(id, c.Id, (byte)shot.WeaponIndex, w, target, plan.At, _elapsed + plan.Seconds));
        BroadcastEvent(EventKind.HomingLaunch, LockCodec.Encode(new HomingLaunchMsg(id, c.Id, (byte)shot.WeaponIndex, N(eye), target, plan.Seconds)));
        return true;
    }

    /// <summary>
    /// The missiles whose flight has ended by now: one that still has its Aircraft hurts it where it flies (and shoots it down at 0), one whose
    /// Aircraft is gone bursts in the air where they would have met. Called once a tick, before the tick's gunship FX are sent (the hit marker rides them).
    /// </summary>
    void DetonateHoming()
    {
        for (var k = _homing.Count - 1; k >= 0; k--)
        {
            var m = _homing[k];
            if (m.Due > _elapsed) continue;
            _homing.RemoveAt(k);
            var def = m.Def.Lock!;
            var aircraft = LiveAircraft(m.Target);
            var at = aircraft?.Position ?? m.At;
            var down = aircraft != null && DamageAircraft(m.Target, aircraft, m.Def.Damage, m.Owner);
            if (aircraft != null) _gunFx.Hit(m.Owner, false, down);
            _explosions++;
            BroadcastEvent(EventKind.Explosion, WeaponCodec.Encode(new ExplosionMsg(m.Id, m.Owner, m.Weapon, at, down ? def.KillBlastRadius : def.HitBlastRadius)));
        }
    }

    /// <summary>Each Aircraft's owner hears the worst of what is on their Aircraft: a missile in the air, a complete Lock, a Lock past the alert share. Only changes are sent.</summary>
    void FlushLockAlerts()
    {
        _lockAlertsNow.Clear();
        foreach (var c in _simClients)
        {
            if (c.Lock.Phase == LockPhase.Idle || c.W.Current.Lock is not { } def) continue;
            RaiseLockAlert(c.Lock.Target, LockRules.AlertOf(def, c.Lock.Phase, c.Lock.Progress));
        }
        foreach (var m in _homing) RaiseLockAlert(m.Target, LockAlert.Launched);
        if (_lockAlertsNow.Count == 0 && _lockAlerts.Count == 0) return;

        foreach (var id in _lockAlerts.Keys)
            if (!_lockAlertsNow.ContainsKey(id)) _lockAlertsNow[id] = LockAlert.None;
        foreach (var (id, level) in _lockAlertsNow)
        {
            var had = _lockAlerts.GetValueOrDefault(id);
            if (had.Level == level) continue;
            // an Aircraft that is gone has no owner to ask: the one who was warned is told it is over
            var owner = level == LockAlert.None ? had.Owner : AircraftOwner(id) ?? had.Owner;
            if (level == LockAlert.None) _lockAlerts.Remove(id); else _lockAlerts[id] = (level, owner);
            if (_byId.TryGetValue(owner, out var pilot)) Send(pilot, new EventMsg((byte)EventKind.LockAlert, LockCodec.Encode(new LockAlertMsg(level, id))));
        }
    }

    void RaiseLockAlert(ushort aircraft, LockAlert level)
    {
        if (aircraft == 0 || level == LockAlert.None) return;
        if (_lockAlertsNow.GetValueOrDefault(aircraft) < level) _lockAlertsNow[aircraft] = level;
    }

    /// <summary>A new round: no missile flies on and nobody stays warned.</summary>
    void LockRoundReset()
    {
        _homing.Clear();
        foreach (var c in _byId.Values) c.Lock.Reset();
    }
}
