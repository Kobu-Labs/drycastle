using System.Numerics;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// Every Duffel of the server, over the match's world entities (GLOSSARY "Duffel"; docs/cartel.md "Duffels"). Pure: the server feeds it the players (ids, feet, look) and a world
/// (<see cref="IDuffelWorld"/>), so the tests drive it with a flat floor and a fake cargo volume.
/// <list type="bullet">
/// <item>A Job (or <c>/duffel</c>) makes one for a party with <see cref="Give"/> (in a player's hands) or <see cref="Place"/> (on the ground), and takes its own away with <see cref="Remove"/> or <see cref="RemoveParty"/>.</item>
/// <item>One per player: a party player who carries none picks up the nearest lying one in reach (<see cref="PickUp"/>).</item>
/// <item>The carrier puts it down at the feet (<see cref="Drop"/>, the throw key's tap and the carrier's death) or throws it (<see cref="Throw"/>, an arc by charge and weight).</item>
/// <item>A cargo volume (<see cref="ICargoVolume"/>, <see cref="AddVolume"/>) takes a Duffel that flies or is put down into it: Loaded, and the volume's owner counts it (<see cref="LoadedIn"/>, <see cref="Loaded"/>).</item>
/// </list>
/// </summary>
public sealed class DuffelBook
{
    readonly DuffelsFile _data;
    readonly WorldEntityManager _entities;
    readonly IDuffelWorld _world;
    readonly Dictionary<ushort, Duffel> _byId = new();
    readonly Dictionary<ushort, Duffel> _byCarrier = new();
    readonly List<ICargoVolume> _volumes = new();

    public DuffelBook(DuffelsFile data, WorldEntityManager entities, IDuffelWorld world)
    {
        _data = data; _entities = entities; _world = world;
        DuffelRules.Register(WorldEntityRegistry.Shared);
        entities.Despawned += w => Forget(w.Id);
    }

    public DuffelsFile Data => _data;

    /// <summary>A Duffel went into a cargo volume (after the volume's own <see cref="ICargoVolume.Load"/>).</summary>
    public event Action<Duffel, ICargoVolume>? Loaded;

    public int Count => _byId.Count;
    public IEnumerable<Duffel> All => _byId.Values;
    public IReadOnlyList<ICargoVolume> Volumes => _volumes;
    /// <summary>The Duffels carried now, by carrier (a copy: safe to drop while iterating).</summary>
    public List<Duffel> Carried() => _byCarrier.Values.ToList();

    public void AddVolume(ICargoVolume volume) { if (!_volumes.Contains(volume)) _volumes.Add(volume); }
    public bool RemoveVolume(ICargoVolume volume) => _volumes.Remove(volume);

    public Duffel? Find(ushort id) => _byId.GetValueOrDefault(id);
    public Duffel? CarriedBy(ushort player) => _byCarrier.GetValueOrDefault(player);
    public int LoadedIn(ICargoVolume volume) => _byId.Values.Count(d => d.State == DuffelState.Loaded && d.Volume == volume);

    /// <summary>A Duffel of the party in the hands of <paramref name="carrier"/> (standing at <paramref name="feet"/>). Null when the carrier carries one already or the world is full.</summary>
    public Duffel? Give(DuffelKind kind, IReadOnlySet<ushort> party, ushort carrier, Vector3 feet)
    {
        if (_byCarrier.ContainsKey(carrier)) return null;
        var d = new Duffel(this, kind, party, feet, 0);
        d.TakeUp(carrier, feet);
        if (!Spawn(d)) return null;
        _byCarrier[carrier] = d;
        return d;
    }

