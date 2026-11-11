namespace Suchohrad.ShooterCore;

/// <summary>How a used killstreak is controlled: nothing (it acts on its own) or a point-of-view takeover (the owner steers an entity).</summary>
public enum KillstreakControl { None, Pov }

/// <summary>One killstreak from killstreaks.json (the table in mods/shooter/v2/killstreaks.md).</summary>
public sealed class KillstreakDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Streak counter value (kills in one life) at which the streak is earned. Hot Streak lowers it by one.</summary>
    public int KillsRequired { get; init; }
    /// <summary>XP level that unlocks the streak for loadouts. levels.json lists the same level.</summary>
    public int UnlockLevel { get; init; }
    /// <summary>Instances of this streak one owner may have active at once.</summary>
    public int MaxActive { get; init; } = 1;
    /// <summary>How long an active instance lasts. 0 = until the streak ends it or its owner dies.</summary>
    public double DurationSeconds { get; init; }
    public KillstreakControl Control { get; init; }
    /// <summary>
    /// Care package only: the weight of each perk id in the random roll (higher = more common). Empty for every other streak.
    /// The ids are checked against perks.json where the table is built (see <see cref="PerkTable"/>).
    /// </summary>
    public IReadOnlyDictionary<string, int> PerkWeights { get; init; } = new Dictionary<string, int>();
    /// <summary>The attack missile's tuning (its "missile" block); null for every other streak.</summary>
    public MissileTuning? Missile { get; init; }
    /// <summary>The helicopter helper's tuning (its "helper" block); null for every other streak.</summary>
    public HelicopterHelperTuning? Helper { get; init; }
}

/// <summary>
/// killstreaks.json, schema version 1: the streak table. Loaded through <see cref="DataFile"/>. levels.json checks its
/// streak unlock lines against <see cref="UnlockLevels"/>, and loadout validation uses the same map.
/// </summary>
public sealed class KillstreakFile : IVersioned
{
    /// <summary>Newest killstreaks.json version this code reads.</summary>
    public const int SupportedVersion = 1;

    public int Version { get; init; }
    public IReadOnlyList<KillstreakDef> Streaks { get; init; } = Array.Empty<KillstreakDef>();

    /// <summary>Unlock level of every streak, by id (the map levels.json and loadout validation use).</summary>
    public IReadOnlyDictionary<string, int> UnlockLevels => Streaks.ToDictionary(s => s.Id, s => s.UnlockLevel);

    /// <summary>The streak with this id, or null when the id is not in the file.</summary>
    public KillstreakDef? Find(string id) => Streaks.FirstOrDefault(s => s.Id == id);

    /// <summary>Aircraft kinds (killstreaks.json "aircraft"): how each circles the map. Empty when the file has none.</summary>
    public IReadOnlyList<AircraftKindDef> AircraftKinds { get; init; } = Array.Empty<AircraftKindDef>();
    /// <summary>The gunship turret and its weapons (killstreaks.json "gunship"), or null when the file has none.</summary>
    public GunshipDef? Gunship { get; init; }

    public static KillstreakFile Load(string path) => Resolve(DataFile.Load<KillstreakRaw>(path, SupportedVersion));

    public static KillstreakFile Parse(string json) => Resolve(DataFile.Parse<KillstreakRaw>(json, "killstreaks.json", SupportedVersion));

