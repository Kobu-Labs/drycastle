namespace Suchohrad.ShooterServer.Persistence;

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

/// <summary>One store = one database (or in-memory set). Disposing it stops the background writer.</summary>
public interface IProgressionStore : IDisposable
{
    IProfileRepository Profiles { get; }
    ILoadoutRepository Loadouts { get; }
    IUnlockRepository Unlocks { get; }
    IStatsRepository Stats { get; }
    ICamoRepository Camo { get; }
}
