using System.Text.Json;

namespace Drycastle.ShooterCore;

/// <summary>
/// One Companion a player can own (companions.json): which animal it looks like and which kind runs it. What it does is the kind's own (<see cref="CompanionKinds"/>): the
/// <see cref="Rules"/> object is read by the kind's class and by nobody else.
/// </summary>
public sealed class CompanionDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The kind that runs it: a name registered in <see cref="CompanionKinds"/> ("heel", "wing").</summary>
    public string Kind { get; set; } = "";
    /// <summary>A species id of animals.json: the model and its clips.</summary>
    public string Species { get; set; } = "";
    /// <summary>A variant id of the species (another model of it); empty or <see cref="AnimalVariant.BaseAssetId"/> for the species' own model.</summary>
    public string Asset { get; set; } = "";
    /// <summary>How big it is drawn, times the species' model as animals.json sizes it (a small bird of a big one). Looks only.</summary>
    public float Scale { get; set; } = 1;
    /// <summary>"bronze", "silver" or "gold": the Hunter's Log collection tier that unlocks it (an unlock row of kind <see cref="CompanionRules.UnlockKind"/>); empty: no tier gives it.</summary>
    public string RewardTier { get; set; } = "";
    public HuntTier RewardTierOf => RewardTier switch { "bronze" => HuntTier.Bronze, "silver" => HuntTier.Silver, "gold" => HuntTier.Gold, _ => HuntTier.Common };
    /// <summary>The kind's own numbers, read by the kind (<see cref="DataFile.Section{T}"/>). Absent: the kind's defaults.</summary>
    public JsonElement Rules { get; set; }

    /// <summary>The variant index of <see cref="Asset"/> in <paramref name="def"/>: 0 for the base model, -1 for an unknown asset.</summary>
    public int VariantIn(AnimalDef def) => Asset == "" || Asset == AnimalVariant.BaseAssetId ? 0 : def.VariantIndex(Asset);
}

/// <summary>
/// companions.json, schema version 1: the Companions of the game (GLOSSARY "Companion"). The index of a Companion in the list is what the wire carries, so the server and
/// the clients must hold the same file. The default is empty: a game without the file has no Companions.
/// </summary>
public sealed class CompanionsFile : IVersioned
{
    public int Version { get; set; }
    public List<CompanionDef> Companions { get; set; } = new();

    public static CompanionsFile Load(string path) => Validate(DataFile.Load<CompanionsFile>(path));

    public static CompanionsFile Parse(string json) => Validate(DataFile.Parse<CompanionsFile>(json, "companions.json"));

    public int IndexOf(string id) => Companions.FindIndex(c => c.Id == id);

    public CompanionDef? Find(string id) => Companions.Find(c => c.Id == id);

    public static CompanionsFile Validate(CompanionsFile f)
    {
        const string F = "companions.json";
        if (f.Companions.Count > ushort.MaxValue) throw new DataFileException($"{F}: too many companions");
        var seen = new HashSet<string>();
        foreach (var c in f.Companions)
        {
            if (c is null || c.Id == "") throw new DataFileException($"{F}: a companion has no id");
            if (!seen.Add(c.Id)) throw new DataFileException($"{F}: companion {c.Id} appears twice");
            if (c.Species == "") throw new DataFileException($"{F}: {c.Id}: species is missing");
            if (!(c.Scale > 0)) throw new DataFileException($"{F}: {c.Id}: scale must be positive");
            if (c.RewardTier != "" && c.RewardTierOf == HuntTier.Common) throw new DataFileException($"{F}: {c.Id}: reward_tier is bronze, silver or gold");
            if (CompanionKinds.Find(c.Kind) is not { } kind) throw new DataFileException($"{F}: {c.Id}: kind '{c.Kind}' is not one of {string.Join(", ", CompanionKinds.Names)}");
            kind.Check(c); // the kind reads its own rules: a bad number fails here, at load
        }
        return f;
    }

    /// <summary>Checks that every species and asset is in <paramref name="animals"/> (loaded after both files).</summary>
    public CompanionsFile CheckAgainst(AnimalsFile animals)
    {
        foreach (var c in Companions)
        {
            if (animals.Find(c.Species) is not { } def) throw new DataFileException($"companions.json: {c.Id}: species {c.Species} is not in animals.json");
            if (c.VariantIn(def) < 0) throw new DataFileException($"companions.json: {c.Id}: asset {c.Asset} is not a variant of {c.Species} (or \"base\")");
        }
        return this;
    }
}
