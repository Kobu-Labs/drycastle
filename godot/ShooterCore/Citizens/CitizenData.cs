namespace Drycastle.ShooterCore;

/// <summary>
/// One look of a citizen's body (holy_mass.json, citizens.models): the model and how it is fitted and recoloured. This is the swap
/// point for the body: a new model is an entry here (and the client's <c>CitizenView</c> builds from it). Surfaces are material names of the model.
/// </summary>
public sealed class CitizenModelDef
{
    /// <summary>The GLB, relative to the asset library (assets/).</summary>
    public string File { get; set; } = "";
    /// <summary>How tall the model is as it imports (the animated characters: 3.147 rig units); with <see cref="HeightM"/> it gives the scale.</summary>
    public float ImportHeight { get; set; } = 1;
    /// <summary>The height in metres the figure is drawn at.</summary>
    public float HeightM { get; set; } = 1.75f;
    /// <summary>Yaw added to the model so it faces the entity's forward (-Z). The character pack faces +Z, so 180.</summary>
    public float YawDeg { get; set; } = 180;
    /// <summary>Surfaces that take the clothing colour, a darker shade of it, and the skin tone; those in <see cref="Hide"/> are not drawn (helmets).</summary>
    public List<string> Clothes { get; set; } = new();
    public List<string> Dark { get; set; } = new();
    public List<string> Skin { get; set; } = new();
    public List<string> Hide { get; set; } = new();
    /// <summary>Clips: walking, falling dead (held on the last frame), and sitting down (held on the last frame while seated).</summary>
    public string WalkClip { get; set; } = "Walk";
    public string DeathClip { get; set; } = "Death";
    public string SitClip { get; set; } = "SitDown";
    /// <summary>Metres from the model's origin up to the pelvis at the last frame of the sit clip, and how far behind the origin it lies.</summary>
    public float SitPelvisM { get; set; }
    public float SitBackM { get; set; }
}

/// <summary>The citizens of the Holy Mass (holy_mass.json, <c>citizens</c>). Lengths are metres, speeds metres per second, times seconds.</summary>
public sealed class CitizensConfig
{
    /// <summary>The most citizens alive or waiting to spawn at once.</summary>
    public int MaxCount { get; set; } = 60;
    public float WalkSpeed { get; set; } = 1.4f;
    /// <summary>Each citizen walks at the walk speed times a factor within this share either way, so a crowd does not step in unison.</summary>
    public float SpeedJitter { get; set; } = 0.12f;
    public float TurnRateDegPerS { get; set; } = 360;
    /// <summary>Default time over which a batch of citizens leaves its houses.</summary>
    public float StaggerSeconds { get; set; } = 30;
    /// <summary>Default radius around the church door in which houses send citizens.</summary>
    public float HouseRadiusM { get; set; } = 150;
    /// <summary>Houses nearer to the church door than this send nobody (the church's own plot).</summary>
    public float HouseMinM { get; set; } = 20;
    /// <summary>How long a killed citizen lies before it is removed.</summary>
    public float CorpseSeconds { get; set; } = 15;
    /// <summary>Height of a pew's seat above the floor. The seat point of church.json is on the seat; a seated citizen is hit as a crouching body standing on the floor below it.</summary>
    public float SeatHeightM { get; set; } = 0.45f;
    public List<string> Palette { get; set; } = new();
    public List<string> SkinTones { get; set; } = new();
    public List<CitizenModelDef> Models { get; set; } = new();
}

/// <summary>holy_mass.json, schema version 1. Only the citizens section exists so far; the Mass event adds its own.</summary>
public sealed class HolyMassFile : IVersioned
{
    public int Version { get; set; }
    public CitizensConfig Citizens { get; set; } = new();

    public static HolyMassFile Load(string path) => Validate(DataFile.Load<HolyMassFile>(path));

    public static HolyMassFile Parse(string json) => Validate(DataFile.Parse<HolyMassFile>(json, "holy_mass.json"));

    public static HolyMassFile Validate(HolyMassFile f)
    {
        const string F = "holy_mass.json";
        var c = f.Citizens;
        if (c.MaxCount is < 1 or > 200) throw new DataFileException($"{F}: citizens.max_count is 1..200");
        if (!(c.WalkSpeed > 0) || !(c.SpeedJitter is >= 0 and < 1)) throw new DataFileException($"{F}: citizens.walk_speed must be positive, speed_jitter 0..1");
        if (!(c.TurnRateDegPerS > 0)) throw new DataFileException($"{F}: citizens.turn_rate_deg_per_s must be positive");
        if (!(c.StaggerSeconds >= 0) || !(c.HouseRadiusM > 0) || !(c.HouseMinM >= 0) || !(c.CorpseSeconds > 0) || !(c.SeatHeightM >= 0))
            throw new DataFileException($"{F}: citizens: stagger_seconds, house_min_m and seat_height_m are not negative; house_radius_m and corpse_seconds are positive");
        foreach (var col in c.Palette.Concat(c.SkinTones))
            if (col.Length != 7 || col[0] != '#' || !col.Skip(1).All(Uri.IsHexDigit)) throw new DataFileException($"{F}: citizens: '{col}' is not a #rrggbb colour");
        if (c.Models.Count == 0 || c.Palette.Count == 0 || c.SkinTones.Count == 0) throw new DataFileException($"{F}: citizens needs at least one model, palette colour and skin tone");
        foreach (var m in c.Models)
        {
            if (!m.File.EndsWith(".glb", StringComparison.Ordinal)) throw new DataFileException($"{F}: a citizen model must be a .glb path in the asset library");
            if (!(m.ImportHeight > 0) || !(m.HeightM > 0)) throw new DataFileException($"{F}: {m.File}: import_height and height_m must be positive");
        }
        return f;
    }
}
