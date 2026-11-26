using System.Text.Json;
using Drycastle.ShooterCore.Data;

namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>
/// house_destruction.json (docs/house-destruction.md Contract 5): carve radii by blast source, the cell materials, support, fracture and debris.
/// Optional: a data directory without the file gets these defaults. Materials are turned into integers once (resist in 1/16 units), so the
/// carve and support maths that use them stay integer and the same on every machine.
/// </summary>
public sealed record DestructionConfig : IVersioned
{
    public int Version { get; init; } = 1;
    /// <summary>Off: no house is ever carved (the server sends no ops).</summary>
    public bool Enabled { get; init; } = true;
    public CarveSection Carve { get; init; } = new();
    /// <summary>Keyed by <see cref="CellMaterial"/> in snake_case (foundation, floor, wall, partition, ceiling, roof).</summary>
    public Dictionary<string, MaterialDef> Materials { get; init; } = DefaultMaterials();
    public SupportSection Support { get; init; } = new();
    public FractureConfig Fracture { get; init; } = new();
    public DebrisConfig Debris { get; init; } = new();

    int[] _resist = [], _weight = [], _strength = [], _span = [];

    /// <summary>Resistance of <paramref name="m"/> in 1/16 units (16 = 1.0); 0 = never carved. Air and foundation are 0.</summary>
    public int Resist(CellMaterial m) => _resist[(int)m];
    public int Weight(CellMaterial m) => _weight[(int)m];
    /// <summary>Load a cell of <paramref name="m"/> carries before it crumbles; 0 = no limit.</summary>
    public int Strength(CellMaterial m) => _strength[(int)m];
    /// <summary>Cells sideways from the nearest vertical support a cell of <paramref name="m"/> may reach before it crumbles; 0 = off.</summary>
    public int Span(CellMaterial m) => _span[(int)m];

    /// <summary>Carve radius in metres of a blast: by_source[source], else splash radius * default_share; 0 = no carve.</summary>
    public float CarveRadius(BlastSource source, float splashRadius) =>
        Carve.BySource.TryGetValue(SourceKey(source), out var r) ? r : MathF.Max(0, splashRadius) * Carve.DefaultShare;

    /// <summary>The by_source key of <paramref name="s"/> (snake_case: gunship_rocket).</summary>
    public static string SourceKey(BlastSource s) => JsonNamingPolicy.SnakeCaseLower.ConvertName(s.ToString());
    public static string MaterialKey(CellMaterial m) => JsonNamingPolicy.SnakeCaseLower.ConvertName(m.ToString());

    public static DestructionConfig Default => new DestructionConfig().Validate();

    public static DestructionConfig Parse(string json) => DataFile.Parse<DestructionConfig>(json, "house_destruction.json").Validate();

    public static DestructionConfig Load(string path) => DataFile.Load<DestructionConfig>(path).Validate();

    static Dictionary<string, MaterialDef> DefaultMaterials() => new()
    {
        ["foundation"] = new() { Resist = 0, Weight = 0, Strength = 0 },
        ["floor"] = new() { Resist = 1.2f, Weight = 3, Strength = 60000 },
        ["wall"] = new() { Resist = 1.0f, Weight = 4, Strength = 1500 },
        ["partition"] = new() { Resist = 0.6f, Weight = 2, Strength = 800 },
        ["ceiling"] = new() { Resist = 0.9f, Weight = 2, Strength = 2500 },
        ["roof"] = new() { Resist = 0.5f, Weight = 1, Strength = 400 },
    };

