using System.Numerics;
using Drycastle.ShooterCore.Churches;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Net;

namespace Drycastle.ShooterCore.Buildings;

/// <summary>
/// The bus garage of the map (parcel P-0666; garage.json next to the map, written by the pipeline's buildings/landmarks/garage.py), Godot-free, in Godot axes: the <see cref="Doorway"/> its
/// double doors hang in, and where its vehicle stands at the start of a round (<see cref="ParkVehicle"/>, nose to the door). The doors are opened and closed by players
/// on foot with the interact key, from either side (<see cref="InReach"/>); they never close on a vehicle that stands in the doorway (<see cref="InDoorway"/>).
/// </summary>
public sealed class Garage
{
    /// <summary>How far from the plane of the doors, on either side, a player on foot can work them.</summary>
    public const float ReachM = 1.2f;
    /// <summary>How far beside the doorway a player can still work them.</summary>
    public const float ReachSideM = 0.5f;

    public Doorway Doorway { get; }
    /// <summary>The id (vehicles.json) of the vehicle that stands in the garage.</summary>
    public string ParkVehicle { get; }
    public Vector3 ParkPos { get; }
    /// <summary>Godot yaw of the parked vehicle (0 faces -Z, positive turns left).</summary>
    public float ParkYaw { get; }

    public Garage(Doorway doorway, string parkVehicle, Vector3 parkPos, float parkYaw)
    {
        Doorway = doorway; ParkVehicle = parkVehicle; ParkPos = parkPos; ParkYaw = parkYaw;
    }

    /// <summary>Whether a player standing at <paramref name="feet"/> can work the doors: in front of or behind the doorway, close to it.</summary>
    public bool InReach(Vector3 feet)
    {
        var d = feet - Doorway.Pos;
        return MathF.Abs(Vector3.Dot(d, Doorway.Out)) <= ReachM && MathF.Abs(Vector3.Dot(d, Doorway.Right)) <= Doorway.Width / 2 + ReachSideM
            && feet.Y > Doorway.Pos.Y - 1f && feet.Y < Doorway.Pos.Y + Doorway.Height;
    }

    /// <summary>
    /// Whether a vehicle (its footprint: centre, Godot yaw, half width and half length) stands in the doorway, so the doors cannot close. Two oriented rectangles on the
    /// ground, tested on the four axes of the pair; the doorway's rectangle is the closed doors' box with a hand of slack.
    /// </summary>
    public bool InDoorway(Vector3 centre, float yaw, float halfWidth, float halfLength)
    {
        var fwd = new Vector2(-MathF.Sin(yaw), -MathF.Cos(yaw));
        var side = new Vector2(-fwd.Y, fwd.X);
        var dOut = new Vector2(Doorway.Out.X, Doorway.Out.Z);
        var dRight = new Vector2(Doorway.Right.X, Doorway.Right.Z);
        var between = new Vector2(centre.X - Doorway.Pos.X, centre.Z - Doorway.Pos.Z);
        float hw = Doorway.Width / 2, hd = Doorway.Depth / 2 + 0.1f;
        foreach (var axis in new[] { fwd, side, dOut, dRight })
        {
            var vehicle = MathF.Abs(Vector2.Dot(fwd, axis)) * halfLength + MathF.Abs(Vector2.Dot(side, axis)) * halfWidth;
            var door = MathF.Abs(Vector2.Dot(dOut, axis)) * hd + MathF.Abs(Vector2.Dot(dRight, axis)) * hw;
            if (MathF.Abs(Vector2.Dot(between, axis)) > vehicle + door) return false;
        }
        return true;
    }

    /// <summary>The garage of a garage.json text; null for an empty text.</summary>
    public static Garage? FromJson(string? json) => string.IsNullOrEmpty(json) ? null : Parse(json);

    /// <summary>Like <see cref="FromJson"/>, but a broken file is reported to <paramref name="onError"/> and counts as no garage.</summary>
    public static Garage? FromJsonOrNull(string? json, Action<string> onError)
    {
        try { return FromJson(json); }
        catch (DataFileException e) { onError(e.Message); return null; }
    }

