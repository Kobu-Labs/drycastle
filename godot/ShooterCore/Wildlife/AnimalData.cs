namespace Drycastle.ShooterCore;

/// <summary>
/// One species of wildlife or livestock (animals.json). Lengths are metres, speeds metres per second, angles degrees,
/// times seconds. The numbers are placeholders tuned by feel (hp, speeds, radii) or measured from the model (scale, hit box).
/// </summary>
public sealed class AnimalDef
{
    /// <summary>Lower-case id ("deer", "shiba_inu"); the record of one species in the match and in tests.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The GLB, relative to the asset library (assets/), for example "animated-animals/deer.glb".</summary>
    public string Model { get; set; } = "";
    /// <summary>Uniform scale that brings the model, as it imports, to its real size (the packs are authored about 3 to 5 units tall).</summary>
    public float Scale { get; set; } = 1;
    /// <summary>Yaw added to the model so its head points to the entity's forward (-Z). The animal packs face +Z, so 180.</summary>
    public float ModelYawDeg { get; set; } = 180;
    /// <summary>Hit points. At most 255: the hp byte of the wire carries them as they are.</summary>
    public float Hp { get; set; }
    public float WalkSpeed { get; set; }
    /// <summary>Flee speed (the gallop clip).</summary>
    public float RunSpeed { get; set; }
    public float TurnRateDegPerS { get; set; } = 200;
    /// <summary>A threat (a player or a vehicle) closer than this makes the animal flee.</summary>
    public float FleeRadius { get; set; }
    /// <summary>How far from where it spawned the animal wanders. It walks back when a flight took it further out.</summary>
    public float WanderRadius { get; set; }
    /// <summary>Body box in the entity's frame: [width, height, length], feet on the ground, centred on the position. For hit tests.</summary>
    public List<float> HitBox { get; set; } = new();
}

/// <summary>
/// animals.json, schema version 1: the species table and the rules every animal follows (how long a corpse lies, how
/// long a shot animal keeps fleeing, how often it idles, eats and walks).
/// </summary>
public sealed class AnimalsFile : IVersioned
{
    /// <summary>The species the wire can name (the species index travels in the owner field, so the true limit is 65535; the data stays small).</summary>
    public const int MaxSpecies = 64;

    public int Version { get; set; }
    /// <summary>Seconds a killed animal lies (its death clip plays, then the last frame holds) before it is removed.</summary>
    public float CorpseSeconds { get; set; } = 30;
    /// <summary>Seconds a shot (and living) animal keeps running from where the shot came from, whoever is near.</summary>
    public float AlarmSeconds { get; set; } = 8;
    /// <summary>Seconds without a threat in range before a fleeing animal calms down and stops.</summary>
    public float CalmSeconds { get; set; } = 2;
    /// <summary>The largest height change of the ground in one step; a bigger one blocks the animal (a wall, a ditch, water the world refuses).</summary>
    public float MaxStepM { get; set; } = 0.8f;
    /// <summary>The nearest a wander target lies from the animal.</summary>
    public float WanderMinM { get; set; } = 3;
    public float IdleMinSeconds { get; set; } = 2;
    public float IdleMaxSeconds { get; set; } = 8;
    /// <summary>Chance that the next thing after standing still is eating (head down) rather than walking somewhere.</summary>
    public float EatChance { get; set; } = 0.35f;
    public float EatMinSeconds { get; set; } = 3;
    public float EatMaxSeconds { get; set; } = 7;
    public List<AnimalDef> Animals { get; set; } = new();

    Dictionary<string, AnimalDef>? _byId;

    public static AnimalsFile Load(string path) => Validate(DataFile.Load<AnimalsFile>(path));

    public static AnimalsFile Parse(string json) => Validate(DataFile.Parse<AnimalsFile>(json, "animals.json"));

    /// <summary>The species with this id, or null.</summary>
    public AnimalDef? Find(string? id) => DataIndex.Find(ref _byId, Animals, a => a.Id, id);

    /// <summary>The species index (the position in the list, what the wire carries), or -1.</summary>
    public int IndexOf(string id) => Animals.FindIndex(a => a.Id == id);

    public static AnimalsFile Validate(AnimalsFile f)
    {
        const string F = "animals.json";
        if (f.Animals.Count > MaxSpecies) throw new DataFileException($"{F}: at most {MaxSpecies} species");
        if (!(f.CorpseSeconds > 0) || !(f.AlarmSeconds >= 0) || !(f.CalmSeconds >= 0)) throw new DataFileException($"{F}: corpse_seconds must be positive, alarm_seconds and calm_seconds not negative");
        if (!(f.MaxStepM > 0) || !(f.WanderMinM >= 0)) throw new DataFileException($"{F}: max_step_m must be positive, wander_min_m not negative");
        if (!(f.IdleMinSeconds > 0) || f.IdleMaxSeconds < f.IdleMinSeconds) throw new DataFileException($"{F}: idle_min_seconds must be positive and not above idle_max_seconds");
        if (!(f.EatMinSeconds > 0) || f.EatMaxSeconds < f.EatMinSeconds) throw new DataFileException($"{F}: eat_min_seconds must be positive and not above eat_max_seconds");
        if (f.EatChance is < 0 or > 1) throw new DataFileException($"{F}: eat_chance is 0..1");
        var seen = new HashSet<string>();
        foreach (var a in f.Animals)
        {
            if (a is null || a.Id == "" || a.Id.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')))
                throw new DataFileException($"{F}: a species id is lower case letters, digits and underscores");
            if (!seen.Add(a.Id)) throw new DataFileException($"{F}: species id {a.Id} appears twice");
            if (a.Name == "") throw new DataFileException($"{F}: {a.Id}: name is empty");
            if (!a.Model.EndsWith(".glb", StringComparison.Ordinal)) throw new DataFileException($"{F}: {a.Id}: model must be a .glb path in the asset library");
            if (!(a.Scale > 0)) throw new DataFileException($"{F}: {a.Id}: scale must be positive");
            if (!(a.Hp > 0) || a.Hp > 255) throw new DataFileException($"{F}: {a.Id}: hp must be 1..255 (the wire carries it in one byte)");
            if (!(a.WalkSpeed > 0) || a.RunSpeed < a.WalkSpeed) throw new DataFileException($"{F}: {a.Id}: walk_speed must be positive and not above run_speed");
            if (!(a.TurnRateDegPerS > 0)) throw new DataFileException($"{F}: {a.Id}: turn_rate_deg_per_s must be positive");
            if (!(a.FleeRadius > 0)) throw new DataFileException($"{F}: {a.Id}: flee_radius must be positive");
            if (!(a.WanderRadius >= f.WanderMinM)) throw new DataFileException($"{F}: {a.Id}: wander_radius must be at least wander_min_m");
            if (a.HitBox.Count != 3 || a.HitBox.Any(x => !(x > 0))) throw new DataFileException($"{F}: {a.Id}: hit_box is [width, height, length], all positive");
        }
        return f;
    }
}
