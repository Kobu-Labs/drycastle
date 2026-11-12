namespace Drycastle.ShooterCore;

/// <summary>
/// The typed stats a perk can change (perks.json "modifiers" keys, snake_case). Every modifier has a neutral value
/// (see <see cref="PerkModifiers"/>), so a loadout without a perk, or a perk that does not touch a stat, changes nothing.
/// </summary>
public enum PerkModifier
{
    /// <summary>Multiplier on running speed (1.12 = +12 %). Applied in the shared weapon step, so prediction and server agree.</summary>
    RunSpeed,
    /// <summary>Multiplier on reload speed (1.33 = the reload takes 1/1.33 of its time, -25 %).</summary>
    ReloadSpeed,
    /// <summary>Multiplier on explosive (rocket, splash, vehicle blast) damage the player takes (0.8 = -20 %).</summary>
    ExplosiveDamageTaken,
    /// <summary>Multiplier on the speed of switching to a secondary (off-hand) weapon (1.5 = the swap takes 1/1.5 of its time).</summary>
    WeaponSwapSpeed,
    /// <summary>Multiplier on damage the player deals to vehicles, turrets included (1.25 = +25 %).</summary>
    VehicleDamageDealt,
    /// <summary>Kills subtracted from each killstreak's cost (1 = one less kill). Consumer: the killstreak ticket.</summary>
    StreakCostReduction,
    /// <summary>Multiplier on the sniper breath-hold time (1.5 = +50 %). Consumer: the sniper ticket.</summary>
    HoldBreathTime,
    /// <summary>Multiplier on how visible the player is to UAV (0 = invisible). Consumer: the UAV ticket.</summary>
    UavVisibility,
}

/// <summary>One perk from perks.json: a name, the XP level that unlocks it and the modifiers it sets (others stay neutral).</summary>
public sealed class PerkDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>XP level that unlocks the perk; 0 = available from the start.</summary>
    public int UnlockLevel { get; init; }
    /// <summary>The modifiers this perk sets, by kind. Absent kinds are neutral.</summary>
    public IReadOnlyDictionary<PerkModifier, float> Values { get; init; } = new Dictionary<PerkModifier, float>();
}

/// <summary>
/// The modifier values of one player's perk. A loadout carries exactly one perk, so these are the values of that perk
/// alone: perks never stack. <see cref="None"/> is the neutral set used when a player has no perk.
/// </summary>
public sealed class PerkModifiers
{
    static readonly int Count = Enum.GetValues<PerkModifier>().Length;

    readonly float[] _values;

    PerkModifiers(float[] values) => _values = values;

    /// <summary>All neutral: no perk.</summary>
    public static readonly PerkModifiers None = new(Neutral());

    /// <summary>The modifiers of <paramref name="perk"/> (null: <see cref="None"/>). Only that perk's own values are applied.</summary>
    public static PerkModifiers Of(PerkDef? perk)
    {
        if (perk == null) return None;
        var v = Neutral();
        foreach (var (kind, value) in perk.Values) v[(int)kind] = value;
        return new PerkModifiers(v);
    }

    public float Get(PerkModifier kind) => _values[(int)kind];

    public float RunSpeed => Get(PerkModifier.RunSpeed);
    public float ReloadSpeed => Get(PerkModifier.ReloadSpeed);
    public float ExplosiveDamageTaken => Get(PerkModifier.ExplosiveDamageTaken);
    public float WeaponSwapSpeed => Get(PerkModifier.WeaponSwapSpeed);
    public float VehicleDamageDealt => Get(PerkModifier.VehicleDamageDealt);
    public float StreakCostReduction => Get(PerkModifier.StreakCostReduction);
    public float HoldBreathTime => Get(PerkModifier.HoldBreathTime);
    public float UavVisibility => Get(PerkModifier.UavVisibility);

