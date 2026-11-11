namespace Suchohrad.ShooterServer.Persistence.InMemory;

/// <summary>
/// Test and dev store with the same semantics as the SQLite store (five loadouts and a stats row per new player,
/// idempotent grants, zero-initialised stats). Writes for an unknown player are ignored rather than logged.
/// </summary>
public sealed class InMemoryProgressionStore : IProgressionStore
{
    readonly object _gate = new();
    readonly Dictionary<string, Profile> _byToken = new();
    readonly Dictionary<long, Profile> _byId = new();
    readonly Dictionary<long, Loadout[]> _loadouts = new();
    readonly Dictionary<long, int> _activeSlot = new();
    readonly Dictionary<long, HashSet<Unlock>> _unlocks = new();
    readonly Dictionary<long, StatsRow> _stats = new();
    readonly LevelCurve _curve;
    long _nextId = 1;

    sealed record StatsRow(long Kills, long Deaths, long Matches, Dictionary<string, long> Weapons);

    public InMemoryProgressionStore(LevelCurve curve)
    {
        _curve = curve;
        Profiles = new ProfileRepo(this);
        Loadouts = new LoadoutRepo(this);
        Unlocks = new UnlockRepo(this);
        Stats = new StatsRepo(this);
    }

    public IProfileRepository Profiles { get; }
    public ILoadoutRepository Loadouts { get; }
    public IUnlockRepository Unlocks { get; }
    public IStatsRepository Stats { get; }

    public void Dispose() { }

    sealed class ProfileRepo(InMemoryProgressionStore s) : IProfileRepository
    {
        public Task<Profile> GetOrCreateAsync(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("token is required", nameof(token));
            lock (s._gate)
            {
                if (s._byToken.TryGetValue(token, out var existing))
                {
                    var touched = existing with { LastSeenUtc = DateTime.UtcNow };
                    s._byToken[token] = touched;
                    s._byId[touched.Id] = touched;
                    return Task.FromResult(touched);
                }
                var now = DateTime.UtcNow;
                var id = s._nextId++;
                var profile = new Profile(id, token, "", 0, 1, now, now);
                s._byToken[token] = profile;
                s._byId[id] = profile;
                s._loadouts[id] = Enumerable.Range(0, Loadout.SlotCount).Select(Loadout.Empty).ToArray();
                s._stats[id] = new StatsRow(0, 0, 0, new Dictionary<string, long>());
                return Task.FromResult(profile);
            }
        }

        public Task<Profile?> FindAsync(long playerId)
        {
            lock (s._gate) return Task.FromResult(s._byId.TryGetValue(playerId, out var p) ? p : null);
        }

        public Task SetNameAsync(long playerId, string name)
        {
            lock (s._gate) Update(playerId, p => p with { Name = name ?? "" });
            return Task.CompletedTask;
        }

        public Task AddXpAsync(long playerId, int amount)
        {
            if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount), amount, "XP cannot go down");
            lock (s._gate) Update(playerId, p => p with { Xp = p.Xp + amount, Level = s._curve.LevelForXp(p.Xp + amount) });
            return Task.CompletedTask;
        }

        void Update(long id, Func<Profile, Profile> f)
        {
            if (!s._byId.TryGetValue(id, out var p)) return;
            var next = f(p);
            s._byId[id] = next;
            s._byToken[next.Token] = next;
        }
    }

    sealed class LoadoutRepo(InMemoryProgressionStore s) : ILoadoutRepository
    {
        public Task<IReadOnlyList<Loadout>> GetAllAsync(long playerId)
        {
            lock (s._gate)
                return Task.FromResult<IReadOnlyList<Loadout>>(s._loadouts.TryGetValue(playerId, out var l) ? l.ToList() : new List<Loadout>());
        }

        public Task SaveAsync(long playerId, Loadout loadout)
        {
            if (loadout.Slot < 0 || loadout.Slot > Loadout.MaxSlot) throw new ArgumentOutOfRangeException(nameof(loadout), loadout.Slot, $"slot must be 0..{Loadout.MaxSlot}");
            lock (s._gate)
                if (s._loadouts.TryGetValue(playerId, out var l)) l[loadout.Slot] = loadout;
            return Task.CompletedTask;
        }

        public Task<int> GetActiveSlotAsync(long playerId)
        {
            lock (s._gate) return Task.FromResult(s._activeSlot.GetValueOrDefault(playerId));
        }

        public Task SetActiveSlotAsync(long playerId, int slot)
        {
            if (slot < 0 || slot > Loadout.MaxSlot) throw new ArgumentOutOfRangeException(nameof(slot), slot, $"slot must be 0..{Loadout.MaxSlot}");
            lock (s._gate)
                if (s._byId.ContainsKey(playerId)) s._activeSlot[playerId] = slot;
            return Task.CompletedTask;
        }
    }

    sealed class UnlockRepo(InMemoryProgressionStore s) : IUnlockRepository
    {
        public Task<IReadOnlyList<Unlock>> GetAllAsync(long playerId)
        {
            lock (s._gate)
            {
                IReadOnlyList<Unlock> list = s._unlocks.TryGetValue(playerId, out var set)
                    ? set.OrderBy(u => u.Kind, StringComparer.Ordinal).ThenBy(u => u.ItemId, StringComparer.Ordinal).ToList()
                    : new List<Unlock>();
                return Task.FromResult(list);
            }
        }

        public Task GrantAsync(long playerId, Unlock unlock)
        {
            if (string.IsNullOrWhiteSpace(unlock.Kind) || string.IsNullOrWhiteSpace(unlock.ItemId))
                throw new ArgumentException("unlock needs a kind and an item id", nameof(unlock));
            lock (s._gate)
                if (s._byId.ContainsKey(playerId))
                {
                    if (!s._unlocks.TryGetValue(playerId, out var set)) s._unlocks[playerId] = set = new HashSet<Unlock>();
                    set.Add(unlock);
                }
            return Task.CompletedTask;
        }
    }

    sealed class StatsRepo(InMemoryProgressionStore s) : IStatsRepository
    {
        public Task<PlayerStats> GetAsync(long playerId)
        {
            lock (s._gate)
            {
                if (!s._stats.TryGetValue(playerId, out var st)) return Task.FromResult(PlayerStats.Zero);
                return Task.FromResult(new PlayerStats(st.Kills, st.Deaths, st.Matches, new Dictionary<string, long>(st.Weapons)));
            }
        }

        public Task RecordKillAsync(long playerId, string weaponId)
        {
            lock (s._gate)
                if (s._stats.TryGetValue(playerId, out var st))
                {
                    st.Weapons[weaponId] = st.Weapons.GetValueOrDefault(weaponId) + 1;
                    s._stats[playerId] = st with { Kills = st.Kills + 1 };
                }
            return Task.CompletedTask;
        }

        public Task RecordDeathAsync(long playerId)
        {
            lock (s._gate)
                if (s._stats.TryGetValue(playerId, out var st)) s._stats[playerId] = st with { Deaths = st.Deaths + 1 };
            return Task.CompletedTask;
        }

        public Task RecordMatchAsync(long playerId)
        {
            lock (s._gate)
                if (s._stats.TryGetValue(playerId, out var st)) s._stats[playerId] = st with { Matches = st.Matches + 1 };
            return Task.CompletedTask;
        }
    }
}
