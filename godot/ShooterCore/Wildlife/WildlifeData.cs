using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Mining;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Scenery;

namespace Drycastle.ShooterCore.Wildlife;

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
/// A rare wild species (wildlife.json, "rare"; GLOSSARY "Unicorn"): <see cref="Count"/> of it live on the whole map at a time, each at a random point anywhere with ground
/// that is at least <see cref="MinBuildingM"/> from every building (no habitat zone). A killed one is replaced by a new one at a new point <see cref="RespawnSeconds"/> after the kill.
/// </summary>
public sealed class RareSpecies
{
    public string Id { get; set; } = "";
    /// <summary>How many live at once (each has its own respawn clock).</summary>
    public int Count { get; set; } = 1;
    /// <summary>Spawn points lie at least this far (flat metres) from every building.</summary>
    public float MinBuildingM { get; set; } = 60;
    /// <summary>From a kill (or a failed search for a point) to the next try, seconds.</summary>
    public float RespawnSeconds { get; set; } = 60;
}

/// <summary>
/// The Phoenix (wildlife.json, "phoenix"; GLOSSARY "Phoenix"): a rare Mystery animal of the night sky. A night is counted each time the server's clock passes <see cref="SpawnHour"/>; every
/// <see cref="NightsBetween"/>th one puts a Phoenix in the sky (the first on the 5th night). It patrols a wide circle at <see cref="AltitudeMinM"/> to <see cref="AltitudeMaxM"/> above the ground, falls to a
/// clear patch of ground when hit, and burns when hit again there. Everything the keeper and the animal need is here; nothing is hard-coded in the rules.
/// </summary>
public sealed class PhoenixRules
{
    /// <summary>The animals.json species (it has <see cref="AnimalDef.Flies"/>).</summary>
    public string Id { get; set; } = "phoenix";
    /// <summary>A Phoenix is due at every this-many-th night (counted from the server's start, not reset by a round).</summary>
    public int NightsBetween { get; set; } = 5;
    /// <summary>The hour of the day (0..24, 0 = midnight) that counts a night and spawns the due Phoenix.</summary>
    public float SpawnHour { get; set; }
    /// <summary>The hour a Phoenix still Flying leaves (it flies off and is gone). A Crashed one stays until killed or the round ends.</summary>
    public float DawnHour { get; set; } = 5;
    /// <summary>Height of the flying body centre above the ground at the patrol centre: a value of this range per Phoenix, metres.</summary>
    public float AltitudeMinM { get; set; } = 80;
    public float AltitudeMaxM { get; set; } = 140;
    /// <summary>Radius of the patrol circle (it is cut to fit the map), metres, and the speed along it, m/s.</summary>
    public float PatrolRadiusM { get; set; } = 350;
    public float SpeedMps { get; set; } = 14;
    /// <summary>How long the fall (the dive from the hit to the crash site) takes, seconds.</summary>
    public float FallSeconds { get; set; } = 4;
    /// <summary>The crash site lies this far (flat metres) from where it was hit: min..max, ahead of it within about 40 degrees if the ground there is clear, else all round.</summary>
    public float CrashMinM { get; set; } = 40;
    public float CrashMaxM { get; set; } = 120;
    /// <summary>The site needs a free circle of this radius (6 m across by default): ground all over it, no water, nothing solid, and its ground no more than <see cref="CrashMaxSlopeM"/> higher or lower than its centre.</summary>
    public float CrashClearM { get; set; } = 3;
    public float CrashMaxSlopeM { get; set; } = 0.9f;
    /// <summary>The site is at least this far (flat metres) from every building (a parcel that holds one).</summary>
    public float CrashBuildingM { get; set; } = 10;
    /// <summary>How long the flame burns after the second hit, seconds; then it is Dead and the mineral is left.</summary>
    public float BurnSeconds { get; set; } = 6;
    /// <summary>How long the Dead Phoenix stays (the body is gone at once on the client, embers rise) before the entity ends, seconds. The mineral it dropped has its own minute.</summary>
    public float DeadSeconds { get; set; } = 6;
    /// <summary>The mineral (<see cref="Minerals"/> id) the dead Phoenix leaves where it burned.</summary>
    public string Drops { get; set; } = "pheonixite";
}

/// <summary>One member rule of a <see cref="MixedHerd"/>: an animals.json id that is a fixed <see cref="Min"/>..<see cref="Max"/> of each herd (a stag or two), and/or fills the rest by <see cref="Weight"/>.</summary>
public sealed class HerdMember
{
    public string Id { get; set; } = "";
    /// <summary>A herd holds this many of the member (rolled min..max, with Max 0: none by count). Together the counted members leave at least one animal for the weighted ones.</summary>
    public int Min { get; set; }
    public int Max { get; set; }
    /// <summary>The share of the animals left after the counted members that are this one (0: none; relative to the other members' weights).</summary>
    public float Weight { get; set; }
}

