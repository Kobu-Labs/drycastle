namespace Drycastle.ShooterCore;

/// <summary>How a species gathers into herds (wildlife.json): its animals are split into groups of <see cref="Min"/> to <see cref="Max"/> that stay together (<see cref="Herd"/>).</summary>
public sealed class HerdRules
{
    public int Min { get; set; } = 2;
    public int Max { get; set; } = 5;
    /// <summary>How far members wander from the herd's centre, and how far from it each starts, metres.</summary>
    public float SpreadM { get; set; } = 10;
    /// <summary>The herd's centre drifts to a new spot within this distance of where the herd formed, metres.</summary>
    public float RangeM { get; set; } = 40;
    /// <summary>The centre drifts every this many to <see cref="MoveMaxSeconds"/> seconds while the herd is calm.</summary>
    public float MoveMinSeconds { get; set; } = 30;
    public float MoveMaxSeconds { get; set; } = 90;
}

/// <summary>One wild species the map holds (wildlife.json): an id of animals.json and how many of it each round.</summary>
public sealed class WildlifeSpecies
{
    public string Id { get; set; } = "";
    /// <summary>How many of the species a round starts with (fewer when the map has too few candidate points for it).</summary>
    public int Count { get; set; }
    /// <summary>Null: every animal of the species is on its own. Set: they gather into herds.</summary>
    public HerdRules? Herd { get; set; }
}

/// <summary>
/// A kind of bird flock (wildlife.json, "flocks"): <see cref="Flocks"/> flocks of <see cref="BirdsMin"/> to <see cref="BirdsMax"/> birds fly about habitat
/// points (the ground attribute value <c>wildlife:&lt;Id&gt;</c>), each bird a boid (<see cref="BoidRules"/>). Cosmetic: see <see cref="Flock"/>.
/// </summary>
public sealed class FlockSpecies
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The GLB, relative to the asset library (assets/) and then the private packs (assets-private/); a small wedge stands in when it is in neither.</summary>
    public string Model { get; set; } = "";
    public float Scale { get; set; } = 1;
    /// <summary>Yaw added to the model so its head points to the bird's forward (-Z).</summary>
    public float ModelYawDeg { get; set; }
    /// <summary>How many flocks of this kind a round starts with.</summary>
    public int Flocks { get; set; } = 1;
    public int BirdsMin { get; set; } = 6;
    public int BirdsMax { get; set; } = 14;
    /// <summary>The height above the ground the flock cruises at: a new height in this range for each waypoint, metres.</summary>
    public float AltitudeMinM { get; set; } = 15;
    public float AltitudeMaxM { get; set; } = 35;
    /// <summary>How far from where it formed the flock's waypoints lie, metres.</summary>
    public float RangeM { get; set; } = 150;
    public float SpeedMps { get; set; } = 7;
    public float TurnRateDegPerS { get; set; } = 35;
    /// <summary>A player or moving vehicle this close (flat distance, metres) scatters the flock.</summary>
    public float ScatterRadiusM { get; set; } = 30;
    public float ScatterSeconds { get; set; } = 6;
    public BoidRules Boids { get; set; } = new();
}

/// <summary>
/// wildlife.json, schema version 1: which wild species the map holds and how many of each. WHERE they stand is not in here: a species is a zone
/// kind (its animals.json id), and its candidate points are baked into the map (<see cref="ZonePoints"/>).
/// The defaults are inert (no species), so a missing file means a world without wildlife.
/// </summary>
public sealed class WildlifeFile : IVersioned
{
    /// <summary>Hard ceiling of <see cref="MaxAnimals"/>: wildlife, pets and every other world entity share the entity cap (<see cref="WorldEntityManager.MaxEntities"/>).</summary>
    public const int MaxAnimalsLimit = 200;

    public int Version { get; set; }
    /// <summary>Most wild animals in the whole map at once.</summary>
    public int MaxAnimals { get; set; }
    public List<WildlifeSpecies> Species { get; set; } = new();
    /// <summary>Bird flocks (flying boids); not animals: they are not in animals.json and cannot be shot. Flock entities count towards the world's entity cap.</summary>
    public List<FlockSpecies> Flocks { get; set; } = new();

    public static WildlifeFile Load(string path) => Validate(DataFile.Load<WildlifeFile>(path));

