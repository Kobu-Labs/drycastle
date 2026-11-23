using Drycastle.ShooterCore.Camo;
using Drycastle.ShooterCore.Hunts;
using Drycastle.ShooterCore.Mining;
using Drycastle.ShooterCore.Progression;

namespace Drycastle.ShooterServer.Persistence;

// Repository interfaces (ADR 0003). Callers are never on the game tick: reads are awaited on a worker,
// writes are queued and complete in the background. The SQLite store logs a failed write instead of throwing it.

public interface IProfileRepository
{
    /// <summary>Returns the profile for this token, creating it (with five empty loadouts and a stats row) on first use. Updates last_seen.</summary>
    Task<Profile> GetOrCreateAsync(string token);
    Task<Profile?> FindAsync(long playerId);
    Task SetNameAsync(long playerId, string name);
    /// <summary>Adds XP (amount must be &gt;= 0). Level is derived from XP on read.</summary>
    Task AddXpAsync(long playerId, int amount);
    /// <summary>The Companion the player chose (#488): an id of companions.json, empty for none chosen or an unknown player. Whether they own it is the caller's to check.</summary>
    Task<string> GetCompanionAsync(long playerId);
    Task SetCompanionAsync(long playerId, string companionId);
}

public interface ILoadoutRepository
{
    /// <summary>All slots for the player, ordered 0..4.</summary>
    Task<IReadOnlyList<Loadout>> GetAllAsync(long playerId);
    Task SaveAsync(long playerId, Loadout loadout);
    Task<int> GetActiveSlotAsync(long playerId);
    Task SetActiveSlotAsync(long playerId, int slot);
}

public interface IUnlockRepository
{
    Task<IReadOnlyList<Unlock>> GetAllAsync(long playerId);
    /// <summary>Idempotent: granting an existing unlock is a no-op.</summary>
    Task GrantAsync(long playerId, Unlock unlock);
}

public interface IStatsRepository
{
    Task<PlayerStats> GetAsync(long playerId);
    /// <summary>Counts one kill with <paramref name="weaponId"/>; a roadkill (vehicle run-over) also counts in the roadkill total.</summary>
    Task RecordKillAsync(long playerId, string weaponId, bool roadkill = false);
    Task RecordDeathAsync(long playerId);
    Task RecordMatchAsync(long playerId);
}

/// <summary>A loadout's camouflage (tickets #140, #141): one encoded <see cref="CamoSpec"/> per player and slot. Validated against camo.json by the caller.</summary>
public interface ICamoRepository
{
    /// <summary>The saved camouflage of every slot that has one (slot 0..4). A slot with no row uses the default look.</summary>
    Task<IReadOnlyList<(int Slot, CamoSpec Spec)>> GetAllAsync(long playerId);
    Task SaveAsync(long playerId, int slot, CamoSpec spec);
}

/// <summary>The Hunter's Log (#336): every hunt, kept forever, and each killer's longest player kill (#430). Rules (records, first-times, redaction) are <see cref="HuntBook"/>'s, not the store's; the store keeps rows and pages them.</summary>
public interface IHuntRepository
{
    /// <summary>Queues one hunt (its <see cref="HuntRecord.Id"/> is ignored). A failed write is logged, never thrown. A hunt of an unknown player is not stored.</summary>
    Task AppendAsync(HuntRecord hunt);
    /// <summary>One page of <see cref="HuntBook.PageSize"/> hunts, filtered and sorted as <see cref="HuntQuery"/> says, with the hunters' names as players.name has them.</summary>
    Task<HuntPage> PageAsync(HuntQuery query);
    /// <summary>Every hunt, oldest first (ties by id): what <see cref="HuntBook.From"/> replays at server start.</summary>
    Task<IReadOnlyList<HuntRow>> GetAllAsync();
    /// <summary>Keeps a player kill (#430) as its killer's longest when it is longer than the one stored (a tie keeps the earlier one); one row per killer. A failed write is logged, never thrown. A kill of an unknown player is not stored.</summary>
    Task SavePlayerKillAsync(PlayerKillRecord kill);
    /// <summary>The longest kill of every killer, oldest first: what <see cref="PlayerKillBook.From"/> replays at server start.</summary>
    Task<IReadOnlyList<PlayerKillRow>> GetPlayerKillsAsync();
}

/// <summary>The Mineral Log's finds (#401): one row per player and mineral, kept forever. The rules (once per player, the place among the finders) are <see cref="MineralBook"/>'s.</summary>
public interface IMineralRepository
{
    /// <summary>Queues one find. A second find of the same mineral by the same player, and a find of an unknown player, are not stored. A failed write is logged, never thrown.</summary>
    Task AppendAsync(MineralFind find);
    /// <summary>Every find, oldest first (ties by id): what <see cref="MineralBook.From"/> replays at server start.</summary>
    Task<IReadOnlyList<MineralFind>> GetAllAsync();
}

/// <summary>One store = one database (or in-memory set). Disposing it stops the background writer.</summary>
public interface IProgressionStore : IDisposable
{
    IProfileRepository Profiles { get; }
    ILoadoutRepository Loadouts { get; }
    IUnlockRepository Unlocks { get; }
    IStatsRepository Stats { get; }
    ICamoRepository Camo { get; }
    IHuntRepository Hunts { get; }
    IMineralRepository Minerals { get; }
    /// <summary>Writes and reads queued behind the database thread (0 for a store with no queue). Logged by the server telemetry line.</summary>
    int QueueDepth => 0;
}