/// <summary>The size range of a <see cref="MixedHerd"/>: animals per herd, min..max.</summary>
public sealed class HerdSize
{
    public int Min { get; set; } = 3;
    public int Max { get; set; } = 6;
}

/// <summary>
/// A herd of more than one species (wildlife.json, "herds"): <see cref="Count"/> animals in all, split into herds of <see cref="Size"/> animals. Each herd rolls its
/// size, then the counted members (<see cref="HerdMember.Min"/>..Max, such as 1-3 stags, capped so one animal is left), then fills the rest by weight.
/// All members stand in the zones of one kind (<see cref="Habitat"/>), so the herd forms at one habitat point.
/// </summary>
public sealed class MixedHerd
{
    public string Id { get; set; } = "";
    /// <summary>The animals in all the herds of this rule (fewer when the map has too few points); a count not a multiple ends in a smaller herd.</summary>
    public int Count { get; set; }
    /// <summary>The zone kind the herds form in. Empty: the first weighted member's id.</summary>
    public string Habitat { get; set; } = "";
    public HerdSize Size { get; set; } = new();
    public float SpreadM { get; set; } = 10;
    public float RangeM { get; set; } = 40;
    public float MoveMinSeconds { get; set; } = 30;
    public float MoveMaxSeconds { get; set; } = 90;
    public List<HerdMember> Members { get; set; } = new();

    /// <summary>The zone kind the herds are drawn in: <see cref="Habitat"/>, or the first weighted member.</summary>
    public string HabitatKind => Habitat != "" ? Habitat : Members.FirstOrDefault(m => m.Weight > 0)?.Id ?? "";

    /// <summary>The members of one herd of <paramref name="size"/> animals (ids): the counted members first, then the rest by weight.</summary>
    public List<string> Roll(int size, Random rng)
    {
        var ids = new List<string>(size);
        var room = Math.Max(0, size - 1); // at least one animal is a weighted one
        foreach (var m in Members)
        {
            if (m.Max <= 0 || room <= 0) continue;
            var n = Math.Min(room, rng.Next(m.Min, m.Max + 1));
            for (var i = 0; i < n; i++) ids.Add(m.Id);
            room -= n;
        }
        var total = Members.Sum(m => m.Weight);
        while (ids.Count < size && total > 0)
        {
            var roll = (float)rng.NextDouble() * total;
            var pick = Members.Last(m => m.Weight > 0);
            foreach (var m in Members)
            {
                if (!(m.Weight > 0)) continue;
                if (roll < m.Weight) { pick = m; break; }
                roll -= m.Weight;
            }
            ids.Add(pick.Id);
        }
        return ids;
    }
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
    /// <summary>Herds of several species (<see cref="MixedHerd"/>): their members are animals of animals.json, placed apart from <see cref="Species"/>.</summary>
    public List<MixedHerd> Herds { get; set; } = new();
    /// <summary>Rare wild species (<see cref="RareSpecies"/>): not zone species; they count toward <see cref="MaxAnimals"/> and the entity cap.</summary>
    public List<RareSpecies> Rare { get; set; } = new();
    /// <summary>The Phoenix, a rare Mystery animal of the night sky (<see cref="PhoenixRules"/>); null: none. Counts toward <see cref="MaxAnimals"/> and the entity cap.</summary>
    public PhoenixRules? Phoenix { get; set; }
    /// <summary>Bird flocks (flying boids); not animals: they are not in animals.json and cannot be shot. Flock entities count towards the world's entity cap.</summary>
    public List<FlockSpecies> Flocks { get; set; } = new();

    public static WildlifeFile Load(string path) => Validate(DataFile.Load<WildlifeFile>(path));

    public static WildlifeFile Parse(string json) => Validate(DataFile.Parse<WildlifeFile>(json, "wildlife.json"));

    const string F = "wildlife.json";

    public static WildlifeFile Validate(WildlifeFile f)
    {
        if (f.MaxAnimals is < 0 or > MaxAnimalsLimit) throw new DataFileException($"{F}: max_animals is 0..{MaxAnimalsLimit} (wildlife, pets and their poop share the world's entity cap)");
        var seen = ValidateSpecies(f);
        var seenRare = ValidateRare(f, seen);
        ValidatePhoenix(f, seen, seenRare);
        ValidateHerds(f);
        ValidateFlocks(f, seen, seenRare);
        if (f.Flocks.Sum(k => k.Flocks) + f.MaxAnimals > MaxAnimalsLimit + 20) throw new DataFileException($"{F}: animals and flocks together exceed the entity budget");
        return f;
    }

