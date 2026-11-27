using System.Numerics;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>What a Heist's drop is (GLOSSARY "Receiver"): a stash house with a pallet and a buyer, a helicopter on a landing spot, or a boat at a mooring. The wire carries the number.</summary>
public enum ReceiverKind : byte { StashHouse = 0, Helicopter = 1, Boat = 2 }

/// <summary>Where a Receiver is in its life (machine <c>Receiver.Run</c>).</summary>
public enum ReceiverPhase : byte { Receiving, Leaving, Gone }

/// <summary>What the interact key would do for a party player at a Receiver's Cartel man now (the prompt of event <see cref="EventKind.ReceiverReach"/>).</summary>
public enum ReceiverAsk : byte
{
    /// <summary>Nothing: no Receiver in reach, or it is not the player's Job's.</summary>
    None = 0,
    /// <summary>The leader's press sends it off: at least the minimum is on it.</summary>
    Leave = 1,
    /// <summary>The leader's press would be refused: fewer than the minimum are on it.</summary>
    NeedMore = 2,
    /// <summary>Only the leader sends it off.</summary>
    NotLeader = 3,
}

/// <summary>cartel.json <c>receivers.weights</c>: how likely each kind of Receiver is among the kinds the map has.</summary>
public sealed class ReceiverWeights
{
    public float StashHouse { get; set; } = 1;
    public float Helicopter { get; set; } = 1;
    public float Boat { get; set; } = 1;

    public float Of(ReceiverKind kind) => kind switch { ReceiverKind.StashHouse => StashHouse, ReceiverKind.Helicopter => Helicopter, _ => Boat };
}

/// <summary>Where the Cartel man of a Receiver stands in its frame (forward -Z, origin on the ground or the waterline), and how he looks: a Gang Warfare character drawn as the Contact is (<see cref="ContactLook"/>).</summary>
public sealed class ReceiverFigure
{
    /// <summary>[x, y, z] in the Receiver's frame, metres: his feet (a seated pilot's: the seat).</summary>
    public float[] Pos { get; set; } = { 0, 0, 0 };
    /// <summary>The turn about Y, degrees (0: he faces the Receiver's forward, -Z).</summary>
    public float YawDeg { get; set; }
    public ContactLook Look { get; set; } = new();
}

/// <summary>cartel.json <c>receivers.leave</c>: how a helicopter or a boat leaves once sent off (scripted, no physics: the server moves the entity, every client interpolates it).</summary>
public sealed class ReceiverLeaveData
{
    /// <summary>A helicopter climbs this high (metres) over its landing spot, at climb_mps, then flies off at fly_mps over the nearest map edge, nose down pitch_deg, and is removed exit_margin_m past the edge.</summary>
    public float ClimbM { get; set; } = 60;
    public float ClimbMps { get; set; } = 9;
    public float FlyMps { get; set; } = 35;
    public float ExitMarginM { get; set; } = 150;
    public float PitchDeg { get; set; } = 10;
    /// <summary>How fast it turns to face the way it flies, degrees a second.</summary>
    public float TurnDegPerS { get; set; } = 45;
    /// <summary>A boat sails its route at sail_mps and is removed at the route's last point.</summary>
    public float SailMps { get; set; } = 12;
    /// <summary>The first seconds of the leave ease in (speed rises from nothing).</summary>
    public float RampSeconds { get; set; } = 4;

    internal void Validate()
    {
        foreach (var (v, name) in new[] { (ClimbM, "climb_m"), (ClimbMps, "climb_mps"), (FlyMps, "fly_mps"), (SailMps, "sail_mps"), (TurnDegPerS, "turn_deg_per_s") })
            if (!float.IsFinite(v) || !(v > 0)) throw new DataFileException($"cartel.json: receivers.leave.{name} must be more than 0");
        foreach (var (v, name) in new[] { (ExitMarginM, "exit_margin_m"), (PitchDeg, "pitch_deg"), (RampSeconds, "ramp_seconds") })
            if (!float.IsFinite(v) || v < 0) throw new DataFileException($"cartel.json: receivers.leave.{name} must be 0 or more");
    }
}

