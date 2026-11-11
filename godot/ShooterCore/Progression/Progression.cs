namespace Suchohrad.ShooterCore;

/// <summary>
/// A player's persistent profile (ADR 0003). Identity is the client-generated token; <see cref="Id"/> is the
/// database row. Plain data only: no database or Godot types live in ShooterCore.
/// </summary>
public sealed record Profile(long Id, string Token, string Name, int Xp, int Level, DateTime CreatedUtc, DateTime LastSeenUtc);

/// <summary>
/// One of a player's loadout slots (five per player). Item ids are weapon/perk/killstreak ids from the data files;
/// an empty string means "not set". The server validates ids against the data files, not this type.
/// </summary>
public sealed record Loadout(int Slot, string PrimaryWeapon, string SecondaryWeapon, string Perk, string Streak1, string Streak2, string Streak3)
{
    public const int SlotCount = 5;
    public const int MaxSlot = SlotCount - 1;

    public static Loadout Empty(int slot) => new(slot, "", "", "", "", "", "");
}

/// <summary>Something a player owns: kind is "weapon", "perk" or "streak"; item id is the data-file id.</summary>
public sealed record Unlock(string Kind, string ItemId);

/// <summary>Lifetime counters. Per-weapon kills are keyed by weapon id.</summary>
public sealed record PlayerStats(long Kills, long Deaths, long Matches, IReadOnlyDictionary<string, long> WeaponKills)
{
    public static PlayerStats Zero { get; } = new(0, 0, 0, new Dictionary<string, long>());
}

/// <summary>
/// XP to level mapping. Placeholder curve until the levels data file lands (ADR 0003, Data files):
/// level = 1 + floor(sqrt(xp / 100)), so 100 XP is level 2 and 400 XP is level 3.
/// </summary>
public static class LevelCurve
{
    public static int LevelForXp(int xp) => xp <= 0 ? 1 : 1 + (int)Math.Floor(Math.Sqrt(xp / 100.0));
}