    /// <summary>Species ids are one namespace with rare species and flocks; returns the species ids.</summary>
    static HashSet<string> ValidateSpecies(WildlifeFile f)
    {
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
        return seen;
    }

    static HashSet<string> ValidateRare(WildlifeFile f, HashSet<string> seen)
    {
        var seenRare = new HashSet<string>();
        foreach (var r in f.Rare)
        {
            if (r is null || r.Id == "") throw new DataFileException($"{F}: a rare species has no id");
            if (!seenRare.Add(r.Id) || seen.Contains(r.Id)) throw new DataFileException($"{F}: rare species {r.Id}: the id is used twice (rare and species are one namespace)");
            if (r.Count is < 1 or > 10) throw new DataFileException($"{F}: rare species {r.Id}: count is 1..10");
            if (!(r.MinBuildingM >= 0) || !(r.RespawnSeconds > 0)) throw new DataFileException($"{F}: rare species {r.Id}: min_building_m must not be negative and respawn_seconds must be positive");
        }
        return seenRare;
    }

    static void ValidatePhoenix(WildlifeFile f, HashSet<string> seen, HashSet<string> seenRare)
    {
        if (f.Phoenix is { } px)
        {
            const string P = F + ": phoenix: ";
            if (px.Id == "" || seen.Contains(px.Id) || seenRare.Contains(px.Id)) throw new DataFileException(P + "id is empty or used twice (phoenix, rare and species are one namespace)");
            if (px.NightsBetween < 1 || !(px.SpawnHour >= 0 && px.SpawnHour < 24) || !(px.DawnHour >= 0 && px.DawnHour < 24)) throw new DataFileException(P + "nights_between is at least 1, spawn_hour and dawn_hour are 0..24");
            if (!(px.AltitudeMinM > 0) || px.AltitudeMaxM < px.AltitudeMinM) throw new DataFileException(P + "altitude_min_m must be positive and not above altitude_max_m");
            if (!(px.PatrolRadiusM >= 20) || !(px.SpeedMps > 0) || !(px.FallSeconds > 0) || !(px.BurnSeconds > 0)) throw new DataFileException(P + "patrol_radius_m is at least 20; speed_mps, fall_seconds and burn_seconds must be positive");
            if (!(px.CrashMinM >= 0) || px.CrashMaxM < px.CrashMinM || !(px.CrashClearM > 0) || !(px.CrashMaxSlopeM >= 0) || !(px.CrashBuildingM >= 0)) throw new DataFileException(P + "crash_min_m is not negative and not above crash_max_m; crash_clear_m must be positive; crash_max_slope_m and crash_building_m not negative");
            if (Minerals.Find(px.Drops) == null) throw new DataFileException(P + $"drops {px.Drops} is not a mineral (Minerals.All)");
        }
    }

    static void ValidateHerds(WildlifeFile f)
    {
        var seenHerds = new HashSet<string>();
        foreach (var m in f.Herds)
        {
            if (m is null || m.Id == "") throw new DataFileException($"{F}: a herd has no id");
            var h = $"{F}: herd {m.Id}";
            if (!seenHerds.Add(m.Id)) throw new DataFileException($"{h} appears twice");
            if (m.Count < 1) throw new DataFileException($"{h}: count must be at least 1");
            if (m.Size is null || m.Size.Min < 2 || m.Size.Max < m.Size.Min) throw new DataFileException($"{h}: size min is at least 2 and not above max");
            if (!(m.SpreadM > 0) || !(m.RangeM >= 0)) throw new DataFileException($"{h}: spread_m must be positive and range_m not negative");
            if (!(m.MoveMinSeconds > 0) || m.MoveMaxSeconds < m.MoveMinSeconds) throw new DataFileException($"{h}: move_min_seconds must be positive and not above move_max_seconds");
            if (m.Members.Count < 2) throw new DataFileException($"{h}: members lists at least 2 animals (one species is a species entry with a herd)");
            var ids = new HashSet<string>();
            var counted = 0;
            foreach (var k in m.Members)
            {
                if (k is null || k.Id == "") throw new DataFileException($"{h}: a member has no id");
                if (!ids.Add(k.Id)) throw new DataFileException($"{h}: member {k.Id} appears twice");
                if (k.Min < 0 || k.Max < k.Min) throw new DataFileException($"{h}: member {k.Id}: min is not negative and not above max");
                if (k.Weight < 0 || float.IsNaN(k.Weight)) throw new DataFileException($"{h}: member {k.Id}: weight must be positive");
                if (k.Max <= 0 && !(k.Weight > 0)) throw new DataFileException($"{h}: member {k.Id}: needs a max (a count) or a weight");
                counted += k.Min;
            }
            if (!m.Members.Any(k => k.Weight > 0)) throw new DataFileException($"{h}: at least one member needs a weight (it fills the herd)");
            if (counted >= m.Size.Min) throw new DataFileException($"{h}: the members' min counts ({counted}) must be below size min ({m.Size.Min})");
        }
    }

