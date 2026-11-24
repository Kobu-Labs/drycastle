using System.Numerics;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Turrets;
using Drycastle.ShooterCore.Weapons;
using static Drycastle.ShooterCore.Movement.Planar;

namespace Drycastle.ShooterCore.Events;

/// <summary>
/// The Bigfoot (issue #264): a world entity with its own brain, not a bot. It has a hit count instead of hit points, three phases and an enrage, chases the
/// nearest player on a straight line over the terrain (through trees, round buildings), throws logs, stomps, throws vehicles, and grabs and launches
/// players. Godot-free: the server feeds it the world through <see cref="IBigfootWorld"/> and the shots through <see cref="Hit"/>.
/// </summary>
public sealed partial class Bigfoot : IWorldEntity
{
    readonly BigfootFile _d;
    readonly IBigfootWorld _w;
    readonly Random _rng;

    // life
    float _taken;                     // the hit count taken so far, in weighted hits
    int _need;                        // the hit count that kills it (only goes up, see Tick)
    bool _hitToggle;
    readonly Dictionary<ushort, int> _hitsBy = new();
    HashSet<ushort> _near = new();    // the living players within the participant radius at the last tick
    float _alone;                     // seconds with no living player within the despawn radius
    float _deadFor;

    // behaviour
    readonly StateMachine<BigfootAct> _act = new(BigfootSpecs.Act);     // the act and the seconds left of a timed one
    readonly StateMachine<BigfootLife> _life = new(BigfootSpecs.Life);  // fighting, waiting after a wipe, dead
    ushort _target;
    float _retarget;
    float _speed;                     // how fast it moves now: it gathers speed and sheds it (m/s)
    bool _running, _stopped;          // the chase's two choices, each with its own slack so a target on the line does not flip them every tick
    float _stuckFor;                  // seconds no heading let it through: a moment of that is not standing
    float _detourFor, _detourHeading; // the way round it found, kept for a moment so it does not turn back into the wall
    float _groundY;                   // the ground under it: its height follows this a little late, so a ledge is a ramp
    bool _roarDue;                    // a phase began (or the enrage): it roars the next time it is free to
    float _roarIn;                    // seconds of the fight until it roars by the clock (it runs between hunts)
    bool _hunting;                    // it sprints at _target and nobody else until it has them
    float _huntLeft;
    float _fight;                     // seconds awake with a target: the phases also begin by this clock
    float _trunkCd, _stompCd, _vehicleCd, _grabCd;
    ushort _vehicleToThrow;
    Vector3 _vehicleFrom;             // where the vehicle it lifts stood
    ushort _held;                     // the hold's clock is the Hold act's countdown
    readonly List<Wave> _waves = new();
    readonly List<Flyer> _flyers = new();
    Vector3 _targetSeen, _targetVelocity; // where the target was last tick and how fast it moves (the aim leads by it)
    bool _targetSeenValid;

    sealed class Wave { public Vector3 Origin; public float Radius; public readonly HashSet<ushort> Hit = new(); }
    sealed class Flyer { public ushort Player; public float Age, Left; public readonly HashSet<ushort> Hit = new(); }

    public Bigfoot(BigfootFile data, IBigfootWorld world, Vector3 spawn, float yaw, int seed, int participants = 0)
    {
        _d = data; _w = world; _rng = new Random(seed);
        Position = _w.GroundY(spawn.X, spawn.Z, spawn.Y) is { } y ? new Vector3(spawn.X, y, spawn.Z) : spawn;
        Yaw = yaw;
        _groundY = Position.Y;
        _roarIn = data.Hunt.RoarEverySeconds;
        _need = BigfootRules.HitsNeeded(data.Hits, participants);
        _trunkCd = 1.5f; // it does not open with a throw the moment it appears
    }

