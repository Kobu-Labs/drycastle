using System.Buffers.Binary;
using System.Numerics;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Net;

namespace Drycastle.ShooterCore.CarePackages;

/// <summary>
/// Care package rules (ticket #124, reward reworked in #151): the killstreak that drops a crate on a parachute. The owner's
/// crate lands where the owner throws it (Q: a canister flies like a smoke grenade, the crate parachutes down at the spot); any living player within reach holds the interact key for
/// <see cref="OpenHoldSeconds"/> to open it. Opening grants a random killstreak from <see cref="StreakRewards"/> and the
/// crate despawns at once. An unopened crate disappears after <see cref="LifetimeSeconds"/>.
/// </summary>
public static class CarePackageRules
{
    /// <summary>Wire id of the care package world entity type.</summary>
    public const byte TypeId = 30;
    /// <summary>killstreaks.json id of the care package streak.</summary>
    public const string StreakId = "care_package";
    /// <summary>Seconds an unopened crate stays on the map, counted from the drop.</summary>
    public const float LifetimeSeconds = 60f;
    /// <summary>Where the crate lands, metres ahead of the owner's feet.</summary>
    public const float DropDistanceMetres = 5f;
    /// <summary>Height the crate starts its fall from, above the ground under the landing point.</summary>
    public const float StartHeightMetres = 30f;
    /// <summary>Descent speed on the parachute, m/s.</summary>
    public const float FallSpeedMetresPerSecond = 6f;
    /// <summary>Hold time of the interact key that opens a landed crate (client holds it, server trusts the send, as with corpses).</summary>
    public const float OpenHoldSeconds = 2f;
    /// <summary>Reach of the opening, metres from the crate.</summary>
    public const float ReachMetres = 2.5f;
    /// <summary>Extra reach the server allows for latency, as for corpses.</summary>
    public const float LatencySlackMetres = 0.5f;

    /// <summary>State byte on the wire: still on its parachute, or on the ground and openable.</summary>
    public const byte StateFalling = 0, StateLanded = 1;
    /// <summary>More state bytes: the canister still flying from the owner's hand, and lying on the ground marking the spot with smoke before the crate comes.</summary>
    public const byte StateThrown = 2, StateMarking = 3;
    /// <summary>Seconds the landed canister smokes before the crate is dropped on it.</summary>
    public const float MarkSeconds = 3f;

    /// <summary>Registers the type once (idempotent). A crate carries no hit points.</summary>
    public static void Register(WorldEntityRegistry registry)
    {
        registry.RegisterOnce(new WorldEntityDef(TypeId, "care package", HasHp: false));
    }

    /// <summary>The landing point of a drop: <see cref="DropDistanceMetres"/> ahead of <paramref name="feet"/> along the owner's yaw (the movement forward).</summary>
    public static Vector3 DropPoint(Vector3 feet, float yaw)
    {
        var (sin, cos) = MathF.SinCos(yaw);
        return feet + new Vector3(-sin, 0, -cos) * DropDistanceMetres;
    }

    /// <summary>Speed of the thrown smoke canister that marks the drop, m/s.</summary>
    public const float ThrowSpeedMetresPerSecond = 16f;
    /// <summary>Gravity on the canister, m/s².</summary>
    public const float ThrowGravity = 20f;
    /// <summary>Longest flight simulated, seconds (a throw into the sky lands somewhere).</summary>
    public const float ThrowMaxSeconds = 4f;

    /// <summary>A throw: the canister leaves <see cref="Origin"/> with <see cref="Velocity"/> and flies <see cref="Seconds"/> under <see cref="ThrowGravity"/> to <see cref="Landing"/>.</summary>
    public readonly record struct ThrowFlight(Vector3 Origin, Vector3 Velocity, float Seconds, Vector3 Landing);

    /// <summary>Where the thrown canister lands: see <see cref="Throw"/>.</summary>
    public static Vector3 ThrowLanding(Vector3 eye, float yaw, float pitch, Func<Vector3, Vector3, float, float> castDistance) =>
        Throw(eye, yaw, pitch, castDistance).Landing;

