using Godot;
using Drycastle.ShooterCore.Buildings.Destruction;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Glass;
using NVec3 = System.Numerics.Vector3;
using MapLoaderT = Drycastle.MapLoader.MapLoader;

namespace Drycastle.Shooter.Buildings.Destruction;

/// <summary>
/// The client's destructible houses (docs/house-destruction.md, WP-F). One <see cref="HouseReplica"/> per house of house_voxels.bin follows the server's
/// carve ops (242), the catch-up and resync runs (243) and the settings (244). An undamaged house keeps the map's flat material and its exact shell collider;
/// on its first carve its visual gets the discard shader (an R8 3D texture of the cells it hides near holes, HouseDamageLook), a voxel mesh draws the live cells there
/// and the attic's inside, and its collider
/// becomes voxel trimesh columns (<see cref="HouseVoxelCollider"/>, the server's too). Texture, mesh and collider are rebuilt at most once per house per frame, and the houses of
/// one frame beyond the first only within <see cref="RebuildBudgetMs"/> (the rest wait for the next frames). Fallen and crumbled cells go to <see cref="Debris"/> (WP-G) in the frame's
/// rebuild, after the collider has its hole. The wire is <see cref="HouseCodec"/>'s. A different op seq or hash than the server's is logged ("house desync &lt;id&gt;") and a 249 asked for.
/// The vehicle this client drives cuts its way through a house at once (<see cref="IHousePlough"/>, the guesses of <see cref="HouseReplica"/>): its prediction never
/// stops at the wall and the hole is there before the server's op (254) comes. Another vehicle's cut waits until that vehicle is drawn there (<see cref="Due"/>), so the
/// wall does not open ahead of it. Everything is drawn and collided from <see cref="HouseReplica.View"/>.
/// One per map, client only: <see cref="Current"/>.
/// </summary>
public partial class HouseWorldNode : Node3D, IHousePlough
{
    public const string ShaderPath = "res://shaders/house_destruction.gdshader";
    /// <summary>The same cut for a house's textured faces (fence walls): the library texture times the vertex colour.</summary>
    public const string TexShaderPath = "res://shaders/house_destruction_tex.gdshader";
    /// <summary>A fence wall's grid (fence_&lt;index&gt;, docs/fence-destruction.md): one cell thick, its faces, caps and piers up to this many cells outside it read the nearest cell.</summary>
    const string FenceGridPrefix = "fence_";
    const float FenceEdgeCells = 2f;
    public static HouseWorldNode? Current { get; private set; }
    const double ResyncRetrySeconds = 5;
    /// <summary>A house still unlike the server's after this long of asking with the server's house unchanged (its map data differs: a full 243 never matches) is frozen as it is until the round resets.</summary>
    const double ResyncGiveUpSeconds = 20;

    IHouseDebris? _debris;
    /// <summary>The debris of fallen parts (WP-G); null = none. Set, it gets the current rubble mode.</summary>
    public IHouseDebris? Debris
    {
        get => _debris;
        set { _debris = value; _debris?.SetRubble(_rubbleStay); }
    }

    /// <summary>Whether a plough's op (254) may be drawn now (set by the net client): the vehicle that cut it is drawn there, or it is this client's own. Null: at once.</summary>
    public Func<HouseCutMsg, bool>? Due { get; set; }
    /// <summary>A held op is applied after this long whatever <see cref="Due"/> says (seconds): a stalled render clock never keeps a wall up.</summary>
    const double MaxHoldS = 0.4;

    /// <summary>Sends 249 HouseResync for a house (set by the net client).</summary>
    public Action<ushort>? RequestResync { get; set; }

    MapLoaderT _map = null!;
    DestructionConfig _cfg = null!;
    /// <summary>house_destruction.json as this map was loaded with it.</summary>
    public DestructionConfig Config => _cfg;
    House?[] _houses = [];
    readonly List<House> _dirty = new();
    readonly List<House> _awaiting = new();
    bool _rubbleStay, _warmed;
    static Shader? _shader, _texShader;

    /// <summary>The rubble mode the server last set (244): stay or fade. The fences' falling pieces follow it.</summary>
    public bool RubbleStay => _rubbleStay;