    public Vector3 Position { get; private set; }
    /// <summary>Godot convention: 0 faces -Z.</summary>
    public float Yaw { get; private set; }
    public BigfootAct Act => _act.Current;
    /// <summary>Fighting, waiting after a wipe, or dead (see <see cref="BigfootSpecs.Life"/>).</summary>
    public BigfootLife Life => _life.Current;
    public int Phase { get; private set; }
    public bool Enraged { get; private set; }
    public bool Alive => !_life.Is(BigfootLife.Dead);
    /// <summary>True from a wipe until a living player comes near again.</summary>
    public bool Waiting => _life.Is(BigfootLife.Waiting);
    /// <summary>True while it sprints at the one player it picked after a roar (<see cref="BigfootHunt"/>).</summary>
    public bool Hunting => _hunting;
    /// <summary>The hit count that kills it now.</summary>
    public int HitsNeeded => _need;
    /// <summary>The hits taken, weighted (a rocket counts many).</summary>
    public float HitsTaken => _taken;
    /// <summary>The fraction of the hit count taken, 0 to 1.</summary>
    public float TakenFraction => _need > 0 ? Math.Clamp(_taken / _need, 0f, 1f) : 0f;
    /// <summary>The player it holds, or 0.</summary>
    public ushort HeldPlayer => _held;
    public ushort Target => _target;
    /// <summary>Hits (not weights) by player since it was last healed.</summary>
    public IReadOnlyDictionary<ushort, int> HitsBy => _hitsBy;
    /// <summary>The players the win is credited to (set when it dies): those who hit it at least <see cref="BigfootReward.CreditMinHits"/> times, and every living player that was near.</summary>
    public IReadOnlyCollection<ushort> Credited { get; private set; } = Array.Empty<ushort>();
    /// <summary>The player whose hit killed it (0 when an admin ended it).</summary>
    public ushort Killer { get; private set; }
    /// <summary>Raised once when it dies.</summary>
    public event Action<Bigfoot>? Died;

    public byte TypeId => BigfootRules.TypeId;
    /// <summary>The player it holds: a client that sees its own id here stops predicting its body.</summary>
    public ushort Owner => _held;
    public WorldDespawnReason? Ended { get; private set; }

    /// <summary>The standing hit volume the shots test against: the player's capsule at <see cref="BigfootFile.HitScale"/>.</summary>
    public HitTarget HitVolume => new(BigfootRules.TargetId, Position, Stance.Stand, Yaw, HeadGuarded: true, Scale: _d.HitScale, BaseRadius: PlayerHitBox.WideRadius);

    // ---- hits ----

    /// <summary>
    /// A shot or a blast landed. <paramref name="weight"/> is what the hit counts (<see cref="BigfootRules.Weight"/>); no headshot bonus exists. Returns true when
    /// this hit killed it. A hit that does not kill flips the hit bit, so a client plays its reaction. A dead Bigfoot takes nothing; a waiting one does.
    /// </summary>
    public bool Hit(ushort attacker, float weight)
    {
        if (Ended != null || !Alive || !(weight > 0)) return false;
        _taken += weight;
        _hitsBy[attacker] = _hitsBy.GetValueOrDefault(attacker) + 1;
        if (Waiting) Wake(_near.Count); // shot while it waited: it is awake, and the hit count it needs is the one for who is near
        if (_taken >= _need) { Die(attacker); return true; }
        _hitToggle = !_hitToggle;
        UpdatePhase();
        return false;
    }

    public void OnDamage(float amount, ushort attacker) => Hit(attacker, amount);

    /// <summary>Kills it at once (an admin ended the event as a win). <see cref="Killer"/> is 0.</summary>
    public void Kill() { if (Alive && Ended == null) Die(0); }

    /// <summary>Removes it without a death (the event was cancelled or timed out).</summary>
    public void Remove(WorldDespawnReason reason)
    {
        ReleaseHeld();
        DropLiftedVehicle();
        Ended ??= reason;
    }

    void Die(ushort killer)
    {
        Killer = killer;
        _taken = _need;
        ReleaseHeld();
        DropLiftedVehicle();
        _waves.Clear();
        _flyers.Clear();
        _act.Enter(BigfootAct.Dead);
        _life.Enter(BigfootLife.Dead);
        var credited = new HashSet<ushort>(_near);
        foreach (var (p, n) in _hitsBy) if (n >= _d.Reward.CreditMinHits) credited.Add(p);
        if (killer != 0) credited.Add(killer);
        Credited = credited.ToList();
        Died?.Invoke(this);
    }

    void UpdatePhase()
    {
        var f = TakenFraction;
        var phase = Math.Max(BigfootRules.PhaseAt(_d.Hits, f), BigfootRules.PhaseAfter(_d.Hits, _fight));
        var enraged = BigfootRules.EnrageAt(_d.Hits, f) || BigfootRules.EnrageAfter(_d.Hits, _fight);
        if (phase > Phase) { _w.Fx(BigfootFxKind.Roar, Position, 0, (byte)phase); _roarDue = true; }
        if (enraged && !Enraged) _roarDue = true;
        Phase = Math.Max(Phase, phase);
        Enraged = Enraged || enraged;
    }

    // ---- the tick ----