    /// <summary>
    /// The throw: the canister leaves <paramref name="eye"/> along the owner's look (yaw, pitch) and flies under gravity until
    /// <paramref name="castDistance"/> (from, direction, length: distance to the first surface, infinity if none) stops it. The crate parachutes down there.
    /// </summary>
    public static ThrowFlight Throw(Vector3 eye, float yaw, float pitch, Func<Vector3, Vector3, float, float> castDistance)
    {
        var (sy, cy) = MathF.SinCos(yaw);
        var (sp, cp) = MathF.SinCos(pitch);
        var v0 = new Vector3(-sy * cp, sp, -cy * cp) * ThrowSpeedMetresPerSecond;
        var vel = v0;
        var pos = eye;
        const float dt = 1f / 30f;
        var t = 0f;
        for (; t < ThrowMaxSeconds; t += dt)
        {
            vel.Y -= ThrowGravity * dt;
            var move = vel * dt;
            var len = move.Length();
            var d = castDistance(pos, move / len, len);
            if (d <= len) return new ThrowFlight(eye, v0, t + dt * (d / len), pos + move / len * d);
            pos += move;
        }
        return new ThrowFlight(eye, v0, t, pos);
    }

    public static bool InReach(Vector3 player, Vector3 crate, float slack = 0) =>
        Vector3.Distance(player, crate) <= ReachMetres + slack;
}

/// <summary>Where a crate is in its life: on the parachute, on the ground and openable, or ended (opened, or timed out unopened).</summary>
public enum CarePackageState : byte { Falling, Landed, Opened, Expired, Thrown, Marking }

/// <summary>Why a crate could not be opened.</summary>
public enum OpenResult : byte { Ok, Gone, NotLanded, OutOfReach }

/// <summary>
/// A care package on the map: a canister thrown by the owner that lands and smokes, then the crate falls from <see cref="CarePackageRules.StartHeightMetres"/> above its landing point on a
/// parachute and is openable once it lands. Takes no damage. It despawns after its lifetime, or when opened.
/// </summary>
public sealed class CarePackage : IWorldEntity
{
    public static readonly StateMachineSpec<CarePackageState> Spec = StateMachineSpec<CarePackageState>
        .Define("CarePackage.Life", "A care package: a canister flies from the owner's hand and smokes where it lands, then the crate falls on its parachute, lands and can be opened, then ends opened or expired (the world manager despawns it once it has ended).", CarePackageState.Thrown)
        .Allow(CarePackageState.Thrown, CarePackageState.Marking, CarePackageState.Expired)
        .Allow(CarePackageState.Marking, CarePackageState.Falling, CarePackageState.Expired)
        .Allow(CarePackageState.Falling, CarePackageState.Landed, CarePackageState.Expired)
        .Note(CarePackageState.Thrown, "The canister is in the air (ballistic, no crate yet); not openable.")
        .Note(CarePackageState.Marking, "The canister lies at the landing point and smokes for the mark time; then the crate starts from above it. Not openable.")
        .Allow(CarePackageState.Landed, CarePackageState.Opened, CarePackageState.Expired)
        .Note(CarePackageState.Falling, "Descending at the fall speed; not openable. The lifetime already runs (counted from the drop).")
        .Note(CarePackageState.Landed, "On the ground (altitude at the landing point's Y): openable by anyone in reach.")
        .Note(CarePackageState.Opened, "Opened by a player: ended, the reward is granted, the book despawns it at once. Cannot be reached from Expired.")
        .Note(CarePackageState.Expired, "The lifetime ran out unopened: ended. An ended crate cannot be opened even before it is despawned.")
        .Build();

    readonly Vector3 _landing;
    readonly StateMachine<CarePackageState> _fsm = new(Spec);
    float _altitude;
    Lifetime _life;
    Vector3 _flightPos, _flightVel;
    float _flightLeft;

