using System.Numerics;
using System.Text.Json;

namespace Drycastle.ShooterCore;

/// <summary>One seat of the church: where the pelvis rests on the pew, the way the sitter faces, the scripted route to it from the door, and the floor (0 ground, 1 gallery). A standing spot (<c>Stand</c>) has no pew: its point is on the floor and the citizen stands or kneels there.</summary>
public sealed record ChurchSeat(int Id, Vector3 Pos, float Yaw, IReadOnlyList<Vector3> Path, int Floor, bool Stand = false);

/// <summary>
/// The church the citizens walk to (church.json, exported by the map pipeline next to bunkers.json). Godot-free; every point is in Godot axes
/// (x east, y up, z = -north) and every yaw is a Godot yaw (0 faces -Z, positive turns left). In the file points are local map metres like the
/// other map JSON: <c>[x east, y north, z up]</c>, and a yaw is a compass bearing in degrees (0 north, 90 east). The route of a seat runs from the
/// door to the seat along floor points (aisle, then the pew's row, or the stairs to the gallery); the citizen walks it point to point, not on the
/// navigation mesh. A seat without a path takes the aisle.
/// </summary>
public sealed class Church
{
    public Vector3 DoorPos { get; }
    public float DoorYaw { get; }
    /// <summary>The route from the door along the aisle, used by seats that give no path of their own.</summary>
    public IReadOnlyList<Vector3> Aisle { get; }
    public IReadOnlyList<ChurchSeat> Seats { get; }

    readonly Dictionary<int, ChurchSeat> _byId = new();

    public Church(Vector3 doorPos, float doorYaw, IReadOnlyList<Vector3> aisle, IReadOnlyList<ChurchSeat> seats)
    {
        DoorPos = doorPos; DoorYaw = doorYaw; Aisle = aisle; Seats = seats;
        foreach (var s in seats) if (!_byId.TryAdd(s.Id, s)) throw new DataFileException($"church.json: seat id {s.Id} appears twice");
    }

    public ChurchSeat? Seat(int id) => _byId.GetValueOrDefault(id);

    /// <summary>The walk from the door to the seat's feet: the door, the seat's path (the aisle when it has none), then the floor below the seat.</summary>
    public List<Vector3> RouteTo(ChurchSeat seat, float seatHeight)
    {
        var route = new List<Vector3> { DoorPos };
        route.AddRange(seat.Path.Count > 0 ? seat.Path : Aisle);
        route.Add(FeetOf(seat, seatHeight));
        return route;
    }

    /// <summary>The floor under the seat, where a sitter's feet stand (the seat point is on the seat).</summary>
    public static Vector3 FeetOf(ChurchSeat seat, float seatHeight) => seat.Stand ? seat.Pos : seat.Pos - new Vector3(0, seatHeight, 0);

    /// <summary>Parse church.json: <c>{"door": {"x","y","z","yaw"}, "aisle": [[x,y,z]...], "seats": [{"id","pos":[x,y,z],"yaw","path":[[x,y,z]...],"floor","stand"}]}</c> (aisle, path, floor and stand optional).</summary>
    public static Church Parse(string json, string name = "church.json")
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException e) { throw new DataFileException($"{name}: not JSON: {e.Message}", e); }
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("door", out var door) || door.ValueKind != JsonValueKind.Object) throw new DataFileException($"{name}: missing door");
            var doorPos = Local(door.GetProperty("x").GetSingle(), door.GetProperty("y").GetSingle(), door.GetProperty("z").GetSingle());
            var doorYaw = Bearing(door.TryGetProperty("yaw", out var dy) ? dy.GetSingle() : 0);
            var aisle = root.TryGetProperty("aisle", out var a) ? Points(a, name, "aisle") : new List<Vector3>();
            if (!root.TryGetProperty("seats", out var seats) || seats.ValueKind != JsonValueKind.Array || seats.GetArrayLength() == 0)
                throw new DataFileException($"{name}: no seats array, or it is empty");
            var list = new List<ChurchSeat>();
            foreach (var s in seats.EnumerateArray())
            {
                if (!s.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) throw new DataFileException($"{name}: a seat has no id");
                var id = idEl.GetInt32();
                if (!s.TryGetProperty("pos", out var pos)) throw new DataFileException($"{name}: seat {id} has no pos");
                var floor = s.TryGetProperty("floor", out var fl) ? fl.GetInt32() : 0;
                if (floor is < 0 or > 1) throw new DataFileException($"{name}: seat {id}: floor is 0 (ground) or 1 (gallery)");
                list.Add(new ChurchSeat(id, Point(pos, name, $"seat {id} pos"), Bearing(s.TryGetProperty("yaw", out var y) ? y.GetSingle() : 0),
                    s.TryGetProperty("path", out var p) ? Points(p, name, $"seat {id} path") : new List<Vector3>(), floor,
                    s.TryGetProperty("stand", out var st) && st.ValueKind == JsonValueKind.True));
            }
            return new Church(doorPos, doorYaw, aisle, list);
        }
    }

    public static Church Load(string path) => Parse(File.ReadAllText(path), Path.GetFileName(path));

    /// <summary>A local map point (x east, y north, z up) in Godot axes.</summary>
    static Vector3 Local(float x, float y, float z) => new(x, z, -y);

    /// <summary>A compass bearing in degrees (0 north, 90 east, clockwise) as a Godot yaw (0 faces -Z, positive turns left).</summary>
    static float Bearing(float degrees) => -degrees * MathF.PI / 180f;

    static Vector3 Point(JsonElement e, string file, string what)
    {
        if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != 3) throw new DataFileException($"{file}: {what} is [x, y, z]");
        return Local(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
    }

    static List<Vector3> Points(JsonElement e, string file, string what)
    {
        if (e.ValueKind != JsonValueKind.Array) throw new DataFileException($"{file}: {what} is a list of [x, y, z]");
        return e.EnumerateArray().Select(p => Point(p, file, what)).ToList();
    }
}