/// <summary>
/// One kind of Receiver (cartel.json <c>receivers.stash_house|helicopter|boat</c>): how a client draws it, where its cargo goes and where its Cartel man stands. Everything is in the Receiver's frame:
/// forward -Z, x right, y up, origin on the ground under it (the pallet's base centre, the helicopter's mast, the boat's waterline).
/// </summary>
public sealed class ReceiverKindData
{
    /// <summary>The model: a path under assets-private/synty/glb (no .glb) when <see cref="Atlas"/> is set, else an asset the map library knows (the helicopter's generated glb). Empty: a box.</summary>
    public string Model { get; set; } = "";
    /// <summary>The atlas under synty/ the model wears; empty: the model brings its own materials.</summary>
    public string Atlas { get; set; } = "";
    /// <summary>The turn of the model in the frame, degrees (the Synty pack faces +Z, the frame's forward is -Z: 180).</summary>
    public float ModelYawDeg { get; set; }
    /// <summary>The box drawn without the model: [x, y, z] size (the box stands on the origin), and its colour.</summary>
    public float[] SizeM { get; set; } = { 1, 1, 1 };
    public string Colour { get; set; } = "#8a6a45";
    /// <summary>The cargo: the box the bags go into and the slots they lie on (same schema as vehicles.json cargo_volume; no doors, no access point: nothing is taken out of a Receiver). 15 slots; a run uses as many as the Heist's maximum.</summary>
    public VehicleCargoVolumeDef CargoVolume { get; set; } = new();
    public ReceiverFigure Figure { get; set; } = new();
    /// <summary>The van is "at the drop" within this many ground metres of the Receiver: the Assault clock starts and the party unloads.</summary>
    public float AreaM { get; set; } = 30;

    internal void Validate(string what)
    {
        DataFileException Bad(string text) => new($"cartel.json: receivers.{what}.{text}");
        var v = CargoVolume;
        if (v.Centre is not { Length: 3 } || v.Size is not { Length: 3 } || v.Centre.Any(x => !float.IsFinite(x)) || v.Size.Any(x => !(x > 0) || !float.IsFinite(x))) throw Bad("cargo_volume: centre is [x, y, z] and size [width, height, length], all positive");
        if (v.Capacity != VehicleCargoVolumeDef.MaxCapacity || v.Slots.Count != v.Capacity) throw Bad($"cargo_volume: capacity {VehicleCargoVolumeDef.MaxCapacity} with one slot each (a run uses the first as many as its Heist's maximum)");
        for (var i = 0; i < v.Slots.Count; i++)
        {
            var s = v.Slots[i];
            if (s is not { Length: 4 } || s.Any(x => !float.IsFinite(x))) throw Bad($"cargo_volume: slots[{i}] is [x, y, z, yaw_deg]");
            if (MathF.Abs(s[0] - v.Centre[0]) > v.Size[0] / 2 + 0.002f || MathF.Abs(s[2] - v.Centre[2]) > v.Size[2] / 2 + 0.002f || s[1] < v.Centre[1] - v.Size[1] / 2 - 0.002f || s[1] > v.Centre[1] + v.Size[1] / 2 + 0.002f)
                throw Bad($"cargo_volume: slots[{i}] lies outside the volume");
        }
        if (v.Doors.Count != 0 || v.Access != null) throw Bad("cargo_volume: a Receiver has no doors and no access point (nothing is taken out of it)");
        if (SizeM.Length != 3 || SizeM.Any(x => !(x > 0) || !float.IsFinite(x))) throw Bad("size_m needs 3 numbers, each more than 0");
        if (Figure.Pos.Length != 3 || Figure.Pos.Any(x => !float.IsFinite(x)) || !float.IsFinite(Figure.YawDeg)) throw Bad("figure.pos is [x, y, z]");
        if (Figure.Look.Model == "" || Figure.Look.Atlas == "") throw Bad("figure.look.model and figure.look.atlas are named");
        if (!float.IsFinite(AreaM) || !(AreaM > 0)) throw Bad("area_m must be more than 0");
    }
}