    static KillstreakFile Resolve(KillstreakRaw raw)
    {
        const string F = "killstreaks.json";
        if (raw.Streaks is null || raw.Streaks.Count == 0) throw new DataFileException($"{F}: no killstreaks");

        var streaks = new List<KillstreakDef>();
        var ids = new HashSet<string>();
        for (var i = 0; i < raw.Streaks.Count; i++)
        {
            var r = raw.Streaks[i];
            if (string.IsNullOrEmpty(r.Id)) throw new DataFileException($"{F}: streak #{i + 1}: field \"id\" is required");
            if (!ids.Add(r.Id)) throw new DataFileException($"{F}: duplicate streak id \"{r.Id}\"");
            var where = $"streak \"{r.Id}\"";
            if (string.IsNullOrWhiteSpace(r.Name)) throw new DataFileException($"{F}: {where}: field \"name\" is required");
            var kills = Need(r.KillsRequired, F, where, "kills_required", 1);
            var unlock = Need(r.UnlockLevel, F, where, "unlock_level", 0);
            var maxActive = r.MaxActive ?? 1;
            if (maxActive < 1) throw new DataFileException($"{F}: {where}: field \"max_active\" must be at least 1");
            var duration = r.DurationSeconds ?? 0;
            if (!double.IsFinite(duration) || duration < 0)
                throw new DataFileException($"{F}: {where}: field \"duration_seconds\" must be 0 or more");
            var control = r.Control switch
            {
                "none" => KillstreakControl.None,
                "pov" => KillstreakControl.Pov,
                _ => throw new DataFileException($"{F}: {where}: field \"control\" must be none or pov"),
            };
            var weights = new Dictionary<string, int>();
            foreach (var (perk, w) in r.PerkWeights ?? new())
            {
                if (string.IsNullOrEmpty(perk)) throw new DataFileException($"{F}: {where}: \"perk_weights\" has an empty perk id");
                if (w < 1) throw new DataFileException($"{F}: {where}: perk_weights \"{perk}\" must be 1 or more");
                weights[perk] = w;
            }
            if (r.Id == CarePackageRules.StreakId && weights.Count == 0)
                throw new DataFileException($"{F}: {where}: field \"perk_weights\" is required for the care package");
            var isMissile = r.Id == AttackMissileStreak.StreakId;
            if (!isMissile && r.Missile != null) throw new DataFileException($"{F}: {where}: a \"missile\" block is only for the attack missile");
            var isHelper = r.Id == HelicopterHelperStreak.StreakId;
            if (!isHelper && r.Helper != null) throw new DataFileException($"{F}: {where}: a \"helper\" block is only for the helicopter helper");
            streaks.Add(new KillstreakDef
            {
                Id = r.Id, Name = r.Name, KillsRequired = kills, UnlockLevel = unlock, MaxActive = maxActive,
                DurationSeconds = duration, Control = control, PerkWeights = weights,
                Missile = isMissile ? MissileTuning.From(r.Missile, F, where) : null,
                Helper = isHelper ? HelicopterHelperTuning.From(r.Helper, where) : null,
            });
        }

        // the starter kit carries this streak, so it must be open at level 0
        var starter = streaks.FirstOrDefault(s => s.Id == KillstreakIds.StarterStreak)
            ?? throw new DataFileException($"{F}: the starter streak \"{KillstreakIds.StarterStreak}\" is missing");
        if (starter.UnlockLevel != 0) throw new DataFileException($"{F}: the starter streak \"{starter.Id}\" must have unlock_level 0");

        var (kinds, gunship) = AircraftData.Resolve(raw.Aircraft, raw.Gunship);
        return new KillstreakFile { Version = raw.Version, Streaks = streaks, AircraftKinds = kinds, Gunship = gunship };
    }

    static int Need(int? value, string file, string where, string field, int min) =>
        value is { } v && v >= min ? v : throw new DataFileException($"{file}: {where}: field \"{field}\" is required and must be {min} or more");

    internal sealed class KillstreakRaw : IVersioned
    {
        public int Version { get; set; }
        public List<StreakRaw>? Streaks { get; set; }
        public List<AircraftData.AircraftRaw>? Aircraft { get; set; }
        public AircraftData.GunshipRaw? Gunship { get; set; }
    }

    internal sealed class StreakRaw
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public int? KillsRequired { get; set; }
        public int? UnlockLevel { get; set; }
        public int? MaxActive { get; set; }
        public double? DurationSeconds { get; set; }
        public string? Control { get; set; }
        public Dictionary<string, int>? PerkWeights { get; set; }
        public MissileTuning.MissileRaw? Missile { get; set; }
        public HelicopterHelperTuning.HelperRaw? Helper { get; set; }
    }
}
