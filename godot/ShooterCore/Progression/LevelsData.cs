namespace Drycastle.ShooterCore;

/// <summary>What a level unlocks: a loadout item kind (levels.json "kind": weapon, perk, streak, loadout_slot).</summary>
public enum UnlockKind { Weapon, Perk, Streak, LoadoutSlot }

/// <summary>One line of the unlock table: <paramref name="Id"/> becomes available at <paramref name="Level"/>.</summary>
public sealed record UnlockEntry(int Level, UnlockKind Kind, string Id);

/// <summary>XP per match event (levels.json "awards"). All values are tunable data, nothing is hard-coded in the code.</summary>
public sealed class XpAwards
{
    public int Kill { get; init; }
    /// <summary>Added to the kill when the kill was a headshot.</summary>
    public int Headshot { get; init; }
    /// <summary>Each player who damaged the victim within <see cref="AssistWindowSeconds"/> before the kill.</summary>
    public int Assist { get; init; }
    /// <summary>Added to a kill made by a killstreak's own weapon.</summary>
    public int StreakKill { get; init; }
    /// <summary>Replaces <see cref="Kill"/> for a run-over kill (slightly above a normal kill).</summary>
    public int Roadkill { get; init; }
    public int MatchWin { get; init; }
    /// <summary>Places 2 and 3 in the match ranking (ties for the win are not top-3 places).</summary>
    public int MatchTop3 { get; init; }
    /// <summary>Each player credited with winning an event (the Bigfoot, issue #264). Optional in the file: 300 when it is left out.</summary>
    public int EventWin { get; init; }
    public double AssistWindowSeconds { get; init; }
}

/// <summary>
/// levels.json, schema version 1: the XP curve, the XP awards and the unlock table. Loaded through <see cref="DataFile"/>
/// and validated against weapons.json and perks.json, so a unlock that names an unknown item fails at load.
/// </summary>
public sealed class LevelsFile
{
    /// <summary>Newest levels.json version this code reads.</summary>
    public const int SupportedVersion = 1;
    /// <summary>The XP curve has exactly this many levels (levels 1-40).</summary>
    public const int LevelCount = 40;
    /// <summary>Loadout slots a killstreak can be unlocked into beyond the first (the starting kit has one streak slot).</summary>
    public static readonly IReadOnlySet<string> LoadoutSlotIds = new HashSet<string> { "streak_slot_2", "streak_slot_3" };

    public int Version { get; init; }
    public LevelCurve Curve { get; init; } = null!;
    public XpAwards Awards { get; init; } = null!;
    public IReadOnlyList<UnlockEntry> Unlocks { get; init; } = Array.Empty<UnlockEntry>();
    /// <summary>Required level for every selectable item; the basis of <see cref="IsUnlocked(Profile, UnlockKind, string)"/>.</summary>
    public UnlockTable Table { get; init; } = null!;

    public static LevelsFile Load(string path, WeaponsFile weapons, PerksFile perks, IReadOnlyDictionary<string, int>? streakLevels = null) =>
        Resolve(DataFile.Load<LevelsRaw>(path, SupportedVersion), weapons, perks, streakLevels);

    public static LevelsFile Parse(string json, WeaponsFile weapons, PerksFile perks, IReadOnlyDictionary<string, int>? streakLevels = null) =>
        Resolve(DataFile.Parse<LevelsRaw>(json, "levels.json", SupportedVersion), weapons, perks, streakLevels);

    /// <summary>Whether a profile at its current level may use the item. Unknown ids are never unlocked.</summary>
    public bool IsUnlocked(Profile profile, UnlockKind kind, string id) => Table.IsUnlocked(profile.Level, kind, id);

    /// <summary><paramref name="unlockAll"/> is the dev-mode gate override (<see cref="DevUnlocks"/>): every known item counts as unlocked.</summary>
    public bool IsUnlocked(Profile profile, UnlockKind kind, string id, bool unlockAll) => Table.IsUnlocked(profile.Level, kind, id, unlockAll);