/// <summary>
/// cartel.json <c>receivers</c> (docs/cartel.md "Receivers"): which Receiver a Heist's drop is, how they look and hold bags, and how a helicopter or boat leaves. The defaults are the shipped numbers.
/// </summary>
public sealed class ReceiversData
{
    public ReceiverWeights Weights { get; set; } = new();
    /// <summary>A party player is at a Receiver's Cartel man (the interact key works) within this many ground metres of him and <see cref="ReachUpM"/> up or down.</summary>
    public float ReachM { get; set; } = 3.5f;
    public float ReachUpM { get; set; } = 3f;
    public ReceiverKindData StashHouse { get; set; } = ReceiverDefaults.Stash();
    public ReceiverKindData Helicopter { get; set; } = ReceiverDefaults.Helicopter();
    public ReceiverKindData Boat { get; set; } = ReceiverDefaults.Boat();
    public ReceiverLeaveData Leave { get; set; } = new();

    public ReceiverKindData Of(ReceiverKind kind) => kind switch { ReceiverKind.StashHouse => StashHouse, ReceiverKind.Helicopter => Helicopter, _ => Boat };

    internal ReceiversData Validate()
    {
        if (!(ReachM > 0) || !float.IsFinite(ReachM) || !(ReachUpM > 0) || !float.IsFinite(ReachUpM)) throw new DataFileException("cartel.json: receivers.reach_m and reach_up_m must be more than 0");
        foreach (var k in new[] { ReceiverKind.StashHouse, ReceiverKind.Helicopter, ReceiverKind.Boat })
            if (!float.IsFinite(Weights.Of(k)) || Weights.Of(k) < 0) throw new DataFileException("cartel.json: receivers.weights must be 0 or more");
        if (Weights.StashHouse + Weights.Helicopter + Weights.Boat <= 0) throw new DataFileException("cartel.json: receivers.weights: at least one kind has a weight");
        StashHouse.Validate("stash_house"); Helicopter.Validate("helicopter"); Boat.Validate("boat");
        Leave.Validate();
        return this;
    }
}

/// <summary>The shipped Receivers (cartel.json <c>receivers</c> has the same numbers, with their comments).</summary>
static class ReceiverDefaults
{
    const float BagLength = 0.56f, BagDepth = 0.28f, BagHeight = 0.24f; // duffels.json view.box [0.7, 0.3, 0.35] times view.scale 0.8, lying along x

    static List<float[]> Pile(float baseY, float[] xs, float[] zs, int layers)
    {
        var slots = new List<float[]>();
        for (var l = 0; l < layers; l++)
            foreach (var z in zs)
                foreach (var x in xs)
                    if (slots.Count < VehicleCargoVolumeDef.MaxCapacity) slots.Add(new[] { x, baseY + l * BagHeight, z, 0f });
        return slots;
    }

    static VehicleCargoVolumeDef Volume(float[] centre, float[] size, List<float[]> slots) => new() { Centre = centre, Size = size, Capacity = VehicleCargoVolumeDef.MaxCapacity, Slots = slots };

    static ContactLook Man(string idle) => new() { Model = "gang_boss_01", Atlas = "gang-warfare/textures/PolygonGangWarfare_02_B.png", Idle = idle };

    /// <summary>The Cartel pallet's own model and size (the glb's, as pallet.size_m), two bags across, four rows deep, two layers (8 + 7); the buyer stands beside it.</summary>
    public static ReceiverKindData Stash() => new()
    {
        Model = "gang-warfare/SM_Prop_Pallet_01", Atlas = "gang-warfare/textures/PolygonGangWarfare_01_A.png", SizeM = new[] { 1.441f, 0.235f, 1.418f },
        CargoVolume = Volume(new[] { 0f, 0.735f, 0f }, new[] { 1.44f, 1f, 1.42f }, Pile(0.235f, new[] { -0.3f, 0.3f }, new[] { -0.495f, -0.165f, 0.165f, 0.495f }, 2)),
        Figure = new() { Pos = new[] { 1.25f, 0f, 0f }, YawDeg = 90, Look = Man("idle_standing_masc_g") },
        AreaM = 25,
    };