    /// <summary>A crate dropping from above its landing point at once (no flight), or, with a <paramref name="flight"/>, a canister thrown first.</summary>
    public CarePackage(ushort owner, Vector3 landing, CarePackageRules.ThrowFlight? flight = null)
    {
        Owner = owner;
        _landing = landing;
        _altitude = landing.Y + CarePackageRules.StartHeightMetres;
        if (flight is { Seconds: > 0 } f)
        {
            _flightPos = f.Origin; _flightVel = f.Velocity; _flightLeft = f.Seconds;
        }
        else
        {
            _fsm.Enter(CarePackageState.Marking);
            _fsm.Enter(CarePackageState.Falling);
        }
    }

    public byte TypeId => CarePackageRules.TypeId;
    public ushort Owner { get; }
    public CarePackageState State => _fsm.Current;
    public WorldDespawnReason? Ended => _fsm.Current switch
    {
        CarePackageState.Opened => WorldDespawnReason.Opened,
        CarePackageState.Expired => WorldDespawnReason.Expired,
        _ => null,
    };
    /// <summary>The landing point (its Y is the ground).</summary>
    public Vector3 Landing => _landing;
    public bool Landed => !InFlight && _altitude <= _landing.Y;
    /// <summary>The canister stage: in the air or smoking on the ground (no crate yet).</summary>
    public bool InFlight => _fsm.IsAny(CarePackageState.Thrown, CarePackageState.Marking);
    public Vector3 Position => _fsm.Current switch
    {
        CarePackageState.Thrown => _flightPos,
        CarePackageState.Marking => _landing,
        _ => new Vector3(_landing.X, _altitude, _landing.Z),
    };

    public void Tick(float dt)
    {
        var expired = _life.Tick(dt, CarePackageRules.LifetimeSeconds);
        if (_fsm.Is(CarePackageState.Thrown))
        {
            var step = MathF.Min(dt, _flightLeft);
            _flightVel.Y -= CarePackageRules.ThrowGravity * step;
            _flightPos += _flightVel * step;
            _flightLeft -= dt;
            if (_flightLeft <= 0) { _flightPos = _landing; _fsm.Enter(CarePackageState.Marking, CarePackageRules.MarkSeconds); }
        }
        else if (_fsm.Is(CarePackageState.Marking) && _fsm.Tick(dt)) _fsm.Enter(CarePackageState.Falling);
        else if (!InFlight && !Landed) _altitude = MathF.Max(_landing.Y, _altitude - CarePackageRules.FallSpeedMetresPerSecond * dt);
        SyncLanded();
        if (Ended == null && expired) _fsm.Enter(CarePackageState.Expired);
    }

    /// <summary>Falling becomes Landed the moment the altitude reaches the ground.</summary>
    void SyncLanded()
    {
        if (_fsm.Is(CarePackageState.Falling) && Landed) _fsm.Enter(CarePackageState.Landed);
    }

    public void OnDamage(float amount, ushort attacker) { }

    public WorldState Serialize() => WorldState.Make(Position, 0, 0, Owner, WireState, 0);

    byte WireState => _fsm.Current switch
    {
        CarePackageState.Thrown => CarePackageRules.StateThrown,
        CarePackageState.Marking => CarePackageRules.StateMarking,
        _ => Landed ? CarePackageRules.StateLanded : CarePackageRules.StateFalling,
    };

    /// <summary>Marks the crate as opened; the book then despawns it with <see cref="WorldDespawnReason.Opened"/>. An ended crate (expired, or already opened) stays as it ended.</summary>
    internal void MarkOpened()
    {
        if (Ended != null) return;
        SyncLanded();
        _fsm.Enter(CarePackageState.Opened);
    }
}

/// <summary>
/// The care packages on the map. Owned by the match next to the entity manager; a crate that the manager despawns (expiry,
/// opened, round end) is forgotten. Opening draws the reward streak from the table, so the caller only grants it.
/// </summary>
public sealed class CarePackageBook
{
    readonly WorldEntityManager _world;
    readonly StreakRewards _rewards;
    readonly Dictionary<ushort, CarePackage> _crates = new();

    public CarePackageBook(WorldEntityManager world, StreakRewards rewards)
    {
        _world = world;
        _rewards = rewards;
        world.Despawned += d => _crates.Remove(d.Id);
    }

    public int Count => _crates.Count;

