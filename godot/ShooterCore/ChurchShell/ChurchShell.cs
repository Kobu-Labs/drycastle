using System.Numerics;
using System.Text.Json;

namespace Drycastle.ShooterCore;

/// <summary>
/// The shell of the church (church.json: <c>doorway</c> and <c>rooms</c>), Godot-free, in Godot axes (x east, y up, z = -north; a yaw is a
/// Godot yaw, 0 faces -Z, positive turns left). Gives the doorway the doors fill, the volumes that make the church a shelter
/// (<see cref="Rooms"/>, loaded into <see cref="Shelters"/> like the bunker rooms) and the rule that keeps a closing door from
/// shutting through a body (<see cref="PushOutOfDoorway"/>). A map without a <c>doorway</c> has no church to close (<see cref="FromJson"/> gives null).
/// </summary>
public sealed class ChurchShell
{
    /// <summary>The <see cref="BunkerRoom.BunkerId"/> of the church rooms: nave and chancel share it (and level 0), so they are one shelter.</summary>
    public const string RoomId = "church";

    /// <summary>How deep the closed doors' collision box is (the doorway runs through a 0.4 m wall).</summary>
    public const float DoorDepth = 0.3f;

    /// <summary>The middle of the opening on the floor.</summary>
    public Vector3 DoorwayPos { get; }
    /// <summary>Godot yaw of the outward direction (a node with this yaw has its -Z pointing out of the church).</summary>
    public float DoorwayYaw { get; }
    public float Width { get; }
    public float Height { get; }
    public IReadOnlyList<BunkerRoom> Rooms { get; }

    public ChurchShell(Vector3 doorwayPos, float doorwayYaw, float width, float height, IReadOnlyList<BunkerRoom> rooms)
    {
        DoorwayPos = doorwayPos; DoorwayYaw = doorwayYaw; Width = width; Height = height; Rooms = rooms;
    }

    /// <summary>Unit vector out of the church through the doorway.</summary>
    public Vector3 Out => new(-MathF.Sin(DoorwayYaw), 0, -MathF.Cos(DoorwayYaw));
    /// <summary>Unit vector across the doorway, to the right of someone standing outside and facing in (the local +X of a node with <see cref="DoorwayYaw"/>).</summary>
    public Vector3 Right => new(MathF.Cos(DoorwayYaw), 0, -MathF.Sin(DoorwayYaw));

    /// <summary>Is <paramref name="p"/> inside the nave or the chancel?</summary>
    public bool InRooms(Vector3 p)
    {
        foreach (var r in Rooms) if (r.Contains(p)) return true;
        return false;
    }

    /// <summary>
    /// Where a body standing at <paramref name="feet"/> (a capsule of <paramref name="radius"/>) must be moved so the closing doors do not shut
    /// through it, or null when it is clear of them. It moves along the doorway's depth only, to the nearer side (outside on a tie).
    /// </summary>
    public Vector3? PushOutOfDoorway(Vector3 feet, float radius, float height = 1.8f)
    {
        var d = feet - DoorwayPos;
        var across = Vector3.Dot(d, Right);
        var depth = Vector3.Dot(d, Out);
        var reach = DoorDepth / 2 + radius;
        if (MathF.Abs(across) >= Width / 2 + radius || MathF.Abs(depth) >= reach || feet.Y + height <= DoorwayPos.Y || feet.Y >= DoorwayPos.Y + Height) return null;
        var target = (depth >= 0 ? 1f : -1f) * (reach + 0.05f);
        return feet + Out * (target - depth);
    }

    /// <summary>The church of a church.json text; null for an empty text or a map whose church.json has no <c>doorway</c> (older maps).</summary>
    public static ChurchShell? FromJson(string? json) => string.IsNullOrEmpty(json) ? null : Parse(json);

    /// <summary>Like <see cref="FromJson"/>, but a broken file is reported to <paramref name="onError"/> and counts as no church.</summary>
    public static ChurchShell? FromJsonOrNull(string? json, Action<string> onError)
    {
        try { return FromJson(json); }
        catch (DataFileException e) { onError(e.Message); return null; }
    }

    /// <summary>
    /// <c>{"doorway": {"pos": [x, y, z], "yaw": compass degrees outward, "width", "height"}, "rooms": [{"polygon": [[x, y]...], "z0", "z1"}]}</c>,
    /// points in local map metres (x east, y north, z up). Null when there is no doorway.
    /// </summary>
    public static ChurchShell? Parse(string json, string name = "church.json")
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException e) { throw new DataFileException($"{name}: not JSON: {e.Message}", e); }
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("doorway", out var dw) || dw.ValueKind != JsonValueKind.Object) return null;
            try
            {
                var pos = dw.GetProperty("pos");
                var at = new Vector3(pos[0].GetSingle(), pos[2].GetSingle(), -pos[1].GetSingle());
                var yaw = -dw.GetProperty("yaw").GetSingle() * MathF.PI / 180f;
                float w = dw.GetProperty("width").GetSingle(), h = dw.GetProperty("height").GetSingle();
                if (w <= 0 || h <= 0) throw new DataFileException($"{name}: the doorway has no width or height");
                var rooms = new List<BunkerRoom>();
                if (root.TryGetProperty("rooms", out var rs) && rs.ValueKind == JsonValueKind.Array)
                    foreach (var r in rs.EnumerateArray())
                    {
                        var poly = r.GetProperty("polygon");
                        if (poly.GetArrayLength() < 3) throw new DataFileException($"{name}: a room has no polygon of at least 3 points");
                        var ring = poly.EnumerateArray().Select(c => new Vector2(c[0].GetSingle(), -c[1].GetSingle())).ToArray();
                        float y0 = r.GetProperty("z0").GetSingle(), y1 = r.GetProperty("z1").GetSingle();
                        if (y1 <= y0) throw new DataFileException($"{name}: a room has z1 <= z0");
                        rooms.Add(new BunkerRoom
                        {
                            BunkerId = RoomId, Level = 0, Ring = ring, Y0 = y0, Y1 = y1,
                            MinX = ring.Min(p => p.X), MaxX = ring.Max(p => p.X), MinZ = ring.Min(p => p.Y), MaxZ = ring.Max(p => p.Y),
                        });
                    }
                return new ChurchShell(at, yaw, w, h, rooms);
            }
            catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException)
            {
                throw new DataFileException($"{name}: the doorway or a room is malformed ({e.Message})", e);
            }
        }
    }
}

/// <summary>The church door event (<see cref="EventKind.ChurchDoor"/>): one byte, 1 open, 0 closed.</summary>
public static class ChurchDoorCodec
{
    public static byte[] Encode(bool open) => new[] { open ? (byte)1 : (byte)0 };
    public static bool? Decode(byte[] payload) => payload.Length == 1 && payload[0] <= 1 ? payload[0] == 1 : null;
}
