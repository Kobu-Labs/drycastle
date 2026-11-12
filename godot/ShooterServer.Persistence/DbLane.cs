using System.Collections.Concurrent;
using System.Data.Common;

namespace Drycastle.ShooterServer.Persistence;

/// <summary>
/// Logs at most one line per interval and counts the lines it skipped (T2-14). A database outage fails many writes at once;
/// this keeps the log readable and still says how many went unlogged.
/// </summary>
internal sealed class ThrottledLog
{
    readonly Action<string> _log;
    readonly long _intervalMs;
    readonly object _gate = new();
    long _last = long.MinValue / 2;
    long _suppressed;

    public ThrottledLog(Action<string> log, int intervalMs = 10_000)
    {
        _log = log;
        _intervalMs = intervalMs;
    }

    public void Write(string message)
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (now - _last < _intervalMs) { _suppressed++; return; }
            var more = _suppressed > 0 ? $" ({_suppressed} more since the last line)" : "";
            _suppressed = 0;
            _last = now;
            _log(message + more);
        }
    }
}

/// <summary>
/// Caps the writes queued behind the database threads (T2-14). Past the cap a write is dropped and counted, so an outage cannot
/// grow the queue (and memory) without bound. Reads are not capped: their callers wait for them.
/// </summary>
internal sealed class WriteBudget
{
    public const int DefaultMax = 10_000;

    readonly int _max;
    readonly ThrottledLog _log;
    int _queued;
    long _dropped;

    public WriteBudget(ThrottledLog log, int max = DefaultMax)
    {
        _log = log;
        _max = max;
    }

    public long Dropped => Interlocked.Read(ref _dropped);

    public bool TryReserve(string what)
    {
        if (Interlocked.Increment(ref _queued) <= _max) return true;
        Interlocked.Decrement(ref _queued);
        var total = Interlocked.Increment(ref _dropped);
        _log.Write($"db write queue full ({_max} queued): dropped {what}; {total} dropped so far");
        return false;
    }

    public void Release() => Interlocked.Decrement(ref _queued);
}

/// <summary>
/// Reconnects with a growing pause after a failed open (T2-14): while the pause runs, jobs fail at once instead of each waiting
/// for a connect timeout. The pause starts at 1 s and doubles to 30 s; a successful open resets it.
/// </summary>
internal sealed class Reconnector
{
    readonly ThrottledLog _log;
    int _delayMs;
    long _nextTryAt;

    public Reconnector(ThrottledLog log) => _log = log;

    /// <summary>True when the connection is open (or has just been opened).</summary>
    public bool Ensure(DbConnection conn, string lane)
    {
        if (conn.State == System.Data.ConnectionState.Open) return true;
        var now = Environment.TickCount64;
        if (now < _nextTryAt) return false;
        try
        {
            conn.Open();
            _delayMs = 0;
            _log.Write($"db connection restored ({lane})");
            return true;
        }
        catch (Exception e)
        {
            _delayMs = _delayMs == 0 ? 1000 : Math.Min(_delayMs * 2, 30_000);
            _nextTryAt = now + _delayMs;
            _log.Write($"db connection down ({lane}): {e.Message}; next attempt in {_delayMs / 1000} s");
            return false;
        }
    }
}

/// <summary>
/// One connection owned by one background thread, with a FIFO job queue. The SQLite and PostgreSQL workers each run two
/// lanes: writes (in order) and reads (on their own connection, so a queued write never holds up a read; T2-12).
/// </summary>
internal sealed class DbLane<TConn> where TConn : DbConnection
{
    readonly TConn _conn;
    readonly BlockingCollection<Action<TConn>> _queue = new();
    readonly Thread _thread;
    readonly string _name;
    readonly ThrottledLog _log;
    readonly Func<TConn, bool>? _ensureOpen;

    public DbLane(TConn conn, string name, ThrottledLog log, Func<TConn, bool>? ensureOpen = null)
    {
        _conn = conn;
        _name = name;
        _log = log;
        _ensureOpen = ensureOpen;
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        _thread.Start();
    }

    public bool TryAdd(Action<TConn> job) => _queue.TryAdd(job);
    public int Depth => _queue.Count;

    void Loop()
    {
        foreach (var job in _queue.GetConsumingEnumerable())
        {
            _ensureOpen?.Invoke(_conn); // a closed connection makes the job fail quickly, see Reconnector
            job(_conn);
        }
        _conn.Dispose();
    }

    /// <summary>Stops taking jobs; the thread finishes the queue and closes its connection.</summary>
    public void Complete() => _queue.CompleteAdding();

    /// <summary>Waits up to <paramref name="wait"/> for the queue to drain. Logs how many jobs were left behind (T3-027).</summary>
    public void Join(TimeSpan wait)
    {
        if (Thread.CurrentThread == _thread) return;
        if (_thread.Join(wait)) return;
        _log.Write($"{_name} still busy after {wait.TotalSeconds:0} s: {_queue.Count} queued jobs abandoned");
    }
}
