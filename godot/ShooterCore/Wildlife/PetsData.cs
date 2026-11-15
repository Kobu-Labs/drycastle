namespace Drycastle.ShooterCore;

/// <summary>One species a pet can be (pets.json): an id of animals.json and its weight among the species.</summary>
public sealed class PetSpecies
{
    public string Id { get; set; } = "";
    public float Weight { get; set; } = 1;
}

/// <summary>
/// A pet a named property group keeps on top of the random ones (pets.json, "pinned"): each round, each of <see cref="Count"/> pets of
/// <see cref="Species"/> is there with <see cref="Chance"/>.
/// </summary>
public sealed class PinnedPet
{
    /// <summary>Property group id (G-NNNN).</summary>
    public string Group { get; set; } = "";
    /// <summary>A species id of animals.json; it need not be in the random species list.</summary>
    public string Species { get; set; } = "";
    /// <summary>A variant id of the species (animals.json, "variants"), or <see cref="AnimalVariant.BaseAssetId"/> for its base model (reserved: no variant has that id). Empty: a random look, as for any pet.</summary>
    public string Asset { get; set; } = "";
    /// <summary>The variant index of <see cref="Asset"/> in <paramref name="def"/>: null for a random look, -1 for an unknown asset.</summary>
    public int? VariantIn(AnimalDef def) => Asset == "" ? null : Asset == AnimalVariant.BaseAssetId ? 0 : def.VariantIndex(Asset);
    /// <summary>Chance, 0..1, rolled per pet each round.</summary>
    public float Chance { get; set; } = 1;
    public int Count { get; set; } = 1;
}

/// <summary>
/// pets.json, schema version 1: which property groups keep pets, how many, of which species, and the poop rules. Times in seconds.
/// The defaults are inert (no group has a chance), so a missing file means a world without pets.
/// </summary>
public sealed class PetsFile : IVersioned
{
    /// <summary>Hard ceiling of <see cref="MaxPets"/>: pets, their poop and every other world entity share the entity cap (<see cref="WorldEntityManager.MaxEntities"/>).</summary>
    public const int MaxPetsLimit = 100;

    public int Version { get; set; }
    /// <summary>Chance, 0..1, that a property group keeps pets this round.</summary>
    public float GroupChance { get; set; }
    /// <summary>Most pets in the whole map at once.</summary>
    public int MaxPets { get; set; }
    public int PetsPerGroupMin { get; set; } = 1;
    public int PetsPerGroupMax { get; set; } = 1;
    public List<PetSpecies> Species { get; set; } = new();
    /// <summary>Pets named groups keep besides the random ones. Rolled before the random groups, so the map cap never starves them; they count towards it.</summary>
    public List<PinnedPet> Pinned { get; set; } = new();
    public float PoopMinSeconds { get; set; } = 60;
    public float PoopMaxSeconds { get; set; } = 180;
    /// <summary>How long a poop lies before it is gone.</summary>
    public float PoopLifetimeSeconds { get; set; } = 300;
    /// <summary>Most poops one property group keeps; the next one removes the oldest.</summary>
    public int PoopMaxPerGroup { get; set; } = 6;

    public static PetsFile Load(string path) => Validate(DataFile.Load<PetsFile>(path));

    public static PetsFile Parse(string json) => Validate(DataFile.Parse<PetsFile>(json, "pets.json"));

    public static PetsFile Validate(PetsFile f)
    {
        const string F = "pets.json";
        if (f.GroupChance is < 0 or > 1 || float.IsNaN(f.GroupChance)) throw new DataFileException($"{F}: group_chance is 0..1");
        if (f.MaxPets is < 0 or > MaxPetsLimit) throw new DataFileException($"{F}: max_pets is 0..{MaxPetsLimit} (pets and their poop share the world's entity cap)");
        if (f.PetsPerGroupMin < 1 || f.PetsPerGroupMax < f.PetsPerGroupMin) throw new DataFileException($"{F}: pets_per_group_min must be at least 1 and not above pets_per_group_max");
        if (!(f.PoopMinSeconds > 0) || f.PoopMaxSeconds < f.PoopMinSeconds) throw new DataFileException($"{F}: poop_min_seconds must be positive and not above poop_max_seconds");
        if (!(f.PoopLifetimeSeconds > 0)) throw new DataFileException($"{F}: poop_lifetime_seconds must be positive");
        if (f.PoopMaxPerGroup is < 1 or > 20) throw new DataFileException($"{F}: poop_max_per_group is 1..20");
        if (f.GroupChance > 0 && f.MaxPets > 0 && f.Species.Count == 0) throw new DataFileException($"{F}: species is empty but pets are on");
        var seen = new HashSet<string>();
        foreach (var s in f.Species)
        {
            if (s is null || s.Id == "") throw new DataFileException($"{F}: a species has no id");
            if (!seen.Add(s.Id)) throw new DataFileException($"{F}: species {s.Id} appears twice");
            if (!(s.Weight > 0)) throw new DataFileException($"{F}: species {s.Id}: weight must be positive");
        }
        foreach (var p in f.Pinned)
        {
            if (p is null || p.Group == "" || p.Species == "") throw new DataFileException($"{F}: a pinned pet needs a group and a species");
            if (p.Chance is < 0 or > 1 || float.IsNaN(p.Chance)) throw new DataFileException($"{F}: pinned {p.Species} in {p.Group}: chance is 0..1");
            if (p.Count < 1) throw new DataFileException($"{F}: pinned {p.Species} in {p.Group}: count must be at least 1");
        }
        return f;
    }

    /// <summary>Checks that every species id is a species of <paramref name="animals"/> (loaded after both files).</summary>
    public PetsFile CheckAgainst(AnimalsFile animals)
    {
        foreach (var s in Species)
            if (animals.IndexOf(s.Id) < 0) throw new DataFileException($"pets.json: species {s.Id} is not in animals.json");
        foreach (var p in Pinned)
        {
            if (animals.Find(p.Species) is not { } def) throw new DataFileException($"pets.json: pinned species {p.Species} is not in animals.json");
            if (p.VariantIn(def) is < 0) throw new DataFileException($"pets.json: pinned {p.Species} in {p.Group}: asset {p.Asset} is not a variant of the species (or \"base\")");
        }
        return this;
    }

    /// <summary>The species index (animals.json order) of a weighted pick: <paramref name="roll"/> is 0..1.</summary>
    public int Pick(AnimalsFile animals, double roll)
    {
        var total = Species.Sum(s => s.Weight);
        var at = roll * total;
        foreach (var s in Species)
        {
            at -= s.Weight;
            if (at < 0) return animals.IndexOf(s.Id);
        }
        return animals.IndexOf(Species[^1].Id);
    }
}
