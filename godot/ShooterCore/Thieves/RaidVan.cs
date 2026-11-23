using System.Numerics;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using static Drycastle.ShooterCore.Movement.Planar;

namespace Drycastle.ShooterCore.Thieves;

/// <summary>What a Raid van is doing: the replicated state byte (GLOSSARY "Raid van").</summary>
public enum RaidVanState : byte { Arriving = 0, Opening = 1, Parked = 2, Boarding = 3, Closing = 4, Leaving = 5, Destroyed = 6 }

/// <summary>What the van needs to know of its Raid's crew to decide when to leave (the Raid answers).</summary>
public interface IRaidCrew
{
    /// <summary>Members still to get out of the van.</summary>
    int StillToSpawn { get; }
    /// <summary>Members alive and not yet back aboard.</summary>
    int Afoot { get; }
    /// <summary>Members sitting in the van right now: still to get out, or back aboard. The wire carries it (the client seats that many).</summary>
    int Inside { get; }
}

/// <summary>Rules of the Raid van entity that need no world: the wire type, the hit ids, the state layout, the hit volume.</summary>
public static class RaidVanRules
{
    /// <summary>Wire id of the Raid van world entity type (54 is the Gang member).</summary>
    public const byte TypeId = 55;

    /// <summary>
    /// The hit ids a van has as a target, two for each (a front and a rear capsule): just above the Gang members' range (0x7100..0x73DF) and below the wild animals' (0x7400), above the turret records (0x7000 + a vehicle index).
    /// </summary>
    public const ushort FirstHitId = 0x73E0, HitIdCount = 0x20, IdsPerVan = 2;

    /// <summary>The most vans that can be in the world at once (a Raid's slot is its hit id pair).</summary>
    public const int MaxVans = HitIdCount / IdsPerVan;

    /// <summary>Loot counter capacity of the wire (5 bits).</summary>
    public const int MaxLoad = 31;

    public static bool IsHitId(ushort id) => id >= FirstHitId && id < FirstHitId + HitIdCount;
    public static int SlotOf(ushort hitId) => (hitId - FirstHitId) / IdsPerVan;
    public static ushort HitIdOf(int slot, int part) => (ushort)(FirstHitId + slot * IdsPerVan + part);

    // The hp byte: bits 0-4 Loot loaded, bits 5-7 the damage stage (0 whole, 7 nearly gone). The owner field: bits 0-5 the door open in 63rds, bits 6-10 the song that plays at the van
    // (the server picks it at the van's birth, the client plays song number modulo the songs it has), bits 11-14 the crew sitting inside (0-15), bit 15 the door is on the left.
    const int LoadMask = 31, StageShift = 5, StageMask = 7;
    public const int DoorMax = 63;
    const int LeftBit = 0x8000, SongShift = 6, SongMask = 31, InsideShift = 11, InsideMask = 15;
    /// <summary>The most crew the wire says are inside (4 bits).</summary>
    public const int MaxInside = InsideMask;
    /// <summary>The songs a van's number can name (5 bits).</summary>
    public const int SongCount = SongMask + 1;

    public static byte PackHp(int load, int stage) => (byte)((Math.Clamp(load, 0, LoadMask)) | ((Math.Clamp(stage, 0, StageMask)) << StageShift));
    public static int LoadOf(byte hp) => hp & LoadMask;
    public static int StageOf(byte hp) => (hp >> StageShift) & StageMask;

    public static ushort PackOwner(float doorOpen, bool left, int song = 0, int inside = 0) =>
        (ushort)((int)MathF.Round(Math.Clamp(doorOpen, 0f, 1f) * DoorMax) | ((song & SongMask) << SongShift) | (Math.Clamp(inside, 0, InsideMask) << InsideShift) | (left ? LeftBit : 0));
    public static int SongOf(ushort owner) => (owner >> SongShift) & SongMask;
    public static int InsideOf(ushort owner) => (owner >> InsideShift) & InsideMask;
    public static float DoorOf(ushort owner) => Math.Min(owner & 0x3F, DoorMax) / (float)DoorMax;
    public static bool LeftOf(ushort owner) => (owner & LeftBit) != 0;