    /// <summary>The utility helicopter's open cabin: floor 0.732, the left half of the cabin in front of the bench, two bags across, six rows, two layers (12 + 3).</summary>
    public static ReceiverKindData Helicopter() => new()
    {
        Model = "drycastle-generated-models/utility_helicopter.glb", SizeM = new[] { 2.4f, 3.7f, 12.6f }, Colour = "#5c6638",
        CargoVolume = Volume(new[] { 0f, 1.232f, -0.025f }, new[] { 2.3f, 1f, 2.45f }, Pile(0.732f, new[] { -0.82f, -0.24f }, new[] { -1.05f, -0.77f, -0.49f, -0.21f, 0.07f, 0.35f }, 2)),
        Figure = new() { Pos = new[] { 0.55f, 0.55f, -1.9f }, YawDeg = 0, Look = Man("crouch_idle_masc") },
        AreaM = 45,
    };

    /// <summary>The Battle Royale boat's cockpit floor between the bench and the console, three bags across, two rows, three layers (6 + 6 + 3), in the frame the boat sails in (forward -Z: the model turned 180 degrees); the boatman stands at the helm behind the console.</summary>
    public static ReceiverKindData Boat() => new()
    {
        Model = "battle-royale/SM_Veh_Boat_01", Atlas = "battle-royale/polygon-battle-royale-01/Assets/PolygonBattleRoyale/Textures/PolygonBattleRoyale_Vehicles_01.png", ModelYawDeg = 180, SizeM = new[] { 3.45f, 3f, 8.35f }, Colour = "#d8d4c8",
        CargoVolume = Volume(new[] { 0f, 0.7f, 1.075f }, new[] { 1.72f, 1f, 2.25f }, Pile(0.21f, new[] { -0.58f, 0f, 0.58f }, new[] { 0.14f, 0.42f }, 3)),
        Figure = new() { Pos = new[] { 0f, 0.21f, 1.85f }, YawDeg = 0, Look = Man("idle_standing_masc_g") },
        AreaM = 45,
    };
}

/// <summary>The rules of the Receiver that need no world: the wire type and bytes, the words, the choice of kind, what a press at the Cartel man would do.</summary>
public static class ReceiverRules
{
    /// <summary>Wire id of the Receiver world entity type (59; 60 and 61 are the Bigfoot's).</summary>
    public const byte TypeId = 59;

    /// <summary>Set in the owner field of a Loaded Duffel that lies in a Receiver (a vehicle's world id never has it): the low 15 bits are the Receiver's entity id.</summary>
    public const ushort WireBit = 0x8000;

    public static readonly ReceiverKind[] AllKinds = { ReceiverKind.StashHouse, ReceiverKind.Helicopter, ReceiverKind.Boat };

    public static void Register(WorldEntityRegistry registry) => registry.RegisterOnce(new WorldEntityDef(TypeId, "receiver", HasHp: false));

    /// <summary>The state byte: the kind in bits 0-1, the phase in bits 2-3.</summary>
    public static byte Pack(ReceiverKind kind, ReceiverPhase phase) => (byte)((byte)kind | (byte)phase << 2);

    /// <summary>The kind of a state byte; one this build does not know is null (the client draws nothing).</summary>
    public static ReceiverKind? KindOf(byte b) => (b & 3) is var k && k < AllKinds.Length ? (ReceiverKind)k : null;

    public static ReceiverPhase PhaseOf(byte b) => (b >> 2 & 3) is var p && p <= (int)ReceiverPhase.Gone ? (ReceiverPhase)p : ReceiverPhase.Gone;

    /// <summary>The owner field of a Duffel loaded in the Receiver of entity <paramref name="entity"/>.</summary>
    public static ushort OwnerOf(ushort entity) => (ushort)(WireBit | entity);

    /// <summary>The owner field names a Receiver (its entity id out), not a vehicle.</summary>
    public static bool IsReceiver(ushort owner, out ushort entity)
    {
        entity = (ushort)(owner & ~WireBit);
        return (owner & WireBit) != 0;
    }

    /// <summary>The kind's name in cartel.json and in the logs.</summary>
    public static string NameOf(ReceiverKind kind) => kind switch { ReceiverKind.StashHouse => "stash_house", ReceiverKind.Helicopter => "helicopter", _ => "boat" };

    /// <summary>Where the van goes, as the tracker line says it ("the helicopter").</summary>
    public static string Place(ReceiverKind kind) => kind switch { ReceiverKind.StashHouse => "the stash house", ReceiverKind.Helicopter => "the helicopter", _ => "the boat" };

