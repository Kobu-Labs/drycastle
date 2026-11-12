using Microsoft.Data.Sqlite;

namespace Drycastle.ShooterServer.Persistence.Sqlite;

/// <summary>
/// The SQLite database behind the store: a write connection and a read connection, each on its own thread (WAL lets the
/// reader run while the writer commits). Every read and write is queued and runs on one of those threads, so nothing touches
/// the database on the game tick. Writes run in FIFO order; reads never wait behind them (T2-12).
/// </summary>
internal sealed class SqliteWorker : IDisposable
{
    /// <summary>How long Dispose waits for the queued writes before it gives up on them (T3-027).</summary>
    static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(5);

    readonly ThrottledLog _log;
    readonly WriteBudget _budget;
    public int QueueDepth => _writer.Depth + _reader.Depth;

    readonly DbLane<SqliteConnection> _writer;
    readonly DbLane<SqliteConnection> _reader;

    SqliteWorker(SqliteConnection writer, SqliteConnection reader, Action<string> log)
    {
        _log = new ThrottledLog(log);
        _budget = new WriteBudget(_log);
        _writer = new DbLane<SqliteConnection>(writer, "shooter-db", _log);
        _reader = new DbLane<SqliteConnection>(reader, "shooter-db-read", _log);
    }

    /// <summary>Opens the file, turns on foreign keys, applies pending migrations, and starts the workers. Throws if the database cannot be opened or migrated.</summary>
    public static SqliteWorker Open(string path, Action<string> log)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true }.ToString());
        SqliteConnection? reader = null;
        try
        {
            writer.Open();
            using (var pragma = writer.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL;";
                pragma.ExecuteNonQuery();
            }
            // WAL with NORMAL: a commit no longer waits for the disk on every write. A power cut can lose the last few
            // commits but never corrupts the file (T2-13).
            using (var pragma = writer.CreateCommand())
            {
                pragma.CommandText = "PRAGMA synchronous=NORMAL;";
                pragma.ExecuteNonQuery();
            }
            Migrations.Apply(writer);
            reader = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true, Mode = SqliteOpenMode.ReadOnly }.ToString());
            reader.Open();
            return new SqliteWorker(writer, reader, log);
        }
        catch
        {
            reader?.Dispose();
            writer.Dispose();
            throw;
        }
    }

    /// <summary>Runs a query on the read connection. Exceptions propagate to the awaiting caller.</summary>
    public Task<T> Read<T>(Func<SqliteConnection, T> work) => Run(_reader, work);

    /// <summary>
    /// Runs work that reads and writes and returns a value (a new profile's insert and read). It waits its turn behind the queued
    /// writes, so it sees them. Exceptions propagate to the awaiting caller.
    /// </summary>
    public Task<T> Mutate<T>(Func<SqliteConnection, T> work) => Run(_writer, work);

    static Task<T> Run<T>(DbLane<SqliteConnection> lane, Func<SqliteConnection, T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!lane.TryAdd(c =>
            {
                try { tcs.SetResult(work(c)); }
                catch (Exception e) { tcs.SetException(e); }
            }))
            tcs.SetException(new ObjectDisposedException(nameof(SqliteWorker)));
        return tcs.Task;
    }

    /// <summary>
    /// Fire-and-forget write. A failure is logged (rate-limited) and never thrown, so a DB problem cannot crash the match.
    /// Past the write budget the write is dropped and counted instead (T2-14). With <paramref name="report"/> the returned task
    /// faults when the write fails or is dropped, so a caller that tells a player "saved" can tell the truth (#195); it is still
    /// logged, and a caller that ignores the task is unaffected.
    /// </summary>
    public Task Write(string what, Action<SqliteConnection> work, bool report = false)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_budget.TryReserve(what))
        {
            if (report) tcs.SetException(new IOException($"db write dropped, queue full ({what})")); else tcs.SetResult();
            return tcs.Task;
        }
        if (!_writer.TryAdd(c =>
            {
                Exception? failure = null;
                try { work(c); }
                catch (Exception e) { failure = e; _log.Write($"db write failed ({what}): {e.Message}"); }
                finally { _budget.Release(); }
                if (failure != null && report) tcs.SetException(failure); else tcs.SetResult();
            }))
        {
            _budget.Release();
            _log.Write($"db write dropped, store is closed ({what})");
            if (report) tcs.SetException(new ObjectDisposedException(nameof(SqliteWorker))); else tcs.SetResult();
        }
        return tcs.Task;
    }

    public void Dispose()
    {
        _writer.Complete();
        _reader.Complete();
        _writer.Join(ShutdownWait);
        _reader.Join(ShutdownWait);
    }
}

/// <summary>Applies numbered migrations/NNNN_name.sql embedded resources in version order, each once.</summary>
internal static class Migrations
{
    public static int Apply(SqliteConnection conn)
    {
        using (var create = conn.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at TEXT NOT NULL);";
            create.ExecuteNonQuery();
        }

        var applied = new HashSet<int>();
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT version FROM schema_migrations;";
            using var r = read.ExecuteReader();
            while (r.Read()) applied.Add(r.GetInt32(0));
        }

        var count = 0;
        foreach (var (version, name, sql) in Load())
        {
            if (applied.Contains(version)) continue;
            using var tx = conn.BeginTransaction();
            using (var run = conn.CreateCommand())
            {
                run.Transaction = tx;
                run.CommandText = sql;
                run.ExecuteNonQuery();
            }
            using (var record = conn.CreateCommand())
            {
                record.Transaction = tx;
                record.CommandText = "INSERT INTO schema_migrations (version, name, applied_at) VALUES ($v, $n, $t);";
                record.Parameters.AddWithValue("$v", version);
                record.Parameters.AddWithValue("$n", name);
                record.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
                record.ExecuteNonQuery();
            }
            tx.Commit();
            count++;
        }
        return count;
    }

    static IEnumerable<(int Version, string Name, string Sql)> Load()
    {
        var asm = typeof(Migrations).Assembly;
        var found = new List<(int Version, string Name, string Sql)>();
        foreach (var res in asm.GetManifestResourceNames())
        {
            if (!res.StartsWith("migrations/", StringComparison.Ordinal) || !res.EndsWith(".sql", StringComparison.Ordinal)) continue;
            var file = res["migrations/".Length..];
            var dash = file.IndexOf('_');
            if (dash != 4 || !int.TryParse(file.AsSpan(0, 4), out var version))
                throw new InvalidOperationException($"migration {file} must be named NNNN_name.sql");
            using var stream = asm.GetManifestResourceStream(res)!;
            using var reader = new StreamReader(stream);
            found.Add((version, file[..^4], reader.ReadToEnd()));
        }
        var dupes = found.GroupBy(m => m.Version).FirstOrDefault(g => g.Count() > 1);
        if (dupes != null) throw new InvalidOperationException($"two migrations numbered {dupes.Key:0000}");
        return found.OrderBy(m => m.Version);
    }
}
