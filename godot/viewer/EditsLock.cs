using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// The writer lock on edits.json: an exclusive advisory flock(2) on <c>&lt;edits&gt;.lock</c>, the same file and the same call the MCP server
/// (pipeline mcp_server.edits_lock, Python fcntl.flock) takes around its load..save window, so the editor and the MCP server never lose each other's
/// write. flock is called directly (not through FileStream's own FileShare locking, which a runtime switch can turn off) so it is flock-compatible
/// by construction. Not Unix (Windows): FileShare.None on the lock file, which is the same mutual exclusion between processes that use this class.
/// Waits a bounded time: a caller that gets null reports and does not write.
/// </summary>
public sealed class EditsLock : IDisposable
{
    public const int DefaultWaitMs = 2000;

    [ThreadStatic] static int _heldHere; // a nested Mutate on this thread must not wait for itself

    readonly FileStream? _file;
    readonly bool _counted;

    EditsLock(FileStream? file, bool counted) { _file = file; _counted = counted; }

    [DllImport("libc")]
    static extern int flock(int fd, int operation);

    const int LockEx = 2, LockNb = 4, LockUn = 8;

    /// <summary>Take the lock of <paramref name="editsPath"/>, waiting up to <paramref name="waitMs"/>; null when another writer still holds it (nothing was taken).</summary>
    public static EditsLock? TryAcquire(string editsPath, int waitMs = DefaultWaitMs)
    {
        if (_heldHere > 0) { _heldHere++; return new EditsLock(null, true); }
        var full = Path.GetFullPath(editsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var lockPath = full + ".lock";
        var until = Environment.TickCount64 + waitMs;
        while (true)
        {
            FileStream? fs = null;
            try
            {
                fs = OperatingSystem.IsWindows()
                    ? new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
                    : new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                if (OperatingSystem.IsWindows() || flock((int)fs.SafeFileHandle.DangerousGetHandle(), LockEx | LockNb) == 0)
                {
                    _heldHere++;
                    return new EditsLock(fs, true);
                }
                fs.Dispose(); // flock refused (EWOULDBLOCK: held, or EINTR): try again until the wait is over
            }
            // The runtime itself flocks a file it opens (shared, non-blocking), so opening fails with a sharing violation while another process (the MCP server,
            // Python flock LOCK_EX) holds the lock: that is "busy", same as EWOULDBLOCK above. Any other IOException ends as a timeout too, never as a write.
            catch (IOException) { fs?.Dispose(); }
            if (Environment.TickCount64 >= until) return null;
            Thread.Sleep(20);
        }
    }

    public void Dispose()
    {
        if (_counted) _heldHere--;
        if (_file == null) return;
        if (!OperatingSystem.IsWindows()) { try { flock((int)_file.SafeFileHandle.DangerousGetHandle(), LockUn); } catch { /* closing the file releases it anyway */ } }
        _file.Dispose();
    }
}
