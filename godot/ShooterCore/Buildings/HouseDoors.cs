using System.Numerics;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Buildings;

/// <summary>How a house's door moves: one leaf that swings in on a hinge, or a garage door that tips up under the ceiling.</summary>
public enum HouseDoorKind : byte { Hinged, Garage }

/// <summary>One door of a hollow house (interiors.json), in Godot axes.</summary>
public sealed record HouseDoor(Doorway Doorway, HouseDoorKind Kind, string Colour);

/// <summary>A house's door: shut or open.</summary>
public enum HouseDoorState : byte { Shut, Open }

/// <summary>
/// The doors of the map's hollow houses (the <c>doors</c> of each house in interiors.json, written by the pipeline's buildings/interior.py) and the single owner of which are
/// open. A door is named by its place in the list (houses in file order, their doors in theirs), the same on the server and every client of one map. Pure and Godot-free; the
/// caller applies a change (the box, the push out of the doorway, the broadcast).
/// </summary>
public sealed class HouseDoors
{
    /// <summary>How far from the plane of a door, on either side, a player on foot can work it.</summary>
    public const float ReachM = 1.2f;
    /// <summary>How far beside the doorway a player can still work it.</summary>
    public const float ReachSideM = 0.3f;

    public static readonly StateMachineSpec<HouseDoorState> Spec = StateMachineSpec<HouseDoorState>
        .Define("House.Door", "One door or garage door of a hollow house: a player on foot at the doorway opens and closes it with the interact key.", HouseDoorState.Shut)
        .Allow(HouseDoorState.Shut, HouseDoorState.Open)
        .Allow(HouseDoorState.Open, HouseDoorState.Shut)
        .Note(HouseDoorState.Shut, "Closed and solid (the start state, and every new server): the leaf fills the doorway.")
        .Note(HouseDoorState.Open, "The leaf is swung into the house, or tipped up under the garage's ceiling, and nothing is solid. Stays open until a player closes it; it does not close while a vehicle stands in the doorway.")
        .Build();

    readonly StateMachine<HouseDoorState>[] _doors;

    public IReadOnlyList<HouseDoor> All { get; }

    public HouseDoors(IReadOnlyList<HouseDoor> doors)
    {
        if (doors.Count > ushort.MaxValue) throw new DataFileException($"interiors.json: {doors.Count} doors, more than the wire can name");
        All = doors;
        _doors = doors.Select(_ => new StateMachine<HouseDoorState>(Spec)).ToArray();
    }

    public bool IsOpen(int door) => _doors[door].Is(HouseDoorState.Open);

    /// <summary>The door a player at <paramref name="feet"/> would work (the nearest in reach), or -1.</summary>
    public int Nearest(Vector3 feet)
    {
        var best = -1;
        var bestSq = float.MaxValue;
        for (var i = 0; i < All.Count; i++)
        {
            var way = All[i].Doorway;
            var sq = Vector3.DistanceSquared(way.Pos, feet);
            if (sq < bestSq && way.InReach(feet, ReachM, ReachSideM)) { best = i; bestSq = sq; }
        }
        return best;
    }

    /// <summary>A player works a door. True when it moved; false when it is open and <paramref name="vehicleInDoorway"/> keeps it from closing.</summary>
    public bool Use(int door, bool vehicleInDoorway)
    {
        var open = IsOpen(door);
        if (open && vehicleInDoorway) return false;
        return _doors[door].Enter(open ? HouseDoorState.Shut : HouseDoorState.Open);
    }

    /// <summary>The doors of an interiors.json text; null for an empty text (a map with no hollow house).</summary>
    public static HouseDoors? FromJson(string? json) => string.IsNullOrEmpty(json) ? null : Parse(json);

    /// <summary>Like <see cref="FromJson"/>, but a broken file is reported to <paramref name="onError"/> and counts as no doors.</summary>
    public static HouseDoors? FromJsonOrNull(string? json, Action<string> onError)
    {
        try { return FromJson(json); }
        catch (DataFileException e) { onError(e.Message); return null; }
    }

    public static HouseDoors Parse(string json, string name = "interiors.json")
    {
        var f = DataFile.Parse<InteriorsFile>(json, name);
        var doors = new List<HouseDoor>();
        foreach (var house in f.Houses)
            foreach (var d in house.Doors)
            {
                var what = $"{name}: door {d.Id} of {house.Id}";
                if (!(d.Width > 0) || !(d.Height > 0)) throw new DataFileException($"{what} needs a width and a height");
                if (d.Pos.Length != 3 || !d.Pos.All(float.IsFinite) || !float.IsFinite(d.Yaw)) throw new DataFileException($"{what} needs a pos [x, y, z] and a yaw");
                var way = new Doorway(new Vector3(d.Pos[0], d.Pos[2], -d.Pos[1]), -d.Yaw * MathF.PI / 180f, d.Width, d.Height);
                doors.Add(new HouseDoor(way, d.Kind, d.Colour));
            }
        return new HouseDoors(doors);
    }
}

/// <summary>interiors.json as far as the game reads it: the houses' doors. The rooms and partitions are not read yet.</summary>
public sealed class InteriorsFile : IVersioned
{
    public int Version { get; set; }
    public List<InteriorHouseDef> Houses { get; set; } = new();
}

public sealed class InteriorHouseDef
{
    public string Id { get; set; } = "";
    public List<InteriorDoorDef> Doors { get; set; } = new();
}

public sealed class InteriorDoorDef
{
    public string Id { get; set; } = "";
    public HouseDoorKind Kind { get; set; }
    public float[] Pos { get; set; } = Array.Empty<float>();
    public float Yaw { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public string Colour { get; set; } = "#5a4636";
}

/// <summary>The house door event (<see cref="Net.EventKind.HouseDoor"/>): the door's index (u16, little endian) and 1 open, 0 closed.</summary>
public static class HouseDoorCodec
{
    public static byte[] Encode(int door, bool open) => new[] { (byte)door, (byte)(door >> 8), open ? (byte)1 : (byte)0 };

    public static (int Door, bool Open)? Decode(byte[] payload) =>
        payload.Length == 3 && payload[2] <= 1 ? (payload[0] | payload[1] << 8, payload[2] == 1) : null;
}
