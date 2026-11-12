using Npgsql;

namespace Drycastle.ShooterServer.Persistence.Postgres;

/// <summary>
/// The PostgreSQL database behind the store: a write connection and a read connection, each on its own thread (same model as
/// the SQLite worker). Every read and write is queued and runs on one of those threads, so nothing touches the database on the
/// game tick. Writes run in FIFO order; reads never wait behind them (T2-12). A dropped connection is reopened before the next
/// job, with a growing pause while the database is down (T2-14).
/// </summary>
internal sealed class PostgresWorker : IDisposable
{
    /// <summary>How long Dispose waits for the queued writes before it gives up on them (T3-027).</summary>
    static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(5);

    readonly ThrottledLog _log;
    readonly WriteBudget _budget;
    public int QueueDepth => _writer.Depth + _reader.Depth;

    readonly DbLane<NpgsqlConnection> _writer;
    readonly DbLane<NpgsqlConnection> _reader;

    PostgresWorker(NpgsqlConnection writer, NpgsqlConnection reader, Action<string> log)
    {
        _log = new ThrottledLog(log);
        _budget = new WriteBudget(_log);
        var writeUp = new Reconnector(_log);
        var readUp = new Reconnector(_log);
        _writer = new DbLane<NpgsqlConnection>(writer, "shooter-db", _log, c => writeUp.Ensure(c, "write"));
        _reader = new DbLane<NpgsqlConnection>(reader, "shooter-db-read", _log, c => readUp.Ensure(c, "read"));
    }

    /// <summary>Connects (retrying while the database starts), applies pending migrations and starts the workers. Throws if it cannot.</summary>
    public static PostgresWorker Open(string connectionString, Action<string> log, int attempts = 30, int delayMs = 2000)
    {
        var conn = new NpgsqlConnection(connectionString);
        for (var i = 1; ; i++)
        {
            try { conn.Open(); break; }
            catch (Exception e) when (i < attempts)
            {
                log($"postgres not ready ({e.Message}); retry {i}/{attempts - 1}");
                Thread.Sleep(delayMs);
            }
            catch { conn.Dispose(); throw; }
        }
        NpgsqlConnection? reader = null;
        try
        {
            PgMigrations.Apply(conn);
            reader = new NpgsqlConnection(connectionString); // opened by its first job, see Reconnector
            return new PostgresWorker(conn, reader, log);
        }
        catch
        {
            reader?.Dispose();
            conn.Dispose();
            throw;
        }
    }

    /// <summary>Runs a query on the read connection. Exceptions propagate to the awaiting caller.</summary>
    public Task<T> Read<T>(Func<NpgsqlConnection, T> work) => Run(_reader, work);

    /// <summary>
    /// Runs work that reads and writes and returns a value (a new profile's insert and read). It waits its turn behind the queued
    /// writes, so it sees them. Exceptions propagate to the awaiting caller.
    /// </summary>
    public Task<T> Mutate<T>(Func<NpgsqlConnection, T> work) => Run(_writer, work);

    static Task<T> Run<T>(DbLane<NpgsqlConnection> lane, Func<NpgsqlConnection, T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!lane.TryAdd(c =>
            {
                try { tcs.SetResult(work(c)); }
                catch (Exception e) { tcs.SetException(e); }
            }))
            tcs.SetException(new ObjectDisposedException(nameof(PostgresWorker)));
        return tcs.Task;
    }

    /// <summary>
    /// Fire-and-forget write. A failure is logged (rate-limited) and never thrown, so a database problem cannot crash the match.
    /// Past the write budget the write is dropped and counted instead (T2-14). With <paramref name="report"/> the returned task
    /// faults when the write fails or is dropped, so a caller that tells a player "saved" can tell the truth (#195); it is still
    /// logged, and a caller that ignores the task is unaffected.
    /// </summary>
    public Task Write(string what, Action<NpgsqlConnection> work, bool report = false)
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
            if (report) tcs.SetException(new ObjectDisposedException(nameof(PostgresWorker))); else tcs.SetResult();
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

/// <summary>Applies the embedded migrations-pg/NNNN_name.sql files in version order, each once.</summary>
internal static class PgMigrations
{
    const string Prefix = "migrations-pg/";

    public static int Apply(NpgsqlConnection conn)
    {
        Run(conn, "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at TIMESTAMPTZ NOT NULL);");
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
            Run(conn, sql);
            using (var record = conn.CreateCommand())
            {
                record.CommandText = "INSERT INTO schema_migrations (version, name, applied_at) VALUES (@v, @n, @t);";
                record.Parameters.AddWithValue("v", version);
                record.Parameters.AddWithValue("n", name);
                record.Parameters.AddWithValue("t", DateTime.UtcNow);
                record.ExecuteNonQuery();
            }
            tx.Commit();
            count++;
        }
        return count;
    }

    static void Run(NpgsqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static IEnumerable<(int Version, string Name, string Sql)> Load()
    {
        var asm = typeof(PgMigrations).Assembly;
        var found = new List<(int, string, string)>();
        foreach (var res in asm.GetManifestResourceNames())
        {
            if (!res.StartsWith(Prefix, StringComparison.Ordinal) || !res.EndsWith(".sql", StringComparison.Ordinal)) continue;
            var file = res[Prefix.Length..];
            if (file.IndexOf('_') != 4 || !int.TryParse(file.AsSpan(0, 4), out var version))
                throw new InvalidOperationException($"migration {file} must be named NNNN_name.sql");
            using var reader = new StreamReader(asm.GetManifestResourceStream(res)!);
            found.Add((version, file[..^4], reader.ReadToEnd()));
        }
        return found.OrderBy(m => m.Item1);
    }
}
