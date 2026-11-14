namespace Drycastle.ShooterCore;

/// <summary>
/// One look of a citizen's body (holy_mass.json, citizens.models): the model and how it is fitted and recoloured. This is the swap
/// point for the body: a new model is an entry here (and the client's <c>CitizenView</c> builds from it). Surfaces are material names of the model.
/// </summary>
public sealed class CitizenModelDef
{
    /// <summary>The Synty model's name (assets-private/synty/glb/{file}.glb). The other fields are left from the stand-in models and unused.</summary>
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
    /// <summary>The sprint home when the demon appears (times the citizen's own speed factor).</summary>
    public float FleeSpeed { get; set; } = 6;
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

/// <summary>The Mass event (holy_mass.json, <c>mass</c>): the timeline, the pastor, the seats kept for players and the blessing. Times in seconds, lengths in metres.</summary>
public sealed class MassConfig
{
    /// <summary>Citizens walk in during the gather (the farthest house takes house_radius_m / walk_speed s, the stagger adds to it), then the Mass runs for mass_seconds of progress.</summary>
    public float GatherSeconds { get; set; } = 120;
    /// <summary>The Mass begins this long after the last citizen that was still coming has reached its place (the gather time is the upper limit).</summary>
    public float StartDelaySeconds { get; set; } = 4;
    public float MassSeconds { get; set; } = 120;
    /// <summary>After the Mass completes: citizens stand and clap, the pastor flexes; then everyone is removed and the event is over.</summary>
    public float CompletedSeconds { get; set; } = 12;
    /// <summary>The time over which the citizens leave their houses, and the radius of the houses that send one (a house 100 m away is 70 s of walking).</summary>
    public float StaggerSeconds { get; set; } = 25;
    public float HouseRadiusM { get; set; } = 100;
    /// <summary>Seats no citizen is sent to, so players can take them during the gather (ground floor, spread along the nave). A player takes any seat no citizen has, these or one a killed citizen left.</summary>
    public int PlayerSeats { get; set; } = 8;
    /// <summary>How near a player must be to a seat's point (ground distance) to sit in it.</summary>
    public float SitReachM { get; set; } = 1.6f;
    /// <summary>The share of the Mass a player must have sat through to be blessed.</summary>
    public float BlessedShare { get; set; } = 0.8f;
    /// <summary>A killed pastor: a new one walks out of the sacristy after this long, and the Mass continues this long after he has arrived. The pastor killed this many times turns.</summary>
    public float PastorReplaceSeconds { get; set; } = 10;
    public float ResumeSeconds { get; set; } = 5;
    public int PastorKillsToTurn { get; set; } = 3;
    /// <summary>The share of the citizens (and the monks) that clap when the Mass completes.</summary>
    public float ClapShare { get; set; } = 0.4f;
    /// <summary>Synty models of the pastor and the monks, and the atlas texture (under assets-private/synty/) they wear.</summary>
    public string PastorModel { get; set; } = "priest_01";
    public string MonkModel { get; set; } = "monk_01";
    public string Atlas { get; set; } = "glb/atlas/fantasy_kingdom_01_A.png";
    /// <summary>The pastor does a gesture every few seconds (a random time within these) between the praying base clips. A monk who sees the pastor killed is shocked; this share of them dab.</summary>
    public float GestureMinSeconds { get; set; } = 5;
    public float GestureMaxSeconds { get; set; } = 11;
    public float MonkDabShare { get; set; } = 0.03f;
    /// <summary>Audio files under the shipped assets (a missing file is skipped, logged once; the chant is not made yet). The chant loops while the Mass progresses; the bells play village-wide once at the start.</summary>
    public string ChantSound { get; set; } = "audio/holy_mass/chant.ogg";
    public string BellsSound { get; set; } = "drycastle-generated-sounds/church_bells.ogg";
    /// <summary>The bells ring at the start of the gather; the first citizen leaves its house this long after (the gather's clock starts then too).</summary>
    public float BellsLeadSeconds { get; set; }
    /// <summary>The chant is heard within this distance of the pastor, fading out (positional, with the church's echo).</summary>
    public float ChantRangeM { get; set; } = 40;
}

/// <summary>holy_mass.json, schema version 1: the citizens and the Mass event.</summary>
public sealed class HolyMassFile : IVersioned
{
    public int Version { get; set; }
    public CitizensConfig Citizens { get; set; } = new();
    public MassConfig Mass { get; set; } = new();