    /// <summary>A landed, live crate a player at <paramref name="player"/> could open (in reach, with the latency slack): it owns the interact key.</summary>
    public bool AnyInReach(Vector3 player, float slack = CarePackageRules.LatencySlackMetres) =>
        _crates.Values.Any(c => c.Ended == null && c.Landed && CarePackageRules.InReach(player, c.Position, slack));

    /// <summary>Drops a crate at <paramref name="landing"/>. Null when the world refuses it (cap reached).</summary>
    public ushort? Drop(ushort owner, Vector3 landing, CarePackageRules.ThrowFlight? flight = null)
    {
        var crate = new CarePackage(owner, landing, flight);
        var id = _world.Spawn(crate);
        if (id is { } i) _crates[i] = crate;
        return id;
    }

    /// <summary>
    /// Opens a crate for a player at <paramref name="player"/>. On <see cref="OpenResult.Ok"/> the rolled reward streak id is
    /// in <paramref name="streakId"/> and the crate is gone (despawned with <see cref="WorldDespawnReason.Opened"/>).
    /// Nothing changes otherwise.
    /// </summary>
    public OpenResult TryOpen(ushort crateId, Vector3 player, Random rng, out string? streakId, float slack = CarePackageRules.LatencySlackMetres)
    {
        streakId = null;
        if (!_crates.TryGetValue(crateId, out var crate)) return OpenResult.Gone;
        if (crate.Ended != null) return OpenResult.Gone; // expired in Tick but not despawned yet: too late (it must not turn Expired into Opened)
        if (!crate.Landed) return OpenResult.NotLanded;
        if (!CarePackageRules.InReach(player, crate.Position, slack)) return OpenResult.OutOfReach;
        streakId = _rewards.Pick(rng);
        crate.MarkOpened();
        _world.Despawn(crateId, WorldDespawnReason.Opened);
        return OpenResult.Ok;
    }
}

/// <summary>
/// The care package killstreak: a use throws a canister and drops a crate where it lands and ends at once (the crate lives on by itself).
/// Refused (the streak stays held) when the world has no room or there is no ground under the landing point.
/// </summary>
public sealed class CarePackageStreak : IKillstreak
{
    readonly CarePackageBook _book;
    readonly Func<ushort, Vector3, CarePackageRules.ThrowFlight> _throwOf;
    readonly Func<Vector3, float?> _groundAt;

    /// <param name="book">Where the crate goes.</param>
    /// <param name="throwOf">The owner's throw (owner, feet): see <see cref="CarePackageRules.Throw"/>.</param>
    /// <param name="groundAt">Height of the ground under a point, or null when there is none.</param>
    public CarePackageStreak(CarePackageBook book, Func<ushort, Vector3, CarePackageRules.ThrowFlight> throwOf, Func<Vector3, float?> groundAt)
    {
        _book = book;
        _throwOf = throwOf;
        _groundAt = groundAt;
    }

    public string Id => CarePackageRules.StreakId;
    public IReadOnlySet<string> OwnedWeapons { get; } = new HashSet<string>();

    public bool Activate(KillstreakUse use)
    {
        var flight = _throwOf(use.Owner, use.Position);
        var drop = flight.Landing;
        if (_groundAt(drop) is not { } ground)
        {
            use.Log("no ground at the drop point; streak kept");
            return false;
        }
        var landing = new Vector3(drop.X, ground, drop.Z);
        if (_book.Drop(use.Owner, landing, flight) is not { } id)
        {
            use.Log("no room for another world entity; streak kept");
            return false;
        }
        use.Log($"care package {id} dropped at {landing.X:0.0}, {landing.Z:0.0}");
        use.End();
        return true;
    }
}

/// <summary>Payloads of the care package's client-to-server event (little-endian).</summary>
public static class CarePackageCodec
{
    /// <summary>Client to server, <see cref="EventKind.OpenCarePackage"/>: u16 crate id.</summary>
    public static byte[] EncodeOpen(ushort crate)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, crate);
        return b;
    }

    public static bool TryDecodeOpen(ReadOnlySpan<byte> p, out ushort crate)
    {
        crate = 0;
        if (p.Length != 2) return false;
        crate = BinaryPrimitives.ReadUInt16LittleEndian(p);
        return true;
    }
}
