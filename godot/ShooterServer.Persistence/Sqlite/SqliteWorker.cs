using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Suchohrad.ShooterServer.Persistence.Sqlite;

/// <summary>
/// One SQLite connection owned by one background thread. Every read and write is queued and runs there, so
/// nothing touches the database on the game tick. Work runs in FIFO order.
/// </summary>
internal sealed class SqliteWorker : IDisposable
{
    readonly SqliteConnection _conn;
    readonly Action<string> _log;
    readonly BlockingCollection<Action<SqliteConnection>> _queue = new();
    public int QueueDepth => _queue.Count;
    readonly Thread _thread;

    SqliteWorker(SqliteConnection conn, Action<string> log)
    {
        _conn = conn;
        _log = log;
        _thread = new Thread(Loop) { IsBackground = true, Name = "shooter-db" };
        _thread.Start();
    }

    /// <summary>Opens the file, turns on foreign keys, applies pending migrations, and starts the writer. Throws if the database cannot be opened or migrated.</summary>
    public static SqliteWorker Open(string path, Action<string> log)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = full, ForeignKeys = true }.ToString());
        try
        {
            conn.Open();
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL;";
                pragma.ExecuteNonQuery();
            }
            Migrations.Apply(conn);
            return new SqliteWorker(conn, log);
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    /// <summary>Runs a query or a write that returns a value. Exceptions propagate to the awaiting caller.</summary>
    public Task<T> Read<T>(Func<SqliteConnection, T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.TryAdd(c =>
            {
                try { tcs.SetResult(work(c)); }
                catch (Exception e) { tcs.SetException(e); }
            }))
            tcs.SetException(new ObjectDisposedException(nameof(SqliteWorker)));
        return tcs.Task;
    }

    /// <summary>Fire-and-forget write. A failure is logged and never thrown, so a DB problem cannot crash the match.</summary>
    public Task Write(string what, Action<SqliteConnection> work)
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
        foreach (var work in _queue.GetConsumingEnumerable()) work(_conn);
        _conn.Dispose();
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (Thread.CurrentThread != _thread) _thread.Join();
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
