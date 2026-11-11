using System.Buffers.Binary;
using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>Corpse rules (ticket #130): what a killed player leaves behind and who may take it.</summary>
public static class CorpseRules
{
    /// <summary>Wire id of the corpse world entity type.</summary>
    public const byte TypeId = 1;
    public const float LifetimeSeconds = 120f;
    /// <summary>Map-wide cap; the oldest corpse goes first when a new one would exceed it.</summary>
    public const int MaxCorpses = 16;
    /// <summary>Reach of the pickup, metres from the corpse.</summary>
    public const float ReachMetres = 2.5f;
    /// <summary>Extra reach the server allows for latency: the client prompts at <see cref="ReachMetres"/>, the packet arrives later.</summary>
    public const float LatencySlackMetres = 0.5f;

    /// <summary>A body thrown by a blast or a car (the ragdoll): the push is the blast knockback (or the car's velocity) times <see cref="ThrowScale"/>, plus lift.</summary>
    public const float ThrowScale = 3f, ThrowLift = 0.4f, MaxThrowSpeed = 30f, Gravity = 20f, BodyRadius = 0.4f, MaxFlightSeconds = 6f;
    /// <summary>Pitch value (rad) a flying corpse carries in its replicated state; a corpse at rest carries 0.</summary>
    public const float FlightPitch = 1f;

    /// <summary>The velocity a killed player is thrown with from a blast knockback (<see cref="ProjectileMath.Knockback(float, Vector3, Vector3, float)"/>): scaled up, with extra lift that grows with the horizontal speed.</summary>
    public static Vector3 ThrowFromBlast(Vector3 knockback)
    {
        var v = knockback * ThrowScale;
        v.Y += MathF.Sqrt(v.X * v.X + v.Z * v.Z) * ThrowLift;
        return v;
    }

    /// <summary>The velocity of a body run over: carried forward at the car's speed, with a hop that grows with it.</summary>
    public static Vector3 ThrowFromCar(Vector3 forward, float speed) =>
        forward * speed * 0.9f + Vector3.UnitY * (2.5f + MathF.Abs(speed) * 0.2f);

    /// <summary>Hotbar positions a corpse weapon can go into: 0 = primary, 1 = secondary (the knife is never replaced).</summary>
    public const int PrimaryPos = 0, SecondaryPos = 1;

    /// <summary>Registers the corpse type once (idempotent), so the snapshot encoder knows it carries hp (here: ammo).</summary>
    public static void Register(WorldEntityRegistry registry)
    {
        if (registry.Find(TypeId) == null) registry.Register(new WorldEntityDef(TypeId, "corpse", HasHp: true));
    }

    public static bool InReach(Vector3 player, Vector3 corpse, float slack = 0) =>
        Vector3.Distance(player, corpse) <= ReachMetres + slack;

    /// <summary>
    /// Whether a weapon may go into a hotbar position. A primary replaces the primary or the secondary position;
    /// a pistol (secondary) replaces only the secondary position; nothing else is a valid replacement.
    /// </summary>
    public static bool CanReplace(WeaponDef weapon, int hotbarPos) => weapon.Slot switch
    {
        WeaponSlot.Primary => hotbarPos is PrimaryPos or SecondaryPos,
        WeaponSlot.Secondary => hotbarPos == SecondaryPos,
        _ => false,
    };
}

public enum PickUpResult : byte { Ok, Gone, OutOfReach, BadSlot, WrongKind, AlreadyHeld }

/// <summary>
/// A killed player's body on the ground. It carries one weapon (weapons.json index) and its remaining rounds (magazine
/// plus reserve, capped at 255 on the wire). The state is replicated as <see cref="WorldState"/>: the weapon in
/// <see cref="WorldState.State"/> and the rounds in <see cref="WorldState.Hp"/> (corpses have no hit points).
/// Corpses take no damage. They despawn after <see cref="CorpseRules.LifetimeSeconds"/>.
/// </summary>
public sealed class Corpse : IWorldEntity
{
    readonly float _yaw;
    float _age;

    public Corpse(ushort owner, Vector3 position, float yaw, int weapon, int rounds)
    {
        Owner = owner; _body = new ThrownBody(position); _yaw = yaw; Weapon = weapon; Rounds = rounds;
    }

    public byte TypeId => CorpseRules.TypeId;
    public ushort Owner { get; }
    public WorldDespawnReason? Ended { get; private set; }
    readonly ThrownBody _body;
    public Vector3 Position => _body.Position;
    /// <summary>Velocity while thrown (a blast, a car); zero at rest.</summary>
    public Vector3 Velocity => _body.Velocity;
    /// <summary>True while the body is thrown and has not landed. Replicated in the pitch field (<see cref="CorpseRules.FlightPitch"/>) so the client tumbles it.</summary>
    public bool Airborne => _body.Airborne;
    /// <summary>weapons.json index of the weapon the corpse carries.</summary>
    public int Weapon { get; private set; }
    public int Rounds { get; private set; }

    public void Tick(float dt)
    {
        _age += dt;
        if (_age >= CorpseRules.LifetimeSeconds) Ended = WorldDespawnReason.Expired;
    }

    public void OnDamage(float amount, ushort attacker) { }

    public WorldState Serialize() => WorldState.Make(Position, _yaw, Airborne ? CorpseRules.FlightPitch : 0, Owner, (byte)Weapon, Rounds);

    /// <summary>Throws the body with <paramref name="velocity"/> (m/s). A zero push leaves it lying where it fell.</summary>
    public void Launch(Vector3 velocity) => _body.Launch(velocity);

    /// <summary>One step of the flight, see <see cref="ThrownBody.Fly"/>.</summary>
    public void Fly(float dt, Func<Vector3, Vector3, float, float?> cast) => _body.Fly(dt, cast);

    /// <summary>Puts a weapon on the corpse (the one a taker swapped out).</summary>
    public void Carry(int weapon, int rounds)
    {
        Weapon = weapon;
        Rounds = rounds;
    }
}

/// <summary>
/// The corpses on the map, in spawn order: enforces the cap (oldest first) and the pickup rules against the taker's
/// <see cref="PlayerWeapons"/>. Owned by the match next to <see cref="WorldEntityManager"/>; it forgets a corpse when the
/// manager despawns it, whatever the reason (expiry, cap, round end).
/// </summary>
public sealed class CorpseBook
{
    readonly WorldEntityManager _world;
    readonly List<(ushort Id, Corpse Body)> _order = new();

    public CorpseBook(WorldEntityManager world)
    {
        _world = world;
        world.Despawned += d => _order.RemoveAll(x => x.Id == d.Id);
    }

    public int Count => _order.Count;

    /// <summary>The corpses on the map, oldest first.</summary>
    public IEnumerable<Corpse> Bodies => _order.Select(x => x.Body);

    /// <summary>Puts a new corpse on the map, removing the oldest corpses first to stay within <see cref="CorpseRules.MaxCorpses"/>.</summary>
    public ushort? Add(Corpse body)
    {
        while (_order.Count >= CorpseRules.MaxCorpses)
            if (!_world.Despawn(_order[0].Id, WorldDespawnReason.Removed)) _order.RemoveAt(0);
        var id = _world.Spawn(body);
        if (id is { } i) _order.Add((i, body));
        return id;
    }

    /// <summary>
    /// Pickup: the taker at <paramref name="player"/> takes the corpse's weapon into hotbar position <paramref name="hotbarPos"/>
    /// (fresh magazine, half reserve). The weapon it replaces is dropped on the corpse, so it can be swapped back.
    /// </summary>
    public PickUpResult TryPickUp(ushort corpseId, Vector3 player, PlayerWeapons taker, WeaponsFile file, int hotbarPos, float slack = CorpseRules.LatencySlackMetres)
    {
        var entry = _order.FirstOrDefault(x => x.Id == corpseId);
        if (entry.Body == null) return PickUpResult.Gone;
        var body = entry.Body;
        if (!CorpseRules.InReach(player, body.Position, slack)) return PickUpResult.OutOfReach;
        if (hotbarPos < 0 || hotbarPos >= taker.HotbarCount) return PickUpResult.BadSlot;
        if (!CorpseRules.CanReplace(file.Weapons[body.Weapon], hotbarPos)) return PickUpResult.WrongKind;
        if (taker.HotbarHolds(body.Weapon)) return PickUpResult.AlreadyHeld;
        taker.ReplaceHotbar(hotbarPos, body.Weapon, out var dropped, out var rounds);
        body.Carry(dropped, rounds);
        return PickUpResult.Ok;
    }
}

/// <summary>Payloads of the corpse event kinds (little-endian).</summary>
public static class CorpseCodec
{
    /// <summary>Client to server, <see cref="EventKind.PickUpCorpse"/>: u16 corpse id, u8 hotbar position (0 primary, 1 secondary).</summary>
    public static byte[] EncodePickUp(ushort corpse, byte hotbarPos)
    {
        var b = new byte[3];
        BinaryPrimitives.WriteUInt16LittleEndian(b, corpse);
        b[2] = hotbarPos;
        return b;
    }

    public static bool TryDecodePickUp(ReadOnlySpan<byte> p, out ushort corpse, out byte hotbarPos)
    {
        corpse = 0; hotbarPos = 0;
        if (p.Length != 3) return false;
        corpse = BinaryPrimitives.ReadUInt16LittleEndian(p);
        hotbarPos = p[2];
        return true;
    }

    /// <summary>Server to one player, <see cref="EventKind.Hotbar"/>: u8 count, count * u8 weapons.json index (primary, secondary, knife order).</summary>
    public static byte[] EncodeHotbar(IReadOnlyList<int> weapons)
    {
        var b = new byte[1 + weapons.Count];
        b[0] = (byte)weapons.Count;
        for (var i = 0; i < weapons.Count; i++) b[1 + i] = (byte)weapons[i];
        return b;
    }

    public static bool TryDecodeHotbar(ReadOnlySpan<byte> p, out int[] weapons)
    {
        weapons = Array.Empty<int>();
        if (p.Length < 1 || p.Length != 1 + p[0]) return false;
        weapons = new int[p[0]];
        for (var i = 0; i < weapons.Length; i++) weapons[i] = p[1 + i];
        return true;
    }
}

/// <summary>
/// A body in flight (the ragdoll): thrown with a velocity, falls under gravity, stops against walls and lands. Used by corpses and by
/// target dummies. <see cref="Fly"/> is stepped by the server with a world ray cast; nothing here knows the physics engine.
/// </summary>
public sealed class ThrownBody
{
    float _flight;

    public ThrownBody(Vector3 position) => Position = position;

    public Vector3 Position { get; private set; }
    public Vector3 Velocity { get; private set; }
    public bool Airborne { get; private set; }

    /// <summary>Throws the body with <paramref name="velocity"/> (m/s), capped at <see cref="CorpseRules.MaxThrowSpeed"/>. A zero push does nothing.</summary>
    public void Launch(Vector3 velocity)
    {
        var v = velocity.Length() > CorpseRules.MaxThrowSpeed ? Vector3.Normalize(velocity) * CorpseRules.MaxThrowSpeed : velocity;
        if (v.LengthSquared() < 1e-4f) return;
        Velocity = v;
        Airborne = true;
        _flight = 0;
        Position += Vector3.UnitY * 0.05f; // clear of the floor it stands on
    }

    /// <summary>Back on <paramref name="position"/> at rest (a dummy standing up again).</summary>
    public void Reset(Vector3 position)
    {
        Position = position;
        Velocity = Vector3.Zero;
        Airborne = false;
    }

    /// <summary>
    /// One step of the flight: gravity, then the horizontal move (stops dead against a wall), then the vertical one (lands on
    /// the ground, bumps the ceiling). <paramref name="cast"/> gives the distance to the first solid along a ray from a point
    /// (direction is unit length), or null when nothing is hit within the length. The flight ends after
    /// <see cref="CorpseRules.MaxFlightSeconds"/> whatever happens.
    /// </summary>
    public void Fly(float dt, Func<Vector3, Vector3, float, float?> cast)
    {
        if (!Airborne) return;
        _flight += dt;
        var v = Velocity;
        v.Y -= CorpseRules.Gravity * dt;
        var p = Position;

        var h = new Vector3(v.X, 0, v.Z) * dt;
        var hl = h.Length();
        if (hl > 1e-4f)
        {
            var dir = h / hl;
            var hit = cast(p + Vector3.UnitY * 0.6f, dir, hl + CorpseRules.BodyRadius);
            if (hit is { } d) { p += dir * MathF.Max(0, d - CorpseRules.BodyRadius); v.X = 0; v.Z = 0; }
            else p += h;
        }

        var dy = v.Y * dt;
        if (dy < 0)
        {
            var hit = cast(p + Vector3.UnitY * 0.3f, -Vector3.UnitY, 0.3f - dy);
            if (hit is { } d) { p.Y += 0.3f - d; Land(p); return; }
            p.Y += dy;
        }
        else
        {
            var hit = cast(p + Vector3.UnitY * 0.9f, Vector3.UnitY, dy + 0.1f);
            if (hit != null) v.Y = 0; else p.Y += dy;
        }
        Position = p;
        Velocity = v;
        if (_flight >= CorpseRules.MaxFlightSeconds) Land(p);
    }

    void Land(Vector3 at)
    {
        Position = at;
        Velocity = Vector3.Zero;
        Airborne = false;
    }
}