    /// <summary>How the leader finishes it, as the tracker line says it.</summary>
    public static string Leave(ReceiverKind kind) => kind switch { ReceiverKind.StashHouse => "hand over to the buyer", ReceiverKind.Helicopter => "tell the pilot to leave", _ => "tell the boatman to leave" };

    /// <summary>The Cartel man's name in the chat.</summary>
    public static string Man(ReceiverKind kind) => kind switch { ReceiverKind.StashHouse => "buyer", ReceiverKind.Helicopter => "pilot", _ => "boatman" };

    public static DropText Text(ReceiverKind kind) => new(Place(kind), Leave(kind));

    /// <summary>
    /// The kinds to try for a Heist's drop, in order: a random order weighted by <paramref name="weights"/> among the kinds in <paramref name="has"/> (the kinds this map has) whose weight is above 0. The
    /// server tries each until one has a free place, so a kind the map has but cannot give now falls through to the next.
    /// </summary>
    public static List<ReceiverKind> Order(ReceiverWeights weights, IReadOnlyCollection<ReceiverKind> has, Random rng)
    {
        var pool = has.Where(k => weights.Of(k) > 0).ToList();
        var order = new List<ReceiverKind>();
        while (pool.Count > 0)
        {
            var total = pool.Sum(k => (double)weights.Of(k));
            var x = rng.NextDouble() * total;
            var pick = pool[^1];
            foreach (var k in pool) { if ((x -= weights.Of(k)) < 0) { pick = k; break; } }
            order.Add(pick); pool.Remove(pick);
        }
        return order;
    }

    /// <summary>What the interact key does for a party player at the Cartel man: the leader's press sends it off at the minimum or more, is refused under it, and a mate's press does nothing.</summary>
    public static ReceiverAsk Ask(bool leader, int onBoard, int min) => !leader ? ReceiverAsk.NotLeader : onBoard >= min ? ReceiverAsk.Leave : ReceiverAsk.NeedMore;
}

/// <summary>The Receiver event's payload (docs/cartel.md "Receivers"): what the player's key would do at the Cartel man now, for the prompt.</summary>
public static class ReceiverCodec
{
    /// <summary>u16 Receiver entity id (0: none in reach), u8 kind, u8 <see cref="ReceiverAsk"/>, u8 on board, u8 minimum.</summary>
    public static byte[] EncodeReach(ushort id, ReceiverKind kind, ReceiverAsk ask, int onBoard, int min) =>
        new[] { (byte)id, (byte)(id >> 8), (byte)kind, (byte)ask, (byte)Math.Clamp(onBoard, 0, 255), (byte)Math.Clamp(min, 0, 255) };

    public static bool TryDecodeReach(ReadOnlySpan<byte> p, out ushort id, out ReceiverKind kind, out ReceiverAsk ask, out int onBoard, out int min)
    {
        id = 0; kind = default; ask = default; onBoard = 0; min = 0;
        if (p.Length < 6 || p[2] >= ReceiverRules.AllKinds.Length || p[3] > (byte)ReceiverAsk.NotLeader) return false;
        id = (ushort)(p[0] | p[1] << 8); kind = (ReceiverKind)p[2]; ask = (ReceiverAsk)p[3]; onBoard = p[4]; min = p[5];
        return true;
    }
}

/// <summary>
/// The way a helicopter or boat leaves (docs/cartel.md "Receivers"), pure: a polyline with a speed on each leg. A helicopter's is the climb and the flight to the map edge; a boat's the placed route down
/// the river. Moved along by distance, so the server's tick and the tests step it the same way.
/// </summary>
public sealed class ReceiverPath
{
    readonly Vector3[] _pts;
    readonly float[] _speed;
    readonly float[] _end; // distance at the end of each leg

    public ReceiverPath(IReadOnlyList<Vector3> points, IReadOnlyList<float> speeds)
    {
        if (points.Count < 2 || speeds.Count != points.Count - 1) throw new ArgumentException("a path has at least two points and a speed for each leg");
        _pts = points.ToArray(); _speed = speeds.ToArray(); _end = new float[_speed.Length];
        var s = 0f;
        for (var i = 0; i < _end.Length; i++) { s += Vector3.Distance(_pts[i], _pts[i + 1]); _end[i] = s; }
        Length = s;
    }