    DestructionConfig Validate()
    {
        const string f = "house_destruction.json";
        if (!(Carve.DefaultShare >= 0)) throw new DataFileException($"{f}: carve.default_share must not be negative");
        var sources = Enum.GetValues<BlastSource>().Where(s => s != BlastSource.Other).Select(SourceKey).ToHashSet();
        foreach (var (k, r) in Carve.BySource)
        {
            if (!sources.Contains(k)) throw new DataFileException($"{f}: carve.by_source has no source {k} (known: {string.Join(", ", sources)})");
            if (!(r >= 0)) throw new DataFileException($"{f}: carve.by_source.{k} must not be negative");
        }
        var mats = Enum.GetValues<CellMaterial>();
        foreach (var k in Materials.Keys)
            if (k == "air" || !mats.Any(m => MaterialKey(m) == k)) throw new DataFileException($"{f}: materials has no material {k}");
        _resist = new int[mats.Length]; _weight = new int[mats.Length]; _strength = new int[mats.Length]; _span = new int[mats.Length];
        foreach (var m in mats)
        {
            if (m == CellMaterial.Air) continue;
            var key = MaterialKey(m);
            if (!Materials.TryGetValue(key, out var d)) throw new DataFileException($"{f}: materials.{key} is missing");
            if (!(d.Resist >= 0) || d.Resist > 64) throw new DataFileException($"{f}: materials.{key}.resist must be 0..64");
            if (d.Weight < 0 || d.Strength < 0 || d.Span < 0) throw new DataFileException($"{f}: materials.{key} weight, strength and span must not be negative");
            _resist[(int)m] = m == CellMaterial.Foundation ? 0 : (int)MathF.Round(d.Resist * 16);
            _weight[(int)m] = d.Weight;
            _strength[(int)m] = d.Strength;
            _span[(int)m] = d.Span ?? Support.SpanCells;
        }
        if (Support.MaxPasses < 1) throw new DataFileException($"{f}: support.max_passes must be at least 1");
        if (Support.SpanCells < 0) throw new DataFileException($"{f}: support.span_cells must not be negative");
        if (Fracture.CellsPerPiece < 1) throw new DataFileException($"{f}: fracture.cells_per_piece must be at least 1");
        if (!(Fracture.ImpactBias >= 0 && Fracture.ImpactBias <= 1)) throw new DataFileException($"{f}: fracture.impact_bias must be 0..1");
        if (Fracture.DustBelowCells < 0) throw new DataFileException($"{f}: fracture.dust_below_cells must not be negative");
        if (Debris.FadeAfterS < 0 || Debris.FadeS < 0 || Debris.MaxBodies < 0 || Debris.MaxDistanceM < 0 || Debris.BreakImpulse < 0 || Debris.StayCapCells < 0)
            throw new DataFileException($"{f}: debris numbers must not be negative");
        return this;
    }
}

/// <summary>house_destruction.json <c>carve</c>.</summary>
public sealed record CarveSection
{
    /// <summary>Carve radius as a share of the splash radius, for a source without its own entry.</summary>
    public float DefaultShare { get; init; } = 0.2f;
    /// <summary>Carve radius in metres by <see cref="BlastSource"/> key; 0 = no carve.</summary>
    public Dictionary<string, float> BySource { get; init; } = new()
    {
        ["rpg"] = 2.0f, ["grenade"] = 1.5f, ["cannon"] = 0.6f, ["flak"] = 0.0f, ["gunship_rocket"] = 1.6f, ["gunship_cannon"] = 2.5f,
        ["missile"] = 3.0f, ["car"] = 1.5f, ["scud"] = 10.0f, ["moab"] = 14.0f, ["demon"] = 0.0f,
    };
}

/// <summary>One entry of house_destruction.json <c>materials</c>.</summary>
public sealed record MaterialDef
{
    /// <summary>Resistance to a carve (1.0 = the sphere's radius as is); 0 = never carved. Turned into 1/16 units.</summary>
    public float Resist { get; init; } = 1;
    public int Weight { get; init; }
    /// <summary>Load before the cell crumbles; 0 = no limit.</summary>
    public int Strength { get; init; }
    /// <summary>Optional span rule of this material (cells); absent = support.span_cells.</summary>
    public int? Span { get; init; }
}

/// <summary>house_destruction.json <c>support</c>.</summary>
public sealed record SupportSection
{
    public int MaxPasses { get; init; } = 4;
    /// <summary>The span rule for every material without its own; 0 = off.</summary>
    public int SpanCells { get; init; }
}

/// <summary>house_destruction.json <c>fracture</c> (<see cref="Destruction.Fracture.Split"/>).</summary>
public sealed record FractureConfig
{
    public int CellsPerPiece { get; init; } = 24;
    /// <summary>0..1: how much closer the seeds pack near the impact.</summary>
    public float ImpactBias { get; init; } = 0.6f;
    /// <summary>Pieces under this many cells go to the last group, dust.</summary>
    public int DustBelowCells { get; init; } = 4;
}

/// <summary>The rubble of fallen pieces (D8): sink away, or stay as frozen piles for the round.</summary>
public enum RubbleMode { Fade, Stay }

/// <summary>house_destruction.json <c>debris</c>: the client's cosmetic debris (WP-G).</summary>
public sealed record DebrisConfig
{
    public RubbleMode Rubble { get; init; } = RubbleMode.Fade;
    public float FadeAfterS { get; init; } = 15;
    public float FadeS { get; init; } = 2;
    public int MaxBodies { get; init; } = 64;
    public float MaxDistanceM { get; init; } = 150;
    public float BreakImpulse { get; init; } = 4;
    public bool SecondBreak { get; init; } = true;
    public int StayCapCells { get; init; } = 20000;
}
