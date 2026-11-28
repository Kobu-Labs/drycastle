using System.Numerics;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// Every Duffel of the server, over the match's world entities (GLOSSARY "Duffel"; docs/cartel.md "Duffels"). Pure: the server feeds it the players (ids, feet, look) and a world
/// (<see cref="IDuffelWorld"/>), so the tests drive it with a flat floor and a fake cargo volume.
/// <list type="bullet">
/// <item>A Job (or <c>/duffel</c>) makes one for a party with <see cref="Give"/> (in a player's hands) or <see cref="Place"/> (on the ground), and takes its own away with <see cref="Remove"/> or <see cref="RemoveParty"/>.</item>
/// <item>One per player: a party player who carries none picks up the nearest lying one in reach (<see cref="PickUp"/>).</item>
/// <item>The carrier throws it (<see cref="Throw"/>: one press, a fixed range, an arc); it is put down where they stand only when they cannot carry it on (<see cref="Drop"/>: death, a seat, leaving the party or the server).</item>
/// <item>A cargo volume (<see cref="ICargoVolume"/>, <see cref="AddVolume"/>: every live cargo vehicle's, and every Heist's <see cref="Receiver"/>) takes a Duffel that flies or lands into it, when it accepts it: Loaded, on a slot; one it does not accept stops at it as at a wall. A party player with empty hands takes the last loaded one out (<see cref="TakeOut"/>).</item>
/// </list>
/// Every change of who carries what raises <see cref="CarrierChanged"/>, whoever made it (a pick-up, a throw, a drop, a Job's removal, the round's reset), so the server burdens and unburdens the body in one place.
/// </summary>
public sealed class DuffelBook
{
    readonly DuffelsFile _data;
    readonly WorldEntityManager _entities;
    readonly IDuffelWorld _world;
    readonly Dictionary<ushort, Duffel> _byId = new();
    readonly Dictionary<ushort, Duffel> _byCarrier = new();
    readonly List<ICargoVolume> _volumes = new();
    uint _loadCount;

    public DuffelBook(DuffelsFile data, WorldEntityManager entities, IDuffelWorld world)
    {
        _data = data; _entities = entities; _world = world;
        DuffelRules.Register(WorldEntityRegistry.Shared);
        entities.Despawned += w => Forget(w.Id);
    }

    /// <summary>A player took a Duffel (the Duffel) or carries none any more (null): a pick-up, a gift, a drop, a throw, a load, a removal, a reset.</summary>
    public event Action<ushort, Duffel?>? CarrierChanged;

    public int Count => _byId.Count;
    /// <summary>How many Duffels are carried now.</summary>
    public int CarriedCount => _byCarrier.Count;

    /// <summary>The Duffels carried now, copied into <paramref name="into"/> (cleared first; safe to drop while iterating, and the caller keeps the list so a tick allocates nothing).</summary>
    public void Carried(List<Duffel> into)
    {
        into.Clear();
        into.AddRange(_byCarrier.Values);
    }

    public void AddVolume(ICargoVolume volume) { if (!_volumes.Contains(volume)) _volumes.Add(volume); }
    public bool RemoveVolume(ICargoVolume volume) => _volumes.Remove(volume);

    public Duffel? Find(ushort id) => _byId.GetValueOrDefault(id);
    public Duffel? CarriedBy(ushort player) => _byCarrier.GetValueOrDefault(player);

    /// <summary>A Duffel of the party in the hands of <paramref name="carrier"/> (standing at <paramref name="feet"/>). Null when the carrier carries one already or the world is full.</summary>
    public Duffel? Give(DuffelKind kind, IReadOnlySet<ushort> party, ushort carrier, Vector3 feet)
    {
        if (_byCarrier.ContainsKey(carrier)) return null;
        var d = new Duffel(this, kind, party, feet, 0);
        d.TakeUp(carrier, feet);
        if (!Spawn(d)) return null;
        SetCarrier(carrier, d);
        return d;
    }

    /// <summary>A Duffel of the party lying at <paramref name="at"/> (a Job's table, a safe, the Cartel pallet), its length along <paramref name="yaw"/>. Null when the world is full.</summary>
    public Duffel? Place(DuffelKind kind, IReadOnlySet<ushort> party, Vector3 at, float yaw = 0)
    {
        var d = new Duffel(this, kind, party, at, yaw);
        return Spawn(d) ? d : null;
    }

    bool Spawn(Duffel d)
    {
        if (_entities.Spawn(d) is not { } id) return false;
        d.Id = id;
        _byId[id] = d;
        return true;
    }

