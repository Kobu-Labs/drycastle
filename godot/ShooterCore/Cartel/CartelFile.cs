using System.Text.Json;
using Drycastle.ShooterCore.Data;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// cartel.json (docs/cartel.md, ADR 0046): the Cartel ranks and the Respect each needs, the job tiers (the rank that unlocks each and the Respect it pays), and one section per
/// kind of Job under <c>jobs</c>, read by that kind (<see cref="DataFile.Section{T}"/>). The defaults here are the shipped values, so a data directory without the file still plays.
/// Parsed and checked once; a bad field throws with the file and the field named. Nothing in it was played: first guesses.
/// </summary>
public sealed class CartelFile : IVersioned
{
    public int Version { get; set; } = 1;

    /// <summary>The ladder, lowest first. The first needs 0 Respect; each next one more than the one before.</summary>
    public List<CartelRankDef> Ranks { get; set; } = new()
    {
        new() { Id = "runner", Name = "Runner", Respect = 0 },
        new() { Id = "dealer", Name = "Dealer", Respect = 1000 },
        new() { Id = "sicario", Name = "Sicario", Respect = 3000 },
        new() { Id = "capo", Name = "Capo", Respect = 7000 },
        new() { Id = "patron", Name = "Patrón", Respect = 15000 },
    };

    /// <summary>The job tiers, 1 first and numbered without a gap. A tier is open to a leader of its rank or higher; it pays its Respect to every earning member of the party.</summary>
    public List<CartelTierDef> Tiers { get; set; } = new()
    {
        new() { Tier = 1, Rank = "runner", Respect = 100 },
        new() { Tier = 2, Rank = "dealer", Respect = 200 },
        new() { Tier = 3, Rank = "sicario", Respect = 350 },
        new() { Tier = 4, Rank = "capo", Respect = 550 },
        new() { Tier = 5, Rank = "patron", Respect = 800 },
    };

    /// <summary>How near the Contact (ground metres) a player must stand to join, quit or start a Job.</summary>
    public float ContactReachM { get; set; } = 3f;

    /// <summary>How far (metres) a Job's marker must move before its party is sent the new point (a marker on something that moves is not sent every tick).</summary>
    public float MarkerResendM { get; set; } = 2f;

    /// <summary>The Cartel pallet's bag spots and look (<c>pallet</c>); every Delivery's Duffel lies on one of its spots.</summary>
    public CartelPalletData Pallet { get; set; } = new();

    /// <summary>The Heists' drops (<c>receivers</c>): which Receiver a Heist hands its Duffels to, and how each looks, holds bags and leaves (docs/cartel.md "Receivers").</summary>
    public ReceiversData Receivers { get; set; } = new();

    /// <summary>How the Rival crew's vehicles (Assault vans, chase cars) cross open ground when the roads do not bring them closer (<c>off_road</c>; docs/cartel.md "Assaults").</summary>
    public OffRoadData OffRoad { get; set; } = new();

    /// <summary>The Assault van (<c>assault_car</c>; docs/cartel.md "Assaults"): its vehicle and skin, crew posts in the bay, where it is put down, when it is stalled and when it gives up. It drives on each Heist's <c>chase</c> numbers.</summary>
    public AssaultCarData AssaultCar { get; set; } = new();

    /// <summary>The "Job done" cinematic (<c>job_done_cinematic</c>; docs/cartel.md "Job done"): the camera's stages and legs, the flash, the music's fade, the skip.</summary>
    public JobDoneCinematicData JobDoneCinematic { get; set; } = new();

    /// <summary>One section per kind of Job, by the kind's id (<c>test</c>, later <c>delivery</c>, the heists): only that kind reads it.</summary>
    public Dictionary<string, JsonElement> Jobs { get; set; } = new();

    public static CartelFile Parse(string json) => DataFile.Parse<CartelFile>(json, "cartel.json").Validate();

    public static CartelFile Load(string path) => DataFile.Load<CartelFile>(path).Validate();

    /// <summary>The shipped numbers (the defaults of every field), checked.</summary>
    public static CartelFile Default => new CartelFile().Validate();

    /// <summary>The rank a player with this much Respect holds (an index of <see cref="Ranks"/>).</summary>
    public int RankFor(int respect)
    {
        var rank = 0;
        for (var i = 1; i < Ranks.Count; i++) if (respect >= Ranks[i].Respect) rank = i;
        return rank;
    }

    /// <summary>The index of a rank by id, or -1.</summary>
    public int RankIndex(string id) => Ranks.FindIndex(r => r.Id == id);

    /// <summary>The tier of that number, or null.</summary>
    public CartelTierDef? Tier(int tier) => tier >= 1 && tier <= Tiers.Count ? Tiers[tier - 1] : null;

