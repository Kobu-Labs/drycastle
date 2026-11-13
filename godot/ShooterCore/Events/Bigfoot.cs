using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>A player as the Bigfoot sees them: where their feet are and whether they stand on the ground or ride a vehicle. Only living players are listed.</summary>
public readonly record struct BigfootPlayer(ushort Id, Vector3 Feet, bool OnFloor, bool InVehicle);

/// <summary>A vehicle it may throw: a ground vehicle that is not already flying. <see cref="Feet"/> is where it stands.</summary>
public readonly record struct BigfootVehicle(ushort Id, Vector3 Feet);

/// <summary>What of the Bigfoot's damage a hurt was (the kill feed and the logs name it).</summary>
public enum BigfootHurt : byte { Trunk, Shockwave, Landing, LaunchHit }

/// <summary>The one-shot effects the clients play: a sound, a shake, a ring (<see cref="BigfootFx"/> event).</summary>
public enum BigfootFxKind : byte
{
    /// <summary>It appeared (the event began).</summary>
    Appear = 0,
    /// <summary>A phase began (the roar): u8 phase in the extra byte.</summary>
    Roar = 1,
    /// <summary>It began to wind up a stomp: the telegraph (a sound; the pose is in the entity's state).</summary>
    StompWindup = 2,
    /// <summary>The shockwave began at this point; clients draw a ring that grows at the shockwave's speed.</summary>
    Shockwave = 3,
    /// <summary>It released a log (a throw sound at its hand).</summary>
    TrunkThrow = 4,
    /// <summary>A vehicle was thrown: the vehicle's id (u16) follows, then the flight in tenths of a second (u8).</summary>
    VehicleThrow = 5,
    /// <summary>A thrown vehicle landed: its id (u16) follows.</summary>
    VehicleLand = 6,
    /// <summary>It picked a player up: the player's id (u16) follows.</summary>
    Grab = 7,
    /// <summary>It launched a player: the player's id (u16) follows.</summary>
    Launch = 8,
    /// <summary>It died.</summary>
    Death = 9,
    /// <summary>It healed after a wipe.</summary>
    Heal = 10,
}

/// <summary>
/// What the Bigfoot needs from the server: the ground and the solid world under it, the players and the vehicles, and the hands to hurt, hold, throw and
/// show. The server implements it (<c>NetServer.Bigfoot.cs</c>); a test fakes it. Everything about the fight that needs the physics engine or a
/// connection is behind it, so the brain itself is plain code.
/// </summary>
public interface IBigfootWorld
{
    /// <summary>The ground height at (x, z) for something standing near height <paramref name="nearY"/>, or null where it must not go (no ground, water too deep). Trees are no ground.</summary>
    float? GroundY(float x, float z, float nearY);

    /// <summary>True when the solid world (a building, a wall: not a tree) stands between <paramref name="from"/> and <paramref name="to"/> for a body of <paramref name="radius"/>.</summary>
    bool Blocked(Vector3 from, Vector3 to, float radius);

    /// <summary>The living players now (on foot or riding), humans and bots alike.</summary>
    IReadOnlyList<BigfootPlayer> Players { get; }

    /// <summary>The ground vehicles that may be thrown now (not flying, not destroyed, not already thrown).</summary>
    IReadOnlyList<BigfootVehicle> Vehicles { get; }

    /// <summary>The Bigfoot made a log: the server spawns it as a world entity.</summary>
    void SpawnTrunk(Vector3 from, Vector3 velocity);

    /// <summary>The player takes <paramref name="damage"/> from the Bigfoot (2 to 5), the way <paramref name="how"/> says.</summary>
    void Hurt(ushort player, float damage, BigfootHurt how);

    /// <summary>The player is knocked with this velocity (the shockwave): an impulse on their body.</summary>
    void Knock(ushort player, Vector3 velocity);

    /// <summary>The player is held at <paramref name="at"/> this tick: the server puts their body there and ignores their input.</summary>
    void Hold(ushort player, Vector3 at);