    /// <summary>The nearest lying Duffel a player standing at <paramref name="feet"/> may pick up: of their party, within reach, and only while they carry none.</summary>
    public Duffel? Pickable(ushort player, Vector3 feet)
    {
        if (_byCarrier.ContainsKey(player)) return null;
        Duffel? best = null;
        var bd = (float)_data.ReachM;
        foreach (var d in _byId.Values)
        {
            if (d.State != DuffelState.Lying || MathF.Abs(d.Position.Y - feet.Y) > _data.ReachUpM) continue;
            var dist = new Vector2(d.Position.X - feet.X, d.Position.Z - feet.Z).Length();
            if (dist <= bd && d.MayTouch(player)) { best = d; bd = dist; } // the party last: the cheap distance rules out the most
        }
        return best;
    }

    /// <summary>The player picks up the nearest Duffel they may (<see cref="Pickable"/>). False when there is none.</summary>
    public bool PickUp(ushort player, Vector3 feet)
    {
        if (Pickable(player, feet) is not { } d) return false;
        d.TakeUp(player, feet);
        SetCarrier(player, d);
        return true;
    }

    /// <summary>A carried Duffel stays with its carrier (the server calls it every tick with the carrier's feet).</summary>
    public void Follow(ushort player, Vector3 feet)
    {
        if (_byCarrier.TryGetValue(player, out var d)) d.Position = feet;
    }

    /// <summary>
    /// The carrier puts it down at <paramref name="feet"/> on the ground under them: the carrier's death (where they fell), a seat, leaving the party or the server (never the player's own act: the
    /// throw key throws). No ground just below (put down from high up: a roof's edge, a ledge): it falls from the feet and lands as a throw does, so it never hangs out of reach in the air. False
    /// when they carry none.
    /// </summary>
    public bool Drop(ushort player, Vector3 feet)
    {
        if (!TakeFrom(player, out var d)) return false;
        if (_world.GroundY(feet, 0.5f, 3f) is { } ground) d.PutDown(new Vector3(feet.X, ground, feet.Z));
        else d.Launch(feet, Vector3.Zero);
        return true;
    }

    /// <summary>
    /// The carrier throws it from the eye (<paramref name="eye"/>, looking along <paramref name="yaw"/> and <paramref name="pitch"/>) at the fixed range (<see cref="DuffelThrow.Velocity"/>). The flight
    /// is stepped by the entity's tick. False when they carry none.
    /// </summary>
    public bool Throw(ushort player, Vector3 eye, float yaw, float pitch)
    {
        if (!TakeFrom(player, out var d)) return false;
        var (sin, cos) = MathF.SinCos(yaw);
        var forward = new Vector3(-sin, 0, -cos);
        var from = eye + forward * (float)_data.HandAheadM;
        if (_world.Blocked(eye, from, out var at, out var n)) from = at + n * (float)_data.WallGapM; // a wall right in front: it leaves from the face, not through it
        d.Launch(from, DuffelThrow.Velocity(_data, yaw, pitch));
        return true;
    }

    /// <summary>
    /// The Duffel that comes out of a cargo volume into the hands of a party player standing at <paramref name="feet"/> with empty hands: of the nearest volume whose open cargo area they stand at
    /// (<see cref="ICargoVolume.TakeOutDistance"/>) that holds one of their party's, the last loaded. Null when there is none (not their party's, hands full).
    /// </summary>
    public Duffel? Takeable(ushort player, Vector3 feet)
    {
        if (_byCarrier.ContainsKey(player)) return null;
        ICargoVolume? volume = null; var best = float.PositiveInfinity;
        foreach (var v in _volumes)
            if (v.TakeOutDistance(feet) is { } dist && dist < best && HasLoaded(v, player)) { volume = v; best = dist; }
        if (volume == null) return null;
        Duffel? last = null;
        foreach (var d in _byId.Values)
            if (d.State == DuffelState.Loaded && ReferenceEquals(d.Volume, volume) && d.MayTouch(player) && (last == null || d.LoadOrder > last.LoadOrder)) last = d;
        return last;
    }

    bool HasLoaded(ICargoVolume v, ushort player)
    {
        foreach (var d in _byId.Values) if (d.State == DuffelState.Loaded && ReferenceEquals(d.Volume, v) && d.MayTouch(player)) return true;
        return false;
    }

    /// <summary>The player takes the Duffel <see cref="Takeable"/> out into their hands; it leaves its slot. Null when nothing can be taken out.</summary>
    public Duffel? TakeOut(ushort player, Vector3 feet)
    {
        if (Takeable(player, feet) is not { } d) return null;
        d.Volume!.Unload(d);
        d.TakeUp(player, feet);
        SetCarrier(player, d);
        return d;
    }