    static void ValidateFlocks(WildlifeFile f, HashSet<string> seen, HashSet<string> seenRare)
    {
        var seenFlocks = new HashSet<string>();
        foreach (var k in f.Flocks)
        {
            if (k is null || k.Id == "") throw new DataFileException($"{F}: a flock has no id");
            if (!seenFlocks.Add(k.Id) || seen.Contains(k.Id) || seenRare.Contains(k.Id)) throw new DataFileException($"{F}: flock {k.Id}: the id is used twice (flocks and species share one namespace: it is the habitat name)");
            if (k.Flocks < 1 || k.Flocks > 20) throw new DataFileException($"{F}: flock {k.Id}: flocks is 1..20");
            if (k.BirdsMin < 2 || k.BirdsMax < k.BirdsMin || k.BirdsMax > 120) throw new DataFileException($"{F}: flock {k.Id}: birds_min is at least 2, not above birds_max, and birds_max is at most 120");
            if (!(k.AltitudeMinM >= 0) || k.AltitudeMaxM < k.AltitudeMinM) throw new DataFileException($"{F}: flock {k.Id}: altitude_min_m is not negative and not above altitude_max_m");
            if (!(k.RangeM >= 0) || !(k.SpeedMps > 0) || !(k.TurnRateDegPerS > 0) || !(k.ScatterRadiusM >= 0) || !(k.ScatterSeconds > 0) || !(k.Scale > 0)) throw new DataFileException($"{F}: flock {k.Id}: range_m, speed_mps, turn_rate_deg_per_s, scatter_radius_m, scatter_seconds and scale must be positive");
            var b = k.Boids;
            if (!(b.NeighbourM > 0) || !(b.SeparationM > 0) || !(b.MinSpeed > 0) || b.MaxSpeed < b.MinSpeed || !(b.MaxAccel > 0) || !(b.BandM >= 0) || !(b.LeashM > 0) || !(b.PanicSpeedFactor >= 1) || !(b.AvoidMarginM >= 0) || !(b.AvoidAccelFactor > 0) || !(b.AvoidHorizonS > 0))
                throw new DataFileException($"{F}: flock {k.Id}: boids: neighbour_m, separation_m, min_speed, max_accel and leash_m must be positive, max_speed not below min_speed, panic_speed_factor at least 1");
        }
    }

    /// <summary>Checks that every species and herd member id is a wild species of <paramref name="animals"/> (one that flees: a pet lives in a Pen, not in a zone).</summary>
    public WildlifeFile CheckAgainst(AnimalsFile animals)
    {
        foreach (var s in Species)
        {
            if (animals.Find(s.Id) is not { } def) throw new DataFileException($"wildlife.json: species {s.Id} is not in animals.json");
            if (!def.Flees) throw new DataFileException($"wildlife.json: species {s.Id} is a pet (flees is off): pets.json places those");
        }
        foreach (var r in Rare)
        {
            if (animals.Find(r.Id) is not { } def) throw new DataFileException($"wildlife.json: rare species {r.Id} is not in animals.json");
            if (!def.Flees) throw new DataFileException($"wildlife.json: rare species {r.Id} is a pet (flees is off): pets.json places those");
            if (Herds.Any(h => h.Members.Any(k => k.Id == r.Id))) throw new DataFileException($"wildlife.json: rare species {r.Id} is also a herd member");
        }
        if (Phoenix is { } px)
        {
            if (animals.Find(px.Id) is not { } def) throw new DataFileException($"wildlife.json: phoenix {px.Id} is not in animals.json");
            if (!def.Flies) throw new DataFileException($"wildlife.json: phoenix {px.Id} has no \"flies\": true in animals.json");
        }
        foreach (var herd in Herds)
            foreach (var k in herd.Members)
            {
                if (animals.Find(k.Id) is not { } def) throw new DataFileException($"wildlife.json: herd {herd.Id}: member {k.Id} is not in animals.json");
                if (!def.Flees) throw new DataFileException($"wildlife.json: herd {herd.Id}: member {k.Id} is a pet (flees is off): pets.json places those");
            }
        return this;
    }
}