    public void Tick(float dt)
    {
        if (Ended != null || dt <= 0) return;
        if (!Alive)
        {
            _deadFor += dt;
            if (_deadFor >= _d.Reward.DeathSeconds) Ended = WorldDespawnReason.Destroyed;
            return;
        }
        var players = _w.Players;
        TrackTarget(players, dt);
        UpdateNearby(players, dt);
        if (Ended != null) return;
        TickWaves(players, dt);
        TickFlyers(players, dt);
        _trunkCd -= dt; _stompCd -= dt; _vehicleCd -= dt; _grabCd -= dt;
        Position = new Vector3(Position.X, Position.Y + Math.Clamp(_groundY - Position.Y, -ClimbMps * dt, ClimbMps * dt), Position.Z);
        if (Waiting) { Rest(); return; }
        if (_target != 0) { _fight += dt; UpdatePhase(); if (!_hunting) _roarIn -= dt; }

        switch (Act)
        {
            case BigfootAct.TrunkWindup:
                FaceTarget(players, dt);
                if (_act.Tick(dt)) ReleaseTrunk(players);
                break;
            case BigfootAct.StompWindup:
                if (_act.Tick(dt)) Stomp();
                break;
            case BigfootAct.VehicleWindup:
                if (_act.Tick(dt)) ThrowTheVehicle();
                break;
            case BigfootAct.GrabReach:
                if (_hunting) Lunge(players, dt);
                if (_act.Tick(dt)) TakeHold(players);
                break;
            case BigfootAct.Hold:
                TickHold(players, dt);
                break;
            case BigfootAct.Roar:
                FaceTarget(players, dt);
                if (_act.Tick(dt)) { Rest(); StartHunt(players); }
                break;
            case BigfootAct.Stomp:
            case BigfootAct.Launch:
                if (_act.Tick(dt)) Rest();
                break;
            default:
                Decide(players, dt);
                break;
        }
    }

    /// <summary>The players near it (participants), the wipe, the wake, the despawn clock and the hit count the nearby players need.</summary>
    void UpdateNearby(IReadOnlyList<BigfootPlayer> players, float dt)
    {
        var near = new HashSet<ushort>();
        var anyInDespawn = false;
        var anyWake = false;
        foreach (var p in players)
        {
            var d = Flat(p.Feet, Position);
            if (d <= _d.Hits.ParticipantRadiusM) near.Add(p.Id);
            if (d <= _d.Wipe.DespawnRadiusM) anyInDespawn = true;
            if (d <= _d.Wipe.WakeRadiusM) anyWake = true;
        }
        // the last of the players who were near died or left (they are not listed any more, and nobody else is near): that is a wipe;
        // players who walk out of the radius alive are not
        var wiped = _near.Count > 0 && near.Count == 0 && _near.All(id => !players.Any(p => p.Id == id));
        _near = near;

        _alone = anyInDespawn ? 0 : _alone + dt;
        if (_alone >= _d.Wipe.DespawnMinutes * 60f) { Remove(WorldDespawnReason.Expired); return; }

        if (Waiting)
        {
            if (anyWake) Wake(near.Count);
            return;
        }
        _need = Math.Max(_need, BigfootRules.HitsNeeded(_d.Hits, near.Count)); // the count only goes up: players who leave do not make it easier
        if (_taken > 0 && wiped) Wipe();
    }

    /// <summary>Every participant died: it heals to full and waits where it stands.</summary>
    void Wipe()
    {
        _taken = 0; _hitsBy.Clear(); _life.Enter(BigfootLife.Waiting);
        Phase = 0; Enraged = false; _fight = 0; _roarDue = false; EndHunt();
        _need = BigfootRules.HitsNeeded(_d.Hits, 0);
        ReleaseHeld();
        DropLiftedVehicle();
        _waves.Clear(); _flyers.Clear();
        Rest();
        _target = 0;
        _w.Fx(BigfootFxKind.Heal, Position);
    }

    /// <summary>A living player came within the wake radius, or a shot landed: it fights again, needing the hit count for the players near it (the count only goes up).</summary>
    void Wake(int near)
    {
        _life.Enter(BigfootLife.Fighting);
        _need = Math.Max(_need, BigfootRules.HitsNeeded(_d.Hits, near));
    }

    /// <summary>Back to standing: where every ended act, a lost target and a wipe go.</summary>
    void Rest() { _speed = 0; _act.Enter(BigfootAct.Idle); }