    /// <summary>The value a modifier has when no perk sets it: 1 for multipliers, 0 for the kill reduction.</summary>
    public static float NeutralValue(PerkModifier kind) => kind == PerkModifier.StreakCostReduction ? 0f : 1f;

    static float[] Neutral()
    {
        var v = new float[Count];
        foreach (var kind in Enum.GetValues<PerkModifier>()) v[(int)kind] = NeutralValue(kind);
        return v;
    }
}

/// <summary>
/// perks.json, schema version 1: the eight v2 perks. Loaded through <see cref="DataFile"/> like the other data files.
/// Unknown modifier names are rejected (a typo must not silently do nothing); unknown top-level fields are ignored.
/// </summary>
public sealed class PerksFile
{
    /// <summary>Newest perks.json version this code reads.</summary>
    public const int SupportedVersion = 1;

    public int Version { get; init; }
    public IReadOnlyList<PerkDef> Perks { get; init; } = Array.Empty<PerkDef>();

    Dictionary<string, PerkDef>? _byId;
    public PerkDef? Find(string id) => DataIndex.Find(ref _byId, Perks, p => p.Id, id);

    public static PerksFile Load(string path) => Resolve(DataFile.Load<PerksRaw>(path, SupportedVersion));

    public static PerksFile Parse(string json) => Resolve(DataFile.Parse<PerksRaw>(json, "perks.json", SupportedVersion));

    static PerksFile Resolve(PerksRaw raw)
    {
        if (raw.Perks is null || raw.Perks.Count == 0) throw new DataFileException("perks.json: no perks");
        var perks = new List<PerkDef>();
        var ids = new HashSet<string>();
        foreach (var r in raw.Perks)
        {
            var where = $"perk \"{r.Id}\"";
            if (string.IsNullOrEmpty(r.Id)) throw new DataFileException($"perks.json: perk #{perks.Count + 1}: field \"id\" is required");
            if (!ids.Add(r.Id)) throw new DataFileException($"perks.json: duplicate perk id \"{r.Id}\"");
            if (string.IsNullOrWhiteSpace(r.Name)) throw Invalid(where, "name", "is required");
            if (r.UnlockLevel is < 0) throw Invalid(where, "unlock_level", "must be >= 0");

            var values = new Dictionary<PerkModifier, float>();
            foreach (var (key, value) in r.Modifiers ?? new())
            {
                if (!TryModifier(key, out var kind)) throw Invalid(where, "modifiers", $"unknown modifier \"{key}\"");
                if (!float.IsFinite(value) || value < 0) throw Invalid(where, key, "must be a finite number >= 0");
                // a zero multiplier would stop the stat outright; only the kill reduction and UAV visibility may be 0
                if (value == 0 && kind is not (PerkModifier.StreakCostReduction or PerkModifier.UavVisibility))
                    throw Invalid(where, key, "must be > 0");
                values[kind] = value;
            }
            perks.Add(new PerkDef { Id = r.Id, Name = r.Name, UnlockLevel = r.UnlockLevel ?? 0, Values = values });
        }
        return new PerksFile { Version = raw.Version, Perks = perks };
    }

    static DataFileException Invalid(string where, string field, string rule) =>
        new($"perks.json: {where}: field \"{field}\" {rule}");

    /// <summary>Modifier keys match the enum names ignoring case and underscores ("run_speed" = RunSpeed).</summary>
    static bool TryModifier(string key, out PerkModifier kind)
    {
        kind = default;
        if (string.IsNullOrEmpty(key) || !key.All(c => char.IsLetter(c) || c == '_')) return false;
        return Enum.TryParse(key.Replace("_", ""), ignoreCase: true, out kind) && Enum.IsDefined(kind);
    }

    internal sealed class PerksRaw : IVersioned
    {
        public int Version { get; set; }
        public List<PerkRaw>? Perks { get; set; }
    }

    internal sealed class PerkRaw
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public int? UnlockLevel { get; set; }
        public Dictionary<string, float>? Modifiers { get; set; }
    }
}