    public static Garage Parse(string json, string name = "garage.json")
    {
        var f = DataFile.Parse<GarageFile>(json, name);
        if (f.Doorway is not { } d) throw new DataFileException($"{name}: missing doorway");
        if (!(d.Width > 0) || !(d.Height > 0)) throw new DataFileException($"{name}: the doorway needs a width and a height");
        if (f.Parking is not { } p) throw new DataFileException($"{name}: missing parking");
        if (string.IsNullOrEmpty(p.Vehicle)) throw new DataFileException($"{name}: parking needs a vehicle");
        return new Garage(new Doorway(Point(d.Pos, name, "doorway"), Bearing(d.Yaw), d.Width, d.Height), p.Vehicle, Point(p.Pos, name, "parking"), Bearing(p.Yaw));
    }

    static Vector3 Point(float[] p, string file, string what) =>
        p.Length == 3 && p.All(float.IsFinite) ? new Vector3(p[0], p[2], -p[1]) : throw new DataFileException($"{file}: {what} pos is [x, y, z]");

    static float Bearing(float degrees) =>
        float.IsFinite(degrees) ? -degrees * MathF.PI / 180f : throw new DataFileException("garage.json: a yaw is not a number");
}

/// <summary>garage.json as it reads off the disk; <see cref="Garage.Parse"/> checks and converts it. The <c>bay</c> (the room's outline) is not read by the game yet.</summary>
public sealed class GarageFile : IVersioned
{
    public int Version { get; set; }
    public UradDoorwayDef? Doorway { get; set; }
    public GarageParkingDef? Parking { get; set; }
}

public sealed class GarageParkingDef
{
    public string Vehicle { get; set; } = "";
    public float[] Pos { get; set; } = Array.Empty<float>();
    public float Yaw { get; set; }
}

/// <summary>The garage doors: shut or open.</summary>
public enum GarageDoorState : byte { Shut, Open }

/// <summary>
/// The single owner of "are the garage doors open". A player's use of the doors is one <see cref="Use"/>; the caller applies the result (the box, the push out of the
/// doorway, the broadcast). Pure and Godot-free.
/// </summary>
public sealed class GarageDoors
{
    public static readonly StateMachineSpec<GarageDoorState> Spec = StateMachineSpec<GarageDoorState>
        .Define("Garage.Door", "The bus garage's double doors: a player on foot at the doorway opens and closes them with the interact key.", GarageDoorState.Shut)
        .Allow(GarageDoorState.Shut, GarageDoorState.Open)
        .Allow(GarageDoorState.Open, GarageDoorState.Shut)
        .Note(GarageDoorState.Shut, "Closed and solid (the start state, and every new server): the leaves fill the doorway.")
        .Note(GarageDoorState.Open, "The leaves are swung out into the yard and nothing is solid. Stays open until a player closes it; it does not close while a vehicle stands in the doorway.")
        .Build();

    readonly StateMachine<GarageDoorState> _fsm = new(Spec);

    public bool IsOpen => _fsm.Is(GarageDoorState.Open);

    /// <summary>A player works the doors. True when they moved; false when they are open and <paramref name="vehicleInDoorway"/> keeps them from closing.</summary>
    public bool Use(bool vehicleInDoorway)
    {
        if (IsOpen && vehicleInDoorway) return false;
        return _fsm.Enter(IsOpen ? GarageDoorState.Shut : GarageDoorState.Open);
    }
}

/// <summary>The garage door event (<see cref="EventKind.GarageDoor"/>): one byte, 1 open, 0 closed. Same payload as <see cref="ChurchDoorCodec"/>.</summary>
public static class GarageDoorCodec
{
    public static byte[] Encode(bool open) => ChurchDoorCodec.Encode(open);
    public static bool? Decode(byte[] payload) => ChurchDoorCodec.Decode(payload);
}