    /// <summary>The fastest its height follows the ground (m/s): a ledge of <see cref="BigfootMovement.MaxStepM"/> takes a fifth of a second, a steep slope at a run is still kept up with.</summary>
    const float ClimbMps = 20f;
    /// <summary>How it gathers and sheds speed (m/s per second): a run is reached in about a second.</summary>
    const float AccelMps2 = 14f, DecelMps2 = 30f;
    /// <summary>It runs until its target is this share of <see cref="BigfootMovement.RunBeyondM"/> near, and stands until the target is this much beyond the stop distance.</summary>
    const float RunKeepShare = 0.8f, StopSlackM = 1.2f;
    /// <summary>A way round a building is kept this long before it turns to its target again; no way through for this long is standing.</summary>
    const float DetourSeconds = 0.6f, StuckSeconds = 0.3f;

    float SpeedMul => Enraged ? _d.Hits.EnrageSpeedMultiplier : 1f;
    float CooldownMul => Enraged ? _d.Hits.EnrageCooldownMultiplier : 1f;

    /// <summary>Idle, walk or run: pick the next thing to do. The first thing that applies wins, the heaviest first.</summary>
    void Decide(IReadOnlyList<BigfootPlayer> players, float dt)
    {
        var target = Hunted(players, dt) ?? Retarget(players, dt);
        if (target is not { } t) { Rest(); return; }
        var dist = Flat(t.Feet, Position);

        if (_hunting) // nothing but its prey: it sprints at them and lunges when it is on them, whatever the grab's cooldown says; a miss and it sprints on
        {
            if (!t.InVehicle && dist <= _d.Movement.StopDistanceM + StopSlackM)
            {
                _act.Enter(BigfootAct.GrabReach, _d.Grab.ReachSeconds); FaceYaw(t.Feet, dt, snap: true);
                return;
            }
            if (dist <= _d.Movement.StopDistanceM + StopSlackM) EndHunt(); // prey it cannot pick up (in a vehicle): the vehicle is its business now
            else { Chase(t, dist, dt, sprint: true); return; }
        }

        if (_vehicleCd <= 0 && FindVehicleInContact() is { } car) // every attack is its from the start; the phases only make it angrier
        {
            // it takes the vehicle in both hands and lifts it over its head for the wind-up; the throw starts from up there
            _vehicleToThrow = car.Id; _vehicleFrom = car.Feet; _act.Enter(BigfootAct.VehicleWindup, _d.VehicleThrow.WindupSeconds); FaceYaw(car.Feet, dt, snap: true);
            _w.LiftVehicle(car.Id, Position + new Vector3(0, _d.VehicleThrow.HoldHeightM, 0), _d.VehicleThrow.WindupSeconds * 0.75f);
            _w.Fx(BigfootFxKind.VehicleThrow, car.Feet, car.Id, (byte)Math.Clamp((int)MathF.Round((_d.VehicleThrow.WindupSeconds + _d.VehicleThrow.FlightSeconds) * 10), 1, 255));
            return;
        }
        if (_grabCd <= 0 && NearestOnFoot(players, _d.Grab.RangeM) is { } grab)
        {
            _target = grab.Id; _act.Enter(BigfootAct.GrabReach, _d.Grab.ReachSeconds); FaceYaw(grab.Feet, dt, snap: true);
            return;
        }
        if (_stompCd <= 0 && NearestOnFoot(players, _d.Stomp.TriggerM) != null)
        {
            _act.Enter(BigfootAct.StompWindup, _d.Stomp.WindupSeconds);
            _w.Fx(BigfootFxKind.StompWindup, Position);
            return;
        }
        if (_trunkCd <= 0 && dist >= _d.Trunk.MinRangeM && dist <= _d.Trunk.MaxRangeM && !_w.Blocked(Chest, ChestOf(t.Feet), 0.3f))
        {
            _act.Enter(BigfootAct.TrunkWindup, _d.Trunk.WindupSeconds); FaceYaw(t.Feet, dt, snap: true);
            return;
        }
        if (_roarDue || _roarIn <= 0) // a phase began, the enrage, or the clock: with no attack to make this tick it stops and roars, then hunts
        {
            _roarDue = false; _speed = 0; _roarIn = _d.Hunt.RoarEverySeconds;
            _act.Enter(BigfootAct.Roar, BigfootRules.RoarSeconds);
            return;
        }
        Chase(t, dist, dt);
    }

    // ---- targeting and movement ----

