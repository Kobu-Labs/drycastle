using System.Text.Json;
using Drycastle.ShooterCore.Data;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>Where a prop of a room goes: in the open (a way round it), back to a wall, or on a wall at a height (graffiti).</summary>
public enum TrapPlace : byte { Centre, Wall, WallHigh }

/// <summary>
/// trap_house.json: what a Trap house is dressed with (#496, ADR 0046, docs/cartel.md "Trap house"): the prop kinds (Synty models, size, solid or not, Duffel spots on top), what each
/// room type gets, the clearances that keep the ways free, the posted defenders' spots, the safe, and which houses a Job may take. Read by the server and every client.
/// </summary>
public sealed class TrapHouseFile : IVersioned
{
    public const string FileName = "trap_house.json";

    public int Version { get; set; }
    public TrapChoiceData Choice { get; set; } = new();
    public TrapClearanceData Clearance { get; set; } = new();
    public TrapPostsData Posts { get; set; } = new();
    public TrapSafeData Safe { get; set; } = new();
    public TrapTablesData Tables { get; set; } = new();
    /// <summary>The prop kinds; the index is the wire's kind byte.</summary>
    public List<TrapPropKind> Props { get; set; } = new();
    /// <summary>Room type to its rows [prop, min, max, place], as in the file; <see cref="RulesFor"/> reads them.</summary>
    public Dictionary<string, List<List<JsonElement>>> Rooms { get; set; } = new();

    Dictionary<string, IReadOnlyList<TrapRoomRule>> _rules = new();

    /// <summary>What a room of <paramref name="type"/> gets, in placing order; nothing for a type the file does not list.</summary>
    public IReadOnlyList<TrapRoomRule> RulesFor(string type) => _rules.TryGetValue(type, out var r) ? r : Array.Empty<TrapRoomRule>();

    /// <summary>The kind byte of prop <paramref name="id"/>, or -1.</summary>
    public int KindOf(string id) => Props.FindIndex(p => p.Id == id);

    public static TrapHouseFile Parse(string json, string name = FileName)
    {
        var f = DataFile.Parse<TrapHouseFile>(json, name);
        f.Validate(name);
        return f;
    }

    public static TrapHouseFile Load(string path)
    {
        if (!File.Exists(path)) throw new DataFileException($"{path}: file not found");
        return Parse(File.ReadAllText(path), Path.GetFileName(path));
    }

    void Validate(string f)
    {
        static void Positive(float v, string what, string f) { if (!(v > 0) || !float.IsFinite(v)) throw new DataFileException($"{f}: {what} must be above 0"); }
        static void NotNegative(float v, string what, string f) { if (!(v >= 0) || !float.IsFinite(v)) throw new DataFileException($"{f}: {what} must be 0 or more"); }
        Positive(Choice.RoadMaxM, "choice.road_max_m", f); NotNegative(Choice.MinRoadWidthM, "choice.min_road_width_m", f);
        Positive(Clearance.DoorClearM, "clearance.door_clear_m", f); NotNegative(Clearance.WallGapM, "clearance.wall_gap_m", f); NotNegative(Clearance.PropGapM, "clearance.prop_gap_m", f);
        NotNegative(Clearance.CentreWallGapM, "clearance.centre_wall_gap_m", f); Positive(Clearance.StepM, "clearance.step_m", f);
        NotNegative(Clearance.TagDoorGapM, "clearance.tag_door_gap_m", f); NotNegative(Clearance.TagApartM, "clearance.tag_apart_m", f);
        if (Posts.PerRoom < 0) throw new DataFileException($"{f}: posts.per_room must be 0 or more");
        NotNegative(Posts.MinRoomM2, "posts.min_room_m2", f); NotNegative(Posts.WallGapM, "posts.wall_gap_m", f); NotNegative(Posts.PropGapM, "posts.prop_gap_m", f); NotNegative(Posts.ApartM, "posts.apart_m", f);
        NotNegative(Safe.StandM, "safe.stand_m", f);
        if (Props.Count is 0 or > 255) throw new DataFileException($"{f}: props must list 1 to 255 kinds");
        var ids = new HashSet<string>();
        foreach (var p in Props)
        {
            var what = $"prop '{p.Id}'";
            if (p.Id == "" || !ids.Add(p.Id)) throw new DataFileException($"{f}: every prop needs an id of its own ('{p.Id}')");
            if (p.Size.Length != 3 || !(p.Size[0] > 0) || !(p.Size[1] > 0) || !(p.Size[2] >= 0)) throw new DataFileException($"{f}: {what}: size is [across, up, front to back], above 0 (front to back may be 0 for graffiti)");
            if (p.Solid && !(p.Size[2] > 0)) throw new DataFileException($"{f}: {what}: a solid prop needs a depth");
            if (p.Models.Count + p.Tags.Count is 0 or > 255) throw new DataFileException($"{f}: {what}: needs 1 to 255 models or tags");
            if (p.Duffel != "" && p.DuffelSpots.Any(s => s.Length != 2)) throw new DataFileException($"{f}: {what}: a duffel spot is [across, front]");
            if (p.Extras.Any(e => e.Model == "" || e.At.Length != 3)) throw new DataFileException($"{f}: {what}: an extra needs a model and at [x, y, z]");
            NotNegative(p.HeightM, $"{what}: height_m", f);
        }
        if (KindOf(Safe.Prop) < 0) throw new DataFileException($"{f}: safe.prop '{Safe.Prop}' is not a prop");
        if (Tables.Min < 0) throw new DataFileException($"{f}: tables.min must be 0 or more");
        foreach (var t in Tables.Props)
            if (KindOf(t) is var k && (k < 0 || Props[k].Duffel == "")) throw new DataFileException($"{f}: tables.props: '{t}' is not a prop with Duffel spots");
        _rules = new();
        foreach (var (type, rows) in Rooms)
        {
            var list = new List<TrapRoomRule>();
            foreach (var row in rows)
            {
                var what = $"rooms.{type}";
                if (row.Count != 4 || row[0].ValueKind != JsonValueKind.String || row[1].ValueKind != JsonValueKind.Number || row[2].ValueKind != JsonValueKind.Number || row[3].ValueKind != JsonValueKind.String)
                    throw new DataFileException($"{f}: {what}: a row is [prop, min, max, place]");
                var kind = KindOf(row[0].GetString()!);
                if (kind < 0) throw new DataFileException($"{f}: {what}: '{row[0].GetString()}' is not a prop");
                int min = row[1].GetInt32(), max = row[2].GetInt32();
                if (min < 0 || max < min) throw new DataFileException($"{f}: {what}: '{row[0].GetString()}' needs 0 <= min <= max");
                TrapPlace place = row[3].GetString() switch
                {
                    "centre" => TrapPlace.Centre, "wall" => TrapPlace.Wall, "wall_high" => TrapPlace.WallHigh,
                    var s => throw new DataFileException($"{f}: {what}: place '{s}' is not centre, wall or wall_high"),
                };
                if ((place == TrapPlace.WallHigh) != (Props[kind].Tags.Count > 0)) throw new DataFileException($"{f}: {what}: wall_high is for the props with tags (graffiti), and only for them");
                list.Add(new TrapRoomRule(kind, min, max, place));
            }
            _rules[type] = list;
        }
    }
}