    /// <summary>A state byte as a state; a value this build does not know is read as Parked (a standing van).</summary>
    public static RaidVanState StateOf(byte state) => state <= (byte)RaidVanState.Destroyed ? (RaidVanState)state : RaidVanState.Parked;

    /// <summary>The states in which the van stands and is solid for players (the box is built from them on the server and the clients alike).</summary>
    public static bool IsSolid(RaidVanState s) => s is RaidVanState.Opening or RaidVanState.Parked or RaidVanState.Boarding or RaidVanState.Closing or RaidVanState.Destroyed;

    public static void Register(WorldEntityRegistry registry)
    {
        if (registry.Find(TypeId) == null) registry.Register(new WorldEntityDef(TypeId, "raid van", HasHp: true));
    }

    /// <summary>The two capsules of the van's hit volume, from its centre target: prone figures grown to the box (radius 0.56 times the scale), <paramref name="offsetM"/> ahead and behind the centre.</summary>
    public static (HitTarget Front, HitTarget Rear) Capsules(in HitTarget centre, float offsetM)
    {
        var fwd = HitMath.Forward(centre.Yaw, 0);
        var slot = SlotOf(centre.Id);
        return (centre with { Id = HitIdOf(slot, 0), Feet = centre.Feet + fwd * offsetM }, centre with { Id = HitIdOf(slot, 1), Feet = centre.Feet - fwd * offsetM });
    }

    /// <summary>
    /// Where a body standing at <paramref name="feet"/> is put to get out of the van's box (a van that stops with someone in the way): out through the nearest side, <paramref name="marginM"/> past it.
    /// The point is the body's own when it is not inside.
    /// </summary>
    public static Vector3 PushOut(Vector3 vanFeet, float yaw, float halfWidth, float halfLength, Vector3 feet, float marginM)
    {
        var right = new Vector3(MathF.Cos(yaw), 0, -MathF.Sin(yaw));
        var fwd = new Vector3(-MathF.Sin(yaw), 0, -MathF.Cos(yaw));
        var d = feet - vanFeet;
        var lx = Vector3.Dot(d, right); var lz = Vector3.Dot(d, fwd);
        var ox = halfWidth + marginM - MathF.Abs(lx); var oz = halfLength + marginM - MathF.Abs(lz);
        if (ox <= 0 || oz <= 0) return feet;
        return ox <= oz ? feet + right * (lx < 0 ? -ox : ox) : feet + fwd * (lz < 0 ? -oz : oz);
    }
}

