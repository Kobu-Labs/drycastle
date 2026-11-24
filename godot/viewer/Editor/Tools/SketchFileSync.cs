using Godot;
using System;
using System.IO;
using System.Text.Json;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// The sketch's file, sketch.json: one per user, not per checkout (same rule as sketchfile.default_path in the pipeline), written by the editor and by the MCP server.
/// Reads it when the editor opens and every quarter second, writes it on every local change. A version that goes up on every write tells the two writers apart.
/// </summary>
public sealed class SketchFileSync
{
    readonly IEditorHost _host;
    int _version;          // the version of sketch.json this editor has written or read last
    long _stamp = -1;      // its modification time then (ticks), -1 when there was no file
    double _pollT;

    public SketchFileSync(IEditorHost host) => _host = host;

    string FilePath => _host.Watcher == null || _host.Watcher.StatePath == "" ? "" : SharedPath();

    static string SharedPath()
    {
        var env = System.Environment.GetEnvironmentVariable("DRYCASTLE_SKETCH");
        if (!string.IsNullOrEmpty(env)) return Path.GetFullPath(env.StartsWith("~/") ? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), env[2..]) : env);
        var state = System.Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var baseDir = !string.IsNullOrEmpty(state) && Path.IsPathRooted(state) ? state : Path.Combine(home, ".local", "state");
        return Path.Combine(baseDir, "drycastle", "sketch.json");
    }

    /// <summary>
    /// Bring back the sketch the last session left (sketch.json; before it existed, the sketch in editor-state.json) before anything is written.
    /// <paramref name="apply"/> takes the sketch and whether it is to be written to sketch.json.
    /// </summary>
    public void Load(Action<SketchDoc, bool> apply)
    {
        if (_host.Watcher == null || _host.Watcher.StatePath == "" || _host.Map.OriginEpsg5514 == null) return;
        try
        {
            var path = FilePath;
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var sketch = SketchDoc.Parse(doc.RootElement, out var version);
                _version = version;
                _stamp = File.GetLastWriteTimeUtc(path).Ticks;
                apply(sketch, false);
            }
            else if (File.Exists(_host.Watcher.StatePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_host.Watcher.StatePath));
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("sketch", out var old) && old.ValueKind == JsonValueKind.Object
                    && SketchDoc.Parse(old, out _) is { Count: > 0 } sketch) apply(sketch, true);
            }
        }
        catch (Exception e) { GD.PushWarning($"sketch not restored from {FilePath}: {e.Message}"); }
    }

    /// <summary>Every quarter second: when something else (the MCP server) wrote a newer sketch.json, <paramref name="adopt"/> takes it over (the sketch differs from <paramref name="current"/>).</summary>
    public void Poll(double delta, SketchDoc current, Action<SketchDoc> adopt)
    {
        _pollT += delta;
        if (_pollT < 0.25) return;
        _pollT = 0;
        var path = FilePath;
        if (path == "" || _host.Map.OriginEpsg5514 == null) return;
        try
        {
            var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : -1;
            if (stamp == _stamp) return;
            var wasMissing = _stamp < 0; // a file that comes back after being gone is adopted whatever its version (the MCP server may restart numbering)
            _stamp = stamp;
            if (stamp < 0) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var next = SketchDoc.Parse(doc.RootElement, out var version);
            // Same version counts too: the viewer and the MCP server may both have written v+1 from v, and the file now holds the other one. Content decides below.
            if (version < _version && !wasMissing) return;
            _version = version;
            if (next.ToJson() == current.ToJson()) return;
            adopt(next);
        }
        catch (Exception e) { GD.PushWarning($"sketch.json not read: {e.Message}"); }
    }

    public void Write(SketchDoc doc)
    {
        var path = FilePath;
        if (path == "") return;
        try
        {
            var version = _version;
            try
            {
                // never go below what is on disk, so a reader that compares versions sees this write as new
                if (File.Exists(path)) { using var cur = JsonDocument.Parse(File.ReadAllText(path)); version = Math.Max(version, SketchDoc.VersionOf(cur.RootElement)); }
            }
            catch { /* unreadable: overwritten */ }
            _version = version + 1;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = $"{path}.{System.Environment.ProcessId}.tmp"; // unique per process: the MCP server writes its own temp name, never this one
            File.WriteAllText(tmp, doc.ToJson(_version) + "\n");
            File.Move(tmp, path, true);
            _stamp = File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch (Exception e) { GD.PushWarning($"could not write {path}: {e.Message}"); }
    }
}
