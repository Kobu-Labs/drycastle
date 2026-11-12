namespace Drycastle.ShooterCore;

/// <summary>
/// A player's persistent profile (ADR 0003). Identity is the client-generated token; <see cref="Id"/> is the
/// database row. Plain data only: no database or Godot types live in ShooterCore.
/// </summary>
public sealed record Profile(long Id, string Token, string Name, int Xp, int Level, DateTime CreatedUtc, DateTime LastSeenUtc);

/// <summary>
/// One of a player's loadout slots (five per player). Item ids are weapon/perk/killstreak ids from the data files;
/// an empty string means "not set". The server validates ids against the data files, not this type.
/// </summary>
public sealed record Loadout(int Slot, string PrimaryWeapon, string SecondaryWeapon, string Perk, string Streak1, string Streak2, string Streak3, string Name = "")
{
    public const int SlotCount = 5;
    public const int MaxSlot = SlotCount - 1;

    public static Loadout Empty(int slot) => new(slot, "", "", "", "", "", "");
}

/// <summary>Something a player owns: kind is "weapon", "perk" or "streak"; item id is the data-file id.</summary>
public sealed record Unlock(string Kind, string ItemId);

/// <summary>Lifetime counters. Per-weapon kills are keyed by weapon id; roadkills are a subset of kills.</summary>
public sealed record PlayerStats(long Kills, long Deaths, long Matches, IReadOnlyDictionary<string, long> WeaponKills, long Roadkills = 0)
{
    public static PlayerStats Zero { get; } = new(0, 0, 0, new Dictionary<string, long>(), 0);
}

/// <summary>
/// XP to level mapping from levels.json (<see cref="LevelsFile"/>). Level 1 starts at 0 XP; level N+1 needs
/// <c>XpToReach[N]</c> XP in total. XP past the last threshold stays at <see cref="MaxLevel"/>.
/// </summary>
public sealed class LevelCurve
{
    readonly int[] _xpToReach;

    public LevelCurve(IReadOnlyList<int> xpToReach)
    {
        if (xpToReach.Count == 0 || xpToReach[0] != 0) throw new ArgumentException("the first level starts at 0 XP", nameof(xpToReach));
        for (var i = 1; i < xpToReach.Count; i++)
            if (xpToReach[i] <= xpToReach[i - 1]) throw new ArgumentException("XP thresholds must strictly increase", nameof(xpToReach));
        _xpToReach = xpToReach.ToArray();
    }

    /// <summary>The highest level (the number of entries in levels.json xp_to_reach).</summary>
    public int MaxLevel => _xpToReach.Length;

    public int LevelForXp(int xp)
    {
        var level = 1;
        while (level < MaxLevel && xp >= _xpToReach[level]) level++;
        return level;
    }

    /// <summary>Total XP needed to reach <paramref name="level"/> (clamped to 1..MaxLevel).</summary>
    public int XpForLevel(int level) => _xpToReach[Math.Clamp(level, 1, MaxLevel) - 1];
}