    public float Length { get; }
    public int Legs => _speed.Length;

    /// <summary>A helicopter's: up <see cref="ReceiverLeaveData.ClimbM"/> from <paramref name="start"/>, then straight to <paramref name="exit"/> (at the climb's height).</summary>
    public static ReceiverPath Helicopter(Vector3 start, Vector2 exit, ReceiverLeaveData d)
    {
        var top = start + new Vector3(0, d.ClimbM, 0);
        return new ReceiverPath(new[] { start, top, new Vector3(exit.X, top.Y, exit.Y) }, new[] { d.ClimbMps, d.FlyMps });
    }

    /// <summary>A boat's: its route, every leg at sail_mps.</summary>
    public static ReceiverPath Boat(IReadOnlyList<Vector3> route, ReceiverLeaveData d) => new(route, Enumerable.Repeat(d.SailMps, route.Count - 1).ToArray());

    /// <summary>The leg the distance <paramref name="s"/> is on (the last one when it is past the end).</summary>
    public int LegAt(float s)
    {
        for (var i = 0; i < _end.Length; i++) if (s < _end[i]) return i;
        return _end.Length - 1;
    }

    public float SpeedAt(float s) => _speed[LegAt(s)];

    public bool Done(float s) => s >= Length;

    public Vector3 PositionAt(float s)
    {
        if (s >= Length) return _pts[^1];
        var i = LegAt(Math.Max(0, s));
        var from = i == 0 ? 0f : _end[i - 1];
        var len = _end[i] - from;
        return len <= 1e-6f ? _pts[i + 1] : Vector3.Lerp(_pts[i], _pts[i + 1], Math.Clamp((s - from) / len, 0f, 1f));
    }

    /// <summary>
    /// The way it should face at distance <paramref name="s"/>: the ground direction (x, z) of the leg, or, on a leg that goes straight up, of the next leg that moves over the ground (a helicopter turns while it
    /// climbs). Null when no leg moves over the ground.
    /// </summary>
    public Vector2? HeadingAt(float s)
    {
        for (var i = LegAt(Math.Max(0, s)); i < _speed.Length; i++)
        {
            var d = new Vector2(_pts[i + 1].X - _pts[i].X, _pts[i + 1].Z - _pts[i].Z);
            if (d.LengthSquared() > 1e-4f) return Vector2.Normalize(d);
        }
        return null;
    }
}

/// <summary>
/// A Receiver (GLOSSARY "Receiver"; docs/cartel.md "Receivers"): where a Heist's Duffels are handed over. A server-side world entity of type <see cref="ReceiverRules.TypeId"/> and a cargo volume
/// (<see cref="ICargoVolume"/>) that takes only its Job's party's Duffels, never gives one back, and is safe (nothing can hurt it). Machine <see cref="Spec"/>: it receives, then leaves when it is sent off
/// (<see cref="Send"/>: a helicopter climbs and flies off, a boat sails its route; a stash house is simply taken at once) and is gone. The leaving is scripted here, no physics: the entity's position and
/// turn move each tick and every client interpolates them, the Duffels lying on it ride with it (they are drawn from its pose and their slot).
/// <para>
/// Layout of the entity state: state byte = <see cref="ReceiverRules.Pack"/> (kind, phase), owner field 0, position = its origin (the pallet's base centre, the helicopter's mast on the ground, the boat's waterline), yaw and pitch
/// = how it faces, no hp byte. Its frame has the forward at -Z (the cargo volume, slots and Cartel man of <see cref="ReceiverKindData"/> are in it).
/// </para>
/// </summary>
public sealed class Receiver : IWorldEntity, ICargoVolume
{
    public static readonly StateMachineSpec<ReceiverPhase> Spec = StateMachineSpec<ReceiverPhase>
        .Define("Receiver.Run", "A Heist's drop: it receives the party's Duffels, is sent off (or takes them at once, a stash house's buyer) and is gone.", ReceiverPhase.Receiving)
        .Allow(ReceiverPhase.Receiving, ReceiverPhase.Leaving, ReceiverPhase.Gone)
        .Allow(ReceiverPhase.Leaving, ReceiverPhase.Gone)
        .Note(ReceiverPhase.Receiving, "Stands where the plan put it with its Cartel man. Takes the party's Duffels thrown or put into its cargo volume, none of anyone else's, and never gives one out. Sent off by itself once every Duffel of the Job is on board, or by the leader's press at its Cartel man, with at least the minimum on board.")
        .Note(ReceiverPhase.Leaving, "A helicopter climbs and flies off over the nearest map edge; a boat sails its route to the map's edge (the entity's pose moves each tick, no physics). Takes no more Duffels. Gone at the end of its way; a stash house's buyer takes the bags at once and is Gone on the next tick.")
        .Note(ReceiverPhase.Gone, "Removed from the world with the Duffels still on it. Never left.")
        .Build();

