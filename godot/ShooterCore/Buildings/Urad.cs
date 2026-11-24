using System.Numerics;
using Drycastle.ShooterCore.Churches;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Net;

namespace Drycastle.ShooterCore.Buildings;

/// <summary>One of the Obecny Urad's bars: its footprint (map metres, x east / y north) between a floor and a ceiling height (absolute, metres up).</summary>
public sealed record UradPart(string Id, IReadOnlyList<Vector2> Footprint, float FloorZ, float CeilingZ);

/// <summary>Where a worker sits: the seat (Godot axes, on the chair: the height is the seat's, like a church seat), the way they face (Godot yaw), their role and whether the body is the female one.</summary>
public sealed record UradWorkerSpot(CitizenRole Role, Vector3 Pos, float Yaw, bool Female);

/// <summary>The dictation table: where the microphone is (Godot axes, on the floor), and the way the announcer's chair faces it (Godot yaw).</summary>
public sealed record UradTable(Vector3 Pos, float Yaw);

/// <summary>
/// urad.json: the Obecny Urad (parcel P-0288) as the game needs it, the contract between the map pipeline and the game for the building's
/// interior (docs/events/README.md, "Rozhlas detour"), exported next to church.json (manifest <c>files.urad</c>). Godot-free. In the file points are local map metres like the other map JSON
/// (<c>[x east, y north]</c> in a footprint, <c>[x east, y north, z up]</c> for a spot) and a yaw is a compass bearing in degrees (0 north, 90 east);
/// here every point is in Godot axes (x east, y up, z = -north) and every yaw is a Godot yaw (0 faces -Z, positive turns left), as <see cref="ChurchShell"/>.
/// <para>
/// <c>{"version":1, "doorway":{"pos":[x,y,z],"yaw","width","height"}, "volume":[{"id","floor_z","ceiling_z","footprint":[[x,y]...]}], "workers":[{"role":"mayor|clerk|announcer","pos":[x,y,z],"yaw","female"}], "table":{"pos":[x,y,z],"yaw"}}</c>.
/// <c>doorway</c> is the entrance the door fills (as church.json's: <c>pos</c> the middle of the opening on the floor, <c>yaw</c> pointing out); <c>volume</c> is the interior rooms, the part of the
/// world that counts as "in the Urad" (<see cref="Contains"/>); <c>workers</c> are the seats (chairs are baked into the map) and <c>table</c> is the dictation desk on the floor. A <c>cctv</c> entry
/// (reserved for a later feature) and any other field is ignored.
/// </para>
/// </summary>
public sealed class Urad
{
    /// <summary>How far outside the door somebody who is shut out stands (<see cref="EvictionPoint"/>).</summary>
    public const float EvictOutsideM = 1.5f;

    /// <summary>A body's feet are tested this far above themselves for "inside": the floor is the volume's lower edge, and a capsule rests a hair above or below it.</summary>
    const float FloorSlackM = 0.1f;

    public Doorway Doorway { get; }
    public IReadOnlyList<UradPart> Parts { get; }
    public IReadOnlyList<UradWorkerSpot> Workers { get; }
    public UradTable Table { get; }
    /// <summary>Node-name prefix of the panes that stand in real openings (the client draws them clear); null when the map names none.</summary>
    public string? GlassPrefix { get; }

    /// <summary>The announcer's chair at the dictation table: where whoever takes the set sits.</summary>
    public UradWorkerSpot AnnouncerSeat => Workers.First(w => w.Role == CitizenRole.Announcer);

    public Urad(Doorway doorway, IReadOnlyList<UradPart> parts, IReadOnlyList<UradWorkerSpot> workers, UradTable table, string? glassPrefix = null)
    {
        Doorway = doorway; Parts = parts; Workers = workers; Table = table; GlassPrefix = glassPrefix;
    }

    /// <summary>
    /// Where a body (a capsule of <paramref name="radius"/>, feet at <paramref name="feet"/>) goes when the door closes, or null when it needs not: anybody in an interior room or in the
    /// doorway is put <see cref="EvictOutsideM"/> out from the door, on the doorway's floor height, so nobody stays locked in until the next broadcast.
    /// </summary>
    public Vector3? EvictionPoint(Vector3 feet, float radius)
    {
        if (!Contains(feet + new Vector3(0, FloorSlackM, 0)) && !Doorway.Overlaps(feet, radius)) return null;
        return Doorway.Outside(EvictOutsideM);
    }

    /// <summary>
    /// Whether a point (Godot axes) is inside the Urad: within one bar's footprint (looking down) and between its floor and ceiling. This is the test that decides which
    /// shots, explosions and deaths the live feed rebroadcasts.
    /// </summary>
    public bool Contains(Vector3 p)
    {
        var map = new Vector2(p.X, -p.Z); // Godot z is minus north
        foreach (var part in Parts)
            if (p.Y >= part.FloorZ && p.Y <= part.CeilingZ && Inside(part.Footprint, map)) return true;
        return false;
    }