    /// <summary>
    /// A window pane of a hollow house (windows.json): its map node, its frame cells (<see cref="HouseFrames"/>, found at the house's first carve), whether the frame is gone
    /// and whether the server has shattered it. The node shows only while it stands in a frame; the glass client's shard rim shows only while the frame is there.
    /// </summary>
    sealed class Pane
    {
        public int Index;
        public GlassPaneRect Rect;
        public MeshInstance3D? Node;
        public int[]? Frame;
        public bool Gone, Shattered;
    }

    /// <summary>Per house (grid index): its panes.</summary>
    List<Pane>[] _panes = [];
    /// <summary>Map pane index to its pane, for the hollow houses' panes only.</summary>
    readonly Dictionary<int, Pane> _paneByIndex = new();
    /// <summary>Told when a hollow house's pane loses or gets back its frame (map pane index, gone): the glass client hides or shows its shard rim.</summary>
    public Action<int, bool>? PaneFrameChanged { get; set; }

    /// <summary>See-through glass for the panes of the hollow houses (the map draws every window as an opaque coloured mesh; these stand in real openings).</summary>
    static readonly StandardMaterial3D ClearPane = new()
    {
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha, AlbedoColor = new Color(0.62f, 0.78f, 0.88f, 0.25f),
        Roughness = 0.08f, Metallic = 0.1f, // culled: a pane that goes through has a quad facing each way already, and two blended in one place would flicker
    };

    readonly List<Quad> _quads = new();
    readonly List<NVec3> _pos = new(), _nrm = new();
    /// <summary>Fallen parts waiting for their house's collider to get its hole (spawned in the next <see cref="_Process"/>, after the rebuild).</summary>
    readonly List<(House House, CarveResult Result, NVec3 Blast, uint Seed)> _falls = new();

    /// <summary>The houses rebuilt in one frame beyond the first stop once this much time went (ms): a blast over many houses is spread over frames.</summary>
    const double RebuildBudgetMs = 6;

    sealed class House
    {
        public House(HouseReplica replica, MapLoaderT.HouseNode? nodes)
        {
            Replica = replica;
            Visual = nodes?.Visual;
            Shape = nodes?.Collision;
            Textured = nodes?.Textured ?? [];
        }

        public readonly HouseReplica Replica;
        public readonly MeshInstance3D? Visual;
        /// <summary>Its textured faces (house_&lt;id&gt;_visual_tex_&lt;texture&gt;), drawn with the cut-out texture shader while the house is damaged.</summary>
        public readonly IReadOnlyList<MeshInstance3D> Textured;
        /// <summary>The textured faces that wear the shader now, with the material they had.</summary>
        public readonly List<(MeshInstance3D Node, Material? Original)> TexSwaps = new();
        /// <summary>The map's collider of the house (its exact shell until the first carve).</summary>
        public readonly CollisionShape3D? Shape;
        /// <summary>The voxel columns that stand in for the shell while the house is damaged (made at the first carve).</summary>
        public HouseVoxelCollider? Collider;
        public Material? FlatMaterial;
        public ShaderMaterial? Material;
        public ImageTexture3D? Texture;
        public Godot.Collections.Array<Image>? Layers;
        public byte[][] LayerBytes = [];
        public MeshInstance3D? Cut;
        /// <summary>Near-hole cells, hidden cells and the attic (made at the first carve).</summary>
        public HouseDamageLook? Look;
        public ArrayMesh? CutMesh;
        public bool Swapped, Dirty, ColliderDirty;
        public double ResyncAt;
        /// <summary>Asking for 243 since the first desync; dropped when one ends it or after <see cref="ResyncGiveUpSeconds"/>.</summary>
        public PendingAnswer Resync;
        /// <summary>The op seq of the last 243 set: one at a new op seq (the server's house moved on) restarts <see cref="Resync"/>.</summary>
        public ushort ResyncSeq;
        public ushort Index => Replica.Grid.Index;
        public string Id => Replica.Grid.Id;
        /// <summary>The server's ops waiting for <see cref="Due"/> (and those after them, in order), with the time each came.</summary>
        public readonly Queue<(object Msg, double At)> Held = new();
        /// <summary>Where the last cut was (map local): a settle-only op's falling parts are pushed from there.</summary>
        public NVec3 LastCut;
    }