    readonly StateMachine<ReceiverPhase> _fsm = new(Spec);
    readonly VehicleCargo _bay;
    readonly ReceiversData _data;
    readonly ReceiverPath? _path;
    float _s;
    float _yaw, _pitch;
    Vector3 _position;

    /// <param name="at">The origin on the ground (or the water surface).</param>
    /// <param name="yaw">How it faces (a node's Y turn: forward -Z along it).</param>
    /// <param name="party">The Job's party: only its Duffels load.</param>
    /// <param name="min">The fewest Duffels the Job's drop takes (the leader cannot send it off under it).</param>
    /// <param name="capacity">How many Duffels it takes (the Heist's maximum, at most 15).</param>
    /// <param name="path">How it leaves: null for a stash house.</param>
    /// <param name="marginM">The cargo volume takes a Duffel this far outside it too (duffels.json <c>cargo_margin_m</c>).</param>
    public Receiver(ReceiverKind kind, ReceiversData data, Vector3 at, float yaw, IReadOnlySet<ushort> party, int min, int capacity, ReceiverPath? path, float marginM)
    {
        Kind = kind; _data = data; _position = at; _yaw = yaw; Party = party; Min = min; _path = path;
        var def = data.Of(kind).CargoVolume;
        capacity = Math.Clamp(capacity, 1, def.Slots.Count);
        var run = new VehicleCargoVolumeDef { Centre = def.Centre, Size = def.Size, Capacity = capacity, FromTop = false, Slots = def.Slots.Take(capacity).ToList() };
        _bay = new VehicleCargo(0, run, () => new VehiclePose(_position, _yaw, _pitch, 0, 0), marginM);
        _bay.Restrict(party);
        _bay.DuffelLoaded += d => DuffelLoaded?.Invoke(d);
        Capacity = capacity;
    }

    /// <summary>The world entity id (0 until it is spawned).</summary>
    public ushort Id { get; set; }
    public ReceiverKind Kind { get; }
    public ReceiverPhase Phase => _fsm.Current;
    public IReadOnlySet<ushort> Party { get; }
    public int Min { get; }
    public int Capacity { get; }
    /// <summary>Duffels on it now (its own count: the bay's).</summary>
    public int Loaded => _bay.Loaded;
    /// <summary>How many were on it when it was sent off (frozen; what the Heist's Respect counts). 0 before.</summary>
    public int SentWith { get; private set; }
    public Vector3 Position => _position;
    public float Yaw => _yaw;
    public float Pitch => _pitch;
    public ReceiverKindData Data => _data.Of(Kind);
    /// <summary>The Duffel on a slot, or null.</summary>
    public Duffel? In(int slot) => _bay.In(slot);
    /// <summary>The Receiver was sent off (it leaves, or has gone).</summary>
    public bool Sent => !_fsm.Is(ReceiverPhase.Receiving);
    /// <summary>It is on its way out or gone: nothing more is loaded.</summary>
    public bool Leaving => _fsm.Is(ReceiverPhase.Leaving);

    /// <summary>A Duffel took a slot (the Heist's Job tells the party its milestones).</summary>
    public event Action<Duffel>? DuffelLoaded;
    /// <summary>It is gone (its way ended): the server takes what lay on it out of the world and it despawns.</summary>
    public event Action<Receiver>? Gone;

    // ---- the cargo volume ----