    /// <summary>The tier is open to a leader of rank <paramref name="rank"/> (false for an unknown tier).</summary>
    public bool TierOpen(int tier, int rank) => Tier(tier) is { } t && RankIndex(t.Rank) <= rank;

    /// <summary>The highest tier open to that rank (at least 1: the first tier needs the first rank).</summary>
    public int HighestTier(int rank)
    {
        var best = 1;
        for (var t = 1; t <= Tiers.Count; t++) if (TierOpen(t, rank)) best = t;
        return best;
    }

    /// <summary>The Respect a job of that tier pays each earning member (0 for an unknown tier).</summary>
    public int RespectFor(int tier) => Tier(tier)?.Respect ?? 0;

    /// <summary>A kind's own section, or an undefined element when the file has none (the kind's defaults).</summary>
    public JsonElement JobSection(string kind) => Jobs.TryGetValue(kind, out var s) ? s : default;

    /// <summary>
    /// The top speed (m/s) of a Cartel van that has a Duffel loaded: <c>loaded_max_kmh</c> of the Heist whose <c>van_type</c> is this vehicle type (the slowest, should two name it), or null for a type no Heist
    /// uses as its van. The server and the driver's client both ask this, so they hold the same cap (<see cref="Vehicles.SpeedGovernor"/>).
    /// </summary>
    public float? LoadedCapMps(string vehicleType)
    {
        if (_caps.TryGetValue(vehicleType, out var known)) return known;
        float? cap = null;
        foreach (var kind in new[] { HarvestData.KindId, TrapHouseHeistData.KindId }) // the Heists; a section the file lacks is the kind's defaults
        {
            var data = DataFile.Section<HeistData>(JobSection(kind), $"cartel.json: jobs.{kind}");
            if (data.VanType != vehicleType) continue;
            var mps = data.LoadedMaxKmh / 3.6f;
            cap = cap is { } c ? MathF.Min(c, mps) : mps;
        }
        return _caps[vehicleType] = cap;
    }

    readonly Dictionary<string, float?> _caps = new(); // the loaded cap by vehicle type, read from the sections once

    CartelFile Validate()
    {
        static DataFileException Bad(string text) => new($"cartel.json: {text}");
        if (Ranks.Count == 0) throw Bad("ranks must list at least one rank");
        if (!(ContactReachM > 0)) throw Bad("contact_reach_m must be more than 0");
        if (!(MarkerResendM >= 0) || !float.IsFinite(MarkerResendM)) throw Bad("marker_resend_m must be 0 or more");
        if (Ranks[0].Respect != 0) throw Bad("the first rank's respect must be 0");
        var ids = new HashSet<string>();
        for (var i = 0; i < Ranks.Count; i++)
        {
            var r = Ranks[i];
            if (string.IsNullOrWhiteSpace(r.Id) || string.IsNullOrWhiteSpace(r.Name)) throw Bad($"ranks[{i}] needs an id and a name");
            if (!ids.Add(r.Id)) throw Bad($"ranks[{i}]: the id '{r.Id}' is used twice");
            if (i > 0 && r.Respect <= Ranks[i - 1].Respect) throw Bad($"ranks[{i}] ({r.Id}): respect must be more than the rank before's");
        }
        Pallet.Validate();
        Receivers.Validate();
        OffRoad.Validate("cartel.json: off_road");
        AssaultCar.Validate("cartel.json: assault_car");
        JobDoneCinematic.Validate();
        if (Tiers.Count == 0) throw Bad("tiers must list at least one tier");
        if (Tiers.Count > byte.MaxValue) throw Bad("at most 255 tiers");
        for (var i = 0; i < Tiers.Count; i++)
        {
            var t = Tiers[i];
            if (t.Tier != i + 1) throw Bad($"tiers[{i}]: tier must be {i + 1} (numbered from 1 without a gap)");
            var rank = RankIndex(t.Rank);
            if (rank < 0) throw Bad($"tier {t.Tier}: unknown rank '{t.Rank}'");
            if (i == 0 && rank != 0) throw Bad("tier 1 must be open to the first rank");
            if (i > 0 && rank < RankIndex(Tiers[i - 1].Rank)) throw Bad($"tier {t.Tier}: its rank must not be lower than tier {t.Tier - 1}'s");
            if (t.Respect <= 0) throw Bad($"tier {t.Tier}: respect must be more than 0");
        }
        return this;
    }
}

/// <summary>One Cartel rank: its id (a stable key), the name players see, and the Respect it needs.</summary>
public sealed class CartelRankDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Respect { get; set; }
}

/// <summary>One job tier: its number, the rank (id) a leader needs to start it, and the Respect it pays.</summary>
public sealed class CartelTierDef
{
    public int Tier { get; set; }
    public string Rank { get; set; } = "";
    public int Respect { get; set; }
}
