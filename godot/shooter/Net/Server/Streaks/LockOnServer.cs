using Godot;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Net.Server.Seating;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Bots;
using Drycastle.ShooterCore.Flight;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Streaks;

/// <summary>
/// The Lock-on launcher on the server (ADR 0031, glossary "Lock-on launcher", "Lock", "Homing missile"). The server alone decides the Lock: once a
/// tick, for every player with the launcher in hand, which enemy Aircraft the lock circle holds in sight (<see cref="LockRules.Pick"/>) and how far
/// the Lock is (<see cref="LockOn"/>); the shooter is sent their Lock when it changes, and the Aircraft's owner the worst of the launchers on it
/// (<see cref="LockAlert"/>). A pull on a complete Lock fires a missile whose whole flight is known at once, because an Aircraft flies a fixed
/// circle (<see cref="LockRules.Intercept"/>, as the flak's shell): the damage lands when it arrives, through the path every anti-aircraft uses
/// (<see cref="GunshipServer.DamageAircraft"/>, ADR 0005), and the blast is the ordinary Explosion event (a kill's is big enough for a Blast wave).
/// </summary>
internal sealed class LockOnServer
{
    /// <summary>A missile on its way: its id (the Explosion's), who fired it with which weapon, the Aircraft it homes on, where it was planned to arrive and when.</summary>
    sealed record Homing(ushort Id, ushort Owner, byte Weapon, WeaponDef Def, ushort Target, NVec3 At, double Due);

    /// <summary>A missile homing on a flown helicopter (a vehicle, so its path is not known at the fire): it flies on the server and re-aims each tick at the vehicle (ADR 0031).</summary>
    sealed class VehicleHoming
    {
        public ushort Id, Owner, Target; public byte Weapon; public WeaponDef Def = null!; public NVec3 Pos; public float Age;
    }

    readonly ServerContext _ctx;
    readonly Fleet _fleet;
    readonly GunshipServer _gunships;
    readonly Func<KillstreakService> _streaks;
    readonly Sitters _sitters;
    readonly Func<ushort, bool> _isPinned;
    readonly Func<ServerCar, int, Vector3> _seatPos;
    readonly WorldHitProbe _worldHit;
    readonly RocketIds _rocketIds;
    readonly SmokeStats _smoke;
    readonly Action<ServerCar, float, ushort, byte, float> _damageVehicle;
    readonly Func<VehiclesFile?> _vehicles;
    readonly IReadOnlyList<ServerClient> _simClients;
    readonly List<Homing> _homing = new();
    readonly List<VehicleHoming> _vehicleHoming = new();
    /// <summary>The level last sent for each target (an Aircraft's entity id, a helicopter's vehicle id) and who it went to, so only a change is sent.</summary>
    readonly Dictionary<ushort, (LockAlert Level, ushort[] Who)> _alerts = new();
    readonly Dictionary<ushort, LockAlert> _alertsNow = new();
    readonly List<LockCandidate> _candidates = new();

    /// <param name="isPinned">Whether the player is held by the demon (no launcher then).</param>
    /// <param name="seatPos">Where a vehicle's seat is in the world.</param>
    /// <param name="simClients">The players simulated this tick, in order (the list the server refills before it steps the Locks).</param>
    public LockOnServer(ServerContext ctx, Fleet fleet, GunshipServer gunships, Func<KillstreakService> streaks, Sitters sitters, Func<ushort, bool> isPinned, Func<ServerCar, int, Vector3> seatPos,
        WorldHitProbe worldHit, RocketIds rocketIds, SmokeStats smoke, Action<ServerCar, float, ushort, byte, float> damageVehicle, Func<VehiclesFile?> vehicles, IReadOnlyList<ServerClient> simClients)
    {
        _ctx = ctx; _fleet = fleet; _gunships = gunships; _streaks = streaks; _sitters = sitters; _isPinned = isPinned; _seatPos = seatPos; _worldHit = worldHit; _rocketIds = rocketIds;
        _smoke = smoke; _damageVehicle = damageVehicle; _vehicles = vehicles; _simClients = simClients;
    }