    /// <summary>Takes every Duffel loaded in the volume out of the world (its vehicle was destroyed, retired or reset); the count removed.</summary>
    public int RemoveLoadedIn(ICargoVolume volume)
    {
        var gone = _byId.Values.Where(d => d.State == DuffelState.Loaded && ReferenceEquals(d.Volume, volume)).ToList();
        foreach (var d in gone) Remove(d);
        return gone.Count;
    }

    /// <summary>One flight step (from the entity's tick): gravity, the world, the volumes, and the landing.</summary>
    internal void StepFlight(Duffel d, float dt)
    {
        var g = MoveConsts.Gravity;
        var before = d.Position;
        var vel = d.Velocity;
        var next = before + vel * dt - new Vector3(0, 0.5f * g * dt * dt, 0);
        vel -= new Vector3(0, g * dt, 0);
        // a cargo volume is met before the world: a vehicle's hull does not stop a drop (ADR 0028), its cargo box does: it takes the bag that it accepts, and stops the one it does not as a wall does
        if (Meet(before, next) is { } hit)
        {
            if (hit.Volume.Accepts(d, hit.Meet.FromTop)) { Load(d, hit.Volume, next); return; }
            var aside = hit.Meet.At + new Vector3(hit.Meet.Normal.X, 0, hit.Meet.Normal.Z) * (float)_data.WallGapM;
            d.Fly(aside, new Vector3(0, MathF.Min(vel.Y, 0), 0));
            return;
        }
        if (_world.Blocked(before, next, out var at, out var normal))
        {
            if (normal.Y >= 0.5f) { Land(d, at); return; }
            // a wall: it drops off the face and falls straight down from there
            var off = at + new Vector3(normal.X, 0, normal.Z) * (float)_data.WallGapM;
            d.Fly(off, new Vector3(0, MathF.Min(vel.Y, 0), 0));
            return;
        }
        d.Fly(next, vel);
        if (d.FlightSeconds >= _data.MaxFlightSeconds) Land(d, new Vector3(next.X, _world.GroundY(next, 1f, 200f) ?? next.Y, next.Z));
    }

    void Land(Duffel d, Vector3 at)
    {
        var p = at + new Vector3(0, 0.1f, 0);
        foreach (var v in _volumes) if (v.Contains(p) && v.Accepts(d, false)) { Load(d, v, at); return; } // lands against the doors: it counts (a bed is never loaded from the ground)
        d.PutDown(at);
    }

    /// <summary>The nearest volume the segment meets, and where.</summary>
    (ICargoVolume Volume, CargoMeet Meet)? Meet(Vector3 from, Vector3 to)
    {
        (ICargoVolume, CargoMeet)? best = null; var bd = float.PositiveInfinity;
        foreach (var v in _volumes)
        {
            if (!v.Meets(from, to, out var m)) continue;
            var dist = Vector3.DistanceSquared(from, m.At);
            if (dist < bd) { bd = dist; best = (v, m); }
        }
        return best;
    }

    void Load(Duffel d, ICargoVolume v, Vector3 at)
    {
        var slot = v.Load(d);
        d.LoadInto(v, slot, at, ++_loadCount);
    }

    void SetCarrier(ushort player, Duffel d)
    {
        _byCarrier[player] = d;
        CarrierChanged?.Invoke(player, d);
    }

    bool TakeFrom(ushort player, out Duffel d)
    {
        if (!_byCarrier.Remove(player, out d!)) return false;
        CarrierChanged?.Invoke(player, null);
        return true;
    }

    /// <summary>Takes one Duffel out of the world (its Job ended). Its carrier carries nothing after it.</summary>
    public void Remove(Duffel d)
    {
        if (d.Id != 0) _entities.Despawn(d.Id, WorldDespawnReason.Removed);
        Forget(d.Id);
    }

    /// <summary>How many Duffels of this party exist now, wherever they are (lying, carried, flying, loaded).</summary>
    public int PartyCount(IReadOnlySet<ushort> party) => _byId.Values.Count(d => ReferenceEquals(d.Party, party));

    /// <summary>
    /// Takes every Duffel of this party out of the world (the Job ended); the count removed. Those loaded in <paramref name="keep"/> stay (a Receiver that is leaving carries its bags off; the server removes them when
    /// it is gone).
    /// </summary>
    public int RemoveParty(IReadOnlySet<ushort> party, ICargoVolume? keep = null)
    {
        var gone = _byId.Values.Where(d => ReferenceEquals(d.Party, party) && !(keep != null && d.State == DuffelState.Loaded && ReferenceEquals(d.Volume, keep))).ToList();
        foreach (var d in gone) Remove(d);
        return gone.Count;
    }

