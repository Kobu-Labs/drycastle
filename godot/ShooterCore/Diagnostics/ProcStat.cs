namespace Drycastle.ShooterCore.Diagnostics;

/// <summary>Reads a line of Linux's /proc/&lt;pid&gt;/task/&lt;tid&gt;/stat: the thread's name and the CPU time it has used.</summary>
public static class ProcStat
{
    /// <summary>The kernel's USER_HZ: the unit of the CPU times in /proc (100 on every Linux the server runs on).</summary>
    public const int ClockTicksPerSecond = 100;

    /// <summary>
    /// The name and the CPU clock ticks (user + system) of a stat line: <c>tid (name) state ppid ... utime stime ...</c>. The name may hold spaces and
    /// brackets, so the fields are counted from the last closing bracket. False when the line is not one.
    /// </summary>
    public static bool Parse(string stat, out string comm, out long ticks)
    {
        comm = ""; ticks = 0;
        int open = stat.IndexOf('('), close = stat.LastIndexOf(')');
        if (open < 0 || close < open) return false;
        comm = stat.Substring(open + 1, close - open - 1);
        var rest = stat.Substring(close + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries); // rest[0] is field 3 (state); utime and stime are fields 14 and 15
        if (rest.Length < 13 || !long.TryParse(rest[11], out var user) || !long.TryParse(rest[12], out var system)) return false;
        ticks = user + system;
        return true;
    }

    /// <summary>The key threads of one kind share: lower case, anything but letters and digits an underscore, a trailing number dropped ("WorkerThread 12" is <c>workerthread</c>).</summary>
    public static string Group(string comm)
    {
        var s = comm.TrimEnd(' ', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '-', '_', '#').ToLowerInvariant();
        var chars = s.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray();
        var key = new string(chars).Trim('_');
        return key.Length == 0 ? "thread" : key;
    }
}
