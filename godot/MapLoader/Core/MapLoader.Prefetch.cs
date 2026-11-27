using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Drycastle.MapLoader.Vegetation;
using Drycastle.ShooterCore.Colliders;

using Drycastle.MapLoader.Core;

namespace Drycastle.MapLoader;

/// <summary>
/// Reading a map ahead on worker threads: the chunk glTF files are parsed into node trees outside the scene tree (the triangles of their
/// collision nodes taken from the parsed arrays, so no GPU mesh is made for them) and the large JSON files into their documents. LoadSteps
/// then only puts the results together on the main thread, in the same order as without a prefetch. Optional: LoadSteps of a folder that
/// was not prefetched reads everything itself.
/// </summary>
public sealed partial class MapLoader
{
    /// <summary>A chunk read off the main thread: its node tree (not in the scene tree) and its collision nodes with their triangles.</summary>
    sealed class PreparedChunk
    {
        public required Node3D Scene;
        public required List<(Node3D Node, Vector3[] Faces)> Colliders;
    }

    sealed class MapPrefetch
    {
        public required string Dir;
        public required bool CollisionOnly;
        /// <summary>By the chunk file's full path.</summary>
        public required Dictionary<string, TaskCompletionSource<PreparedChunk>> Chunks;
        /// <summary>The small files (<see cref="ReadMapFiles"/>), read into the loader's fields.</summary>
        public Task? Files;
        /// <summary>The worker also planned the pack trees after the files (a loader that never had any).</summary>
        public bool PackPlanned;
        /// <summary>trees.json parsed and dressed (after <see cref="Files"/>): the trees, their model, a warning to show.</summary>
        public Task<(List<TreeInstance> Trees, TreeModel Model, string? Warning)>? Trees;
        public Task<CropDoc>? Crops;
        public Task<ClutterDoc>? Clutter;
    }

    MapPrefetch? _prefetch;

    /// <summary>The prefetch of the folder being loaded, when it was made with the same settings; null otherwise.</summary>
    MapPrefetch? Ahead => _prefetch is { } p && p.Dir == _mapDir && p.CollisionOnly == CollisionOnly ? p : null;

    /// <summary>True while LoadSteps waits for a worker (it repeats its last fraction): a caller that draws frames should let one pass.</summary>
    public bool Waiting { get; private set; }

    /// <summary>LoadSteps waits for a worker by yielding (true: a caller that draws frames between steps) or by blocking the thread (false: Load, the server).</summary>
    public bool YieldWhileWaiting { get; set; }

    /// <summary>
    /// Starts reading <paramref name="mapDir"/> on <paramref name="threads"/> worker threads, for the LoadSteps of the same folder that follows.
    /// Set <see cref="CollisionOnly"/> first: the workers leave out what a collision-only load does not keep.
    /// </summary>
    public void Prefetch(string mapDir, int threads)
    {
        var dir = Path.GetFullPath(mapDir);
        var manifestBytes = File.ReadAllBytes(Path.Combine(dir, "manifest.json"));
        using var manifest = JsonDocument.Parse(manifestBytes);
        JsonElement manifestRoot() => JsonDocument.Parse(manifestBytes).RootElement; // its own document: the worker outlives this call
        var chunkFiles = manifest.RootElement.GetProperty("chunks").EnumerateArray().Select(c => Path.Combine(dir, c.GetProperty("file").GetString()!)).ToList();
        var p = new MapPrefetch
        {
            Dir = dir, CollisionOnly = CollisionOnly,
            Chunks = chunkFiles.Distinct().ToDictionary(f => f, _ => new TaskCompletionSource<PreparedChunk>(TaskCreationOptions.RunContinuationsAsynchronously)),
        };
        string? Listed(string key) => manifest.RootElement.TryGetProperty("files", out var fs) && fs.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.String
            && Path.Combine(dir, e.GetString()!) is var path && File.Exists(path) ? path : null;
        p.PackPlanned = _packRoot == null && _chunkRoots.Count == 0; // nothing to free: the plan touches no node
        var planPack = p.PackPlanned;
        _stopPrefetch = false;
        var started = new List<Task>();
        var files = p.Files = Task.Run(() =>
        {
            var t = LoadTimers.Start();
            ReadMapFiles(dir, manifestRoot());
            t = LoadTimers.Lap("worker.files", t);
            if (planPack) { PlanPackTrees(free: false); LoadTimers.Lap("worker.plan_pack_trees", t); }
        });
        started.Add(files);
        started.Add(p.Trees = Task.Run(async () => { await files; var t = LoadTimers.Start(); var r = ParseTrees(TreesJson); LoadTimers.Lap("worker.trees", t); return r; }));
        if (Listed("crops") is { } crops) started.Add(p.Crops = Task.Run(() => { var t = LoadTimers.Start(); var d = CropDoc.Parse(File.ReadAllText(crops)); LoadTimers.Lap("worker.crops_parse", t); return d; }));
        if (!CollisionOnly && Listed("clutter") is { } clutter) started.Add(p.Clutter = Task.Run(() => { var t = LoadTimers.Start(); var d = ClutterDoc.Parse(File.ReadAllText(clutter)); LoadTimers.Lap("worker.clutter_parse", t); return d; }));
        var collisionOnly = CollisionOnly;
        var next = -1;
        for (var w = 0; w < Math.Max(1, threads); w++)
            started.Add(Task.Factory.StartNew(() =>
            {
                for (int i; (i = Interlocked.Increment(ref next)) < chunkFiles.Count;)
                {
                    var tcs = p.Chunks[chunkFiles[i]];
                    if (tcs.Task.IsCompleted) continue;
                    if (_stopPrefetch) { tcs.TrySetCanceled(); continue; } // the game is closing: leave the rest unread
                    try { tcs.SetResult(PrepareChunk(chunkFiles[i], collisionOnly)); }
                    catch (Exception e) { tcs.TrySetException(e); }
                }
            }, TaskCreationOptions.LongRunning));
        lock (_workers) { _workers.RemoveAll(t => t.IsCompleted); _workers.AddRange(started); }
        _prefetch = p;
    }