    /// <summary>Each house's bounding sphere (map local), by grid index: a plough far from it skips the exact test.</summary>
    (NVec3 Centre, float Radius)[] _bounds = [];
    readonly List<House> _holding = new();
    readonly HashSet<House> _guessing = new();
    readonly List<PloughCell> _cut = new();
    readonly List<int> _near = new();
    readonly List<int> _back = new(), _fresh = new();

    /// <summary>
    /// The houses of the map's house_voxels.bin matched to the map's house nodes by id, or null when destruction is off, the map has no file or the file is
    /// broken (logged). Loads the discard shader (a loading step, not the first carve).
    /// </summary>
    public static HouseWorldNode? Create(MapLoaderT map, DestructionConfig cfg)
    {
        if (!cfg.Enabled || map.HouseVoxelsPath() is not { } path) return null;
        IReadOnlyList<HouseGrid> grids;
        try
        {
            using var f = File.OpenRead(path);
            grids = HouseVoxelFile.Read(f);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
        {
            GD.PrintErr($"client: {path}: {e.Message}; houses are not destroyed");
            return null;
        }
        if (grids.Count == 0) return null;
        _shader ??= GD.Load<Shader>(ShaderPath);
        _texShader ??= GD.Load<Shader>(TexShaderPath);
        var node = new HouseWorldNode { Name = "HouseDestruction", _map = map, _cfg = cfg, _houses = new House?[grids.Max(g => g.Index) + 1] };
        var unplaced = 0;
        foreach (var g in grids)
        {
            var nodes = map.HouseNodes.GetValueOrDefault(g.Id);
            if (nodes?.Visual == null) unplaced++;
            node._houses[g.Index] = new House(new HouseReplica(g), nodes);
        }
        node._bounds = new (NVec3, float)[node._houses.Length];
        foreach (var g in grids)
            node._bounds[g.Index] = (g.ToMap(new NVec3(g.Nx, g.Ny, g.Nz) * 0.5f), 0.5f * g.Cell * MathF.Sqrt((float)g.Nx * g.Nx + (float)g.Ny * g.Ny + (float)g.Nz * g.Nz));
        if (unplaced > 0) GD.PrintErr($"client: {unplaced} of {grids.Count} destructible houses have no house_<id>_visual node; their state is kept, not drawn");
        node.FindPanes(grids);
        return node;
    }

    /// <summary>The hollow houses' panes of windows.json, their nodes made see-through.</summary>
    void FindPanes(IReadOnlyList<HouseGrid> grids)
    {
        _panes = new List<Pane>[_houses.Length];
        for (var i = 0; i < _panes.Length; i++) _panes[i] = new();
        List<MapGlassPane> all;
        try { all = MapGlass.Parse(_map.WindowsJson); }
        catch (Drycastle.ShooterCore.Data.DataFileException e) { GD.PrintErr($"client: {e.Message}; house panes stay as they are"); return; }
        var byId = grids.ToDictionary(g => g.Id, g => g.Index);
        var nodes = new Dictionary<string, MeshInstance3D>();
        foreach (var n in _map.Root.FindChildren("window_*", "MeshInstance3D", true, false))
            if (n is MeshInstance3D m) nodes.TryAdd(m.Name, m);
        for (var i = 0; i < all.Count; i++)
        {
            if (!byId.TryGetValue(all[i].Building, out var house)) continue;
            var pane = new Pane { Index = i, Rect = all[i].Rect, Node = nodes.GetValueOrDefault(all[i].Node) };
            if (pane.Node != null) pane.Node.MaterialOverride = ClearPane;
            _panes[house].Add(pane);
            _paneByIndex[i] = pane;
        }
    }

    /// <summary>Whether map pane <paramref name="index"/> is a hollow house's (its opening is real: nothing to darken behind it).</summary>
    public bool IsHousePane(int index) => _paneByIndex.ContainsKey(index);

    /// <summary>Whether the frame of map pane <paramref name="index"/> is gone (its shard rim is not shown).</summary>
    public bool PaneGone(int index) => _paneByIndex.TryGetValue(index, out var p) && p.Gone;

    /// <summary>The server's glass state of a hollow house's pane: a shattered pane's glass is not drawn.</summary>
    public void SetPaneShattered(int index, bool shattered)
    {
        if (!_paneByIndex.TryGetValue(index, out var p)) return;
        p.Shattered = shattered;
        ShowPane(p);
    }

    static void ShowPane(Pane p)
    {
        if (p.Node != null && GodotObject.IsInstanceValid(p.Node)) p.Node.Visible = !p.Gone && !p.Shattered;
    }

    /// <summary>Which of the house's panes lost their frame (or got it back).</summary>
    void UpdatePanes(House h)
    {
        var g = h.Replica.Grid;
        foreach (var p in _panes[g.Index])
        {
            p.Frame ??= HouseFrames.FrameCells(g, p.Rect);
            var gone = p.Frame.Length > 0 && HouseFrames.Unframed(h.Replica.View, p.Frame);
            if (gone == p.Gone) continue;
            p.Gone = gone;
            ShowPane(p);
            PaneFrameChanged?.Invoke(p.Index, gone);
        }
    }

    public override void _EnterTree() => Current = this;

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
    }

