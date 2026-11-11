using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Suchohrad.ShooterServer.Persistence.Sqlite;

/// <summary>SQLite-backed <see cref="IProgressionStore"/>. Open with a file path (default ./shooter.db set by the server).</summary>
public sealed class SqliteProgressionStore : IProgressionStore
{
    readonly SqliteWorker _db;

    SqliteProgressionStore(SqliteWorker db)
    {
        _db = db;
        Profiles = new ProfileRepo(db);
        Loadouts = new LoadoutRepo(db);
        Unlocks = new UnlockRepo(db);
        Stats = new StatsRepo(db);
    }

    public IProfileRepository Profiles { get; }
    public ILoadoutRepository Loadouts { get; }
    public IUnlockRepository Unlocks { get; }
    public IStatsRepository Stats { get; }

    /// <summary>Opens (creating if needed) and migrates the database. Throws if it cannot be opened or migrated; <paramref name="log"/> receives write failures later.</summary>
    public static SqliteProgressionStore Open(string path, Action<string> log) => new(SqliteWorker.Open(path, log));

    public void Dispose() => _db.Dispose();

    static string Stamp(DateTime utc) => utc.ToString("O", CultureInfo.InvariantCulture);
    static DateTime ParseStamp(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    static void Exec(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    static object? Scalar(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteScalar();
    }

    static void RequireSlot(int slot)
    {
        if (slot < 0 || slot > Loadout.MaxSlot) throw new ArgumentOutOfRangeException(nameof(slot), slot, $"slot must be 0..{Loadout.MaxSlot}");
    }

    sealed class ProfileRepo(SqliteWorker db) : IProfileRepository
    {
        const string Select = "SELECT id, token, name, xp, created_at, last_seen FROM players";

        public Task<Profile> GetOrCreateAsync(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("token is required", nameof(token));
            return db.Read(c =>
            {
                using var tx = c.BeginTransaction();
                var now = Stamp(DateTime.UtcNow);
                var inserted = 0;
                using (var ins = c.CreateCommand())
                {
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT OR IGNORE INTO players (token, name, xp, created_at, last_seen) VALUES ($t, '', 0, $now, $now);";
                    ins.Parameters.AddWithValue("$t", token);
                    ins.Parameters.AddWithValue("$now", now);
                    inserted = ins.ExecuteNonQuery();
                }
                var id = (long)Scalar(c, "SELECT id FROM players WHERE token = $t;", ("$t", token))!;
                if (inserted == 1)
                {
                    for (var slot = 0; slot < Loadout.SlotCount; slot++)
                        Exec(c, "INSERT INTO loadouts (player_id, slot) VALUES ($id, $s);", ("$id", id), ("$s", slot));
                    Exec(c, "INSERT INTO stats (player_id) VALUES ($id);", ("$id", id));
                }
                else
                {
                    Exec(c, "UPDATE players SET last_seen = $now WHERE id = $id;", ("$now", now), ("$id", id));
                }
                using var read = c.CreateCommand();
                read.Transaction = tx;
                read.CommandText = Select + " WHERE id = $id;";
                read.Parameters.AddWithValue("$id", id);
                using var r = read.ExecuteReader();
                r.Read();
                var profile = ReadProfile(r);
                tx.Commit();
                return profile;
            });
        }

        public Task<Profile?> FindAsync(long playerId) => db.Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = Select + " WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", playerId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadProfile(r) : null;
        });

        public Task SetNameAsync(long playerId, string name) =>
            db.Write("set name", c => Exec(c, "UPDATE players SET name = $n WHERE id = $id;", ("$n", name ?? ""), ("$id", playerId)));

        public Task AddXpAsync(long playerId, int amount)
        {
            if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount), amount, "XP cannot go down");
            return db.Write("add xp", c => Exec(c, "UPDATE players SET xp = xp + $a WHERE id = $id;", ("$a", amount), ("$id", playerId)));
        }

        static Profile ReadProfile(SqliteDataReader r) => new(
            Id: r.GetInt64(0), Token: r.GetString(1), Name: r.GetString(2), Xp: r.GetInt32(3),
            Level: LevelCurve.LevelForXp(r.GetInt32(3)), CreatedUtc: ParseStamp(r.GetString(4)), LastSeenUtc: ParseStamp(r.GetString(5)));
    }

    sealed class LoadoutRepo(SqliteWorker db) : ILoadoutRepository
    {
        public Task<IReadOnlyList<Loadout>> GetAllAsync(long playerId) => db.Read<IReadOnlyList<Loadout>>(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT slot, primary_weapon, secondary_weapon, perk, streak1, streak2, streak3 FROM loadouts WHERE player_id = $id ORDER BY slot;";
            cmd.Parameters.AddWithValue("$id", playerId);
            using var r = cmd.ExecuteReader();
            var list = new List<Loadout>();
            while (r.Read())
                list.Add(new Loadout(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)));
            return list;
        });

        public Task SaveAsync(long playerId, Loadout l)
        {
            RequireSlot(l.Slot);
            return db.Write("save loadout", c => Exec(c,
                "UPDATE loadouts SET primary_weapon = $p, secondary_weapon = $s, perk = $k, streak1 = $r1, streak2 = $r2, streak3 = $r3 WHERE player_id = $id AND slot = $slot;",
                ("$p", l.PrimaryWeapon), ("$s", l.SecondaryWeapon), ("$k", l.Perk),
                ("$r1", l.Streak1), ("$r2", l.Streak2), ("$r3", l.Streak3), ("$id", playerId), ("$slot", l.Slot)));
        }

        public Task<int> GetActiveSlotAsync(long playerId) => db.Read(c =>
            Convert.ToInt32(Scalar(c, "SELECT active_slot FROM players WHERE id = $id;", ("$id", playerId)) ?? 0L));

        public Task SetActiveSlotAsync(long playerId, int slot)
        {
            RequireSlot(slot);
            return db.Write("set active slot", c => Exec(c, "UPDATE players SET active_slot = $s WHERE id = $id;", ("$s", slot), ("$id", playerId)));
        }
    }

    sealed class UnlockRepo(SqliteWorker db) : IUnlockRepository
    {
        public Task<IReadOnlyList<Unlock>> GetAllAsync(long playerId) => db.Read<IReadOnlyList<Unlock>>(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT kind, item_id FROM unlocks WHERE player_id = $id ORDER BY kind, item_id;";
            cmd.Parameters.AddWithValue("$id", playerId);
            using var r = cmd.ExecuteReader();
            var list = new List<Unlock>();
            while (r.Read()) list.Add(new Unlock(r.GetString(0), r.GetString(1)));
            return list;
        });

        public Task GrantAsync(long playerId, Unlock unlock)
        {
            if (string.IsNullOrWhiteSpace(unlock.Kind) || string.IsNullOrWhiteSpace(unlock.ItemId))
                throw new ArgumentException("unlock needs a kind and an item id", nameof(unlock));
            return db.Write("grant unlock", c => Exec(c,
                "INSERT OR IGNORE INTO unlocks (player_id, kind, item_id) VALUES ($id, $k, $i);",
                ("$id", playerId), ("$k", unlock.Kind), ("$i", unlock.ItemId)));
        }
    }

    sealed class StatsRepo(SqliteWorker db) : IStatsRepository
    {
        public Task<PlayerStats> GetAsync(long playerId) => db.Read(c =>
        {
            long kills = 0, deaths = 0, matches = 0;
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT kills, deaths, matches FROM stats WHERE player_id = $id;";
                cmd.Parameters.AddWithValue("$id", playerId);
                using var r = cmd.ExecuteReader();
                if (r.Read()) (kills, deaths, matches) = (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2));
            }
            var weapons = new Dictionary<string, long>();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT weapon_id, kills FROM weapon_kills WHERE player_id = $id ORDER BY weapon_id;";
                cmd.Parameters.AddWithValue("$id", playerId);
                using var r = cmd.ExecuteReader();
                while (r.Read()) weapons[r.GetString(0)] = r.GetInt64(1);
            }
            return new PlayerStats(kills, deaths, matches, weapons);
        });

        // Kill, death and match counters share one transaction so a half-applied update cannot be read back.
        public Task RecordKillAsync(long playerId, string weaponId) =>
            db.Write("record kill", c =>
            {
                using var tx = c.BeginTransaction();
                Exec(c, "UPDATE stats SET kills = kills + 1 WHERE player_id = $id;", ("$id", playerId));
                Exec(c, "INSERT INTO weapon_kills (player_id, weapon_id, kills) VALUES ($id, $w, 1) ON CONFLICT (player_id, weapon_id) DO UPDATE SET kills = kills + 1;",
                    ("$id", playerId), ("$w", weaponId));
                tx.Commit();
            });

        public Task RecordDeathAsync(long playerId) =>
            db.Write("record death", c => Exec(c, "UPDATE stats SET deaths = deaths + 1 WHERE player_id = $id;", ("$id", playerId)));

        public Task RecordMatchAsync(long playerId) =>
            db.Write("record match", c => Exec(c, "UPDATE stats SET matches = matches + 1 WHERE player_id = $id;", ("$id", playerId)));
    }
}
