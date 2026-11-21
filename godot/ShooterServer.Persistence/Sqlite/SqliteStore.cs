using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Drycastle.ShooterServer.Persistence.Sqlite;

/// <summary>SQLite-backed <see cref="IProgressionStore"/>. Open with a file path (default ./shooter.db set by the server).</summary>
public sealed class SqliteProgressionStore : IProgressionStore
{
    readonly SqliteWorker _db;

    SqliteProgressionStore(SqliteWorker db, LevelCurve curve)
    {
        _db = db;
        Profiles = new ProfileRepo(db, curve);
        Loadouts = new LoadoutRepo(db);
        Unlocks = new UnlockRepo(db);
        Stats = new StatsRepo(db);
        Camo = new CamoRepo(db);
        Hunts = new HuntRepo(db);
        Minerals = new MineralRepo(db);
    }

    public IProfileRepository Profiles { get; }
    public ILoadoutRepository Loadouts { get; }
    public IUnlockRepository Unlocks { get; }
    public IStatsRepository Stats { get; }
    public ICamoRepository Camo { get; }
    public IHuntRepository Hunts { get; }
    public IMineralRepository Minerals { get; }

    /// <summary>
    /// Opens (creating if needed) and migrates the database. <paramref name="curve"/> (from levels.json) turns XP into the Level on read.
    /// Throws if it cannot be opened or migrated; <paramref name="log"/> receives write failures later.
    /// </summary>
    public static SqliteProgressionStore Open(string path, Action<string> log, LevelCurve curve) => new(SqliteWorker.Open(path, log), curve);

    public int QueueDepth => _db.QueueDepth;
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

    sealed class ProfileRepo(SqliteWorker db, LevelCurve curve) : IProfileRepository
    {
        const string Select = "SELECT id, token, name, xp, created_at, last_seen FROM players";

        /// <summary>
        /// A returning player is found on the read connection, so a join does not wait behind the write queue (#195); last_seen is a
        /// queued write. Only a new token goes through the write connection. A just-queued XP grant may not show yet: the server adds
        /// XP as "xp = xp + n", so the stored total stays right.
        /// </summary>
        public async Task<Profile> GetOrCreateAsync(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("token is required", nameof(token));
            var found = await db.Read(c =>
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = Select + " WHERE token = $t;";
                cmd.Parameters.AddWithValue("$t", token);
                using var r = cmd.ExecuteReader();
                return r.Read() ? ReadProfile(r) : null;
            });
            if (found == null) return await Create(token);
            _ = db.Write("touch last seen", c => Exec(c, "UPDATE players SET last_seen = $now WHERE id = $id;", ("$now", Stamp(DateTime.UtcNow)), ("$id", found.Id)));
            return found;
        }

        Task<Profile> Create(string token)
        {
            return db.Mutate(c => // writes (a new profile), so it runs on the write connection behind the queued writes
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

        Profile ReadProfile(SqliteDataReader r) => new(
            Id: r.GetInt64(0), Token: r.GetString(1), Name: r.GetString(2), Xp: r.GetInt32(3),
            Level: curve.LevelForXp(r.GetInt32(3)), CreatedUtc: ParseStamp(r.GetString(4)), LastSeenUtc: ParseStamp(r.GetString(5)));
    }

    sealed class CamoRepo(SqliteWorker db) : ICamoRepository
    {
        public Task<IReadOnlyList<(int Slot, CamoSpec Spec)>> GetAllAsync(long playerId) => db.Read<IReadOnlyList<(int, CamoSpec)>>(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT slot, spec FROM loadout_camo WHERE player_id = $id ORDER BY slot;";
            cmd.Parameters.AddWithValue("$id", playerId);
            using var r = cmd.ExecuteReader();
            var list = new List<(int, CamoSpec)>();
            while (r.Read())
                if (CamoSpec.TryDecode((byte[])r[1]) is { } spec) list.Add((r.GetInt32(0), spec));
            return list;
        });

        public Task SaveAsync(long playerId, int slot, CamoSpec spec)
        {
            RequireSlot(slot);
            var blob = spec.Encode();
            return db.Write("save camo", c => Exec(c,
                "INSERT INTO loadout_camo (player_id, slot, spec) VALUES ($id, $slot, $spec) ON CONFLICT (player_id, slot) DO UPDATE SET spec = excluded.spec;",
                ("$id", playerId), ("$slot", slot), ("$spec", blob)), report: true);
        }
    }