    /// <summary>A Duffel of the party lying at <paramref name="at"/> (a Job's table, a safe). Null when the world is full.</summary>
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
            if (d.State != DuffelState.Lying || !d.MayTouch(player) || MathF.Abs(d.Position.Y - feet.Y) > _data.ReachUpM) continue;
            var dist = new Vector2(d.Position.X - feet.X, d.Position.Z - feet.Z).Length();
            if (dist <= bd) { best = d; bd = dist; }
        }
        return best;
    }

    /// <summary>The player picks up the nearest Duffel they may (<see cref="Pickable"/>). False when there is none.</summary>
    public bool PickUp(ushort player, Vector3 feet)
    {
        if (Pickable(player, feet) is not { } d) return false;
        d.TakeUp(player, feet);
        _byCarrier[player] = d;
        return true;
    }

    /// <summary>A carried Duffel stays with its carrier (the server calls it every tick with the carrier's feet).</summary>
    public void Follow(ushort player, Vector3 feet)
    {
        if (_byCarrier.TryGetValue(player, out var d)) d.Position = feet;
    }

    /// <summary>
    /// The carrier puts it down at <paramref name="feet"/>: on the ground under them, or Loaded when that is inside a cargo volume that takes it (standing in the van's back). The throw key's tap,
    /// the carrier's death (where they fell) and leaving. False when they carry none.
    /// </summary>
    public bool Drop(ushort player, Vector3 feet)
    {
        if (!_byCarrier.Remove(player, out var d)) return false;
        var probe = feet + new Vector3(0, 0.3f, 0);
        if (VolumeFor(d, probe) is { } v) { Load(d, v, probe); return true; }
        var ground = _world.GroundY(feet, 0.5f, 3f) ?? feet.Y;
        d.PutDown(new Vector3(feet.X, ground, feet.Z));
        return true;
    }

    /// <summary>
    /// The carrier throws it from the eye (<paramref name="eye"/>, looking along <paramref name="yaw"/> and <paramref name="pitch"/>) with a charge of 0 to 1 (<see cref="DuffelThrow.Velocity"/>). The flight
    /// is stepped by the entity's tick. False when they carry none.
    /// </summary>
    public bool Throw(ushort player, Vector3 eye, float yaw, float pitch, float charge)
    {
        if (!_byCarrier.Remove(player, out var d)) return false;
        var (sin, cos) = MathF.SinCos(yaw);
        var forward = new Vector3(-sin, 0, -cos);
        var from = eye + forward * (float)_data.HandAheadM;
        if (_world.Blocked(eye, from, out var at, out var n)) from = at + n * (float)_data.WallGapM; // a wall right in front: it leaves from the face, not through it
        d.Launch(from, DuffelThrow.Velocity(_data, d.Kind, yaw, pitch, charge));
        return true;
    }

    /// <summary>One flight step (from the entity's tick): gravity, the world, the volumes, and the landing.</summary>
    internal void StepFlight(Duffel d, float dt)
    {
        var g = MoveConsts.Gravity;
        var before = d.Position;
        var vel = d.Velocity;
        var next = before + vel * dt - new Vector3(0, 0.5f * g * dt * dt, 0);
        vel -= new Vector3(0, g * dt, 0);
        // a cargo volume is met before the world: the van's hull does not stop a drop (ADR 0028), its box catches the bag on the way through
        var mid = (before + next) * 0.5f;
        if ((VolumeFor(d, mid) ?? VolumeFor(d, next)) is { } v) { Load(d, v, next); return; }
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
        if (VolumeFor(d, at + new Vector3(0, 0.1f, 0)) is { } v) { Load(d, v, at); return; }
        d.PutDown(at);
    }

    ICargoVolume? VolumeFor(Duffel d, Vector3 p)
    {
        foreach (var v in _volumes) if (v.Contains(p) && v.Accepts(d)) return v;
        return null;
    }

    void Load(Duffel d, ICargoVolume v, Vector3 at)
    {
        d.LoadInto(v, at);
        v.Load(d);
        Loaded?.Invoke(d, v);
    }

    /// <summary>Takes one Duffel out of the world (its Job ended). Its carrier carries nothing after it.</summary>
    public void Remove(Duffel d)
    {
        if (d.Id != 0) _entities.Despawn(d.Id, WorldDespawnReason.Removed);
        Forget(d.Id);
    }

    /// <summary>Takes every Duffel of this party out of the world (the Job ended); the count removed.</summary>
    public int RemoveParty(IReadOnlySet<ushort> party)
    {
        var gone = _byId.Values.Where(d => ReferenceEquals(d.Party, party)).ToList();
        foreach (var d in gone) Remove(d);
        return gone.Count;
    }

    /// <summary>A new round: the match cleared its entities; nothing is carried or tracked after it.</summary>
    public void Reset()
    {
        foreach (var d in _byId.Values.ToList()) if (d.Id != 0) _entities.Despawn(d.Id, WorldDespawnReason.Removed);
        _byId.Clear(); _byCarrier.Clear();
    }

    void Forget(ushort id)
    {
        if (!_byId.Remove(id, out var d)) return;
        if (d.Carrier != 0 && _byCarrier.TryGetValue(d.Carrier, out var c) && c == d) _byCarrier.Remove(d.Carrier);
    }
}

/// <summary>The throw's arc, shared by the server (which flies it) and the tests: how far a charge throws a kind, and the launch velocity for a look.</summary>
public static class DuffelThrow
{
    /// <summary>Metres a throw of this charge (0 to 1) reaches on flat ground: from min_throw_share of the kind's throw_m (a short hold) up to all of it (a full charge).</summary>
    public static float RangeM(DuffelsFile data, DuffelKind kind, float charge)
    {
        var k = Math.Clamp(charge, 0f, 1f);
        var share = (float)data.MinThrowShare + (1f - (float)data.MinThrowShare) * k;
        return (float)data.Of(kind).ThrowM * share;
    }