public sealed class TrapChoiceData
{
    public float RoadMaxM { get; set; } = 30;
    public float MinRoadWidthM { get; set; } = 3;
}

public sealed class TrapClearanceData
{
    public float DoorClearM { get; set; } = 1.2f;
    public float WallGapM { get; set; } = 0.08f;
    public float PropGapM { get; set; } = 0.45f;
    public float CentreWallGapM { get; set; } = 0.9f;
    public float StepM { get; set; } = 0.25f;
    public float TagDoorGapM { get; set; } = 0.3f;
    public float TagApartM { get; set; } = 1f;
}

public sealed class TrapPostsData
{
    public int PerRoom { get; set; } = 2;
    public float MinRoomM2 { get; set; } = 5;
    public float WallGapM { get; set; } = 0.5f;
    public float PropGapM { get; set; } = 0.5f;
    public float ApartM { get; set; } = 1.6f;
    public List<string> SkipTypes { get; set; } = new();
}

public sealed class TrapSafeData
{
    public string Prop { get; set; } = "safe";
    public float StandM { get; set; } = 0.7f;
    public List<string> Rooms { get; set; } = new();
}

public sealed class TrapTablesData
{
    public int Min { get; set; } = 1;
    public List<string> Props { get; set; } = new();
    public List<string> SkipTypes { get; set; } = new();
}

/// <summary>One prop kind of trap_house.json.</summary>
public sealed class TrapPropKind
{
    public string Id { get; set; } = "";
    /// <summary>A box collider of <see cref="Size"/> on the World layer: it stops players and bullets.</summary>
    public bool Solid { get; set; }
    /// <summary>[across its front, up, front to back], metres.</summary>
    public float[] Size { get; set; } = Array.Empty<float>();
    public string Atlas { get; set; } = "";
    public List<string> Models { get; set; } = new();
    /// <summary>What a Duffel on its top holds ("product", "cash"); "" = none.</summary>
    public string Duffel { get; set; } = "";
    /// <summary>[across, front] on its top.</summary>
    public List<float[]> DuffelSpots { get; set; } = new();
    public List<TrapExtra> Extras { get; set; } = new();
    /// <summary>Graffiti: the tags a variant picks.</summary>
    public List<TrapTag> Tags { get; set; } = new();
    /// <summary>Graffiti: the height of its middle over the floor.</summary>
    public float HeightM { get; set; }

    public float Across => Size[0];
    public float Up => Size[1];
    public float Depth => Size[2];
    /// <summary>How many variants the wire's variant byte picks from.</summary>
    public int Variants => Math.Max(Models.Count, Tags.Count);
}

/// <summary>A model drawn on a prop (looks only): at [x across, y up, z front] in the prop's frame, turned <see cref="YawDeg"/>.</summary>
public sealed class TrapExtra
{
    public string Model { get; set; } = "";
    public float[] At { get; set; } = Array.Empty<float>();
    public float YawDeg { get; set; }
}

public sealed class TrapTag
{
    public string Text { get; set; } = "";
    public string Colour { get; set; } = "#ffffff";
}

/// <summary>One row of a room type: up to <see cref="Max"/> (at least <see cref="Min"/> when they fit) of prop kind <see cref="Kind"/>, placed so.</summary>
public sealed record TrapRoomRule(int Kind, int Min, int Max, TrapPlace Place);