    /// <summary>The target's velocity, from where it stood the tick before: a log is thrown ahead of a runner.</summary>
    void TrackTarget(IReadOnlyList<BigfootPlayer> players, float dt)
    {
        foreach (var p in players)
        {
            if (p.Id != _target) continue;
            _targetVelocity = _targetSeenValid && dt > 0 ? (p.Feet - _targetSeen) / dt : Vector3.Zero;
            _targetVelocity.Y = 0;
            _targetSeen = p.Feet; _targetSeenValid = true;
            return;
        }
        _targetSeenValid = false; _targetVelocity = Vector3.Zero;
    }

    /// <summary>The nearest living player, kept for <see cref="BigfootMovement.RetargetSeconds"/>; a target that is gone is dropped at once.</summary>
    BigfootPlayer? Retarget(IReadOnlyList<BigfootPlayer> players, float dt)
    {
        _retarget -= dt;
        BigfootPlayer? current = null;
        foreach (var p in players) if (p.Id == _target) current = p;
        if (current is { } c && Flat(c.Feet, Position) > _d.Movement.GiveUpM) current = null; // out of reach: given up at once
        if (current != null && _retarget > 0) return current;
        BigfootPlayer? best = null; var bd = _d.Movement.GiveUpM;
        foreach (var p in players)
        {
            var d = Flat(p.Feet, Position);
            if (d <= bd) { bd = d; best = p; }
        }
        if ((best?.Id ?? 0) != _target) _targetSeenValid = false;
        _target = best?.Id ?? 0;
        _retarget = _d.Movement.RetargetSeconds;
        return best;
    }

    BigfootPlayer? NearestOnFoot(IReadOnlyList<BigfootPlayer> players, float within)
    {
        BigfootPlayer? best = null; var bd = within;
        foreach (var p in players)
        {
            if (p.InVehicle) continue;
            var d = Flat(p.Feet, Position);
            if (d <= bd) { bd = d; best = p; }
        }
        return best;
    }

    const float CarHalfLength = 2.3f;

    BigfootVehicle? FindVehicleInContact()
    {
        BigfootVehicle? best = null; var bd = _d.VehicleThrow.ContactM;
        foreach (var v in _w.Vehicles)
        {
            // the distance to its middle, less what a long vehicle is longer than a car; and only one it can reach up to (a helicopter that hovers low, not one that flies over)
            var d = Flat(v.Feet, Position) - MathF.Max(0, v.HalfLength - CarHalfLength);
            if (v.Feet.Y - Position.Y > _d.VehicleThrow.HoldHeightM + 2f) continue;
            if (d <= bd) { bd = d; best = v; }
        }
        return best;
    }

    /// <summary>After a roar: one living player within <see cref="BigfootHunt.RadiusM"/>, picked at random, is its prey until it has them (or the hunt runs out).</summary>
    void StartHunt(IReadOnlyList<BigfootPlayer> players)
    {
        var near = players.Where(p => Flat(p.Feet, Position) <= _d.Hunt.RadiusM).ToList();
        if (near.Count == 0) return;
        var prey = near[_rng.Next(near.Count)];
        if (prey.Id != _target) _targetSeenValid = false;
        _target = prey.Id; _hunting = true; _huntLeft = _d.Hunt.MaxSeconds;
    }

    /// <summary>The hunt is over (caught, lost, timed out, a wipe): it picks its target by distance again, and the clock to the next roar runs.</summary>
    void EndHunt()
    {
        if (!_hunting) return;
        _hunting = false; _retarget = 0;
        _roarIn = _d.Hunt.RoarEverySeconds;
    }

    /// <summary>Its prey while it hunts; null when it does not, or the prey is dead, gone, out of reach or the hunt has run out (which ends it).</summary>
    BigfootPlayer? Hunted(IReadOnlyList<BigfootPlayer> players, float dt)
    {
        if (!_hunting) return null;
        _huntLeft -= dt;
        foreach (var p in players)
            if (p.Id == _target && _huntLeft > 0 && Flat(p.Feet, Position) <= _d.Movement.GiveUpM) return p;
        EndHunt();
        return null;
    }

    /// <summary>The hunt's grab is made on the move: it keeps after its prey through the reach, slower than its run but faster than a player sprints.</summary>
    void Lunge(IReadOnlyList<BigfootPlayer> players, float dt)
    {
        foreach (var p in players)
        {
            if (p.Id != _target) continue;
            if (Flat(p.Feet, Position) > _d.Movement.StopDistanceM) Step(p.Feet, _d.Movement.RunSpeed * SpeedMul * LungeShare, dt);
            else FaceYaw(p.Feet, dt);
            return;
        }
    }