    /// <summary>
    /// The launch velocity: along the look (yaw), at the look's pitch plus lift_deg (held between min and max elevation), with the speed that carries <see cref="RangeM"/> at the lift's elevation from a
    /// standing eye's height (<see cref="MoveConsts.EyeHeight"/>) down to flat ground (a level look throws that far, a look up throws higher and about as far).
    /// </summary>
    public static Vector3 Velocity(DuffelsFile data, DuffelKind kind, float yaw, float pitch, float charge)
    {
        var lift = (float)(data.LiftDeg * Math.PI / 180);
        var range = RangeM(data, kind, charge);
        // x = v cos(a) t and 0 = h + v sin(a) t - g t^2 / 2 give v^2 = g R^2 / (2 cos^2(a) (h + R tan(a)))
        var (ls, lc) = MathF.SinCos(lift);
        var speed = MathF.Sqrt(MoveConsts.Gravity * range * range / (2 * lc * lc * (MoveConsts.EyeHeight + range * ls / lc)));
        var elevation = Math.Clamp(pitch + lift, (float)(data.MinElevationDeg * Math.PI / 180), (float)(data.MaxElevationDeg * Math.PI / 180));
        var (sin, cos) = MathF.SinCos(yaw);
        var (es, ec) = MathF.SinCos(elevation);
        return new Vector3(-sin * ec, es, -cos * ec) * speed;
    }
}

/// <summary>What the throw key's release does.</summary>
public enum DuffelRelease : byte { None, Drop, Throw }

/// <summary>Where one hold of the throw key is: up, or held and charging.</summary>
public enum DuffelKeyState : byte { Idle, Charging }

/// <summary>
/// The Duffel throw key (<c>duffel_throw</c>, X by default): held from a fresh press while carrying, it charges; released before tap_seconds it puts the Duffel down at the feet, later it throws with
/// the charge past the tap. Losing the Duffel or the right to throw (a seat, death) mid-hold ends the hold with nothing. The server counts it from the inputs; the client runs the same for its ring.
/// </summary>
public struct DuffelThrowKey
{
    public static readonly StateMachineSpec<DuffelKeyState> Spec = StateMachineSpec<DuffelKeyState>
        .Define("Duffel.ThrowKey", "The Duffel throw key: up, or held from a fresh press while carrying (charging); the release puts the Duffel down (a tap) or throws it (a hold).", DuffelKeyState.Idle)
        .Allow(DuffelKeyState.Idle, DuffelKeyState.Charging)
        .Allow(DuffelKeyState.Charging, DuffelKeyState.Idle)
        .Note(DuffelKeyState.Idle, "Key up, or held since before the player could throw (a held key must be let go first).")
        .Note(DuffelKeyState.Charging, "Held while carrying: Held counts up. The release is a tap (Drop) under tap_seconds, else a Throw with the charge past it; losing the Duffel ends it with nothing.")
        .Build();

    Edge _key;
    DuffelKeyState _state;

    /// <summary>Seconds the key has been held in this charge (0 when idle).</summary>
    public float Held;

    public readonly DuffelKeyState State => _state;

    /// <summary>The charge (0 to 1) a release now would throw with: the time past the tap over charge_seconds.</summary>
    public readonly float Charge(DuffelsFile data) => _state == DuffelKeyState.Charging ? Math.Clamp((Held - (float)data.TapSeconds) / (float)data.ChargeSeconds, 0f, 1f) : 0f;

    /// <summary>One input tick. <paramref name="canThrow"/>: the player carries a Duffel and is on foot and alive. Returns what the release (if any) does; <paramref name="charge"/> is the throw's.</summary>
    public DuffelRelease Tick(bool down, bool canThrow, float dt, DuffelsFile data, out float charge)
    {
        charge = 0;
        var rose = _key.Pressed(down);
        if (_state == DuffelKeyState.Idle)
        {
            if (rose && canThrow) { Go(DuffelKeyState.Charging); Held = 0; }
            return DuffelRelease.None;
        }
        if (!canThrow) { Go(DuffelKeyState.Idle); Held = 0; return DuffelRelease.None; }
        if (down) { Held += dt; return DuffelRelease.None; }
        charge = Charge(data);
        var tap = Held < data.TapSeconds;
        Go(DuffelKeyState.Idle); Held = 0;
        return tap ? DuffelRelease.Drop : DuffelRelease.Throw;
    }

    public void Reset() { Go(DuffelKeyState.Idle); Held = 0; }

    void Go(DuffelKeyState next)
    {
        if (_state == next) return;
        if (!Spec.Allows(_state, next)) StateMachineDiagnostics.Illegal(Spec.Name, _state.ToString(), next.ToString());
        _state = next;
    }
}

/// <summary>Why the server refused a Duffel action, sent to the player (<see cref="EventKind.DuffelNotice"/>); the client words it.</summary>
public enum DuffelNotice : byte
{
    /// <summary>A carrier tried to take a vehicle seat.</summary>
    NoSeat = 0,
    /// <summary>The player carries one already (one per player).</summary>
    CarryingOne = 1,
}

/// <summary>The Duffel events' payloads: a notice (u8 <see cref="DuffelNotice"/>) and the Duffel in reach (u16 world entity id, 0 none).</summary>
public static class DuffelCodec
{
    public static byte[] EncodeNotice(DuffelNotice notice) => new[] { (byte)notice };

    public static bool TryDecodeNotice(ReadOnlySpan<byte> p, out DuffelNotice notice)
    {
        notice = default;
        if (p.Length < 1 || p[0] > (byte)DuffelNotice.CarryingOne) return false;
        notice = (DuffelNotice)p[0];
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
