using System.Numerics;
using System.Text.Json;

namespace Drycastle.ShooterCore;

/// <summary>
/// One room of a bunker: a ring in Godot ground axes (x east, y = -north) between two heights. The pipeline grows it a
/// little into the walls, the floor and the roof slab, so a blast on a wall's inner face is in the room and one on its
/// outer face is not.
/// </summary>
public sealed class BunkerRoom
{
    public required string BunkerId { get; init; }
    /// <summary>0 is the ground floor; the floors under it count down.</summary>
    public required int Level { get; init; }
    public required Vector2[] Ring { get; init; }
    public required float Y0 { get; init; }
    public required float Y1 { get; init; }
    public float MinX { get; init; }
    public float MinZ { get; init; }
    public float MaxX { get; init; }
    public float MaxZ { get; init; }

    public bool Contains(Vector3 p)
    {
        if (p.Y < Y0 || p.Y > Y1 || p.X < MinX || p.X > MaxX || p.Z < MinZ || p.Z > MaxZ) return false;
        var inside = false;
        for (int i = 0, j = Ring.Length - 1; i < Ring.Length; j = i++)
        {
            var a = Ring[i]; var b = Ring[j];
            if ((a.Y > p.Z) != (b.Y > p.Z) && p.X < (b.X - a.X) * (p.Z - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }
}

/// <summary>
/// The bunker rooms of the current map (bunkers.json) and the shelter rule (#224, ADR 0009): a blast hurts a body only when
/// both are in the open or both are in the same room. Walls of other buildings do not shelter: splash goes through them as
/// before. Godot-free; points are in Godot axes (x east, y up, z = -north).
/// </summary>
public sealed class Shelters
{
    public static readonly Shelters Empty = new(Array.Empty<BunkerRoom>());

    public IReadOnlyList<BunkerRoom> Rooms { get; }
    /// <summary>The ladders between the floors of the bunkers.</summary>
    public Ladders Ladders { get; }

    public Shelters(IReadOnlyList<BunkerRoom> rooms, Ladders? ladders = null)
    {
        Rooms = rooms;
        Ladders = ladders ?? Ladders.Empty;
    }

    /// <summary>The bunkers of a map's bunkers.json text; none for an absent (empty) file.</summary>
    public static Shelters FromJson(string? json) => string.IsNullOrEmpty(json) ? Empty : Parse(json);

    /// <summary>Like <see cref="FromJson"/>, but a broken file is reported to <paramref name="onError"/> and counts as no bunkers.</summary>
    public static Shelters FromJsonOrEmpty(string? json, Action<string> onError)
    {
        try { return FromJson(json); }
        catch (DataFileException e) { onError(e.Message); return Empty; }
    }

    /// <summary>Index of the room that holds <paramref name="p"/>, or -1 in the open.</summary>
    public int RoomAt(Vector3 p)
    {
        for (var i = 0; i < Rooms.Count; i++)
            if (Rooms[i].Contains(p)) return i;
        return -1;
    }

    /// <summary>Does a blast at <paramref name="blast"/> reach a body whose centre is at <paramref name="body"/>?</summary>
    public bool Reaches(Vector3 blast, Vector3 body) => Rooms.Count == 0 || RoomAt(blast) == RoomAt(body);

    /// <summary>
    /// Parse bunkers.json: <c>{"version": 1, "bunkers": [{"id", "parcel", "floors": [{"level", "polygon": ring, "z0", "z1"}],
    /// "ladders": [{"x", "y", "dx", "dy", "z0", "z1"}]}]}</c>. Points and directions are local map metres (x east, y north) and are
    /// converted to Godot ground axes here; z0 and z1 are heights.
    /// </summary>
    public static Shelters Parse(string json, string name = "bunkers.json")
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException e) { throw new DataFileException($"{name}: not JSON: {e.Message}", e); }
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1)
                throw new DataFileException($"{name}: unsupported or missing version (expected 1)");
            if (!root.TryGetProperty("bunkers", out var bunkers) || bunkers.ValueKind != JsonValueKind.Array)
                throw new DataFileException($"{name}: missing bunkers array");
            var rooms = new List<BunkerRoom>();
            var ladders = new List<Ladder>();
            foreach (var b in bunkers.EnumerateArray())
            {
                var id = b.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrEmpty(id)) throw new DataFileException($"{name}: bunker without id");
                if (!b.TryGetProperty("floors", out var floors) || floors.ValueKind != JsonValueKind.Array)
                    throw new DataFileException($"{name}: bunker {id} has no floors array");
                if (b.TryGetProperty("ladders", out var ls) && ls.ValueKind == JsonValueKind.Array)
                    foreach (var l in ls.EnumerateArray())
                    {
                        float x = l.GetProperty("x").GetSingle(), y = l.GetProperty("y").GetSingle();
                        float dx = l.GetProperty("dx").GetSingle(), dy = l.GetProperty("dy").GetSingle();
                        float z0 = l.GetProperty("z0").GetSingle(), z1 = l.GetProperty("z1").GetSingle();
                        if (z1 <= z0 || MathF.Abs(dx * dx + dy * dy - 1f) > 0.05f)
                            throw new DataFileException($"{name}: a ladder of bunker {id} has z1 <= z0 or a facing that is not a unit vector");
                        ladders.Add(new Ladder(new Vector2(x, -y), new Vector2(dx, -dy), z0, z1));
                    }
                foreach (var f in floors.EnumerateArray())
                {
                    if (!f.TryGetProperty("polygon", out var poly) || poly.ValueKind != JsonValueKind.Array || poly.GetArrayLength() < 3)
                        throw new DataFileException($"{name}: a floor of bunker {id} has no polygon of at least 3 points");
                    var ring = poly.EnumerateArray().Select(c => new Vector2(c[0].GetSingle(), -c[1].GetSingle())).ToArray();
                    float y0 = f.GetProperty("z0").GetSingle(), y1 = f.GetProperty("z1").GetSingle();
                    if (y1 <= y0) throw new DataFileException($"{name}: a floor of bunker {id} has z1 <= z0");
                    rooms.Add(new BunkerRoom
                    {
                        BunkerId = id!,
                        Level = f.TryGetProperty("level", out var lv) ? lv.GetInt32() : 0,
                        Ring = ring, Y0 = y0, Y1 = y1,
                        MinX = ring.Min(p => p.X), MaxX = ring.Max(p => p.X),
                        MinZ = ring.Min(p => p.Y), MaxZ = ring.Max(p => p.Y),
                    });
                }
            }
            return new Shelters(rooms, new Ladders(ladders));
        }
    }
}
