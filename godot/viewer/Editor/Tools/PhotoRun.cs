using Godot;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// The build process of the Photo to building tool (<c>facade_pipeline auto</c>, through uv): its output lines arrive on a queue the tool reads every frame, so the main
/// thread never waits for it. Kill stops it with its children (the model calls, the stage code).
/// </summary>
public sealed class PhotoRun
{
    readonly Process _process;
    public readonly ConcurrentQueue<string> Lines = new();
    volatile bool _outDone, _errDone;
    public double Seconds { get; private set; }

    PhotoRun(Process p) => _process = p;

    /// <summary>True when the process has ended and every line it printed was taken from <see cref="Lines"/>.</summary>
    public bool Finished => _process.HasExited && _outDone && _errDone && Lines.IsEmpty;
    public int ExitCode => _process.HasExited ? _process.ExitCode : -1;

    public static PhotoRun Start(ProcessStartInfo psi)
    {
        var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start uv");
        var run = new PhotoRun(p);
        Task.Run(() =>
        {
            try { while (p.StandardOutput.ReadLine() is { } line) run.Lines.Enqueue(line); } catch { /* the process went away */ }
            run._outDone = true;
        });
        Task.Run(() =>
        {
            // stderr is where uv and python put their own messages; a traceback's last line is what the user needs
            try { while (p.StandardError.ReadLine() is { } line) run.Lines.Enqueue("stderr: " + line); } catch { /* the process went away */ }
            run._errDone = true;
        });
        return run;
    }

    public void Tick(double delta) => Seconds += delta;

    public void Kill()
    {
        try { if (!_process.HasExited) _process.Kill(true); } catch { /* already gone */ }
    }

    public void Dispose() => _process.Dispose();
}