    /// <summary>The player is let go and flies with <paramref name="velocity"/>; their fall damage is the launch's own for <paramref name="graceSeconds"/>.</summary>
    void Launch(ushort player, Vector3 velocity, float graceSeconds);

    /// <summary>The server throws the vehicle on a scripted arc to <paramref name="landing"/>, in <paramref name="seconds"/>, turning at <paramref name="spinRadPerS"/>.</summary>
    void ThrowVehicle(ushort vehicle, Vector3 landing, float seconds, float spinRadPerS);

    /// <summary>A one-shot effect at a point; <paramref name="id"/> is the player or vehicle some of them name (0 otherwise).</summary>
    void Fx(BigfootFxKind kind, Vector3 at, ushort id = 0, byte extra = 0);
}

/// <summary>
/// The Bigfoot (issue #264): a world entity with its own brain, not a bot. It has a hit count instead of hit points, three phases and an enrage, chases the
/// nearest player on a straight line over the terrain (through trees, round buildings), throws logs, stomps, throws vehicles, and grabs and launches
/// players. Godot-free: the server feeds it the world through <see cref="IBigfootWorld"/> and the shots through <see cref="Hit"/>.
/// </summary>
public sealed class Bigfoot : IWorldEntity
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
    bool _waiting;                    // wiped: healed, standing, waiting for a player to come
    float _alone;                     // seconds with no living player within the despawn radius
    float _deadFor;

    // behaviour
    float _actLeft;                   // seconds left of a timed act
    ushort _target;
    float _retarget;
    float _trunkCd, _stompCd, _vehicleCd, _grabCd;
    ushort _vehicleToThrow;
    ushort _held;
    float _holdLeft;
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
        _need = BigfootRules.HitsNeeded(data.Hits, participants);
        _trunkCd = 1.5f; // it does not open with a throw the moment it appears
    }

    public Vector3 Position { get; private set; }
    /// <summary>Godot convention: 0 faces -Z.</summary>
    public float Yaw { get; private set; }
    public BigfootAct Act { get; private set; } = BigfootAct.Idle;
    public int Phase { get; private set; }
    public bool Enraged { get; private set; }
    public bool Alive => Act != BigfootAct.Dead;
    /// <summary>True from a wipe until a living player comes near again.</summary>
    public bool Waiting => _waiting;
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
    public HitTarget HitVolume => new(BigfootRules.TargetId, Position, Stance.Stand, Yaw, HeadGuarded: true, Scale: _d.HitScale);

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
        if (_waiting) { _waiting = false; } // shot while it waited: it is awake
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
        Ended ??= reason;
    }

    void Die(ushort killer)
    {
        Killer = killer;
        _taken = _need;
        ReleaseHeld();
        _waves.Clear();
        _flyers.Clear();
        Act = BigfootAct.Dead;
        _actLeft = 0;
        var credited = new HashSet<ushort>(_near);
        foreach (var (p, n) in _hitsBy) if (n >= _d.Reward.CreditMinHits) credited.Add(p);
        if (killer != 0) credited.Add(killer);
        Credited = credited.ToList();
        Died?.Invoke(this);
    }

    void UpdatePhase()
    {
        var f = TakenFraction;
        var phase = BigfootRules.PhaseAt(_d.Hits, f);
        var enraged = BigfootRules.EnrageAt(_d.Hits, f);
        if (phase > Phase) _w.Fx(BigfootFxKind.Roar, Position, 0, (byte)phase);
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
        if (_waiting) { Act = BigfootAct.Idle; return; }

        switch (Act)
        {
            case BigfootAct.TrunkWindup:
                FaceTarget(players, dt);
                if ((_actLeft -= dt) <= 0) ReleaseTrunk(players);
                break;
            case BigfootAct.StompWindup:
                if ((_actLeft -= dt) <= 0) Stomp();
                break;
            case BigfootAct.Stomp:
                if ((_actLeft -= dt) <= 0) Act = BigfootAct.Idle;
                break;
            case BigfootAct.VehicleWindup:
                if ((_actLeft -= dt) <= 0) ThrowTheVehicle();
                break;
            case BigfootAct.GrabReach:
                if ((_actLeft -= dt) <= 0) TakeHold(players);
                break;
            case BigfootAct.Hold:
                TickHold(players, dt);
                break;
            case BigfootAct.Launch:
                if ((_actLeft -= dt) <= 0) Act = BigfootAct.Idle;
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

        if (_waiting)
        {
            if (anyWake) { _waiting = false; _need = Math.Max(_need, BigfootRules.HitsNeeded(_d.Hits, near.Count)); }
            return;
        }
        _need = Math.Max(_need, BigfootRules.HitsNeeded(_d.Hits, near.Count)); // the count only goes up: players who leave do not make it easier
        if (_taken > 0 && wiped) Wipe();
    }

    /// <summary>Every participant died: it heals to full and waits where it stands.</summary>
    void Wipe()
    {
        _taken = 0; _hitsBy.Clear(); _waiting = true;
        Phase = 0; Enraged = false;
        _need = BigfootRules.HitsNeeded(_d.Hits, 0);
        ReleaseHeld();
        _waves.Clear(); _flyers.Clear();
        Act = BigfootAct.Idle;
        _target = 0;
        _w.Fx(BigfootFxKind.Heal, Position);
    }

    float SpeedMul => Enraged ? _d.Hits.EnrageSpeedMultiplier : 1f;
    float CooldownMul => Enraged ? _d.Hits.EnrageCooldownMultiplier : 1f;

    /// <summary>Idle, walk or run: pick the next thing to do. The first thing that applies wins, the heaviest first.</summary>
    void Decide(IReadOnlyList<BigfootPlayer> players, float dt)
    {
        var target = Retarget(players, dt);
        if (target is not { } t) { Act = BigfootAct.Idle; return; }
        var dist = Flat(t.Feet, Position);

        if (Phase >= 1 && _vehicleCd <= 0 && FindVehicleInContact() is { } car)
        {
            _vehicleToThrow = car.Id; Act = BigfootAct.VehicleWindup; _actLeft = _d.VehicleThrow.WindupSeconds; FaceYaw(car.Feet, dt, snap: true);
            return;
        }
        if (Phase >= 2 && _grabCd <= 0 && NearestOnFoot(players, _d.Grab.RangeM) is { } grab)
        {
            _target = grab.Id; Act = BigfootAct.GrabReach; _actLeft = _d.Grab.ReachSeconds; FaceYaw(grab.Feet, dt, snap: true);
            return;
        }
        if (Phase >= 1 && _stompCd <= 0 && NearestOnFoot(players, _d.Stomp.TriggerM) != null)
        {
            Act = BigfootAct.StompWindup; _actLeft = _d.Stomp.WindupSeconds;
            _w.Fx(BigfootFxKind.StompWindup, Position);
            return;
        }
        if (_trunkCd <= 0 && dist >= _d.Trunk.MinRangeM && dist <= _d.Trunk.MaxRangeM && !_w.Blocked(Chest, ChestOf(t.Feet), 0.3f))
        {
            Act = BigfootAct.TrunkWindup; _actLeft = _d.Trunk.WindupSeconds; FaceYaw(t.Feet, dt, snap: true);
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
        if (current != null && _retarget > 0) return current;
        BigfootPlayer? best = null; var bd = float.MaxValue;
        foreach (var p in players)
        {
            var d = Flat(p.Feet, Position);
            if (d < bd) { bd = d; best = p; }
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

    BigfootVehicle? FindVehicleInContact()
    {
        BigfootVehicle? best = null; var bd = _d.VehicleThrow.ContactM;
        foreach (var v in _w.Vehicles)
        {
            var d = Flat(v.Feet, Position);
            if (d <= bd) { bd = d; best = v; }
        }
        return best;
    }

    void Chase(BigfootPlayer t, float dist, float dt)
    {
        var run = dist > _d.Movement.RunBeyondM;
        if (dist <= _d.Movement.StopDistanceM) { FaceYaw(t.Feet, dt); Act = BigfootAct.Idle; return; }
        var speed = (run ? _d.Movement.RunSpeed : _d.Movement.WalkSpeed) * SpeedMul;
        Act = Step(t.Feet, speed, dt) ? (run ? BigfootAct.Run : BigfootAct.Walk) : BigfootAct.Idle;
    }

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
    /// or a step the ground refuses turns the heading aside, the way an animal goes round; trees are not in the way. False when it did not move.
    /// </summary>
    bool Step(Vector3 goal, float speed, float dt)
    {
        var want = TurretMath.YawTo(Position, goal);
        Yaw = TurretMath.StepAngle(Yaw, want, _d.Movement.TurnRateDegPerS * MathF.PI / 180f * dt);
        if (MathF.Abs(TurretMath.AngleDiff(Yaw, want)) > 1.0f) return true; // turning on the spot
        foreach (var turn in new[] { 0f, 0.7f, -0.7f, 1.4f, -1.4f })
        {
            var heading = Yaw + turn;
            var f = new Vector3(-MathF.Sin(heading), 0, -MathF.Cos(heading));
            var next = Position + f * speed * dt;
            if (_w.GroundY(next.X, next.Z, Position.Y) is not { } y || MathF.Abs(y - Position.Y) > _d.Movement.MaxStepM) continue;
            if (_w.Blocked(Position + Vector3.UnitY, new Vector3(next.X, Position.Y + 1f, next.Z), _d.Movement.BodyRadiusM)) continue;
            Position = new Vector3(next.X, y, next.Z);
            if (turn != 0) Yaw = TurretMath.StepAngle(Yaw, heading, _d.Movement.TurnRateDegPerS * MathF.PI / 180f * dt);
            return true;
        }
        return false;
    }

    // ---- attacks ----

    Vector3 Chest => Position + new Vector3(0, _d.HeightM * 0.7f, 0);
    static Vector3 ChestOf(Vector3 feet) => feet + new Vector3(0, MoveConsts.CapsuleHeight * 0.6f, 0);
    Vector3 Forward => new(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw));
    Vector3 Hand => Position + Forward * _d.Grab.HandForwardM + new Vector3(0, _d.Grab.HandHeightM, 0);

    void ReleaseTrunk(IReadOnlyList<BigfootPlayer> players)
    {
        Act = BigfootAct.Idle;
        _trunkCd = _d.Trunk.CooldownSeconds * CooldownMul * (0.8f + 0.4f * (float)_rng.NextDouble());
        BigfootPlayer? t = null;
        foreach (var p in players) if (p.Id == _target) t = p;
        if (t is not { } tp) return;
        var from = Position + Forward * 1.2f + new Vector3(0, _d.HeightM * 0.85f, 0);
        // the flight time is the distance over the log's speed; the aim is where the target will be when it arrives, plus the lead
        var seconds = Math.Clamp(Flat(tp.Feet, from) / MathF.Max(_d.Trunk.SpeedMps, 1f), 0.4f, _d.Trunk.LifeSeconds - 0.2f);
        var aim = ChestOf(tp.Feet + _targetVelocity * (seconds + _d.Trunk.AimLeadSeconds));
        seconds = Math.Clamp(Flat(aim, from) / MathF.Max(_d.Trunk.SpeedMps, 1f), 0.4f, _d.Trunk.LifeSeconds - 0.2f);
        _w.SpawnTrunk(from, Ballistics.LaunchVelocity(from, aim, seconds));
        _w.Fx(BigfootFxKind.TrunkThrow, from);
    }

    void Stomp()
    {
        Act = BigfootAct.Stomp; _actLeft = 0.6f;
        _stompCd = _d.Stomp.CooldownSeconds * CooldownMul;
        _waves.Add(new Wave { Origin = Position, Radius = 0 });
        _w.Fx(BigfootFxKind.Shockwave, Position);
    }

    void TickWaves(IReadOnlyList<BigfootPlayer> players, float dt)
    {
        for (var i = _waves.Count - 1; i >= 0; i--)
        {
            var wave = _waves[i];
            var before = wave.Radius;
            wave.Radius += _d.Stomp.WaveSpeedMps * dt;
            foreach (var p in players)
            {
                if (p.InVehicle || wave.Hit.Contains(p.Id)) continue;
                var d = Flat(p.Feet, wave.Origin);
                if (d < before - _d.Stomp.WaveBandM || d > wave.Radius) continue; // the ring this tick covers [before - band, radius]
                if (!p.OnFloor) continue;                                             // a jump avoids it
                if (_w.GroundY(p.Feet.X, p.Feet.Z, p.Feet.Y) is { } g && p.Feet.Y - g > _d.Stomp.WaveHeightM) continue;
                wave.Hit.Add(p.Id);
                var away = new Vector3(p.Feet.X - wave.Origin.X, 0, p.Feet.Z - wave.Origin.Z);
                away = away.LengthSquared() > 1e-6f ? Vector3.Normalize(away) : Forward;
                _w.Knock(p.Id, away * _d.Stomp.KnockbackMps + new Vector3(0, _d.Stomp.KnockbackLiftMps, 0));
                _w.Hurt(p.Id, _d.RollDamage(_rng), BigfootHurt.Shockwave);
            }
            if (wave.Radius >= _d.Stomp.WaveMaxRadiusM) _waves.RemoveAt(i);
        }
    }

    void ThrowTheVehicle()
    {
        Act = BigfootAct.Idle;
        _vehicleCd = _d.VehicleThrow.CooldownSeconds * CooldownMul;
        BigfootVehicle? car = null;
        foreach (var v in _w.Vehicles) if (v.Id == _vehicleToThrow) car = v;
        _vehicleToThrow = 0;
        if (car is not { } c || Flat(c.Feet, Position) > _d.VehicleThrow.ContactM + 2f) return; // it drove away, or was thrown, or is gone
        // a random heading and distance; the first one with ground under it is where it lands, else the last one with ground, else it goes straight up and down
        Vector3? landing = null;
        for (var i = 0; i < _d.VehicleThrow.PlacementTries; i++)
        {
            var angle = (float)_rng.NextDouble() * MathF.Tau;
            var dist = _d.VehicleThrow.MinDistanceM + (float)_rng.NextDouble() * (_d.VehicleThrow.MaxDistanceM - _d.VehicleThrow.MinDistanceM);
            var x = c.Feet.X + MathF.Cos(angle) * dist; var z = c.Feet.Z + MathF.Sin(angle) * dist;
            if (_w.GroundY(x, z, c.Feet.Y) is not { } y) continue;
            landing = new Vector3(x, y, z);
            break;
        }
        landing ??= c.Feet;
        _w.ThrowVehicle(c.Id, landing.Value, _d.VehicleThrow.FlightSeconds, _d.VehicleThrow.SpinRadPerS);
        _w.Fx(BigfootFxKind.VehicleThrow, c.Feet, c.Id, (byte)Math.Clamp((int)MathF.Round(_d.VehicleThrow.FlightSeconds * 10), 1, 255));
    }

    void TakeHold(IReadOnlyList<BigfootPlayer> players)
    {
        Act = BigfootAct.Idle;
        _grabCd = _d.Grab.CooldownSeconds * CooldownMul;
        BigfootPlayer? p = null;
        foreach (var q in players) if (q.Id == _target && !q.InVehicle && Flat(q.Feet, Position) <= _d.Grab.RangeM + 1.5f) p = q;
        if (p is not { } held) return;
        _held = held.Id; _holdLeft = _d.Grab.HoldSeconds; Act = BigfootAct.Hold;
        _w.Fx(BigfootFxKind.Grab, Position, held.Id);
        _w.Hold(held.Id, Hand);
    }

    void TickHold(IReadOnlyList<BigfootPlayer> players, float dt)
    {
        var still = false;
        foreach (var p in players) if (p.Id == _held) still = true;
        if (!still) { ReleaseHeld(); Act = BigfootAct.Idle; return; } // the held player died or left
        _holdLeft -= dt;
        if (_holdLeft > 0) { _w.Hold(_held, Hand); return; }
        LaunchHeld(players);
    }

    void LaunchHeld(IReadOnlyList<BigfootPlayer> players)
    {
        var who = _held;
        var from = Hand;
        // another living player on foot if there is one (the nearest to the held player), else a random direction
        BigfootPlayer? other = null; var bd = float.MaxValue;
        foreach (var p in players)
        {
            if (p.Id == who || p.InVehicle) continue;
            var d = Flat(p.Feet, from);
            if (d < bd) { bd = d; other = p; }
        }
        Vector3 v;
        if (other is { } o) v = Ballistics.LaunchVelocity(from, o.Feet + new Vector3(0, 0.9f, 0), _d.Grab.LaunchFlightSeconds);
        else
        {
            var a = (float)_rng.NextDouble() * MathF.Tau;
            v = new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * _d.Grab.RandomLaunchSpeedMps + new Vector3(0, 9f, 0);
        }
        _held = 0;
        _flyers.Add(new Flyer { Player = who, Left = _d.Grab.LaunchGraceSeconds });
        _w.Launch(who, v, _d.Grab.LaunchGraceSeconds);
        _w.Fx(BigfootFxKind.Launch, from, who);
        Act = BigfootAct.Launch; _actLeft = 0.5f;
    }

    void ReleaseHeld()
    {
        if (_held == 0) return;
        var who = _held;
        _held = 0;
        _w.Launch(who, Vector3.Zero, 0);
    }

    /// <summary>The launched players in the air: what they hit on the way is hurt (once each), and the landing hurts them (2 to 5, in place of the fall).</summary>
    void TickFlyers(IReadOnlyList<BigfootPlayer> players, float dt)
    {
        for (var i = _flyers.Count - 1; i >= 0; i--)
        {
            var f = _flyers[i];
            f.Age += dt; f.Left -= dt;
            BigfootPlayer? me = null;
            foreach (var p in players) if (p.Id == f.Player) me = p;
            if (me is not { } m || f.Left <= 0) { _flyers.RemoveAt(i); continue; } // died, left, or the grace is over
            foreach (var p in players)
            {
                if (p.Id == f.Player || p.InVehicle || f.Hit.Contains(p.Id)) continue;
                if (Vector3.Distance(p.Feet + new Vector3(0, 0.9f, 0), m.Feet + new Vector3(0, 0.9f, 0)) > _d.Grab.LaunchHitRadiusM + PlayerHitBox.Radius) continue;
                f.Hit.Add(p.Id);
                _w.Hurt(p.Id, _d.RollDamage(_rng), BigfootHurt.LaunchHit);
            }
            if (f.Age > 0.3f && m.OnFloor)
            {
                _w.Hurt(f.Player, _d.RollDamage(_rng), BigfootHurt.Landing);
                _flyers.RemoveAt(i);
            }
        }
    }

    // ---- replication ----

    public WorldState Serialize()
    {
        var state = BigfootRules.Pack(Act, _hitToggle, Phase, Enraged);
        var hp = Alive ? MathF.Ceiling(100f * (1f - TakenFraction)) : 0f;
        return WorldState.Make(Position, Yaw, 0, _held, state, Alive ? MathF.Max(1f, hp) : 0f);
    }

    static float Flat(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X; var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