    /// <summary>A new round: the match cleared its entities; nothing is carried or tracked after it.</summary>
    public void Reset()
    {
        foreach (var d in _byId.Values.ToList()) if (d.Id != 0) _entities.Despawn(d.Id, WorldDespawnReason.Removed);
        foreach (var carrier in _byCarrier.Keys.ToList()) TakeFrom(carrier, out _);
        _byId.Clear();
    }

    void Forget(ushort id)
    {
        if (!_byId.Remove(id, out var d)) return;
        if (d.State == DuffelState.Loaded) d.Volume?.Unload(d);
        if (d.Carrier != 0 && _byCarrier.TryGetValue(d.Carrier, out var c) && c == d) TakeFrom(d.Carrier, out _);
    }
}

/// <summary>The throw's arc, shared by the server (which flies it) and the tests: the one range, and the launch velocity for a look.</summary>
public static class DuffelThrow
{
    /// <summary>
    /// The launch velocity: along the look (yaw), at the look's pitch plus lift_deg (held between min and max elevation), with the speed that carries throw_m (the same range for every kind) at the lift's elevation from a
    /// standing eye's height (<see cref="MoveConsts.EyeHeight"/>) down to flat ground (a level look throws that far, a look up throws higher and about as far).
    /// </summary>
    public static Vector3 Velocity(DuffelsFile data, float yaw, float pitch)
    {
        var lift = (float)(data.LiftDeg * Math.PI / 180);
        var range = (float)data.ThrowM;
        // x = v cos(a) t and 0 = h + v sin(a) t - g t^2 / 2 give v^2 = g R^2 / (2 cos^2(a) (h + R tan(a)))
        var (ls, lc) = MathF.SinCos(lift);
        var speed = MathF.Sqrt(MoveConsts.Gravity * range * range / (2 * lc * lc * (MoveConsts.EyeHeight + range * ls / lc)));
        var elevation = Math.Clamp(pitch + lift, (float)(data.MinElevationDeg * Math.PI / 180), (float)(data.MaxElevationDeg * Math.PI / 180));
        var (sin, cos) = MathF.SinCos(yaw);
        var (es, ec) = MathF.SinCos(elevation);
        return new Vector3(-sin * ec, es, -cos * ec) * speed;
    }
}

/// <summary>Why the server refused a Duffel action or a seat in the Cartel van, sent to the player (<see cref="EventKind.DuffelNotice"/>); the client words it (a toast).</summary>
public enum DuffelNotice : byte
{
    /// <summary>A carrier tried to take a vehicle seat.</summary>
    NoSeat = 0,
    /// <summary>The player carries one already (one per player).</summary>
    CarryingOne = 1,
    /// <summary>A player outside a Heist's crew tried to get into its Cartel van (a reserved vehicle, <see cref="Vehicles.VehicleSeat.Reserve"/>).</summary>
    VanReserved = 2,
}

/// <summary>The Duffel events' payloads: a notice (u8 <see cref="DuffelNotice"/>) and the Duffel in reach (u16 world entity id, 0 none).</summary>
public static class DuffelCodec
{
    public static byte[] EncodeNotice(DuffelNotice notice) => new[] { (byte)notice };

    public static bool TryDecodeNotice(ReadOnlySpan<byte> p, out DuffelNotice notice)
    {
        notice = default;
        if (p.Length < 1 || p[0] > (byte)DuffelNotice.VanReserved) return false;
        notice = (DuffelNotice)p[0];
        return true;
    }

    /// <summary>The take-out event's payload: u16 vehicle id (0: nothing to take out now), u8 the kind of the Duffel that would come out.</summary>
    public static byte[] EncodeTake(ushort vehicle, DuffelKind kind) => new[] { (byte)vehicle, (byte)(vehicle >> 8), (byte)kind };

    public static bool TryDecodeTake(ReadOnlySpan<byte> p, out ushort vehicle, out DuffelKind kind)
    {
        vehicle = 0; kind = default;
        if (p.Length < 3 || p[2] >= DuffelRules.AllKinds.Length) return false;
        vehicle = (ushort)(p[0] | p[1] << 8);
        kind = (DuffelKind)p[2];
        return true;
    }

    public static byte[] EncodeReach(ushort id) => new[] { (byte)id, (byte)(id >> 8) };

    public static bool TryDecodeReach(ReadOnlySpan<byte> p, out ushort id)
    {
        id = 0;
        if (p.Length < 2) return false;
        id = (ushort)(p[0] | p[1] << 8);
        return true;
    }
}