    House? Of(ushort index) => index < _houses.Length ? _houses[index] : null;

    /// <summary>242: one op, applied as the server did; a mismatch asks for the house's cells.</summary>
    public void OnCarve(HouseCarveMsg m)
    {
        if (Of(m.House) is not { } h) return;
        if (h.Held.Count > 0) { Hold(h, m); return; } // after a held cut, in order
        ApplyCarve(h, m);
    }

    void ApplyCarve(House h, HouseCarveMsg m)
    {
        var (outcome, r) = h.Replica.OnCarve(m, _cfg);
        if (r != null)
        {
            MarkDirty(h);
            h.Collider?.Mark(r);
            // the debris waits for the house's rebuild this frame, so the falling parts do not start inside the old collider
            if (_debris != null) _falls.Add((h, r, h.Replica.Grid.ToMap(new NVec3(m.Op.Cx, m.Op.Cy, m.Op.Cz) / 4f), (uint)m.House << 16 | m.OpSeq));
        }
        if (outcome == HouseReplica.CarveOutcome.Desync) Desync(h);
    }

    /// <summary>254: a plough's cells; another vehicle's wait until it is drawn there (<see cref="Due"/>).</summary>
    public void OnCut(HouseCutMsg m)
    {
        if (Of(m.House) is not { } h) return;
        if (h.Held.Count > 0 || Due?.Invoke(m) == false) { Hold(h, m); return; }
        ApplyCut(h, m);
    }

    void Hold(House h, object m)
    {
        if (h.Held.Count == 0) _holding.Add(h);
        h.Held.Enqueue((m, Time.GetTicksMsec() / 1000.0));
    }

    /// <summary>The held ops that are due now, each house's in order.</summary>
    void ReleaseHeld()
    {
        var now = Time.GetTicksMsec() / 1000.0;
        for (var k = _holding.Count - 1; k >= 0; k--)
        {
            var h = _holding[k];
            while (h.Held.TryPeek(out var e))
            {
                if (e.Msg is HouseCutMsg cut && now - e.At < MaxHoldS && Due?.Invoke(cut) == false) break;
                h.Held.Dequeue();
                if (e.Msg is HouseCutMsg c) ApplyCut(h, c);
                else ApplyCarve(h, (HouseCarveMsg)e.Msg);
            }
            if (h.Held.Count == 0) _holding.RemoveAt(k);
        }
    }

    void ApplyCut(House h, HouseCutMsg m)
    {
        var g = h.Replica.Grid;
        // the cells not already gone from the view (this client's own guess drew them): only those change what is drawn
        var view = h.Replica.View;
        _fresh.Clear();
        foreach (var i in m.Cells) if ((uint)i < (uint)g.Count && view.Solid(i)) _fresh.Add(i);
        if (m.Cells.Length > 0) h.LastCut = Centre(g, m.Cells);
        var (outcome, r) = h.Replica.OnCut(m, _cfg);
        if (r != null && (_fresh.Count > 0 || r.Crumbled.Length > 0 || r.Fallen.Count > 0))
        {
            MarkDirty(h);
            h.Collider?.Mark(r);
            if (_debris != null) _falls.Add((h, r with { Carved = _fresh.ToArray() }, h.LastCut, (uint)m.House << 16 | m.OpSeq));
        }
        if (outcome == HouseReplica.CarveOutcome.Desync) Desync(h);
    }