/// <summary>
/// A Raid's van (GLOSSARY "Raid van", ADR 0021): a scripted world entity of its own wire type, not a vehicle. It follows a road path at a set speed with its height from the ground, parks at
/// the stop, opens its side door for the crew, takes the crew back and the Loot loaded (a counter ticket 04 feeds), and drives off. It never brakes for anything in its way: the server rams
/// players and shoves vehicles aside by its box. It has a Hit count, also while driving; destroyed, its Raid is over (the Raid's crew flees, the Loot loaded goes back).
/// Godot-free, deterministic; the server asks for the heights through <see cref="Ground"/> and gives it the players' places for the honk.
/// <para>
/// Layout of the entity state: owner = the door (<see cref="RaidVanRules.PackOwner"/>), state byte = <see cref="RaidVanState"/>, hp byte = <see cref="RaidVanRules.PackHp"/> (Loot loaded, damage stage),
/// position = where its centre stands on the ground, yaw = its heading, pitch = the slope it lies on.
/// </para>
/// </summary>
public sealed class RaidVan : IWorldEntity
{
    /// <summary>
    /// The van's states and the edges between them. It drives in (Arriving), stops and opens the side door (Opening, the crew gets out), stands while the crew works (Parked), calls the crew back
    /// and waits (Boarding), closes the door (Closing) and drives off (Leaving; the entity ends at the map edge). Destroyed comes from anywhere; the wreck lies, then ends.
    /// </summary>
    public static readonly StateMachineSpec<RaidVanState> Spec = StateMachineSpec<RaidVanState>
        .Define("RaidVan.State", "What a Raid van is doing; the replicated state byte.", RaidVanState.Arriving)
        .Allow(RaidVanState.Arriving, RaidVanState.Opening)
        .Allow(RaidVanState.Opening, RaidVanState.Parked)
        .Allow(RaidVanState.Parked, RaidVanState.Boarding, RaidVanState.Closing)
        .Allow(RaidVanState.Boarding, RaidVanState.Closing)
        .Allow(RaidVanState.Closing, RaidVanState.Leaving)
        .FromAnywhere(RaidVanState.Destroyed)
        .Note(RaidVanState.Arriving, "Drives the road path from the map edge to the stop at its speed (it brakes only for the stop, never for what is in its way); no clock. Ends in Opening at the stop.")
        .Note(RaidVanState.Opening, "Stopped; the side door slides open (clock: door seconds) and the crew starts getting out, one every stagger seconds.")
        .Note(RaidVanState.Parked, "The crew works (the stay clock runs beside). Leaves for Boarding (or Closing when nobody is afoot) when the Loot loaded reaches capacity or the stay time ran out; a dead crew does not send it off.")
        .Note(RaidVanState.Boarding, "The living crew is called back (clock: board wait seconds); ends when none is afoot, or the wait ran out (those left behind flee).")
        .Note(RaidVanState.Closing, "The door slides shut (clock: door seconds), then the van drives off.")
        .Note(RaidVanState.Leaving, "Drives the way out to the map edge, accelerating; the entity ends at its end (no state follows).")
        .Note(RaidVanState.Destroyed, "Hit count taken, from any state: the Raid is over. The wreck lies where it stopped (clock: wreck seconds), then the entity ends. Never left.")
        .Build();

    const float TurnSlowest = 0.25f, MinCreepMps = 0.4f;

    readonly VanConfig _d;
    readonly IGangWorld? _world;
    readonly Random _rng;
    readonly StateMachine<RaidVanState> _fsm = new(Spec);
    RoadPath _route;
    float _s;                       // metres travelled along the route
    float _speed;
    float _taken;
    float _y;
    float _pitch;
    float _clock;                   // seconds since birth: what the blast dedupe reads
    float _lastBlastAt = -1000;
    ushort _lastBlastBy;
    Countdown _stay, _honk, _honkNear;

    /// <param name="route">The path in (its end is the stop); empty to put the van at <paramref name="start"/> already stopped (a fallback spawn).</param>
    public RaidVan(VanConfig cfg, IGangWorld? world, RoadPath route, Vector3 start, float yaw, int slot, int doorSide, int seed)
    {
        _d = cfg; _world = world; _route = route; Slot = slot; DoorSide = doorSide < 0 ? -1 : 1;
        _rng = new Random(seed);
        Position = PrevPosition = start; Yaw = yaw; _y = start.Y;
        _honk.Start(RandomHonk());
        _stay.Start(cfg.StaySeconds);
        if (route.IsEmpty) { _fsm.Enter(RaidVanState.Opening, cfg.DoorSeconds); } // put at its stop: the door opens at once
    }

    /// <summary>Raised once per honk: where the van is.</summary>
    public event Action<RaidVan>? Honked;
    /// <summary>Raised after every state change: (from, to).</summary>
    public event Action<RaidVanState, RaidVanState>? StateChanged
    {
        add => _fsm.Changed += value;
        remove => _fsm.Changed -= value;
    }
    /// <summary>Raised once, when the hit that destroys it lands (the Raid ends, its crew flees, the Loot loaded goes back).</summary>
    public event Action<RaidVan>? DestroyedEvent;

    /// <summary>The ground height under a point (x, z), NaN where there is none (the van then keeps the height it had). Null: the start height all along.</summary>
    public Func<float, float, float>? Ground { get; set; }

