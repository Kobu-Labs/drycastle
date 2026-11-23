using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Drycastle.Viewer;

/// <summary>
/// What the six `Viewer*Smoke.cs` files share: the PASS/FAIL tally, the entry wrapper that prints the verdict and
/// quits, frame and key helpers, the watcher waits, and <see cref="SmokeCtx"/> (plan.json, the scratch edits file and
/// the expected text of each step). The Python drivers match on the printed text, so keep messages as they are.
/// </summary>
public partial class Viewer
{
    bool _editOk = true;
    readonly List<(string step, double ms)> _latencies = new();

    void Check(bool pass, string message)
    {
        _editOk &= pass;
        GD.Print($"{(pass ? "PASS" : "FAIL")} {message}");
    }

    /// <summary>Runs one smoke body, prints "NAME SMOKE PASS/FAIL" (and the edit latencies, if any) and quits with its exit code.</summary>
    async Task RunSmoke(string name, Func<Task> body)
    {
        try { await body(); }
        catch (Exception e)
        {
            GD.Print($"FAIL {name} smoke threw: {e}");
            _editOk = false;
        }
        GD.Print($"{name.ToUpperInvariant()} SMOKE {(_editOk ? "PASS" : "FAIL")}");
        if (_latencies.Count > 0)
            GD.Print("edit_to_applied_ms: " + string.Join(", ", _latencies.Select(l => $"{l.step}={l.ms:F0}"))
                     + $" (mean {_latencies.Average(l => l.ms):F0})");
        GetTree().Quit(_editOk ? 0 : 1);
    }

    async Task Frame(int n = 2)
    {
        for (var i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    static void Press(Key k, bool ctrl = false, bool shift = false)
        => Input.ParseInputEvent(new InputEventKey { Keycode = k, Pressed = true, CtrlPressed = ctrl, ShiftPressed = shift });

    async Task PressWait(Key k, bool ctrl = false, bool shift = false, int frames = 2)
    {
        Press(k, ctrl, shift);
        await Frame(frames);
    }

    async Task<bool> WaitUntil(Func<bool> cond, double seconds)
    {
        var t0 = Time.GetTicksMsec();
        while (!cond())
        {
            if ((Time.GetTicksMsec() - t0) / 1000.0 > seconds) return false;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        return true;
    }

    async Task<bool> WaitIdle(double seconds)
    {
        // no parcel-run in flight and none starting for 2 s in a row
        var t0 = Time.GetTicksMsec();
        var quietSince = Time.GetTicksMsec();
        while ((Time.GetTicksMsec() - t0) / 1000.0 < seconds)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_watcher!.Busy) quietSince = Time.GetTicksMsec();
            else if (Time.GetTicksMsec() - quietSince > 2000) return true;
        }
        return false;
    }

    /// <summary>Atomic write like the editor and the MCP server do (temp file, then rename).</summary>
    static void PutEdits(string editsPath, string source)
    {
        var tmp = editsPath + ".tmp";
        File.Copy(source, tmp, true);
        File.Move(tmp, editsPath, true);
    }

    void CheckSeed(SmokeCtx c) => Check(c.Disk() == c.Expect("seed"), "scratch edits file is the planned seed");

    /// <summary>The edits file is exactly the text the Python writer produced for this step.</summary>
    void FileIs(SmokeCtx c, string step, string what)
    {
        var now = c.Disk(); var want = c.Expect(step);
        Check(now == want, $"{what}: edits.json is exactly the expected text" + (now == want ? "" : $"\n--- got\n{now}--- want\n{want}"));
    }
}

/// <summary>One smoke run: plan.json, the scratch edits file and (when the plan names one) the folder the driver validates.</summary>
class SmokeCtx : IDisposable
{
    readonly JsonDocument _doc;
    public JsonElement Plan => _doc.RootElement;
    public string EditsPath { get; }
    public string StepsDir { get; private set; } = "";

    public SmokeCtx(string planPath, string editsPath)
    {
        _doc = JsonDocument.Parse(File.ReadAllText(planPath));
        EditsPath = editsPath;
    }

    public void Dispose() => _doc.Dispose();

    public string S(string key) => Plan.GetProperty(key).GetString()!;
    public double[] D(string key) => Plan.GetProperty(key).EnumerateArray().Select(x => x.GetDouble()).ToArray();
    public string Expect(string step) => File.ReadAllText(Plan.GetProperty("expect").GetProperty(step).GetString()!);
    public string Disk() => File.ReadAllText(EditsPath);
    public string Step(string name) => Plan.GetProperty("steps").GetProperty(name).GetString()!;

    public void MakeStepsDir()
    {
        StepsDir = S("steps_dir");
        Directory.CreateDirectory(StepsDir);
    }

    /// <summary>Copies the edits file as it is now to the steps folder, for the driver's Python validator.</summary>
    public void SaveStep(string name) => File.Copy(EditsPath, Path.Combine(StepsDir, name + ".json"), true);
}