    /// <summary>The mean of the cells' centres, map local.</summary>
    static NVec3 Centre(HouseGrid g, ReadOnlySpan<int> cells)
    {
        var sum = NVec3.Zero;
        foreach (var i in cells) { var (x, y, z) = g.Coords(i); sum += new NVec3(x + 0.5f, y + 0.5f, z + 0.5f); }
        return g.ToMap(sum / cells.Length);
    }

    // ---- the driver's own plough, guessed (IHousePlough through the prediction's DriveProbe) ----

    /// <summary>
    /// The predicted step of the vehicle this client drives: the cells it sweeps next are cut in the view at once (as the server will, <see cref="HousePlough"/>),
    /// keyed by the input; the collider loses them now, so the next step drives on through; the speed they took is returned.
    /// </summary>
    public float Plough(in PloughQuery q)
    {
        var rc = _cfg.Ram;
        if (!rc.Enabled || q.Speed < rc.KeepSpeed) return 0;
        var centre = HousePlough.Box(q, rc.MarginM).Centre;
        var reach = HousePlough.ReachRadius(q, rc);
        _near.Clear();
        for (var i = 0; i < _houses.Length; i++)
        {
            if (_houses[i] is not { } hh || hh.Replica.AwaitingResync) continue;
            var (c, r) = _bounds[i];
            if (NVec3.DistanceSquared(c, centre) <= (r + reach) * (r + reach)) _near.Add(i);
        }
        if (_near.Count == 0) return 0;
        var seq = q.Seq;
        var plan = HousePlough.Plan(q, _cfg, _near, i => _houses[i]!.Replica.Cells, (h, i) => _houses[h]!.Replica.PresentFor(i, seq), _cut);
        if (plan.Count == 0) return 0;
        for (var k = 0; k < _cut.Count;)
        {
            var h = _houses[_cut[k].House]!;
            var view = h.Replica.View;
            _fresh.Clear();
            for (; k < _cut.Count && _cut[k].House == h.Index; k++)
            {
                var i = _cut[k].Cell;
                if (view.Solid(i)) _fresh.Add(i);
                h.Replica.Guess(i, seq);
            }
            _guessing.Add(h);
            if (_fresh.Count == 0) continue; // a replay cutting again what it had cut
            var cells = _fresh.ToArray();
            h.LastCut = Centre(h.Replica.Grid, cells);
            if (!h.Swapped) Swap(h);
            h.Collider?.Mark(cells);
            RebuildCollider(h);
            MarkDirty(h);
            _debris?.Fall(h.Replica.Grid, new CarveResult(cells, [], [], 0), h.LastCut, 0);
        }
        return plan.Loss;
    }

    public void PloughAcked(ushort ack)
    {
        foreach (var h in _guessing) { _back.Clear(); h.Replica.Acked(ack, _back); Restored(h); }
        _guessing.RemoveWhere(h => !h.Replica.Guessing);
    }

    public void PloughReplay(ushort ack)
    {
        foreach (var h in _guessing) h.Replica.BeginReplay(ack);
    }

    public void PloughReplayDone()
    {
        foreach (var h in _guessing) { _back.Clear(); h.Replica.EndReplay(_back); Restored(h); }
        _guessing.RemoveWhere(h => !h.Replica.Guessing);
    }