    /// <summary>Set by <see cref="StopPrefetch"/>: the workers read no further file.</summary>
    volatile bool _stopPrefetch;

    /// <summary>The worker tasks of <see cref="Prefetch"/>, for <see cref="StopPrefetch"/> to wait for.</summary>
    readonly List<Task> _workers = new();

    /// <summary>
    /// Stops the prefetch workers (this loader's and the static glTF worker) and waits up to <paramref name="waitMs"/> in total for them
    /// and for the rim and crop-sheet jobs to end, so the game can quit without a worker still calling into the engine. A file being read
    /// is finished; the files after it are skipped. Safe to call when nothing was started, and twice.
    /// </summary>
    public void StopPrefetch(int waitMs)
    {
        _stopPrefetch = true;
        GltfStop = true;
        var tasks = new List<Task>();
        lock (_workers) tasks.AddRange(_workers);
        if (_rimJob is { } rim) tasks.Add(rim);
        if (_cropSheetsJob is { } sheets) tasks.Add(sheets);
        lock (GltfWorkers) tasks.AddRange(GltfWorkers);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var task in tasks)
        {
            try { task.Wait(Math.Max(0, waitMs - (int)clock.ElapsedMilliseconds)); }
            catch (AggregateException) { } // a failed worker: nothing left to wait for
        }
    }

    /// <summary>Waits for <paramref name="task"/>: yields <paramref name="at"/> with <see cref="Waiting"/> set, or blocks, as <see cref="YieldWhileWaiting"/> says.</summary>
    IEnumerable<float> WaitFor(Task task, float at)
    {
        if (task.IsCompleted) yield break;
        if (!YieldWhileWaiting)
        {
            ((IAsyncResult)task).AsyncWaitHandle.WaitOne();
            yield break;
        }
        Waiting = true;
        while (!task.IsCompleted) yield return at;
        Waiting = false;
    }

    /// <summary>
    /// Reads one chunk file into a node tree outside the scene tree; safe on a worker thread. The collision nodes keep no mesh (their
    /// triangles come from the parsed arrays); on a collision-only load the visual nodes keep none either, and those without a collision
    /// node under them are freed.
    /// </summary>
    static PreparedChunk PrepareChunk(string glbPath, bool collisionOnly)
    {
        var t = LoadTimers.Start();
        var doc = new GltfDocument();
        var state = new GltfState();
        var err = doc.AppendFromFile(glbPath, state);
        if (err != Error.Ok) throw new InvalidDataException($"glTF load failed ({err}): {glbPath}");
        t = LoadTimers.Lap("worker.chunk_parse", t);
        var nodes = state.GetNodes();
        var meshes = state.GetMeshes();
        var collision = new List<(int Index, Vector3[] Faces)>();
        var stripped = new List<int>();
        for (var i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            if (n.Mesh < 0) continue;
            if (n.ResourceName.EndsWith("_collision"))
            {
                collision.Add((i, FacesOf(meshes[n.Mesh].Mesh)));
                n.Mesh = -1;
            }
            else if (collisionOnly)
            {
                stripped.Add(i);
                n.Mesh = -1;
            }
        }
        var scene = doc.GenerateScene(state) as Node3D ?? throw new InvalidDataException($"glTF has no Node3D root: {glbPath}");
        var colliders = new List<(Node3D, Vector3[])>(collision.Count);
        foreach (var (index, faces) in collision)
            if (state.GetSceneNode(index) is Node3D node) colliders.Add((node, faces));
        foreach (var index in stripped)
            // a node freed with an earlier, freed ancestor is not valid any more; one with a collision node under it is kept with the collider
            if (state.GetSceneNode(index) is { } node && GodotObject.IsInstanceValid(node) && node != scene && !HasCollisionDescendant(node)) node.Free();
        LoadTimers.Lap("worker.chunk_scene", t);
        return new PreparedChunk { Scene = scene, Colliders = colliders };
    }

    /// <summary>The triangles of a parsed glTF mesh, three corners each, in the mesh's own frame (a chunk's nodes have identity transforms).</summary>
    static Vector3[] FacesOf(ImporterMesh mesh)
    {
        var faces = new List<Vector3>();
        for (var s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            if (mesh.GetSurfacePrimitiveType(s) != Mesh.PrimitiveType.Triangles) continue;
            var arrays = mesh.GetSurfaceArrays(s);
            var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var index = arrays[(int)Mesh.ArrayType.Index];
            var idx = index.VariantType == Variant.Type.Nil ? System.Array.Empty<int>() : index.AsInt32Array();
            if (idx.Length == 0) faces.AddRange(verts);
            else foreach (var i in idx) faces.Add(verts[i]);
        }
        return faces.ToArray();
    }
}