    public static WildlifeFile Parse(string json) => Validate(DataFile.Parse<WildlifeFile>(json, "wildlife.json"));

    public static WildlifeFile Validate(WildlifeFile f)
    {
        const string F = "wildlife.json";
        if (f.MaxAnimals is < 0 or > MaxAnimalsLimit) throw new DataFileException($"{F}: max_animals is 0..{MaxAnimalsLimit} (wildlife, pets and their poop share the world's entity cap)");
        var seen = new HashSet<string>();
        foreach (var s in f.Species)
        {
            if (s is null || s.Id == "") throw new DataFileException($"{F}: a species has no id");
            if (!seen.Add(s.Id)) throw new DataFileException($"{F}: species {s.Id} appears twice");
            if (s.Count < 1) throw new DataFileException($"{F}: species {s.Id}: count must be at least 1");
            if (s.Herd is { } h)
            {
                if (h.Min < 2 || h.Max < h.Min) throw new DataFileException($"{F}: species {s.Id}: herd min is at least 2 and not above max");
                if (!(h.SpreadM > 0) || !(h.RangeM >= 0)) throw new DataFileException($"{F}: species {s.Id}: herd spread_m must be positive and range_m not negative");
                if (!(h.MoveMinSeconds > 0) || h.MoveMaxSeconds < h.MoveMinSeconds) throw new DataFileException($"{F}: species {s.Id}: herd move_min_seconds must be positive and not above move_max_seconds");
            }
        }
        var seenFlocks = new HashSet<string>();
        foreach (var k in f.Flocks)
        {
            if (k is null || k.Id == "") throw new DataFileException($"{F}: a flock has no id");
            if (!seenFlocks.Add(k.Id) || seen.Contains(k.Id)) throw new DataFileException($"{F}: flock {k.Id}: the id is used twice (flocks and species share one namespace: it is the habitat name)");
            if (k.Flocks < 1 || k.Flocks > 20) throw new DataFileException($"{F}: flock {k.Id}: flocks is 1..20");
            if (k.BirdsMin < 2 || k.BirdsMax < k.BirdsMin || k.BirdsMax > 120) throw new DataFileException($"{F}: flock {k.Id}: birds_min is at least 2, not above birds_max, and birds_max is at most 120");
            if (!(k.AltitudeMinM >= 0) || k.AltitudeMaxM < k.AltitudeMinM) throw new DataFileException($"{F}: flock {k.Id}: altitude_min_m is not negative and not above altitude_max_m");
            if (!(k.RangeM >= 0) || !(k.SpeedMps > 0) || !(k.TurnRateDegPerS > 0) || !(k.ScatterRadiusM >= 0) || !(k.ScatterSeconds > 0) || !(k.Scale > 0)) throw new DataFileException($"{F}: flock {k.Id}: range_m, speed_mps, turn_rate_deg_per_s, scatter_radius_m, scatter_seconds and scale must be positive");
            var b = k.Boids;
            if (!(b.NeighbourM > 0) || !(b.SeparationM > 0) || !(b.MinSpeed > 0) || b.MaxSpeed < b.MinSpeed || !(b.MaxAccel > 0) || !(b.BandM >= 0) || !(b.LeashM > 0) || !(b.PanicSpeedFactor >= 1) || !(b.AvoidMarginM >= 0) || !(b.AvoidAccelFactor > 0) || !(b.AvoidHorizonS > 0))
                throw new DataFileException($"{F}: flock {k.Id}: boids: neighbour_m, separation_m, min_speed, max_accel and leash_m must be positive, max_speed not below min_speed, panic_speed_factor at least 1");
        }
        if (f.Flocks.Sum(k => k.Flocks) + f.MaxAnimals > MaxAnimalsLimit + 20) throw new DataFileException($"{F}: animals and flocks together exceed the entity budget");
        return f;
    }

    /// <summary>Checks that every species id is a wild species of <paramref name="animals"/> (one that flees: a pet lives in a Pen, not in a zone).</summary>
    public WildlifeFile CheckAgainst(AnimalsFile animals)
    {
        foreach (var s in Species)
        {
            if (animals.Find(s.Id) is not { } def) throw new DataFileException($"wildlife.json: species {s.Id} is not in animals.json");
            if (!def.Flees) throw new DataFileException($"wildlife.json: species {s.Id} is a pet (flees is off): pets.json places those");
        }
        return this;
    }
}