    /// <summary>Guessed cells the server did not cut (<see cref="_back"/>) are back in the view: the collider gets them now, the drawing next frame.</summary>
    void Restored(House h)
    {
        if (_back.Count == 0) return;
        h.Collider?.Mark(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_back));
        RebuildCollider(h);
        MarkDirty(h);
    }

    /// <summary>243: one message of a house's removed cells (join catch-up or resync); the house is redrawn when it equals the server's, a broken set asks again.</summary>
    public void OnCells(HouseCellsMsg m)
    {
        if (Of(m.House) is not { } h) return;
        if (m.FirstRun == 0 && h.Resync.Active && m.OpSeq != h.ResyncSeq) { h.ResyncSeq = m.OpSeq; h.Resync.Begin(Time.GetTicksMsec() / 1000.0); }
        switch (h.Replica.OnCells(m))
        {
            case CatchUpStep.Done:
                _awaiting.Remove(h);
                h.Resync.End();
                MarkDirty(h);
                h.Collider?.MarkAll();
                break;
            case CatchUpStep.Broken:
                MarkDirty(h);
                h.Collider?.MarkAll();
                Desync(h);
                break;
        }
    }

    /// <summary>244: the rubble mode, and a round reset when asked.</summary>
    public void OnSettings(HouseSettingsMsg s)
    {
        if (s.Reset) ResetAll();
        _rubbleStay = s.RubbleStay;
        _debris?.SetRubble(s.RubbleStay);
    }

    /// <summary>Every house whole, with its flat material and shell collider back, and no debris (round reset, leaving the server).</summary>
    public void ResetAll()
    {
        foreach (var h in _houses)
        {
            if (h == null) continue;
            h.Replica.Reset();
            h.Held.Clear();
            Unswap(h);
            UpdatePanes(h);
            h.Dirty = h.ColliderDirty = false;
            h.Resync.End();
        }
        _dirty.Clear();
        _awaiting.Clear();
        _holding.Clear();
        _guessing.Clear();
        _falls.Clear();
        _debris?.ResetAll();
    }

    void Desync(House h)
    {
        GD.Print($"house desync {h.Id}");
        h.Replica.MarkAwaiting();
        if (!_awaiting.Contains(h)) _awaiting.Add(h);
        var now = Time.GetTicksMsec() / 1000.0;
        if (!h.Resync.Active) h.Resync.Begin(now);
        h.ResyncAt = now + ResyncRetrySeconds;
        RequestResync?.Invoke(h.Index);
    }

    void MarkDirty(House h)
    {
        h.ColliderDirty = true;
        if (h.Dirty) return;
        h.Dirty = true;
        _dirty.Add(h);
    }

    public override void _Process(double delta)
    {
        if (!_warmed) WarmUp();
        if (_holding.Count > 0) ReleaseHeld();
        if (_awaiting.Count > 0)
        {
            var now = Time.GetTicksMsec() / 1000.0;
            for (var k = _awaiting.Count - 1; k >= 0; k--)
            {
                var h = _awaiting[k];
                if (h.Resync.GiveUp(now, ResyncGiveUpSeconds))
                {
                    // still awaiting in the replica: its 242s are ignored, so it asks no more; the next round reset makes it whole
                    GD.PushWarning($"house {h.Id}: still unlike the server's after {ResyncGiveUpSeconds} s of resyncs (map data differs?); frozen until the round resets");
                    _awaiting.RemoveAt(k);
                }
                else if (now >= h.ResyncAt) { h.ResyncAt = now + ResyncRetrySeconds; RequestResync?.Invoke(h.Index); }
            }
        }
        // a house with falling parts first, whatever the budget: its collider gets the hole, then the parts fall
        if (_falls.Count > 0)
        {
            foreach (var f in _falls)
                if (f.House.Dirty) { f.House.Dirty = false; _dirty.Remove(f.House); Rebuild(f.House); }
            foreach (var f in _falls) _debris?.Fall(f.House.Replica.Grid, f.Result, f.Blast, f.Seed);
            _falls.Clear();
        }
        if (_dirty.Count == 0) return;
        // the rest within the budget (one at least); the others next frame, in the order they came
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var done = 0;
        while (done < _dirty.Count)
        {
            var h = _dirty[done++];
            h.Dirty = false;
            Rebuild(h);
            if (System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds >= RebuildBudgetMs) break;
        }
        _dirty.RemoveRange(0, done);
    }

    void Rebuild(House h)
    {
        UpdatePanes(h);
        if (!h.Replica.View.Damaged) { Unswap(h); return; }
        if (!h.Swapped) Swap(h);
        (h.Look ??= new HouseDamageLook(h.Replica.Grid)).Update(h.Replica.View);
        UpdateTexture(h);
        RebuildCut(h);
        if (h.ColliderDirty) RebuildCollider(h);
        h.ColliderDirty = false;
    }

    /// <summary>Map local (Godot frame) to world: the map root's transform.</summary>
    Transform3D MapToWorld => HouseVoxelCollider.MapToWorld(_map.Root);

    static Transform3D GridToMap(HouseGrid g)
    {
        var (x, y, z, o) = HouseFrame.Affine(g);
        return new Transform3D(new Basis(V(x), V(y), V(z)), V(o));
    }

    static Vector3 V(NVec3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The first carve: the shader material with its texture on the visual (and on its textured faces), the cut-face node, the shell collider set aside.</summary>
    void Swap(House h)
    {
        h.Swapped = true;
        var g = h.Replica.Grid;
        var visual = h.Visual != null && GodotObject.IsInstanceValid(h.Visual);
        if (_shader != null && (visual || h.Textured.Count > 0))
        {
            int nx = g.Nx, ny = g.Ny, nz = g.Nz;
            h.LayerBytes = new byte[nz][];
            h.Layers = new Godot.Collections.Array<Image>();
            for (var z = 0; z < nz; z++)
            {
                h.LayerBytes[z] = new byte[nx * ny];
                h.Layers.Add(Image.CreateFromData(nx, ny, false, Image.Format.R8, h.LayerBytes[z]));
            }
            h.Texture = new ImageTexture3D();
            h.Texture.Create(Image.Format.R8, nx, ny, nz, false, h.Layers);
            var toGrid = new Projection((MapToWorld * GridToMap(g)).AffineInverse());
            var edge = g.Id.StartsWith(FenceGridPrefix, StringComparison.Ordinal) ? FenceEdgeCells : 0f;
            if (visual)
            {
                h.Material = new ShaderMaterial { Shader = _shader };
                h.Material.SetShaderParameter("removed", h.Texture);
                h.Material.SetShaderParameter("grid_size", new Vector3(nx, ny, nz));
                h.Material.SetShaderParameter("world_to_grid", toGrid);
                h.Material.SetShaderParameter("edge_cells", edge);
                h.FlatMaterial = h.Visual!.MaterialOverride;
                h.Visual.MaterialOverride = h.Material;
            }
            if (_texShader != null)
                foreach (var node in h.Textured)
                {
                    if (!GodotObject.IsInstanceValid(node) || node.MaterialOverride is not StandardMaterial3D { AlbedoTexture: { } albedo } flat) continue;
                    var mat = new ShaderMaterial { Shader = _texShader };
                    mat.SetShaderParameter("removed", h.Texture);
                    mat.SetShaderParameter("grid_size", new Vector3(nx, ny, nz));
                    mat.SetShaderParameter("world_to_grid", toGrid);
                    mat.SetShaderParameter("albedo_tex", albedo);
                    mat.SetShaderParameter("edge_cells", edge);
                    h.TexSwaps.Add((node, flat));
                    node.MaterialOverride = mat;
                }
            h.CutMesh = new ArrayMesh();
            h.Cut = new MeshInstance3D { Name = $"house_{g.Id}_cut", Mesh = h.CutMesh, MaterialOverride = MapLoaderT.SharedFlatMaterial };
            _map.Root.AddChild(h.Cut);
        }
        if (h.Shape != null && GodotObject.IsInstanceValid(h.Shape) && (h.Collider == null || !GodotObject.IsInstanceValid(h.Collider.Node)))
            h.Collider = new HouseVoxelCollider(g, h.Shape); // its columns replace the shell at the collider rebuild
    }

    /// <summary>Back to the undamaged look and the shell collider.</summary>
    void Unswap(House h)
    {
        if (!h.Swapped) return;
        h.Swapped = false;
        if (h.Visual != null && GodotObject.IsInstanceValid(h.Visual)) h.Visual.MaterialOverride = h.FlatMaterial;
        foreach (var (node, original) in h.TexSwaps)
            if (GodotObject.IsInstanceValid(node)) node.MaterialOverride = original;
        h.TexSwaps.Clear();
        h.Material = null; h.Texture = null; h.Layers = null; h.LayerBytes = []; h.FlatMaterial = null;
        if (h.Cut != null && GodotObject.IsInstanceValid(h.Cut)) h.Cut.QueueFree();
        h.Cut = null; h.CutMesh = null;
        if (h.Collider?.Update(h.Replica.View, MapToWorld) == true) _map.HouseGeometryChanged(); // whole: the shell back
        h.Look = null;
    }

    void UpdateTexture(House h)
    {
        if (h.Texture == null || h.Layers == null || h.Look == null) return;
        var g = h.Replica.Grid;
        var n = g.Nx * g.Ny;
        for (var z = 0; z < h.LayerBytes.Length; z++)
        {
            Buffer.BlockCopy(h.Look.Hide, z * n, h.LayerBytes[z], 0, n);
            h.Layers[z].SetData(g.Nx, g.Ny, false, Image.Format.R8, h.LayerBytes[z]);
        }
        h.Texture.Update(h.Layers);
    }

    void RebuildCut(House h)
    {
        if (h.CutMesh == null || h.Look == null) return;
        var g = h.Replica.Grid;
        _quads.Clear(); _pos.Clear(); _nrm.Clear();
        HouseMesher.Damaged(h.Replica.View, h.Look, _quads);
        HouseFrame.Triangles(g, _quads, _pos, _nrm);
        h.CutMesh.ClearSurfaces();
        if (_pos.Count == 0) return;
        var verts = new Vector3[_pos.Count];
        var normals = new Vector3[_pos.Count];
        var colours = new Color[_pos.Count];
        for (var i = 0; i < verts.Length; i++) { verts[i] = V(_pos[i]); normals[i] = V(_nrm[i]); }
        for (var q = 0; q < _quads.Count; q++)
        {
            var c = HouseFrame.CutColour(g, _quads[q].Palette);
            var col = new Color(c.X, c.Y, c.Z);
            for (var k = 0; k < 6; k++) colours[q * 6 + k] = col;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Color] = colours;
        h.CutMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
    }

    /// <summary>The house's changed collider columns are rebuilt from its live cells (<see cref="HouseVoxelCollider"/>, as the server builds them).</summary>
    void RebuildCollider(House h)
    {
        if (h.Collider?.Update(h.Replica.View, MapToWorld) == true) _map.HouseGeometryChanged();
    }

    /// <summary>Compiles the discard shaders (flat and textured) before the first carve: a speck with each material in front of the camera for half a second.</summary>
    void WarmUp()
    {
        if (_shader == null) { _warmed = true; return; }
        if (GetViewport()?.GetCamera3D() is not { } cam) return;
        _warmed = true;
        var layers = new Godot.Collections.Array<Image> { Image.CreateFromData(1, 1, false, Image.Format.R8, new byte[1]) };
        var tex = new ImageTexture3D();
        tex.Create(Image.Format.R8, 1, 1, 1, false, layers);
        var mat = new ShaderMaterial { Shader = _shader };
        mat.SetShaderParameter("removed", tex);
        mat.SetShaderParameter("world_to_grid", new Projection(Transform3D.Identity));
        var specks = new List<MeshInstance3D> { Speck(cam, mat) };
        if (_texShader != null)
        {
            var texMat = new ShaderMaterial { Shader = _texShader };
            texMat.SetShaderParameter("removed", tex);
            texMat.SetShaderParameter("world_to_grid", new Projection(Transform3D.Identity));
            texMat.SetShaderParameter("albedo_tex", ImageTexture.CreateFromImage(Image.CreateFromData(1, 1, false, Image.Format.Rgba8, [255, 255, 255, 255])));
            specks.Add(Speck(cam, texMat));
        }
        GetTree().CreateTimer(0.5).Timeout += () => { foreach (var s in specks) if (GodotObject.IsInstanceValid(s)) s.QueueFree(); };
    }

    static MeshInstance3D Speck(Camera3D cam, Material mat)
    {
        var speck = new MeshInstance3D { Name = "HouseWarm", Mesh = new QuadMesh { Size = new Vector2(0.002f, 0.002f) }, MaterialOverride = mat, Position = new Vector3(0, 0, -0.3f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        cam.AddChild(speck);
        return speck;
    }
}