    public static HolyMassFile Load(string path) => Validate(DataFile.Load<HolyMassFile>(path));

    public static HolyMassFile Parse(string json) => Validate(DataFile.Parse<HolyMassFile>(json, "holy_mass.json"));

    public static HolyMassFile Validate(HolyMassFile f)
    {
        const string F = "holy_mass.json";
        var c = f.Citizens;
        if (c.MaxCount is < 1 or > 200) throw new DataFileException($"{F}: citizens.max_count is 1..200");
        if (!(c.WalkSpeed > 0) || !(c.FleeSpeed > 0) || !(c.SpeedJitter is >= 0 and < 1)) throw new DataFileException($"{F}: citizens.walk_speed and flee_speed must be positive, speed_jitter 0..1");
        if (!(c.TurnRateDegPerS > 0)) throw new DataFileException($"{F}: citizens.turn_rate_deg_per_s must be positive");
        if (!(c.StaggerSeconds >= 0) || !(c.HouseRadiusM > 0) || !(c.HouseMinM >= 0) || !(c.CorpseSeconds > 0) || !(c.SeatHeightM >= 0))
            throw new DataFileException($"{F}: citizens: stagger_seconds, house_min_m and seat_height_m are not negative; house_radius_m and corpse_seconds are positive");
        var mass = f.Mass;
        if (!(mass.GatherSeconds >= 0) || !(mass.MassSeconds > 0) || !(mass.CompletedSeconds >= 0) || !(mass.StaggerSeconds >= 0) || !(mass.BellsLeadSeconds >= 0) || !(mass.StartDelaySeconds >= 0) || !(mass.HouseRadiusM > 0))
            throw new DataFileException($"{F}: mass: gather_seconds, start_delay_seconds, completed_seconds, stagger_seconds and bells_lead_seconds are not negative; mass_seconds and house_radius_m are positive");
        if (mass.PlayerSeats < 0 || !(mass.SitReachM > 0) || !(mass.BlessedShare is >= 0 and <= 1) || !(mass.ClapShare is >= 0 and <= 1) || !(mass.MonkDabShare is >= 0 and <= 1))
            throw new DataFileException($"{F}: mass: player_seats is not negative, sit_reach_m positive, the shares 0..1");
        if (!(mass.PastorReplaceSeconds >= 0) || !(mass.ResumeSeconds >= 0) || mass.PastorKillsToTurn < 1 || !(mass.GestureMinSeconds > 0) || mass.GestureMaxSeconds < mass.GestureMinSeconds || !(mass.ChantRangeM > 0))
            throw new DataFileException($"{F}: mass: pastor_replace_seconds and resume_seconds are not negative, pastor_kills_to_turn is at least 1, gesture_min_seconds is positive and not above gesture_max_seconds");
        foreach (var col in c.Palette.Concat(c.SkinTones))
            if (col.Length != 7 || col[0] != '#' || !col.Skip(1).All(Uri.IsHexDigit)) throw new DataFileException($"{F}: citizens: '{col}' is not a #rrggbb colour");
        if (c.Models.Count == 0 || c.Palette.Count == 0 || c.SkinTones.Count == 0) throw new DataFileException($"{F}: citizens needs at least one model, palette colour and skin tone");
        foreach (var m in c.Models)
        {
            if (m.File.Length == 0) throw new DataFileException($"{F}: a citizen model needs a file (the name of a Synty model)");
        }
        return f;
    }
}
