using System.Buffers.Binary;
using System.Numerics;

namespace Drycastle.ShooterCore;

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
/// <para>
/// A corpse can also carry <see cref="Items"/>, up to <see cref="ItemRules.MaxCarried"/> (all its player wore and bagged, ADR 0018):
/// not in the world state, but announced by <see cref="EventKind.CorpseItems"/>. They live and die with the corpse.
/// </para>
/// </summary>
public sealed class Corpse : IWorldEntity
{
    readonly float _yaw;
    float _age;
    readonly float _lifetime;

    readonly List<ItemStack> _items = new();

    /// <param name="lifetime">How long it lies, seconds: a player's corpse lasts <see cref="CorpseRules.LifetimeSeconds"/>; the Bigfoot's (issue #264) lasts longer, since it has to be found.</param>
    public Corpse(ushort owner, Vector3 position, float yaw, int weapon, int rounds, IEnumerable<ItemStack>? items = null, float lifetime = CorpseRules.LifetimeSeconds)
    {
        Owner = owner; _body = new ThrownBody(position); _yaw = yaw; Weapon = weapon; Rounds = rounds; _lifetime = lifetime;
        if (items != null) _items.AddRange(items.Take(ItemRules.MaxCarried));
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
    /// <summary>The items the corpse carries (each with how often it has been taken), in the order they were put on it: what was worn, then the bag.</summary>
    public IReadOnlyList<ItemStack> Items => _items;
    /// <summary>The first item the corpse carries, or null: what the single-row chooser and the prompt name.</summary>
    public ItemStack? Item => _items.Count > 0 ? _items[0] : null;

    public void Tick(float dt)
    {
        _age += dt;
        if (_age >= _lifetime) Ended = WorldDespawnReason.Expired;
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

    /// <summary>Puts an item on the corpse at the end of its list. False (nothing changes) when it already carries <see cref="ItemRules.MaxCarried"/>.</summary>
    public bool AddItem(ItemStack item)
    {
        if (_items.Count >= ItemRules.MaxCarried) return false;
        _items.Add(item);
        return true;
    }

    /// <summary>Takes the item at <paramref name="index"/> off the corpse (taken, or crumbled); the ones after it move up. An index out of range is ignored.</summary>
    public void RemoveItem(int index)
    {
        if (index >= 0 && index < _items.Count) _items.RemoveAt(index);
    }

    /// <summary>Puts <paramref name="item"/> in the place of the one at <paramref name="index"/> (the one a taker swapped out). An index out of range is ignored.</summary>
    public void ReplaceItem(int index, ItemStack item)
    {
        if (index >= 0 && index < _items.Count) _items[index] = item;
    }
}

/// <summary>
/// The corpses and the ground items on the map, in spawn order: enforces the cap (oldest first) and the pickup rules against the taker's
/// <see cref="PlayerWeapons"/>, <see cref="Equipment"/> and <see cref="Bag"/>. A ground item (ADR 0018) counts against the corpse cap, so
/// a busy fight cannot fill the map with either. Owned by the match next to <see cref="WorldEntityManager"/>; it forgets one when the
/// manager despawns it, whatever the reason (expiry, cap, round end).
/// </summary>
public sealed class CorpseBook
{
    readonly WorldEntityManager _world;
    readonly List<(ushort Id, Corpse Body)> _order = new();
    readonly List<(ushort Id, GroundItem Item)> _ground = new();
    /// <summary>When each thing was put on the map (a running number), so the cap can find the oldest across both lists.</summary>
    readonly Dictionary<ushort, long> _born = new();
    long _clock;

    public CorpseBook(WorldEntityManager world)
    {
        _world = world;
        world.Despawned += d =>
        {
            _order.RemoveAll(x => x.Id == d.Id);
            _ground.RemoveAll(x => x.Id == d.Id);
            _born.Remove(d.Id);
        };
    }

    /// <summary>Everything the cap counts: the corpses and the ground items.</summary>
    public int Count => _order.Count + _ground.Count;

    public int CorpseCount => _order.Count;

    public int GroundCount => _ground.Count;

    /// <summary>The corpses on the map, oldest first.</summary>
    public IEnumerable<Corpse> Bodies => _order.Select(x => x.Body);

    /// <summary>The corpses on the map with their ids, oldest first.</summary>
    public IEnumerable<(ushort Id, Corpse Body)> WithIds => _order;

    /// <summary>The corpse with this id, or null.</summary>
    public Corpse? Get(ushort id) => _order.FirstOrDefault(x => x.Id == id).Body;

    /// <summary>The ground item with this id, or null.</summary>
    public GroundItem? GetGround(ushort id) => _ground.FirstOrDefault(x => x.Id == id).Item;

    /// <summary>
    /// Removes the oldest things until there is room for one more under <see cref="CorpseRules.MaxCorpses"/>. A thing that carries an item
    /// goes last (every ground item does, and a corpse with items): a busy fight must not wipe out an event reward, so the oldest corpse
    /// without one is taken first.
    /// </summary>
    void MakeRoom()
    {
        while (Count >= CorpseRules.MaxCorpses)
        {
            var plain = _order.Where(x => x.Body.Items.Count == 0).Select(x => (ushort?)x.Id).OrderBy(id => _born.GetValueOrDefault(id!.Value)).FirstOrDefault();
            var victim = plain ?? _born.OrderBy(kv => kv.Value).Select(kv => (ushort?)kv.Key).FirstOrDefault();
            if (victim is not { } id) return;
            if (!_world.Despawn(id, WorldDespawnReason.Removed)) { _order.RemoveAll(x => x.Id == id); _ground.RemoveAll(x => x.Id == id); _born.Remove(id); }
        }
    }

    /// <summary>
    /// Puts a new corpse on the map, removing the oldest things first to stay within <see cref="CorpseRules.MaxCorpses"/> (see
    /// <see cref="MakeRoom"/>).
    /// </summary>
    public ushort? Add(Corpse body)
    {
        MakeRoom();
        var id = _world.Spawn(body);
        if (id is { } i) { _order.Add((i, body)); _born[i] = ++_clock; }
        return id;
    }

    /// <summary>Puts a dropped item on the map at <paramref name="at"/> (the terrain height under the dropper), under the same cap as the corpses. Null when the world refuses it.</summary>
    public ushort? Drop(ushort dropper, Vector3 at, ItemStack stack)
    {
        MakeRoom();
        var item = new GroundItem(dropper, at, stack);
        var id = _world.Spawn(item);
        if (id is { } i) { _ground.Add((i, item)); _born[i] = ++_clock; }
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

    /// <summary>
    /// Item pickup: the taker at <paramref name="player"/> takes the item at <paramref name="index"/> of the corpse's list
    /// (<see cref="ItemRules.Take"/> counts the pickup). It goes into its empty slot, else the bag; with both full it is worn and what was worn
    /// in that slot takes its place on the corpse (<see cref="ItemRules.Place"/>, <paramref name="placed"/> says which). An item taken as often
    /// as its definition allows crumbles instead: it is gone from the corpse and the taker gets nothing. The corpse's weapon is not involved;
    /// this is a separate action.
    /// </summary>
    public ItemPickUp TryPickUpItem(ushort corpseId, int index, Vector3 player, Equipment worn, Bag bag, ItemsFile items, out ItemPlace placed, float slack = CorpseRules.LatencySlackMetres)
    {
        placed = ItemPlace.None;
        var entry = _order.FirstOrDefault(x => x.Id == corpseId);
        if (entry.Body == null) return ItemPickUp.Gone;
        var body = entry.Body;
        if (!CorpseRules.InReach(player, body.Position, slack)) return ItemPickUp.OutOfReach;
        if (index < 0 || index >= body.Items.Count) return ItemPickUp.NoItem;
        var result = ItemRules.Take(items, body.Items[index], out var taken);
        if (result == ItemPickUp.Crumbled) body.RemoveItem(index);
        if (result != ItemPickUp.Ok) return result;
        placed = ItemRules.Place(items, taken, worn, bag, swap: true, out var displaced);
        if (placed == ItemPlace.Swapped) body.ReplaceItem(index, displaced!.Value); else body.RemoveItem(index);
        return ItemPickUp.Ok;
    }

    /// <summary>
    /// Ground item pickup: the taker at <paramref name="player"/> takes it (the dropper too), counting one pickup, into its empty slot, else the
    /// bag. With both full nothing is taken or counted (<see cref="ItemPickUp.NoRoom"/>, the item stays where it is): there is no swap, since the
    /// item that would be left behind has no corpse to go on. An item taken as often as its definition allows crumbles: it is gone.
    /// </summary>
    public ItemPickUp TryTakeGround(ushort id, Vector3 player, Equipment worn, Bag bag, ItemsFile items, out ItemPlace placed, float slack = GroundItemRules.LatencySlackMetres)
    {
        placed = ItemPlace.None;
        var entry = _ground.FirstOrDefault(x => x.Id == id);
        if (entry.Item == null) return ItemPickUp.Gone;
        if (!GroundItemRules.InReach(player, entry.Item.Position, slack)) return ItemPickUp.OutOfReach;
        if (items.At(entry.Item.Stack.Item) is { } def && worn.Get(def.Slot) != null && bag.IsFull) return ItemPickUp.NoRoom; // before the count: a futile attempt must not crumble it
        var result = ItemRules.Take(items, entry.Item.Stack, out var taken);
        if (result == ItemPickUp.Crumbled) _world.Despawn(id, WorldDespawnReason.Removed);
        if (result != ItemPickUp.Ok) return result;
        placed = ItemRules.Place(items, taken, worn, bag, swap: false, out _);
        _world.Despawn(id, WorldDespawnReason.Opened);
        return ItemPickUp.Ok;
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
