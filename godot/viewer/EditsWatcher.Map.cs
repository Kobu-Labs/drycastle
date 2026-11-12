using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

/// <summary>
/// Hot reload of a re-exported map (`make export`): the buildings. The edits file only describes fences, trees,
/// objects and regions; buildings come from the pipeline (overrides.yaml, config/buildings, the model code) and
/// reach the viewer only through the chunk files. The export writes manifest.json last, with a sha256 per chunk
/// file, so the manifest is polled like the edits file: when it changes, the chunks whose hash differs from what
/// is loaded get their building nodes swapped (MapLoader.ReloadChunkBuildings), one chunk per frame.
/// Everything else baked into a chunk (terrain, roads, fences, trees, regions) is not reloaded: fences, trees and
/// regions follow the edits file as before, the rest needs a restart. A map whose parcels changed is not swapped
/// at all (building nodes are keyed by parcel ID); the viewer says to restart.
/// </summary>
public partial class EditsWatcher
{
    /// <summary>Raised on the main thread when a re-exported map was found: the number of chunks about to be swapped.</summary>
    public event Action<int>? MapReloading;
    /// <summary>Raised on the main thread after the swap: chunks, parcels with a building in them, milliseconds.</summary>
    public event Action<int, int, double>? MapReloaded;
    /// <summary>Raised on the main thread when the new map cannot be swapped in: what happened and what to do.</summary>
    public event Action<string>? MapReloadFailed;
    /// <summary>Number of completed map reloads.</summary>
    public int MapReloadCount { get; private set; }

    sealed record ChunkEntry(string File, Vector2I Index, string Sha);

    string _manifestPath = "";
    (DateTime mtime, long len) _manifestSeen;
    bool _manifestDirty;
    readonly Dictionary<string, string> _chunkHashes = new(); // chunk file -> sha256 of what is loaded
    string _parcelsHash = "";
    Queue<ChunkEntry>? _reload;
    int _reloadChunks, _reloadBuildings;
    ulong _reloadStartMs;

    static (DateTime, long) StatOf(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? (fi.LastWriteTimeUtc, fi.Length) : (DateTime.MinValue, -1);
        }
        catch (IOException) { return (DateTime.MinValue, -1); }
    }

    /// <summary>Remember the manifest the map was loaded from. A map without chunk hashes is not watched.</summary>
    void StartMapWatch()
    {
        if (Map.MapDir == "") return;
        var path = Path.Combine(Map.MapDir, "manifest.json");
        var seen = StatOf(path);
        if (!TryReadManifest(path, out var chunks, out var parcels, out var err))
        {
            GD.PushWarning($"edits watcher: {path} unreadable ({err}); a re-exported map will not be reloaded");
            return;
        }
        foreach (var c in chunks) _chunkHashes[c.File] = c.Sha;
        _parcelsHash = parcels;
        _manifestPath = path;
        _manifestSeen = seen;
        GD.Print($"watching {path} ({chunks.Count} chunks)");
    }

    static bool TryReadManifest(string path, out List<ChunkEntry> chunks, out string parcelsHash, out string error)
    {
        chunks = new();
        parcelsHash = "";
        error = "";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var c in doc.RootElement.GetProperty("chunks").EnumerateArray())
            {
                var ix = c.GetProperty("index");
                chunks.Add(new ChunkEntry(
                    c.GetProperty("file").GetString()!,
                    new Vector2I(ix[0].GetInt32(), ix[1].GetInt32()),
                    c.GetProperty("sha256").GetString()!));
            }
            if (doc.RootElement.TryGetProperty("inputs", out var inputs)
                && inputs.TryGetProperty("work/parcels.json", out var ph)) parcelsHash = ph.GetString() ?? "";
            return true;
        }
        catch (Exception e) { error = e.Message; return false; }
    }

    /// <summary>On the poll tick: note a manifest that changed on disk.</summary>
    void PollMap()
    {
        if (_manifestPath == "") return;
        var st = StatOf(_manifestPath);
        if (st == _manifestSeen) return;
        _manifestSeen = st;
        _manifestDirty = true;
    }

    /// <summary>Every frame: swap one queued chunk, or turn a changed manifest into a queue.</summary>
    void StepMapReload()
    {
        if (_reload != null)
        {
            if (_reload.Count > 0)
            {
                var c = _reload.Dequeue();
                try
                {
                    _reloadBuildings += Map.ReloadChunkBuildings(Path.Combine(Map.MapDir, c.File), c.Index);
                    _chunkHashes[c.File] = c.Sha;
                }
                catch (Exception e)
                {
                    // the chunks swapped so far stay; the rest is retried when the manifest changes again
                    _reload = null;
                    MapFail($"{c.File} could not be loaded ({e.Message}). Run the export again or restart the viewer.");
                }
                return;
            }
            _reload = null;
            var ms = Time.GetTicksMsec() - _reloadStartMs;
            MapReloadCount++;
            GD.Print($"map reloaded: buildings of {_reloadChunks} chunk(s), {_reloadBuildings} parcels, in {ms} ms");
            MapReloaded?.Invoke(_reloadChunks, _reloadBuildings, ms);
            return;
        }
        if (!_manifestDirty) return;
        _manifestDirty = false;
        if (!TryReadManifest(_manifestPath, out var chunks, out var parcels, out _))
        {
            _manifestSeen = default; // half-written: look again on the next poll
            return;
        }
        if (parcels != _parcelsHash)
        {
            _parcelsHash = parcels;
            MapFail("the re-exported map has different parcels. Restart the viewer to load it.");
            return;
        }
        var changed = new Queue<ChunkEntry>();
        foreach (var c in chunks)
        {
            if (!_chunkHashes.TryGetValue(c.File, out var loaded))
            {
                MapFail($"the re-exported map has a chunk that was never loaded ({c.File}). Restart the viewer to load it.");
                return;
            }
            if (loaded != c.Sha) changed.Enqueue(c);
        }
        if (changed.Count == 0) return;
        _reload = changed;
        _reloadChunks = changed.Count;
        _reloadBuildings = 0;
        _reloadStartMs = Time.GetTicksMsec();
        GD.Print($"map re-exported: reloading the buildings of {changed.Count} chunk(s)");
        MapReloading?.Invoke(changed.Count);
    }

    void MapFail(string message)
    {
        GD.Print($"map reload error: {message}");
        MapReloadFailed?.Invoke(message);
    }
}