    sealed class LoadoutRepo(SqliteWorker db) : ILoadoutRepository
    {
        public Task<IReadOnlyList<Loadout>> GetAllAsync(long playerId) => db.Read<IReadOnlyList<Loadout>>(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT slot, primary_weapon, secondary_weapon, perk, streak1, streak2, streak3, name FROM loadouts WHERE player_id = $id ORDER BY slot;";
            cmd.Parameters.AddWithValue("$id", playerId);
            using var r = cmd.ExecuteReader();
            var list = new List<Loadout>();
            while (r.Read())
                list.Add(new Loadout(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7)));
            return list;
        });

        public Task SaveAsync(long playerId, Loadout l)
        {
            RequireSlot(l.Slot);
            return db.Write("save loadout", c => Exec(c,
                "UPDATE loadouts SET primary_weapon = $p, secondary_weapon = $s, perk = $k, streak1 = $r1, streak2 = $r2, streak3 = $r3, name = $n WHERE player_id = $id AND slot = $slot;",
                ("$p", l.PrimaryWeapon), ("$s", l.SecondaryWeapon), ("$k", l.Perk),
                ("$r1", l.Streak1), ("$r2", l.Streak2), ("$r3", l.Streak3), ("$n", l.Name), ("$id", playerId), ("$slot", l.Slot)), report: true);
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
            long kills = 0, deaths = 0, matches = 0, roadkills = 0;
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT kills, deaths, matches, roadkills FROM stats WHERE player_id = $id;";
                cmd.Parameters.AddWithValue("$id", playerId);
                using var r = cmd.ExecuteReader();
                if (r.Read()) (kills, deaths, matches, roadkills) = (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
            }
            var weapons = new Dictionary<string, long>();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT weapon_id, kills FROM weapon_kills WHERE player_id = $id ORDER BY weapon_id;";
                cmd.Parameters.AddWithValue("$id", playerId);
                using var r = cmd.ExecuteReader();
                while (r.Read()) weapons[r.GetString(0)] = r.GetInt64(1);
            }
            return new PlayerStats(kills, deaths, matches, weapons, roadkills);
        });

        // Kill, death and match counters share one transaction so a half-applied update cannot be read back.
        public Task RecordKillAsync(long playerId, string weaponId, bool roadkill = false) =>
            db.Write("record kill", c =>
            {
                using var tx = c.BeginTransaction();
                Exec(c, "UPDATE stats SET kills = kills + 1 WHERE player_id = $id;", ("$id", playerId));
                if (roadkill) Exec(c, "UPDATE stats SET roadkills = roadkills + 1 WHERE player_id = $id;", ("$id", playerId));
                Exec(c, "INSERT INTO weapon_kills (player_id, weapon_id, kills) VALUES ($id, $w, 1) ON CONFLICT (player_id, weapon_id) DO UPDATE SET kills = kills + 1;",
                    ("$id", playerId), ("$w", weaponId));
                tx.Commit();
            });

        public Task RecordDeathAsync(long playerId) =>
            db.Write("record death", c => Exec(c, "UPDATE stats SET deaths = deaths + 1 WHERE player_id = $id;", ("$id", playerId)));

        public Task RecordMatchAsync(long playerId) =>
            db.Write("record match", c => Exec(c, "UPDATE stats SET matches = matches + 1 WHERE player_id = $id;", ("$id", playerId)));
    }

    sealed class HuntRepo(SqliteWorker db) : IHuntRepository
    {
        public Task AppendAsync(HuntRecord h) => db.Write("append hunt", c => Exec(c,
            "INSERT INTO hunts (player_id, species, exact_species, variant, kg, distance_m, weapon, hit_along, hit_height, hit_right, hit_head, hit_pose, hit_frame, x, y, z, hunted_at) " +
            "VALUES ($pl, $sp, $ex, $va, $kg, $d, $w, $ha, $hh, $hr, $hd, $hp, $hf, $x, $y, $z, $at);",
            ("$pl", h.PlayerId), ("$sp", h.Species), ("$ex", h.Exact), ("$va", h.Variant), ("$kg", h.Kg), ("$d", h.DistanceM), ("$w", h.Weapon),
            ("$ha", (int?)h.Diagram?.Along), ("$hh", (int?)h.Diagram?.Height), ("$hr", h.Diagram is { } r ? (r.RightFlank ? 1 : 0) : null), ("$hd", h.Diagram is { } g ? (g.Head ? 1 : 0) : null),
            ("$hp", (int?)h.Diagram?.Pose), ("$hf", (int?)h.Diagram?.Frame), ("$x", h.X), ("$y", h.Y), ("$z", h.Z), ("$at", Stamp(h.AtUtc))));

        public Task<HuntPage> PageAsync(HuntQuery q) => db.Read(c =>
        {
            var args = new List<object>();
            var where = HuntSql.Where(q, args);
            var count = Convert.ToInt32(Query(c, $"SELECT COUNT(*) FROM hunts h WHERE {where};", args, r => r.GetInt64(0)).Single());
            var order = HuntSql.OrderBy(q, args);
            var rows = Query(c, $"SELECT {HuntSql.Columns} FROM hunts h JOIN players p ON p.id = h.player_id WHERE {where} ORDER BY {order} LIMIT {HuntBook.PageSize} OFFSET {q.Skip};", args, ReadRow);
            return new HuntPage(rows, count);
        });

        public Task<IReadOnlyList<HuntRow>> GetAllAsync() => db.Read<IReadOnlyList<HuntRow>>(c =>
            Query(c, $"SELECT {HuntSql.Columns} FROM hunts h JOIN players p ON p.id = h.player_id ORDER BY h.hunted_at, h.id;", new List<object>(), ReadRow));

        public Task SavePlayerKillAsync(PlayerKillRecord k) => db.Write("save player kill", c => Exec(c,
            "INSERT INTO player_kills (killer_id, victim_id, distance_m, weapon, x, y, z, killed_at) VALUES ($k, $v, $d, $w, $x, $y, $z, $at) " +
            "ON CONFLICT (killer_id) DO UPDATE SET victim_id = excluded.victim_id, distance_m = excluded.distance_m, weapon = excluded.weapon, x = excluded.x, y = excluded.y, z = excluded.z, killed_at = excluded.killed_at " +
            "WHERE excluded.distance_m > player_kills.distance_m;",
            ("$k", k.KillerId), ("$v", k.VictimId), ("$d", k.DistanceM), ("$w", k.Weapon), ("$x", k.X), ("$y", k.Y), ("$z", k.Z), ("$at", Stamp(k.AtUtc))));

        public Task<IReadOnlyList<PlayerKillRow>> GetPlayerKillsAsync() => db.Read<IReadOnlyList<PlayerKillRow>>(c =>
            Query(c, "SELECT k.killer_id, k.victim_id, killer.name, victim.name, k.distance_m, k.weapon, k.x, k.y, k.z, k.killed_at FROM player_kills k " +
                "JOIN players killer ON killer.id = k.killer_id JOIN players victim ON victim.id = k.victim_id ORDER BY k.killed_at, k.killer_id;", new List<object>(),
                r => new PlayerKillRow(new PlayerKillRecord(r.GetInt64(0), r.GetInt64(1), (float)r.GetDouble(4), r.GetString(5), (float)r.GetDouble(6), (float)r.GetDouble(7), (float)r.GetDouble(8), ParseStamp(r.GetString(9)).ToUniversalTime()), r.GetString(2), r.GetString(3))));

        static List<T> Query<T>(SqliteConnection c, string sql, List<object> args, Func<SqliteDataReader, T> read)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            for (var i = 0; i < args.Count; i++) cmd.Parameters.AddWithValue("@p" + i, args[i]); // every one is in the text: the list grows with the clauses
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(read(r));
            return list;
        }

        static HuntRow ReadRow(SqliteDataReader r)
        {
            HitDiagram? diagram = r.IsDBNull(9) ? null : new HitDiagram((byte)r.GetInt32(9), (byte)r.GetInt32(10), r.GetInt32(11) != 0, r.GetInt32(12) != 0, (byte)r.GetInt32(13), (byte)r.GetInt32(14));
            var hunt = new HuntRecord(r.GetInt64(0), r.GetInt64(1), r.GetString(3), r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : (float)r.GetDouble(6), r.IsDBNull(7) ? null : (float)r.GetDouble(7),
                r.GetString(8), diagram, (float)r.GetDouble(15), (float)r.GetDouble(16), (float)r.GetDouble(17), ParseStamp(r.GetString(18)).ToUniversalTime());
            return new HuntRow(hunt, r.GetString(2));
        }
    }

    sealed class MineralRepo(SqliteWorker db) : IMineralRepository
    {
        public Task AppendAsync(MineralFind f) => db.Write("append mineral find", c => Exec(c,
            "INSERT INTO mineral_finds (player_id, mineral, found_at) VALUES ($pl, $m, $at) ON CONFLICT (player_id, mineral) DO NOTHING;",
            ("$pl", f.PlayerId), ("$m", f.MineralId), ("$at", Stamp(f.AtUtc))));

        public Task<IReadOnlyList<MineralFind>> GetAllAsync() => db.Read<IReadOnlyList<MineralFind>>(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT player_id, mineral, found_at FROM mineral_finds ORDER BY found_at, id;";
            using var r = cmd.ExecuteReader();
            var list = new List<MineralFind>();
            while (r.Read()) list.Add(new MineralFind(r.GetInt64(0), r.GetString(1), ParseStamp(r.GetString(2)).ToUniversalTime()));
            return list;
        });
    }
}