    /// <summary>The Lock of the weapon in a player's hand when they can use it now: alive, not a bot, on foot or in an armed seat, not at a streak's controls.</summary>
    LockDef? LauncherInHand(ServerClient c)
    {
        if (c.IsBot || c.W.Current.Lock is not { } def) return null;
        if (!_ctx.IsAlive(c.Id)) return null;
        if (_streaks().ControlOf(c.Id) != null || _sitters.IsSeated(c.Id) || _isPinned(c.Id)) return null;
        if (!LockRules.SeatAllows(c.Car != null, c.Car != null && c.CarSeat >= 0 && c.CarSeat < c.Car.Def.Seats.Count && c.Car.Def.Seats[c.CarSeat].Armed)) return null;
        return def;
    }

    /// <summary>Whether this input's pull may fire a weapon that needs a Lock (<see cref="WeaponInput.Locked"/>).</summary>
    public static bool HasLock(ServerClient c) => c.Lock.Phase == LockPhase.Locked;

    /// <summary>Once a tick, after the players moved: every Lock steps, the shooters and the Aircraft owners are told what changed.</summary>
    public void Step(float dt)
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
                if (!candidatesBuilt) { BuildCandidates(); candidatesBuilt = true; }
                c.Lock.Update(dt, def, Seen(c, def));
            }
            if (!c.LockSent.Update((c.Lock.Phase, c.Lock.Target, c.Lock.Tracking))) continue;
            _ctx.SendEvent(c, EventKind.LockState, LockCodec.Encode(new LockStateMsg(c.Lock.Phase, c.Lock.Target, c.Lock.Tracking, c.Lock.Progress, def?.Seconds ?? 0f)));
        }
        FlushAlerts();
    }

    void BuildCandidates()
    {
        _candidates.Clear();
        foreach (var (id, e) in _ctx.Match.Entities.OfType(Aircraft.WireType))
            if (e is Aircraft { Ended: null } a) _candidates.Add(new LockCandidate(id, a.Position, a.Def.Id));
        foreach (var car in _fleet.Cars)
            if (LockableHeli(car)) _candidates.Add(new LockCandidate(car.Id, car.Flight.Position, car.Def.Id));
    }

    /// <summary>A flown (piloted, off the ground) air vehicle: the Lock-on launcher's other target. A landed or empty one is not.</summary>
    static bool LockableHeli(ServerCar car) => car is { Destroyed: false, Flies: true } && !car.Flight.Grounded && car.Seat.Occupant(0) != 0;

    /// <summary>Whether an id is a vehicle's (the ranges of vehicle and world entity ids do not meet).</summary>
    static bool IsVehicleId(ushort id) => id >= VehicleRecord.FirstId;

    ServerCar? LiveHeli(ushort id) => IsVehicleId(id) ? _fleet.Find(id) is { } c && LockableHeli(c) ? c : null : null;

    /// <summary>Who is warned about a Lock on <paramref name="id"/>: an Aircraft's owner, everybody aboard a helicopter. Empty when the target is gone.</summary>
    ushort[] AlertRecipients(ushort id)
    {
        if (IsVehicleId(id)) return _fleet.Find(id) is { Destroyed: false } car ? Fleet.SeatOccupants(car).Where(o => o != 0).ToArray() : Array.Empty<ushort>();
        return LiveAircraft(id) is { } a ? new[] { a.Owner } : Array.Empty<ushort>();
    }

    /// <summary>The Aircraft a player's launcher looks at: an enemy's (never their own, never a teammate's), in the circle and the range, with nothing solid in between.</summary>
    ushort? Seen(ServerClient c, LockDef def)
    {
        if (_candidates.Count == 0) return null;
        var feet = c.Car != null ? _seatPos(c.Car, c.CarSeat) : c.Body.GlobalPosition;
        var eye = feet + Vector3.Up * (c.Car != null ? SeatAimRules.EyeHeight(c.Car.Def.Seats[c.CarSeat]) : MovementStep.EyeHeight(c.Body.State.Stance));
        var forward = HitMath.Forward(c.Body.Yaw, c.Body.Pitch);
        return LockRules.Pick(def, eye.ToNumerics(), forward, _candidates, c.Lock.Target, cand =>
        {
            var owner = IsVehicleId(cand.Id) ? LiveHeli(cand.Id)?.Seat.Occupant(0) : LiveAircraft(cand.Id)?.Owner;
            if (owner is not { } o || o == 0 || !BotEnemies.IsEnemy(_ctx.Match, c.Id, o)) return false;
            var to = new Vector3(cand.Position.X, cand.Position.Y, cand.Position.Z) - eye;
            var len = to.Length();
            return _worldHit(eye, to / len, len, out _, c.Car) >= len - 0.05f;
        });
    }

    Aircraft? LiveAircraft(ushort id)
    {
        foreach (var (eid, e) in _ctx.Match.Entities.OfType(Aircraft.WireType))
            if (eid == id) return e is Aircraft { Ended: null } a ? a : null;
        return null;
    }

    /// <summary>
    /// A pull on a complete Lock: the missile leaves, the Lock starts over. The flight is planned here; an Aircraft that cannot be reached (it left in
    /// this very tick) gets an ordinary rocket along the aim instead (false: the caller fires it), which ends on whatever it meets.
    /// </summary>
    public bool Fire(ServerClient c, FireOutcome shot, Vector3 eye)
    {
        var w = shot.Weapon;
        var target = c.Lock.Target;
        c.Lock.Reset();
        if (LiveHeli(target) is { })
        {
            var hid = _rocketIds.Next();
            _smoke.RocketsFired++;
            _vehicleHoming.Add(new VehicleHoming { Id = hid, Owner = c.Id, Target = target, Weapon = (byte)shot.WeaponIndex, Def = w, Pos = eye.ToNumerics() });
            _ctx.Broadcast(EventKind.HomingLaunch, LockCodec.Encode(new HomingLaunchMsg(hid, c.Id, (byte)shot.WeaponIndex, eye.ToNumerics(), target, 0f))); // Seconds 0: the client flies it as the server does
            return true;
        }
        if (LiveAircraft(target) is not { } aircraft || LockRules.Intercept(eye.ToNumerics(), w.ProjectileSpeed, w.ProjectileLife, aircraft.PositionAfter) is not { } plan) return false;
        var id = _rocketIds.Next();
        _smoke.RocketsFired++;
        _homing.Add(new Homing(id, c.Id, (byte)shot.WeaponIndex, w, target, plan.At, _ctx.Now + plan.Seconds));
        _ctx.Broadcast(EventKind.HomingLaunch, LockCodec.Encode(new HomingLaunchMsg(id, c.Id, (byte)shot.WeaponIndex, eye.ToNumerics(), target, plan.Seconds)));
        return true;
    }

    /// <summary>
    /// The missiles whose flight has ended by now: one that still has its Aircraft hurts it where it flies (and shoots it down at 0), one whose
    /// Aircraft is gone bursts in the air where they would have met. Called once a tick, before the tick's gunship FX are sent (the hit marker rides them).
    /// </summary>
    public void DetonateHoming()
    {
        for (var k = _homing.Count - 1; k >= 0; k--)
        {
            var m = _homing[k];
            if (m.Due > _ctx.Now) continue;
            _homing.RemoveAt(k);
            var def = m.Def.Lock!;
            var aircraft = LiveAircraft(m.Target);
            var at = aircraft?.Position ?? m.At;
            var down = aircraft != null && _gunships.DamageAircraft(m.Target, aircraft, m.Def.Damage, m.Owner);
            if (aircraft != null) _gunships.Fx.Hit(m.Owner, false, down);
            _smoke.Explosions++;
            _ctx.Broadcast(EventKind.Explosion, WeaponCodec.Encode(new ExplosionMsg(m.Id, m.Owner, m.Weapon, at, down ? def.KillBlastRadius : def.HitBlastRadius)));
        }
    }

    /// <summary>
    /// Flies the missiles homing on helicopters one tick: each re-aims at its vehicle's position now and moves <c>projectile_speed</c> metres a second.
    /// It hurts the vehicle where it arrives (through the vehicle damage path, credited to the shooter); one whose vehicle is gone (destroyed,
    /// landed, emptied) or that outlives <c>projectile_life</c> bursts in the air where it is.
    /// </summary>
    public void StepVehicleHoming(float dt)
    {
        for (var k = _vehicleHoming.Count - 1; k >= 0; k--)
        {
            var m = _vehicleHoming[k];
            var def = m.Def.Lock!;
            var car = LiveHeli(m.Target);
            m.Age += dt;
            if (car != null)
            {
                var (next, hit) = LockRules.Pursue(m.Pos, car.Flight.Position, m.Def.ProjectileSpeed, MathF.Max(car.HalfLength, 2f), dt);
                if (hit)
                {
                    _vehicleHoming.RemoveAt(k);
                    _damageVehicle(car, m.Def.Damage * (_vehicles()?.RocketDamageMultiplier ?? 1f), m.Owner, m.Weapon, 0);
                    var down = car.Hp <= 0;
                    _gunships.Fx.Hit(m.Owner, false, down);
                    _smoke.Explosions++;
                    _ctx.Broadcast(EventKind.Explosion, WeaponCodec.Encode(new ExplosionMsg(m.Id, m.Owner, m.Weapon, car.Flight.Position, down ? def.KillBlastRadius : def.HitBlastRadius)));
                    continue;
                }
                m.Pos = next;
            }
            if (car != null && m.Age <= m.Def.ProjectileLife) continue;
            _vehicleHoming.RemoveAt(k);
            _smoke.Explosions++;
            _ctx.Broadcast(EventKind.Explosion, WeaponCodec.Encode(new ExplosionMsg(m.Id, m.Owner, m.Weapon, m.Pos, def.HitBlastRadius)));
        }
    }

    /// <summary>Each target's occupants hear the worst of what is on their Aircraft: a missile in the air, a complete Lock, a Lock past the alert share. Only changes are sent.</summary>
    void FlushAlerts()
    {
        _alertsNow.Clear();
        foreach (var c in _simClients)
        {
            if (c.Lock.Phase == LockPhase.Idle || c.W.Current.Lock is not { } def) continue;
            Raise(c.Lock.Target, LockRules.AlertOf(def, c.Lock.Phase, c.Lock.Progress));
        }
        foreach (var m in _homing) Raise(m.Target, LockAlert.Launched);
        foreach (var m in _vehicleHoming) Raise(m.Target, LockAlert.Launched);
        if (_alertsNow.Count == 0 && _alerts.Count == 0) return;

        foreach (var id in _alerts.Keys)
            if (!_alertsNow.ContainsKey(id)) _alertsNow[id] = LockAlert.None;
        foreach (var (id, level) in _alertsNow)
        {
            var had = _alerts.GetValueOrDefault(id, (Level: LockAlert.None, Who: Array.Empty<ushort>()));
            // a target that is gone has nobody to ask: those who were warned are told it is over
            var now = level == LockAlert.None ? Array.Empty<ushort>() : AlertRecipients(id);
            if (had.Level == level && had.Who.SequenceEqual(now)) continue;
            if (level == LockAlert.None) _alerts.Remove(id); else _alerts[id] = (level, now);
            foreach (var who in had.Who.Except(now)) SendAlert(who, LockAlert.None, id);
            foreach (var who in now) if (had.Level != level || !had.Who.Contains(who)) SendAlert(who, level, id);
        }
    }

    void SendAlert(ushort player, LockAlert level, ushort target)
    {
        if (_ctx.Players.TryGetValue(player, out var pilot)) _ctx.SendEvent(pilot, EventKind.LockAlert, LockCodec.Encode(new LockAlertMsg(level, target)));
    }

    void Raise(ushort aircraft, LockAlert level)
    {
        if (aircraft == 0 || level == LockAlert.None) return;
        if (_alertsNow.GetValueOrDefault(aircraft) < level) _alertsNow[aircraft] = level;
    }

    /// <summary>A new round: no missile flies on and nobody stays warned.</summary>
    public void RoundReset()
    {
        _homing.Clear();
        _vehicleHoming.Clear();
        foreach (var c in _ctx.Players.Values) c.Lock.Reset();
    }
}