    /// <summary>Even-odd point-in-polygon on the map plane (a point exactly on an edge may count either way).</summary>
    public static bool Inside(IReadOnlyList<Vector2> polygon, Vector2 p)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i]; var b = polygon[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>The Urad of a urad.json text; null for an empty text.</summary>
    public static Urad? FromJson(string? json) => string.IsNullOrEmpty(json) ? null : Parse(json);

    /// <summary>Like <see cref="FromJson"/>, but a broken file is reported to <paramref name="onError"/> and counts as no Urad.</summary>
    public static Urad? FromJsonOrNull(string? json, Action<string> onError)
    {
        try { return FromJson(json); }
        catch (DataFileException e) { onError(e.Message); return null; }
    }

    public static Urad Parse(string json, string name = "urad.json")
    {
        var f = DataFile.Parse<UradFile>(json, name);
        if (f.Doorway is not { } d) throw new DataFileException($"{name}: missing doorway");
        if (!(d.Width > 0) || !(d.Height > 0)) throw new DataFileException($"{name}: the doorway needs a width and a height");
        var doorway = new Doorway(Point(d.Pos, name, "doorway"), Bearing(d.Yaw), d.Width, d.Height);
        if (f.Volume.Count == 0) throw new DataFileException($"{name}: volume is empty");
        var parts = new List<UradPart>();
        foreach (var v in f.Volume)
        {
            if (v.Footprint.Count < 3) throw new DataFileException($"{name}: volume '{v.Id}' needs a footprint of at least 3 points");
            if (!(v.CeilingZ > v.FloorZ)) throw new DataFileException($"{name}: volume '{v.Id}': ceiling_z must be above floor_z");
            parts.Add(new UradPart(v.Id, v.Footprint.Select(p => p.Length == 2 ? new Vector2(p[0], p[1]) : throw new DataFileException($"{name}: volume '{v.Id}': a footprint point is [x, y]")).ToList(), v.FloorZ, v.CeilingZ));
        }
        var workers = f.Workers.Select(w => new UradWorkerSpot(RoleOf(w.Role, name), Point(w.Pos, name, $"worker {w.Role}"), Bearing(w.Yaw), w.Female)).ToList();
        if (workers.Count(w => w.Role == CitizenRole.Mayor) != 1) throw new DataFileException($"{name}: exactly one mayor");
        if (workers.Count(w => w.Role == CitizenRole.Announcer) != 1) throw new DataFileException($"{name}: exactly one announcer");
        if (f.Table is not { } t) throw new DataFileException($"{name}: missing table");
        return new Urad(doorway, parts, workers, new UradTable(Point(t.Pos, name, "table"), Bearing(t.Yaw)), string.IsNullOrEmpty(f.GlassPrefix) ? null : f.GlassPrefix);
    }

    static CitizenRole RoleOf(string role, string file) => role switch
    {
        "mayor" => CitizenRole.Mayor,
        "clerk" => CitizenRole.Clerk,
        "announcer" => CitizenRole.Announcer,
        _ => throw new DataFileException($"{file}: worker role '{role}' is mayor, clerk or announcer"),
    };

    static Vector3 Point(float[] p, string file, string what) =>
        p.Length == 3 ? new Vector3(p[0], p[2], -p[1]) : throw new DataFileException($"{file}: {what} pos is [x, y, z]");

    static float Bearing(float degrees) => -degrees * MathF.PI / 180f;
}

/// <summary>urad.json as it reads off the disk; <see cref="Urad.Parse"/> checks and converts it.</summary>
public sealed class UradFile : IVersioned
{
    public int Version { get; set; }
    public UradDoorwayDef? Doorway { get; set; }
    public List<UradVolumeDef> Volume { get; set; } = new();
    public List<UradWorkerDef> Workers { get; set; } = new();
    public UradTableDef? Table { get; set; }
    public string? GlassPrefix { get; set; }
}

public sealed class UradVolumeDef
{
    public string Id { get; set; } = "";
    public float FloorZ { get; set; }
    public float CeilingZ { get; set; }
    public List<float[]> Footprint { get; set; } = new();
}

public sealed class UradWorkerDef
{
    public string Role { get; set; } = "";
    public float[] Pos { get; set; } = Array.Empty<float>();
    public float Yaw { get; set; }
    public bool Female { get; set; }
}

public sealed class UradTableDef
{
    public float[] Pos { get; set; } = Array.Empty<float>();
    public float Yaw { get; set; }
}

public sealed class UradDoorwayDef
{
    public float[] Pos { get; set; } = Array.Empty<float>();
    public float Yaw { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
}

/// <summary>The Urad door event (<see cref="EventKind.UradDoor"/>): one byte, 1 open, 0 closed. Same payload as <see cref="ChurchDoorCodec"/>.</summary>
public static class UradDoorCodec
{
    public static byte[] Encode(bool open) => ChurchDoorCodec.Encode(open);
    public static bool? Decode(byte[] payload) => ChurchDoorCodec.Decode(payload);
}