    /// <param name="streakLevels">Killstreak id to its unlock level, from killstreaks.json (<see cref="KillstreakFile.UnlockLevels"/>). A streak line must name a listed id at the same level.</param>
    static LevelsFile Resolve(LevelsRaw raw, WeaponsFile weapons, PerksFile perks, IReadOnlyDictionary<string, int>? streakLevels)
    {
        const string F = "levels.json";
        streakLevels ??= new Dictionary<string, int>();
        if (raw.XpToReach is not { Count: LevelCount })
            throw new DataFileException($"{F}: field \"xp_to_reach\" must list exactly {LevelCount} thresholds (levels 1-{LevelCount})");
        if (raw.XpToReach[0] != 0) throw new DataFileException($"{F}: field \"xp_to_reach\" must start at 0 (level 1)");
        for (var i = 1; i < raw.XpToReach.Count; i++)
            if (raw.XpToReach[i] <= raw.XpToReach[i - 1])
                throw new DataFileException($"{F}: field \"xp_to_reach\" must strictly increase (level {i + 1} is not above level {i})");
        var curve = new LevelCurve(raw.XpToReach);

        if (raw.Awards is null) throw new DataFileException($"{F}: field \"awards\" is required");
        var a = raw.Awards;
        var awards = new XpAwards
        {
            Kill = Award(a.Kill, "kill"), Headshot = Award(a.Headshot, "headshot"), Assist = Award(a.Assist, "assist"),
            StreakKill = Award(a.StreakKill, "streak_kill"), Roadkill = Award(a.Roadkill, "roadkill"),
            MatchWin = Award(a.MatchWin, "match_win"), MatchTop3 = Award(a.MatchTop3, "match_top3"),
            EventWin = a.EventWin is { } ew ? Award(ew, "event_win") : 300,
            AssistWindowSeconds = a.AssistWindowSeconds is { } w && double.IsFinite(w) && w >= 0 ? w
                : throw new DataFileException($"{F}: awards \"assist_window_seconds\" is required and must be >= 0"),
        };

        // Items that the table can gate: every primary/secondary weapon and every perk. Specials (knife, bazooka) are not loadout items.
        var itemLevels = new Dictionary<(UnlockKind, string), int>();
        foreach (var gun in weapons.Weapons.Where(x => x.Slot != WeaponSlot.Special)) itemLevels[(UnlockKind.Weapon, gun.Id)] = gun.UnlockLevel;
        foreach (var p in perks.Perks) itemLevels[(UnlockKind.Perk, p.Id)] = p.UnlockLevel;
        foreach (var (id, level) in streakLevels) itemLevels[(UnlockKind.Streak, id)] = level;

        var unlocks = new List<UnlockEntry>();
        var table = new Dictionary<(UnlockKind, string), int>();
        for (var i = 0; i < (raw.Unlocks?.Count ?? 0); i++)
        {
            var r = raw.Unlocks![i];
            var where = $"unlock #{i + 1} (level {r.Level}, {r.Kind} \"{r.Id}\")";
            if (r.Level is not { } level || level < 0 || level > curve.MaxLevel) throw new DataFileException($"{F}: {where}: field \"level\" must be between 0 and {curve.MaxLevel}");
            if (string.IsNullOrEmpty(r.Id)) throw new DataFileException($"{F}: {where}: field \"id\" is required");
            if (!TryKind(r.Kind, out var kind))
                throw new DataFileException($"{F}: {where}: field \"kind\" must be weapon, perk, streak or loadout_slot");
            var key = (kind, r.Id);
            if (table.ContainsKey(key)) throw new DataFileException($"{F}: {where}: duplicate unlock");

            switch (kind)
            {
                case UnlockKind.Weapon:
                    var item = weapons.Find(r.Id) ?? throw new DataFileException($"{F}: {where}: unknown weapon id (not in weapons.json)");
                    if (item.Slot == WeaponSlot.Special) throw new DataFileException($"{F}: {where}: special weapons are not loadout items");
                    break;
                case UnlockKind.Perk:
                    _ = perks.Find(r.Id) ?? throw new DataFileException($"{F}: {where}: unknown perk id (not in perks.json)");
                    break;
                case UnlockKind.Streak:
                    if (!streakLevels.ContainsKey(r.Id)) throw new DataFileException($"{F}: {where}: unknown killstreak id (not in killstreaks.json)");
                    break;
                case UnlockKind.LoadoutSlot:
                    if (!LoadoutSlotIds.Contains(r.Id)) throw new DataFileException($"{F}: {where}: unknown loadout slot id (expected streak_slot_2 or streak_slot_3)");
                    break;
            }
            // weapons and perks carry their own unlock_level: the table and the item file must agree
            if (itemLevels.TryGetValue(key, out var own) && own != level)
                throw new DataFileException($"{F}: {where}: the item's unlock_level is {own} in its data file; make them match");

            table[key] = level;
            unlocks.Add(new UnlockEntry(level, kind, r.Id));
        }

        // every gated item must be in the table; an item left out is available from level 0, so it must say so
        foreach (var ((kind, id), own) in itemLevels)
        {
            if (table.ContainsKey((kind, id))) continue;
            if (own != 0)
                throw new DataFileException($"levels.json: {ItemLabel(kind)} \"{id}\" has unlock_level {own} but no entry in levels.json \"unlocks\"");
            table[(kind, id)] = 0;
        }

        if (!table.Any(e => e.Value == 0 && e.Key.Item1 == UnlockKind.Weapon && weapons.Find(e.Key.Item2) is { Slot: WeaponSlot.Primary }))
            throw new DataFileException("levels.json: no primary weapon is unlocked at level 0 (the starting kit needs one)");
        if (!table.Any(e => e.Value == 0 && e.Key.Item1 == UnlockKind.Weapon && weapons.Find(e.Key.Item2) is { Slot: WeaponSlot.Secondary }))
            throw new DataFileException("levels.json: no secondary weapon is unlocked at level 0 (the starting kit needs one)");
        if (!table.Any(e => e.Value == 0 && e.Key.Item1 == UnlockKind.Perk))
            throw new DataFileException("levels.json: no perk is unlocked at level 0 (the starting kit needs one)");

        return new LevelsFile
        {
            Version = raw.Version, Curve = curve, Awards = awards, Unlocks = unlocks,
            Table = new UnlockTable(table),
        };
    }

