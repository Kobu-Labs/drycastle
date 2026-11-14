namespace Drycastle.ShooterCore;

/// <summary>
/// The <c>loot</c> section of thieves.json (GLOSSARY "Loot"): how the Thieves go about stealing and how Loot is sent back, then one sub-section per kind. A kind's sub-section is its own: the ticket
/// that adds the kind edits only its class (in the kind's own file, <see cref="TireLootConfig"/>) and its part of the json. Lengths are metres, speeds metres per second, times seconds.
/// </summary>
public sealed class LootConfig
{
    /// <summary>The most Loot lying on the ground at once; over it the oldest goes back to its place by itself.</summary>
    public int MaxLying { get; set; } = 24;
    /// <summary>A player holds the use key this long, within <see cref="ReachM"/> of lying Loot, to send it back. Moving off or taking a hit starts it over.</summary>
    public float ReturnSeconds { get; set; } = 1f;
    public float ReachM { get; set; } = 1.8f;

    /// <summary>A Thief looks for a target within this of the van's door, picks the nearest of a kind chosen by weight, and asks again after <see cref="SeekRetrySeconds"/> when nothing is free.</summary>
    public float SeekRadiusM { get; set; } = 90f;
    public float SeekRetrySeconds { get; set; } = 2f;
    /// <summary>Speed on the way to a target and (faster, carrying it) to the van's door.</summary>
    public float ApproachSpeedMps { get; set; } = 3f;
    public float HaulSpeedMps { get; set; } = 4.5f;
    /// <summary>A Thief that came this near its work spot starts working; one that is not there after <see cref="ApproachGiveUpSeconds"/> (or cannot find a route) gives the target up and will not choose it for <see cref="AvoidSeconds"/>.</summary>
    public float ArriveM { get; set; } = 1f;
    public float ApproachGiveUpSeconds { get; set; } = 45f;
    public float AvoidSeconds { get; set; } = 60f;
    /// <summary>A Thief hauling gives up (drops the Loot where it stands) when it has not reached the door after this long.</summary>
    public float HaulGiveUpSeconds { get; set; } = 60f;
    /// <summary>Taking up Loot that lies on the ground; tossing Loot into the van, within <see cref="LoadReachM"/> of its door.</summary>
    public float PickupSeconds { get; set; } = 1f;
    public float LoadSeconds { get; set; } = 0.6f;
    public float LoadReachM { get; set; } = 1.5f;
    /// <summary>How fast a Thief climbs (up and down, straight along the climb from its base to its top) for a target that needs a climb. Falls at 9.81 m/s2 when killed aloft.</summary>
    public float ClimbSpeed { get; set; } = 1.5f;

    public TireLootConfig Tire { get; set; } = new();
    public PetLootConfig Pet { get; set; } = new();
    public DishLootConfig Dish { get; set; } = new();
    public ConnectorLootConfig Connector { get; set; } = new();

    public LootKindConfig Of(LootKind kind) => kind switch
    {
        LootKind.Tire => Tire,
        LootKind.Pet => Pet,
        LootKind.Dish => Dish,
        _ => Connector,
    };

    public void Validate(string file)
    {
        void Positive(float v, string what) { if (!(v > 0) || !float.IsFinite(v)) throw new DataFileException($"{file}: loot.{what} must be positive"); }
        if (MaxLying < 1) throw new DataFileException($"{file}: loot.max_lying is at least 1");
        Positive(ReturnSeconds, "return_seconds"); Positive(ReachM, "reach_m"); Positive(SeekRadiusM, "seek_radius_m"); Positive(SeekRetrySeconds, "seek_retry_seconds");
        Positive(ApproachSpeedMps, "approach_speed_mps"); Positive(HaulSpeedMps, "haul_speed_mps"); Positive(ArriveM, "arrive_m"); Positive(ApproachGiveUpSeconds, "approach_give_up_seconds");
        Positive(AvoidSeconds, "avoid_seconds"); Positive(HaulGiveUpSeconds, "haul_give_up_seconds"); Positive(PickupSeconds, "pickup_seconds"); Positive(LoadSeconds, "load_seconds");
        Positive(LoadReachM, "load_reach_m"); Positive(ClimbSpeed, "climb_speed");
        foreach (var k in LootKinds.All) Of(k).Validate(file, k.ToString().ToLowerInvariant());
    }
}

/// <summary>What every kind of Loot has: how likely a Thief is to choose it (a kind with weight 0 is never chosen) and how long it works at a thing of that kind before it has it.</summary>
public class LootKindConfig
{
    public float Weight { get; set; } = 1f;
    public float StealSeconds { get; set; } = 5f;

    /// <summary>Checks the numbers (a kind with its own numbers overrides this, calls the base first, and so keeps its checks in its own file).</summary>
    public virtual void Validate(string file, string kind)
    {
        if (!(Weight >= 0) || !float.IsFinite(Weight)) throw new DataFileException($"{file}: loot.{kind}.weight must not be negative");
        if (!(StealSeconds > 0) || !float.IsFinite(StealSeconds)) throw new DataFileException($"{file}: loot.{kind}.steal_seconds must be positive");
    }
}