    const float LungeShare = 0.7f;

    void Chase(BigfootPlayer t, float dist, float dt, bool sprint = false)
    {
        var m = _d.Movement;
        _running = sprint || dist > (_running ? m.RunBeyondM * RunKeepShare : m.RunBeyondM);
        _stopped = !sprint && dist <= (_stopped ? m.StopDistanceM + StopSlackM : m.StopDistanceM);
        if (_stopped) { FaceYaw(t.Feet, dt); Rest(); return; }
        var top = (_running ? m.RunSpeed : m.WalkSpeed) * SpeedMul;
        _speed = _speed < top ? MathF.Min(top, _speed + AccelMps2 * dt) : MathF.Max(top, _speed - DecelMps2 * dt);
        switch (Step(t.Feet, _speed, dt))
        {
            case Stepped.Moved:
                _stuckFor = 0;
                _act.Enter(_running ? BigfootAct.Run : BigfootAct.Walk);
                break;
            case Stepped.Turning: // it turns on the spot at a walk, whatever it was doing, and gathers speed again afterwards
                _stuckFor = 0; _speed = 0;
                _act.Enter(BigfootAct.Walk);
                break;
            default: // no heading let it through: one such tick in a chase is not standing
                _speed = 0; _stuckFor += dt;
                if (_stuckFor >= StuckSeconds || Act == BigfootAct.Idle) _act.Enter(BigfootAct.Idle);
                break;
        }
    }

    enum Stepped { Moved, Turning, Stuck }

    void FaceTarget(IReadOnlyList<BigfootPlayer> players, float dt)
    {
        foreach (var p in players) if (p.Id == _target) { FaceYaw(p.Feet, dt); return; }
    }

    void FaceYaw(Vector3 at, float dt, bool snap = false)
    {
        var want = TurretMath.YawTo(Position, at);
        Yaw = snap ? want : TurretMath.StepAngle(Yaw, want, _d.Movement.TurnRateDegPerS * MathF.PI / 180f * dt * 2f);
    }

    /// <summary>
    /// Turns towards <paramref name="goal"/> and, once roughly facing it, walks one step on the terrain. A building in the way (<see cref="IBigfootWorld.Blocked"/>)
    /// or a step the ground refuses turns the heading aside, the way an animal goes round; trees are not in the way. The way round is kept for
    /// <see cref="DetourSeconds"/> and faced, so it walks along it instead of sidling and turning back into the wall. Its height follows the ground a little
    /// late (<see cref="ClimbMps"/>).
    /// </summary>
    Stepped Step(Vector3 goal, float speed, float dt)
    {
        var want = TurretMath.YawTo(Position, goal);
        var rate = _d.Movement.TurnRateDegPerS * MathF.PI / 180f * dt;
        if (_detourFor > 0) { _detourFor -= dt; want = _detourHeading; rate *= 2f; }
        Yaw = TurretMath.StepAngle(Yaw, want, rate);
        if (MathF.Abs(TurretMath.AngleDiff(Yaw, want)) > 1.0f) return Stepped.Turning;
        foreach (var turn in new[] { 0f, 0.7f, -0.7f, 1.4f, -1.4f })
        {
            var heading = Yaw + turn;
            var f = new Vector3(-MathF.Sin(heading), 0, -MathF.Cos(heading));
            var next = Position + f * speed * dt;
            if (_w.GroundY(next.X, next.Z, Position.Y) is not { } y || MathF.Abs(y - Position.Y) > _d.Movement.MaxStepM) continue;
            if (_w.Blocked(Position + Vector3.UnitY, new Vector3(next.X, Position.Y + 1f, next.Z), _d.Movement.BodyRadiusM)) continue;
            _groundY = y;
            Position = new Vector3(next.X, Position.Y + Math.Clamp(y - Position.Y, -ClimbMps * dt, ClimbMps * dt), next.Z);
            if (turn != 0) { _detourHeading = heading; _detourFor = DetourSeconds; }
            return Stepped.Moved;
        }
        return Stepped.Stuck;
    }

    // ---- replication ----

    public WorldState Serialize()
    {
        var state = BigfootRules.Pack(Act, _hitToggle, Phase, Enraged);
        var hp = Alive ? MathF.Ceiling(100f * (1f - TakenFraction)) : 0f;
        return WorldState.Make(Position, Yaw, 0, _held, state, Alive ? MathF.Max(1f, hp) : 0f);
    }
}