    static string ItemLabel(UnlockKind kind) => kind switch { UnlockKind.Weapon => "weapon", UnlockKind.Perk => "perk", UnlockKind.Streak => "killstreak", _ => "item" };

    static int Award(int? value, string key) =>
        value is { } v && v >= 0 ? v : throw new DataFileException($"levels.json: awards \"{key}\" is required and must be >= 0");

    static bool TryKind(string? s, out UnlockKind kind)
    {
        kind = default;
        switch (s)
        {
            case "weapon": kind = UnlockKind.Weapon; return true;
            case "perk": kind = UnlockKind.Perk; return true;
            case "streak": kind = UnlockKind.Streak; return true;
            case "loadout_slot": kind = UnlockKind.LoadoutSlot; return true;
            default: return false;
        }
    }

    internal sealed class LevelsRaw : IVersioned
    {
        public int Version { get; set; }
        public List<int>? XpToReach { get; set; }
        public AwardsRaw? Awards { get; set; }
        public List<UnlockRaw>? Unlocks { get; set; }
    }

    internal sealed class AwardsRaw
    {
        public int? Kill { get; set; }
        public int? Headshot { get; set; }
        public int? Assist { get; set; }
        public int? StreakKill { get; set; }
        public int? Roadkill { get; set; }
        public int? MatchWin { get; set; }
        public int? MatchTop3 { get; set; }
        public int? EventWin { get; set; }
        public double? AssistWindowSeconds { get; set; }
    }

    internal sealed class UnlockRaw
    {
        public int? Level { get; set; }
        public string? Kind { get; set; }
        public string? Id { get; set; }
    }
}

/// <summary>
/// Which level each loadout item needs. Built from levels.json and checked against the weapon and perk files at load.
/// Use it instead of comparing unlock levels by hand; ids not in the table (including the special weapons) are never unlocked.
/// </summary>
public sealed class UnlockTable
{
    readonly IReadOnlyDictionary<(UnlockKind, string), int> _levels;

    internal UnlockTable(IReadOnlyDictionary<(UnlockKind, string), int> levels) => _levels = levels;

    /// <summary>The level that unlocks the item, or null when the item is unknown.</summary>
    public int? RequiredLevel(UnlockKind kind, string id) => _levels.TryGetValue((kind, id), out var l) ? l : null;

    public bool IsUnlocked(int level, UnlockKind kind, string id) => RequiredLevel(kind, id) is { } req && level >= req;

    /// <summary>The dev-mode override: a known item is unlocked at any level. Unknown ids stay locked.</summary>
    public bool IsUnlocked(int level, UnlockKind kind, string id, bool unlockAll) =>
        RequiredLevel(kind, id) is { } req && (unlockAll || level >= req);
}