    /// <summary>What its Raid's crew is doing, for the leave rules. Null: nobody (it leaves as soon as it has stopped and the stay time is not the reason).</summary>
    public IRaidCrew? Crew { get; set; }

    /// <summary>The path out (set before it leaves). Null: it drives straight on from where it stands.</summary>
    public RoadPath? Exit { get; set; }

    public int Slot { get; }
    /// <summary>The song that plays at this van (0 to <see cref="RaidVanRules.SongCount"/> - 1): picked by the roster at its birth, travels in the owner field.</summary>
    public int Song { get; set; }
    /// <summary>The side the door is on: 1 right, -1 left.</summary>
    public int DoorSide { get; }

    public byte TypeId => RaidVanRules.TypeId;
    public WorldDespawnReason? Ended { get; private set; }
    public ushort Owner => 0;

    public RaidVanState State => _fsm.Current;
    public bool Alive => State != RaidVanState.Destroyed;
    /// <summary>Where its centre stands on the ground, and where it stood at the start of the tick.</summary>
    public Vector3 Position { get; private set; }
    public Vector3 PrevPosition { get; private set; }
    public float Yaw { get; private set; }
    public float Pitch => _pitch;
    /// <summary>Metres per second along its heading (0 when it stands).</summary>
    public float Speed => _speed;
    public Vector3 Forward => new(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw));
    public Vector3 Velocity => Forward * _speed;
    public VanConfig Config => _d;

    /// <summary>The weighted hits taken, and the Hit count that destroys it.</summary>
    public float HitsTaken => _taken;
    public float HitsNeeded => _d.Hits;
    /// <summary>The player whose hit destroyed it (0 when none).</summary>
    public ushort Killer { get; private set; }

    /// <summary>Loot loaded and the most it takes (ticket 04 counts Loot in with <see cref="LoadLoot"/>; <see cref="LootReturnAll"/> takes it all out).</summary>
    public int Load { get; private set; }
    public int Capacity => _d.Capacity;
    public bool Full => Load >= Capacity;

    /// <summary>The seconds of stay left (the clock runs while it stands Parked).</summary>
    public float StayLeft => _stay.Left;

    /// <summary>The tick the pose recorder first saw it, and the poses of recent ticks (the rewind of a shot, ADR 0013).</summary>
    public uint BornTick { get; set; }
    public PoseRing Poses { get; } = new();

    /// <summary>The door: 0 shut to 1 open.</summary>
    public float DoorOpen => State switch
    {
        RaidVanState.Opening => Math.Clamp(_fsm.Elapsed / _d.DoorSeconds, 0f, 1f),
        RaidVanState.Parked or RaidVanState.Boarding => 1f,
        RaidVanState.Closing => 1f - Math.Clamp(_fsm.Elapsed / _d.DoorSeconds, 0f, 1f),
        RaidVanState.Destroyed => 0.35f,
        _ => 0f,
    };

    /// <summary>The side door point: where the crew steps out and where they come back to (on the ground beside the van, a little ahead of its centre).</summary>
    public Vector3 DoorPoint
    {
        get
        {
            var right = new Vector3(MathF.Cos(Yaw), 0, -MathF.Sin(Yaw));
            return Position + right * (DoorSide * (_d.HalfWidthM + _d.DoorOutM)) + Forward * _d.DoorAlongM;
        }
    }

    public bool Solid => RaidVanRules.IsSolid(State);

    /// <summary>The box other vehicles touch: feet at its centre on the ground, its yaw and half sizes.</summary>
    public ContactShape Shape => new(Position, Yaw, _d.HalfWidthM, _d.HalfLengthM, _d.HeightM);

    // ---- Loot ----

    /// <summary>Loot is carried in (ticket 04: a Thief that reaches the door). False when the door is not open or the van is full.</summary>
    public bool LoadLoot(int n = 1)
    {
        if (State is not (RaidVanState.Opening or RaidVanState.Parked or RaidVanState.Boarding) || Load + n > Capacity) return false;
        Load += n;
        return true;
    }

    /// <summary>Everything loaded goes back to its place (ticket 04): the count is zero. Returns how many there were. Called by the Raid when the van is destroyed.</summary>
    public int LootReturnAll() { var n = Load; Load = 0; return n; }

    // ---- hits ----

    /// <summary>
    /// A weapon hit counts <paramref name="weight"/> (<see cref="VanConfig.Hit"/>). True when it destroyed the van. An explosion reaches the two capsules of the hit volume as two hits: the same blast
    /// of the same attacker in the same tick counts once.
    /// </summary>
    public bool Hit(ushort attacker, float weight, bool explosive)
    {
        if (Ended != null || !Alive || !(weight > 0)) return false;
        if (explosive)
        {
            if (_clock <= _lastBlastAt + 1e-4f && _lastBlastBy == attacker) return false; // the clock only moves in a tick: the same tick
            _lastBlastAt = _clock; _lastBlastBy = attacker;
        }
        _taken += weight;
        if (_taken < _d.Hits) return false;
        Destroy(attacker);
        return true;
    }

    public void OnDamage(float amount, ushort attacker) => Hit(attacker, 1, false);

    /// <summary>Destroys it at once (the event ended).</summary>
    public void Remove(WorldDespawnReason reason = WorldDespawnReason.Removed) => Ended ??= reason;

    void Destroy(ushort attacker)
    {
        Killer = attacker;
        _taken = MathF.Max(_taken, _d.Hits);
        _speed = 0;
        _fsm.Enter(RaidVanState.Destroyed, _d.WreckSeconds);
        DestroyedEvent?.Invoke(this);
    }

    // ---- the tick ----

    public WorldState Serialize()
    {
        var stage = Math.Min(7, (int)MathF.Floor(8f * Math.Min(1f, _taken / _d.Hits)));
        return WorldState.Make(Position, Yaw, _pitch, RaidVanRules.PackOwner(DoorOpen, DoorSide < 0, Song, State == RaidVanState.Destroyed ? 0 : Crew?.Inside ?? 0), (byte)State, RaidVanRules.PackHp(Load, stage));
    }

    public void Tick(float dt)
    {
        if (Ended != null || dt <= 0) return;
        _clock += dt;
        PrevPosition = Position;
        _honk.Tick(dt); _honkNear.Tick(dt);
        TickHonk();
        switch (State)
        {
            case RaidVanState.Arriving: TickArriving(dt); break;
            case RaidVanState.Opening:
                _speed = 0;
                if (_fsm.Tick(dt)) _fsm.Enter(RaidVanState.Parked);
                break;
            case RaidVanState.Parked: TickParked(dt); break;
            case RaidVanState.Boarding:
                if (Crew is not { Afoot: > 0 } || _fsm.Tick(dt)) _fsm.Enter(RaidVanState.Closing, _d.DoorSeconds);
                break;
            case RaidVanState.Closing:
                if (_fsm.Tick(dt)) BeginLeaving();
                break;
            case RaidVanState.Leaving: TickLeaving(dt); break;
            case RaidVanState.Destroyed:
                _speed = Math.Max(0, _speed - 20 * dt); // a wreck rolls to a stop
                if (_fsm.Tick(dt)) Ended = WorldDespawnReason.Expired;
                break;
        }
        Settle();
    }

    void TickHonk()
    {
        if (!Alive) return;
        if (_honk.Ready) { Honked?.Invoke(this); _honk.Start(RandomHonk()); return; }
        if (_honkNear.Ready && _world != null)
            foreach (var p in _world.Players)
                if (Flat(p.Feet, Position) <= _d.HonkNearM) { Honked?.Invoke(this); _honkNear.Start(_d.HonkNearGapSeconds); return; }
    }

    float RandomHonk() => _d.HonkMinSeconds + (float)_rng.NextDouble() * (_d.HonkMaxSeconds - _d.HonkMinSeconds);

    void TickArriving(float dt)
    {
        var left = _route.Length - _s;
        if (left <= 0.05f)
        {
            _s = _route.Length; _speed = 0;
            Position = Ground3(_route.End);
            Yaw = _route.YawAt(_route.Length);
            _stay.Start(_d.StaySeconds);
            _fsm.Enter(RaidVanState.Opening, _d.DoorSeconds);
            return;
        }
        // brakes for the stop only: the speed the distance left allows, never below a creep so it arrives
        var target = MathF.Min(_d.SpeedMps, MathF.Max(_d.ParkSpeedMps, MathF.Sqrt(2 * _d.DecelMps2 * left)));
        Drive(dt, target, _route);
    }

    void TickParked(float dt)
    {
        _speed = 0;
        _stay.Tick(dt);
        if (!(Full || _stay.Ready)) return; // a dead crew does not send it off: it stands, solid and destroyable
        if (Crew is { Afoot: > 0 }) _fsm.Enter(RaidVanState.Boarding, _d.BoardWaitSeconds);
        else _fsm.Enter(RaidVanState.Closing, _d.DoorSeconds);
    }

    void BeginLeaving()
    {
        _route = Exit is { IsEmpty: false } e ? e : new RoadPath(new[] { new Vector2(Position.X, Position.Z), new Vector2(Position.X, Position.Z) + new Vector2(Forward.X, Forward.Z) * _d.ExitStraightM });
        _s = 0;
        _fsm.Enter(RaidVanState.Leaving);
    }

    void TickLeaving(float dt)
    {
        if (_s >= _route.Length - 0.05f) { Ended = WorldDespawnReason.Removed; return; }
        Drive(dt, _d.SpeedMps, _route);
    }

    /// <summary>One step along the route: the speed ramps to the target (a van not facing its way slows), the heading turns towards the road, the height follows the ground.</summary>
    void Drive(float dt, float targetSpeed, RoadPath path)
    {
        var want = path.YawAt(_s, 3f);
        var err = Wrap(want - Yaw);
        var turn = _d.TurnDegPerS * MathF.PI / 180f * dt;
        Yaw = Wrap(Yaw + Math.Clamp(err, -turn, turn));
        var facing = Math.Clamp(1f - MathF.Abs(Wrap(want - Yaw)) / (MathF.PI / 2f), TurnSlowest, 1f);
        _speed = Ramp.MoveToward(_speed, MathF.Max(targetSpeed * facing, MinCreepMps), (_speed < targetSpeed * facing ? _d.AccelMps2 : _d.DecelMps2 * 2) * dt);
        _s = MathF.Min(path.Length, _s + _speed * dt);
        Position = Ground3(path.At(_s));
    }

    /// <summary>The point at its ground height (the last height where the ground has none).</summary>
    Vector3 Ground3(Vector2 p)
    {
        if (Ground != null) { var h = Ground(p.X, p.Y); if (float.IsFinite(h)) _y = h; }
        return new Vector3(p.X, _y, p.Y);
    }

    /// <summary>The slope it lies on: the heights a little ahead and behind.</summary>
    void Settle()
    {
        if (Ground == null) { _pitch = 0; return; }
        var f = Forward; var half = _d.HalfLengthM * 0.8f;
        var a = Ground(Position.X + f.X * half, Position.Z + f.Z * half); var b = Ground(Position.X - f.X * half, Position.Z - f.Z * half);
        _pitch = float.IsFinite(a) && float.IsFinite(b) ? MathF.Atan2(a - b, 2 * half) : 0f;
    }

    static float Wrap(float a)
    {
        while (a > MathF.PI) a -= MathF.Tau;
        while (a < -MathF.PI) a += MathF.Tau;
        return a;
    }

    // ---- hit volume ----

    /// <summary>The hit volume's centre (a prone figure's pose, grown to the box): the ids of its two capsules come from <see cref="RaidVanRules.Capsules"/>.</summary>
    public HitTarget HitCentre => new(RaidVanRules.HitIdOf(Slot, 0), Position, Stance.Prone, Yaw, HeadGuarded: true, Scale: _d.HitScale, BaseRadius: PlayerHitBox.WideRadius);
}