    /// <summary>The wire's owner field of a Duffel loaded here (<see cref="ReceiverRules.OwnerOf"/>).</summary>
    public ushort Vehicle => ReceiverRules.OwnerOf(Id);
    public bool Meets(Vector3 from, Vector3 to, out CargoMeet meet) => _bay.Meets(from, to, out meet);
    public bool Contains(Vector3 point) => _bay.Contains(point);
    public bool Accepts(Duffel duffel, bool fromTop) => _fsm.Is(ReceiverPhase.Receiving) && _bay.Accepts(duffel, fromTop);
    public int Load(Duffel duffel) => _bay.Load(duffel);
    public void Unload(Duffel duffel) => _bay.Unload(duffel);
    /// <summary>Nothing is taken out of a Receiver.</summary>
    public float? TakeOutDistance(Vector3 feet) => null;
    /// <summary>The world point of a slot's base centre now, or null.</summary>
    public Vector3? SlotAt(int slot) => _bay.SlotAt(slot);

    // ---- the Cartel man ----

    /// <summary>A point of the Receiver's frame in the world now.</summary>
    public Vector3 ToWorld(Vector3 local) => _position + FlightMath.Rotate(local, _yaw, _pitch, 0);

    /// <summary>Where the Cartel man stands now (his feet).</summary>
    public Vector3 ManAt => ToWorld(new Vector3(Data.Figure.Pos[0], Data.Figure.Pos[1], Data.Figure.Pos[2]));

    /// <summary>A player standing at <paramref name="feet"/> is at the Cartel man: within the reach on the ground and up or down, while it receives.</summary>
    public bool InReach(Vector3 feet)
    {
        if (!_fsm.Is(ReceiverPhase.Receiving)) return false;
        var man = ManAt;
        var dx = feet.X - man.X; var dz = feet.Z - man.Z;
        return dx * dx + dz * dz <= _data.ReachM * _data.ReachM && MathF.Abs(feet.Y - man.Y) <= _data.ReachUpM;
    }

    /// <summary>It may be sent off: it receives and at least the minimum is on it.</summary>
    public bool CanSend => _fsm.Is(ReceiverPhase.Receiving) && Loaded >= Min;

    /// <summary>Sends it off (the count on it now is frozen). False when it cannot be (under the minimum, not receiving).</summary>
    public bool Send()
    {
        if (!CanSend) return false;
        SentWith = Loaded;
        _fsm.Enter(ReceiverPhase.Leaving);
        return true;
    }

    // ---- the world entity ----

    public byte TypeId => ReceiverRules.TypeId;
    public ushort Owner => 0;
    public WorldDespawnReason? Ended => _fsm.Is(ReceiverPhase.Gone) ? WorldDespawnReason.Removed : null;
    public void OnDamage(float amount, ushort attacker) { }

    public void Tick(float dt)
    {
        if (dt <= 0 || !_fsm.Is(ReceiverPhase.Leaving)) return;
        _fsm.Tick(dt);
        if (_path == null) { Finish(); return; }
        var leave = _data.Leave;
        var ramp = leave.RampSeconds <= 0 ? 1f : Math.Min(1f, _fsm.Elapsed / leave.RampSeconds);
        _s += _path.SpeedAt(_s) * ramp * dt;
        _position = _path.PositionAt(_s);
        if (_path.HeadingAt(_s) is { } h) _yaw = TurnToward(_yaw, MathF.Atan2(-h.X, -h.Y), leave.TurnDegPerS * MathF.PI / 180f * dt);
        var pitch = Kind == ReceiverKind.Helicopter && _path.LegAt(_s) >= 1 ? -leave.PitchDeg * MathF.PI / 180f : 0f;
        _pitch = Ramp.MoveToward(_pitch, pitch, 0.35f * dt);
        if (_path.Done(_s)) Finish();
    }

    /// <summary>The yaw turned toward the target by at most <paramref name="maxStep"/> radians, the short way round.</summary>
    static float TurnToward(float yaw, float target, float maxStep)
    {
        var d = MathF.IEEERemainder(target - yaw, MathF.Tau);
        return MathF.Abs(d) <= maxStep ? target : yaw + MathF.Sign(d) * maxStep;
    }

    void Finish()
    {
        _fsm.Enter(ReceiverPhase.Gone);
        Gone?.Invoke(this);
    }

    public WorldState Serialize() => WorldState.Make(_position, _yaw, _pitch, 0, ReceiverRules.Pack(Kind, _fsm.Current), 0);
}
