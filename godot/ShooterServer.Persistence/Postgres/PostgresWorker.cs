using System.Collections.Concurrent;
using Npgsql;

namespace Suchohrad.ShooterServer.Persistence.Postgres;

/// <summary>
/// One PostgreSQL connection owned by one background thread (same model as the SQLite worker): every read and write is queued and
/// runs there in FIFO order, so nothing touches the database on the game tick. A dropped connection is reopened before the next job.
/// </summary>
internal sealed class PostgresWorker : IDisposable
{
    readonly NpgsqlConnection _conn;
    readonly Action<string> _log;
    readonly BlockingCollection<Action<NpgsqlConnection>> _queue = new();
    public int QueueDepth => _queue.Count;
    readonly Thread _thread;

    PostgresWorker(NpgsqlConnection conn, Action<string> log)
    {
        _conn = conn;
        _log = log;
        _thread = new Thread(Loop) { IsBackground = true, Name = "shooter-db" };
        _thread.Start();
    }

    /// <summary>Connects (retrying while the database starts), applies pending migrations and starts the worker. Throws if it cannot.</summary>
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
        try
        {
            PgMigrations.Apply(conn);
            return new PostgresWorker(conn, log);
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    public Task<T> Read<T>(Func<NpgsqlConnection, T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.TryAdd(c =>
            {
                try { tcs.SetResult(work(c)); }
                catch (Exception e) { tcs.SetException(e); }
            }))
            tcs.SetException(new ObjectDisposedException(nameof(PostgresWorker)));
        return tcs.Task;
    }

    /// <summary>Fire-and-forget write. A failure is logged and never thrown, so a database problem cannot crash the match.</summary>
    public Task Write(string what, Action<NpgsqlConnection> work)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.TryAdd(c =>
            {
                try { work(c); }
                catch (Exception e) { _log($"db write failed ({what}): {e.Message}"); }
                tcs.SetResult();
            }))
        {
            _log($"db write dropped, store is closed ({what})");
            tcs.SetResult();
        }
        return tcs.Task;
    }

    void Loop()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            if (_conn.State != System.Data.ConnectionState.Open)
            {
                try { _conn.Open(); _log("db connection restored"); }
                catch (Exception e) { _log($"db connection down: {e.Message}"); }
            }
            work(_conn);
        }
        _conn.Dispose();
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (Thread.CurrentThread != _thread) _thread.Join();
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
