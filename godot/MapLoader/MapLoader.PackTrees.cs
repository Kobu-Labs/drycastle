using System.Diagnostics;
using System.Text.Json;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Scenery;
using Godot;
using NVector3 = System.Numerics.Vector3;
using NVector2 = System.Numerics.Vector2;

namespace Drycastle.MapLoader;

/// <summary>
/// Pack trees (#287): near the player the baked code-built trees give way to detailed models of the private nature pack
/// (<c>assets-private/synty/glb/nature</c>, found as the other private packs are, see <see cref="PrivateAssetsDir"/>). Trees stay one record
/// each in trees.json; the collision is the code-built one and is not touched. Config: shooter/data/pack_trees.json (res://data), which only the
/// shooter has; <c>DRYCASTLE_PACK_TREES=0</c>, a missing pack (nature/manifest.json) and a map without <c>model.base_uv2</c> in trees.json
/// (the props meshes then have no tree bases, see <see cref="TreeCutShaders"/>) leave everything as it was.
/// <para>The cut is on the GPU. The props meshes that hold baked trees (<c>props</c> of a chunk, <c>parcel_ID_props</c>, also the dynamic ones of a parcel swap)
/// get the <c>props_tree_cut</c> material instead of the flat one, and the pack trees the <c>pack_tree</c> material; both read the global shader parameter
/// <c>tree_cut</c> (focus, radius, fade band) that <see cref="PackTreeStep"/> sets each frame, and use the tree's base point (UV2) to keep a tree whole on
/// one side or the other. Inside the band each tree swaps whole at a distance of its own (its threshold, a hash of the world base point, against the far share; no dither). Nothing is cut at runtime.</para>
/// <para>Drawing: the ground is cut into <c>tile_m</c> tiles; each tile within <c>near_m + ahead_m</c> of the focus is built, nearest first, on a worker
/// thread (<see cref="PackTileMesher"/>: every tree's triangles transformed into one mesh per material), and its surfaces are handed to the
/// rendering server a few at a time within <c>budget_ms</c>. A tile costs one draw per material. The model of a tree is chosen from its position and
/// height (<see cref="PackTreeMath.Choose"/>) among the models of its stand (trees.json <c>stand</c>, <c>edge</c>: <see cref="PackModelSets"/>; the tags ride beside
/// <c>_trees</c> in <c>_packTags</c>). Models come from the nature pack and, when installed, the swamp pack (an entry may name its <c>pack</c>). The radius of the cut never passes a tile that is not built yet, so the baked trees only vanish
/// where the pack trees stand. The same streamer feeds it as the meadow (<see cref="MeadowStreamer"/>).</para>
/// <para>The forest ground rides the same tiles and draws. Ground objects (ground_objects.json: logs, stumps, boulders, root plates) are baked low-poly meshes with
/// their base in UV2 like a tree, so the props shader already hides them inside the radius; the pack model of their kind (<see cref="GroundObjectMath"/>) is
/// drawn in their place. The cut is the one shared UV2, so it cannot skip a kind: every kind of the file needs a model or its baked objects vanish near the
/// player. Undergrowth (<see cref="UndergrowthGen"/>: ferns, bushes, mushrooms, twigs, small rocks, grass, saplings and tall shrubs on forest floor; reeds, swamp grass,
/// moss mounds, roots and logs on swamp floor) has no baked twin and no collision: it is generated on the worker from a hash of the cell by the meadow raster's mix of
/// the cell (<c>undergrowth.mixes</c>), and fades out at a radius of its own (<c>undergrowth.near_m</c>, shorter than the trees') each piece dropping out whole at its own distance (nothing stands behind it); it is built, kept and drawn
/// only for the tiles within that radius, as a mesh of its own per tile (<c>pack_under</c>); a piece grows out of the ground over the fade band instead of appearing whole, and the kinds marked <c>far</c> (saplings, shrubs,
/// bushes) are a third mesh with a longer radius (<c>undergrowth.far_m</c>, <c>pack_under_far</c>).</para>
/// <para>Cards (#362): beyond the pack radius a tree (and a ground object) is drawn as three flat cards of the very pack model it becomes near the player
/// (<see cref="CardPainter"/> paints them on a worker at load, <see cref="PackCardMesher"/> lays them out, on bigger tiles of their own, over the whole map unless
/// <c>cards.far_m</c> says less), so the swap at the pack radius changes almost nothing and no baked tree is seen once they are built. The baked trees still cast the
/// sun's shadows. Every mesh of a tile is a <see cref="PackLayer"/> (<see cref="PackLayerMachine"/>).</para>
/// <para>Colour and fallen trees: the mesher writes a per-tree tint and the kind of piece into the vertex colour (<see cref="PackColourMath"/>, <c>pack_tree.gdshader</c>); a log record may get a dead tree
/// laid down (<see cref="GroundObjectMath.PlaceLog"/>).</para>
/// </summary>
public sealed partial class MapLoader
{
    /// <summary>Pack trees on or off (<c>DRYCASTLE_PACK_TREES</c>, on by default); set before Load. They also need the pack, the config and a trees.json with base_uv2.</summary>
    public static bool PackTreesEnabled { get; set; } = PackTreeSettings.ParseEnabled(System.Environment.GetEnvironmentVariable(PackTreeSettings.EnvVar));

    const string PackTreeConfigPath = "res://data/pack_trees.json";

    /// <summary>The meshes a tile may hold. Trees (with the ground objects), Under and UnderFar belong to a pack tile; Cards to a card tile (bigger, whole map).</summary>
    enum PackLayerKind { Trees, Under, UnderFar, Cards }

    /// <summary>One mesh of a tile on its way to the screen (<see cref="PackLayerMachine"/>): the worker's job, the buckets still to hand over, the node, and what it holds (for the counters).</summary>
    sealed class PackLayer
    {
        public readonly StateMachine<PackLayerState> State = new(PackLayerMachine.Spec);
        public Task<PackLayerResult>? Job;
        public readonly Queue<PackBucket> Commit = new();
        public MeshInstance3D? Node;
        public PackPlacement[] Placed = Array.Empty<PackPlacement>(), Objects = Array.Empty<PackPlacement>();

        /// <summary>Everything is in the mesh (a layer with nothing to make is ready at once).</summary>
        public bool Ready => State.Is(PackLayerState.Ready);
        public bool Idle => State.Is(PackLayerState.Idle);
    }

    /// <summary>
    /// A tile: its trees and ground objects as one mesh and, only while it is within the undergrowth's shorter radii, the undergrowth as meshes of its own (one more draw per
    /// material each, but never built, kept or drawn for the many tiles that only the trees reach). A card tile holds only the Cards layer.
    /// </summary>
    sealed class PackTile
    {
        public required Vector3 Centre;
        readonly PackLayer?[] _layers = new PackLayer?[4];
        public PackLayer Of(PackLayerKind kind) => _layers[(int)kind] ??= new PackLayer();
        /// <summary>The layer if it was ever asked for.</summary>
        public PackLayer? Peek(PackLayerKind kind) => _layers[(int)kind];
        public IEnumerable<(PackLayerKind Kind, PackLayer Layer)> Layers()
        {
            for (var i = 0; i < _layers.Length; i++) if (_layers[i] is { } l) yield return ((PackLayerKind)i, l);
        }
    }

    /// <summary>What a worker made of one layer: its buckets and the placements in them (trees or undergrowth in Placed, ground objects in Objects).</summary>
    sealed record PackLayerResult(List<PackBucket> Buckets, PackPlacement[] Placed, PackPlacement[] Objects);

    /// <summary>The pack models of one kind of ground object, the code-built radius at scale 1 and the turn added to the object's rot.</summary>
    sealed record GroundKindModels(GroundCandidate[] Models, float RadiusM, float YawDeg);

    /// <summary>What a worker needs for the undergrowth of one tile, all copies (the meadow raster is not thread safe, so the forest cells are read on the main thread).</summary>
    sealed record UndergrowthInput(IReadOnlyList<(int Mix, ForestMask Cells)> Mixes, float CellM, TreeFoot[] Feet, Func<float, float, float> GroundZ);

    /// <summary>The undergrowth kinds of one meadow mix, resolved against the loaded models.</summary>
    sealed record UndergrowthMixKinds(int Mix, UndergrowthKind[] Kinds);

    PackTreeConfig? _packCfg;
    readonly Dictionary<string, string> _packDirs = new(); // pack name -> its folder (only packs that are installed)
    readonly Dictionary<string, Dictionary<string, string?>> _packFiles = new(); // pack name -> model name -> GLB file
    readonly HashSet<string> _packAbsent = new(); // packs the config names that are not installed
    readonly List<PackModelMesh> _packModels = new();
    readonly Dictionary<string, int> _packModelIds = new(); // "pack/name" -> index into _packModels, -1 = not in the pack
    readonly List<ShaderMaterial> _packMaterials = new();
    readonly Dictionary<string, int> _packMaterialIds = new();
    PackModelSets? _packSets;
    PackTreeTag[] _packTags = Array.Empty<PackTreeTag>(); // stand and edge flag per tree of _trees (trees.json)
    Dictionary<(int Tx, int Ty), List<int>> _packTreeTiles = new();
    GroundObjectFile? _packGround;
    PackGroundObject[] _packObjs = Array.Empty<PackGroundObject>();
    Dictionary<(int Tx, int Ty), List<int>> _packObjectTiles = new();
    Dictionary<string, GroundKindModels> _packObjKinds = new();
    UndergrowthMixKinds[] _packUnder = Array.Empty<UndergrowthMixKinds>();
    // the same kinds split by radius: every kind keeps its place in the list (its hash) and the ones of the other radius have no density
    UndergrowthMixKinds[] _packUnderNear = Array.Empty<UndergrowthMixKinds>(), _packUnderFar = Array.Empty<UndergrowthMixKinds>();
    bool _packUnderFarOn; // some kind is marked far and undergrowth.far_m is beyond near_m
    HashSet<(int Tx, int Ty)> _packTileSet = new(); // tiles with something to draw: trees or ground objects
    Node3D? _packRoot;
    readonly Dictionary<(int Tx, int Ty), PackTile> _packTiles = new();
    // cards (#362): the far trees and ground objects as cards of their pack models, on bigger tiles of their own
    Dictionary<(int Tx, int Ty), List<int>> _packCardTreeTiles = new(), _packCardObjectTiles = new();
    HashSet<(int Tx, int Ty)> _packCardTileSet = new();
    readonly Dictionary<(int Tx, int Ty), PackTile> _packCardTiles = new();
    Task<CardAtlas>? _packCardJob;
    CardModel?[] _packCards = Array.Empty<CardModel?>();
    int _packCardMaterial = -1; // index into _packMaterials once the atlas is painted
    /// <summary>Pri: distance to the stretch from the focus to where it is heading (build order, what is wanted); D: distance to the focus (what is shown, the cut).</summary>
    readonly List<(float Pri, float D, PackLayerKind Kind, int Tx, int Ty)> _packWanted = new();
    readonly List<(int Tx, int Ty)> _packScratch = new();
    readonly List<float> _packUnbuilt = new(), _packUnderUnbuilt = new(), _packUnderFarUnbuilt = new(), _packCardUnbuilt = new();
    Vector2 _packFocus; // world x, z of the last step
    NVector2? _packLastFocus; // map metres, for the speed
    NVector2 _packVelocity;
    float _packCutR, _packUnderCutR, _packUnderFarCutR, _packCardCutR = -1f;

    /// <summary>True when pack trees are set up for this map (the pack, the config and the trees are there).</summary>
    public bool PackTreesActive => _packRoot != null;
    /// <summary>The pack scatters undergrowth itself, so the code-built forest kinds it replaces are not drawn (MapLoader.Meadow.cs).</summary>
    public bool PackUndergrowthActive => PackTreesActive && _packUnder.Length > 0;

    /// <summary>What the pack trees cost and draw now: tiles with pack trees in view range, draw calls (tiles times their materials), and the trees and triangles inside the cut radius (the rest of a tile is collapsed in the shader).</summary>
    /// <remarks>Instances are trees; Objects and Undergrowth count the ground objects and undergrowth pieces inside the radius; Triangles are all of them.</remarks>
    public readonly record struct PackTreeStats(int Tiles, int Draws, int Instances, long Triangles, int Objects = 0, int Undergrowth = 0, int CardTiles = 0);

    public PackTreeStats PackTreeDrawn
    {
        get
        {
            int tiles = 0, draws = 0, instances = 0, objects = 0, under = 0, cardTiles = 0; long tris = 0;
            var r2 = _packCutR * _packCutR;
            var ur2 = _packUnderCutR * _packUnderCutR;
            var uf2 = _packUnderFarCutR > 0 ? _packUnderFarCutR * _packUnderFarCutR : 0f;
            int Inside(PackPlacement[] list, float limit2)
            {
                var n = 0;
                foreach (var p in list)
                {
                    var dx = p.Base.X - _packFocus.X; var dz = p.Base.Z - _packFocus.Y;
                    if (dx * dx + dz * dz >= limit2) continue;
                    n++;
                    tris += _packModels[p.Model].Triangles;
                }
                return n;
            }
            foreach (var t in _packTiles.Values)
            {
                var shown = false;
                foreach (var (kind, l) in t.Layers())
                {
                    if (l.Node is not { Visible: true } node) continue;
                    shown = true;
                    draws += ((ArrayMesh)node.Mesh).GetSurfaceCount();
                    if (kind == PackLayerKind.Trees) { instances += Inside(l.Placed, r2); objects += Inside(l.Objects, r2); }
                    else under += Inside(l.Placed, kind == PackLayerKind.UnderFar ? uf2 : ur2);
                }
                if (shown) tiles++;
            }
            foreach (var t in _packCardTiles.Values)
            {
                if (t.Peek(PackLayerKind.Cards)?.Node is not { Visible: true } node) continue;
                cardTiles++;
                draws += ((ArrayMesh)node.Mesh).GetSurfaceCount();
            }
            return new PackTreeStats(tiles, draws, instances, tris, objects, under, cardTiles);
        }
    }

    /// <summary>The private asset folder (<c>DRYCASTLE_ASSETS_PRIVATE</c>, else <c>assets-private</c> beside the asset library), or null without an asset library.</summary>
    string? PrivateAssetsDir()
    {
        if (System.Environment.GetEnvironmentVariable("DRYCASTLE_ASSETS_PRIVATE") is { Length: > 0 } env) return env;
        return AssetsDir is { } dir ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dir)) ?? "", "assets-private") : null;
    }

    void PackWarn(string key, string message)
    {
        if (_clutterWarned.Add("pack-trees:" + key)) Warn(message);
    }

    // ---- set up and tear down ----

    void FreePackTrees()
    {
        foreach (var key in _packTiles.Keys.ToList()) FreePackTile(_packTiles, key);
        foreach (var key in _packCardTiles.Keys.ToList()) FreePackTile(_packCardTiles, key);
        _packRoot?.QueueFree();
        _packRoot = null;
        _packCfg = null;
        _packDirs.Clear(); _packFiles.Clear(); _packAbsent.Clear();
        _packModels.Clear(); _packModelIds.Clear();
        _packMaterials.Clear(); _packMaterialIds.Clear();
        _packSets = null; _packTags = Array.Empty<PackTreeTag>();
        _packTreeTiles = new();
        _packGround = null; _packObjs = Array.Empty<PackGroundObject>(); _packObjectTiles = new(); _packObjKinds = new();
        _packUnder = Array.Empty<UndergrowthMixKinds>(); _packUnderNear = _packUnder; _packUnderFar = _packUnder; _packUnderFarOn = false; _packTileSet = new();
        _packCardTreeTiles = new(); _packCardObjectTiles = new(); _packCardTileSet = new();
        _packCardJob = null; _packCards = Array.Empty<CardModel?>(); _packCardMaterial = -1; // a painter still running finishes on its own; its atlas is dropped
        _packWanted.Clear();
        _packCutR = 0; _packUnderCutR = 0; _packUnderFarCutR = 0; _packCardCutR = -1f;
        _packLastFocus = null; _packVelocity = default;
        TreeCutShaders.Disable();
    }

    /// <summary>
    /// Decides before the chunks load whether this map gets pack trees (the props meshes then get the cut material): everything needed is there, silently
    /// when the pack or the config is simply not (the viewer has no data folder, a machine without the pack), with one warning for a map that is
    /// not rebuilt for it (no <c>base_uv2</c>).
    /// </summary>
    void PlanPackTrees()
    {
        FreePackTrees();
        if (CollisionOnly || !PackTreesEnabled || string.IsNullOrEmpty(TreesJson)) return;
        if (!Godot.FileAccess.FileExists(PackTreeConfigPath)) return;
        if (PackManifestPath(PackTreeConfig.DefaultPack) == null) return; // no nature pack on this machine

        PackTreeConfig cfg;
        try { cfg = PackTreeConfig.Parse(Godot.FileAccess.Open(PackTreeConfigPath, Godot.FileAccess.ModeFlags.Read).GetAsText()); }
        catch (DataFileException e) { PackWarn("config", $"pack trees: {e.Message}; no pack trees"); return; }
        // measurement only: DRYCASTLE_PACK_NEAR_M overrides near_m (perf runs compare radii without touching the file)
        if (float.TryParse(System.Environment.GetEnvironmentVariable("DRYCASTLE_PACK_NEAR_M"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var nearM) && nearM > 0)
        {
            cfg.NearM = nearM;
            cfg.FadeM = MathF.Min(cfg.FadeM, nearM * 0.25f);
        }
        if (!PackTreeSettings.ReadBaseUv2(TreesJson))
        {
            PackWarn("base-uv2", "pack trees: this map's trees.json has no model.base_uv2 (its props meshes do not carry the base of each tree); pack trees stay off, rebuild the map");
            return;
        }
        _packCfg = cfg;
        foreach (var pack in cfg.Packs()) // the other packs (swamp) are optional: their models are skipped when the pack is not installed
        {
            if (PackManifestPath(pack) is { } manifest) { _packDirs[pack] = Path.GetDirectoryName(manifest)!; _packFiles[pack] = ReadPackManifest(manifest); }
            else _packAbsent.Add(pack);
        }
        if (_packAbsent.Count > 0) PackWarn("packs", $"pack trees: pack {string.Join(", ", _packAbsent)} is not installed; its models are skipped");

        // ground_objects.json is optional: a map without it has none, silently
        var objectsPath = Path.Combine(_mapDir, "ground_objects.json");
        if (cfg.GroundObjects.Enabled && File.Exists(objectsPath))
        {
            var file = GroundObjectFile.Parse(File.ReadAllText(objectsPath));
            if (file == null) PackWarn("ground-objects", "pack trees: ground_objects.json is not a format 1 file; its objects stay baked");
            else if (!file.BaseUv2) PackWarn("ground-objects-uv2", "pack trees: ground_objects.json has no model.base_uv2; its objects stay baked");
            else _packGround = file;
        }
    }

    /// <summary>Loads the pack models after the map is loaded; leaves the pack trees off unless some of the configured models are there.</summary>
    void InitPackTrees()
    {
        if (_packCfg is not { } cfg || !_packDirs.ContainsKey(PackTreeConfig.DefaultPack) || (_trees.Count == 0 && _packGround == null)) return;
        var missing = new List<string>();
        var sets = PackModelSets.Build(cfg, e => PackCandidateOf(e, missing));
        InitGroundObjects(cfg, missing);
        InitUndergrowth(cfg, missing);
        if (missing.Count > 0) PackWarn("models", $"pack trees: {missing.Count} models are not in their pack and are skipped: {string.Join(", ", missing)}");
        var def = sets.Default;
        if (def.Cone.Length == 0 && def.Box.Length == 0) { PackWarn("empty", "pack trees: none of the configured models could be loaded; no pack trees"); _packCfg = null; return; }
        if (cfg.For(true).Count == 0 || cfg.For(false).Count == 0) PackWarn("shape", $"pack trees: no model for the {(cfg.For(true).Count == 0 ? "cone" : "box")} shape; the other shape's models stand in");
        _packSets = sets;
        _packTags = PackTreeTags.Parse(TreesJson, _trees.Count);

        _packTreeTiles = PackTreeMath.TileIndex(_trees, cfg.TileM);
        _packTileSet = new HashSet<(int, int)>(_packTreeTiles.Keys);
        foreach (var k in _packObjectTiles.Keys) _packTileSet.Add(k);
        if (cfg.Cards.Enabled)
        {
            _packCardTreeTiles = PackTreeMath.TileIndex(_trees, cfg.Cards.TileM);
            _packCardTileSet = new HashSet<(int, int)>(_packCardTreeTiles.Keys);
            foreach (var k in _packCardObjectTiles.Keys) _packCardTileSet.Add(k);
            StartCardAtlas(cfg);
        }
        _packRoot = new Node3D { Name = "PackTrees" };
        Root.AddChild(_packRoot);
        EnsureStreamer();
        GD.Print($"pack trees: {_packModels.Count} models ({_packMaterials.Count} materials), near {cfg.NearM} m (+{cfg.FadeM} m fade), undergrowth near {cfg.Undergrowth.NearM} m (+{cfg.Undergrowth.FadeM} m fade), tiles {cfg.TileM} m, {_packObjs.Length} ground objects, undergrowth kinds per mix: {string.Join(", ", _packUnder.Select(m => $"{m.Mix}:{m.Kinds.Length}"))}");
    }

    /// <summary>Resolves the pack models of every kind of ground object the file has and sorts the objects into tiles. A kind without a model keeps no pack object and, the cut being shared, loses its baked ones inside the radius.</summary>
    void InitGroundObjects(PackTreeConfig cfg, List<string> missing)
    {
        if (_packGround == null) return;
        var objs = new List<PackGroundObject>();
        foreach (var kind in _packGround.Objects.Select(o => o.Kind).Distinct())
        {
            if (_packGround.BakedOnly.Contains(kind)) continue; // its baked mesh is never cut
            var models = cfg.GroundObjects.Kinds.TryGetValue(kind, out var kc) ? kc : null;
            var list = new List<GroundCandidate>();
            foreach (var e in models?.Models ?? new())
            {
                if (e.Weight <= 0) continue;
                var id = PackModelId(e, missing);
                if (id < 0) continue;
                list.Add(new GroundCandidate(id, e.Weight, e.Lay && kind == "log"));
            }
            if (models == null || list.Count == 0)
            {
                PackWarn("ground-kind:" + kind, $"pack trees: no pack model for the ground object kind '{kind}'; the cut is shared, so its baked objects vanish inside the radius (map every kind in pack_trees.json ground_objects)");
                continue;
            }
            _packObjKinds[kind] = new GroundKindModels(list.ToArray(), _packGround.RadiusM.TryGetValue(kind, out var r) ? r : models.RadiusM, models.YawDeg);
        }
        foreach (var o in _packGround.Objects) if (_packObjKinds.ContainsKey(o.Kind)) objs.Add(o);
        _packObjs = objs.ToArray();
        for (var i = 0; i < _packObjs.Length; i++)
        {
            var key = (PackTreeMath.TileOf(_packObjs[i].X, cfg.TileM), PackTreeMath.TileOf(_packObjs[i].Y, cfg.TileM));
            if (!_packObjectTiles.TryGetValue(key, out var l)) _packObjectTiles[key] = l = new List<int>();
            l.Add(i);
            if (!cfg.Cards.Enabled) continue;
            var far = (PackTreeMath.TileOf(_packObjs[i].X, cfg.Cards.TileM), PackTreeMath.TileOf(_packObjs[i].Y, cfg.Cards.TileM));
            if (!_packCardObjectTiles.TryGetValue(far, out var fl)) _packCardObjectTiles[far] = fl = new List<int>();
            fl.Add(i);
        }
    }

    /// <summary>
    /// Resolves the undergrowth kinds of every meadow mix against the loaded models. Needs the meadow raster (its zone plane says where each mix is), so a map without
    /// meadow.bin has none. A mix no cell has (the swamp, on a map the pipeline has not written it to) is just never looked up; its models are loaded all the same
    /// (a few small GLBs), so a tile that does have it never waits for a load.
    /// </summary>
    void InitUndergrowth(PackTreeConfig cfg, List<string> missing)
    {
        var u = cfg.Undergrowth;
        if (!u.Enabled || _meadow == null) return;
        var farOn = u.FarM > u.NearM && u.Mixes.Any(m => m.Kinds.Any(k => k.Far));
        var mixes = new List<UndergrowthMixKinds>();
        foreach (var mix in u.Mixes)
        {
            var kinds = new List<UndergrowthKind>();
            foreach (var k in mix.Kinds)
            {
                var ids = new List<int>(); var weights = new List<float>(); var tris = new List<int>();
                foreach (var e in k.Models)
                {
                    if (e.Weight <= 0) continue;
                    var id = PackModelId(e, missing);
                    if (id < 0) continue;
                    ids.Add(id); weights.Add(e.Weight); tris.Add(_packModels[id].Triangles);
                }
                if (ids.Count > 0) kinds.Add(new UndergrowthKind(k.Per100M2 / 100f, k.ScaleMin, k.ScaleMax, k.Clump, k.Foot, ids.ToArray(), weights.ToArray(), tris.ToArray(), farOn && k.Far));
            }
            if (kinds.Count > 0) mixes.Add(new UndergrowthMixKinds(mix.Mix, kinds.ToArray()));
        }
        _packUnder = mixes.ToArray();
        _packUnderFarOn = farOn && mixes.Any(m => m.Kinds.Any(k => k.Far));
        UndergrowthMixKinds[] Only(bool far) => mixes.Select(m => new UndergrowthMixKinds(m.Mix, m.Kinds.Select(k => k.Far == far ? k : k with { PerM2 = 0f }).ToArray())).ToArray();
        _packUnderNear = Only(false);
        _packUnderFar = Only(true);
    }

    /// <summary>The manifest.json of an installed pack (<c>assets-private/synty/glb/PACK</c>), or null when the pack is not on this machine.</summary>
    string? PackManifestPath(string pack)
    {
        if (PrivateAssetsDir() is not { } priv || pack.Length == 0 || pack.Contains('/') || pack.Contains('\\') || pack.Contains("..")) return null;
        var path = Path.Combine(priv, "synty", "glb", pack, "manifest.json");
        return File.Exists(path) ? path : null;
    }

    /// <summary>The models of a pack's manifest.json by name: the GLB path. A collision body is no model; an LOD is, by its own name (the swamp pack's LODs are whole trees).</summary>
    static Dictionary<string, string?> ReadPackManifest(string path)
    {
        var result = new Dictionary<string, string?>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var m in doc.RootElement.GetProperty("models").EnumerateArray())
            {
                var name = m.GetProperty("name").GetString() ?? "";
                if (name.Length == 0 || (m.TryGetProperty("collision_of", out var c) && c.ValueKind == JsonValueKind.String)) continue;
                result[name] = m.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IOException) { }
        return result;
    }

    /// <summary>The candidate of a config entry (null, and noted in <paramref name="missing"/>, when its model cannot be loaded).</summary>
    PackCandidate? PackCandidateOf(PackTreeEntry e, List<string> missing)
    {
        var id = PackModelId(e, missing);
        return id < 0 ? null : new PackCandidate(id, _packModels[id].HeightM, e.Weight, e.Bulk);
    }

    /// <summary>The id of an entry's model, loading it on first use; -1 when it cannot be had. A model of a pack that is not installed is skipped without a word (the pack was warned about once).</summary>
    int PackModelId(PackTreeEntry e, List<string> missing)
    {
        var key = e.Pack + "/" + e.Name;
        if (!_packModelIds.TryGetValue(key, out var id))
        {
            var model = _packFiles.TryGetValue(e.Pack, out var files) && files.TryGetValue(e.Name, out var file) ? LoadPackModel(e.Pack, e.Name, file) : null;
            if (model != null) { _packModels.Add(model); id = _packModels.Count - 1; } else id = -1;
            _packModelIds[key] = id;
        }
        if (id < 0 && !_packAbsent.Contains(e.Pack) && !missing.Contains(key)) missing.Add(key);
        return id;
    }

    PackModelMesh? LoadPackModel(string pack, string name, string? file)
    {
        var dir = _packDirs[pack];
        var path = file != null && File.Exists(Path.Combine(dir, file)) ? Path.Combine(dir, file) : Path.Combine(dir, name + ".glb");
        if (!File.Exists(path)) return null;
        Node3D? scene = null;
        try
        {
            scene = LoadPrefab(path); // the pack's shared materials on its meshes (nature/materials.json)
            if (scene == null) return null;
            var surfaces = new List<PackSurface>();
            float y0 = float.MaxValue, y1 = float.MinValue;
            foreach (var mi in AllMeshes(scene).ToList())
            {
                if (mi.Mesh is not ArrayMesh src) continue;
                var xf = mi.Transform;
                for (var p = mi.GetParent() as Node3D; p != null; p = p.GetParent() as Node3D) xf = p.Transform * xf;
                var normalBasis = xf.Basis.Inverse().Transposed();
                for (var s = 0; s < src.GetSurfaceCount(); s++)
                {
                    if (src.SurfaceGetPrimitiveType(s) != Mesh.PrimitiveType.Triangles) continue;
                    var a = src.SurfaceGetArrays(s);
                    var v = a[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    if (v.Length == 0) continue;
                    var n = a[(int)Mesh.ArrayType.Normal].VariantType == Variant.Type.Nil ? null : a[(int)Mesh.ArrayType.Normal].AsVector3Array();
                    var uv = a[(int)Mesh.ArrayType.TexUV].VariantType == Variant.Type.Nil ? null : a[(int)Mesh.ArrayType.TexUV].AsVector2Array();
                    var idx = a[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil ? null : a[(int)Mesh.ArrayType.Index].AsInt32Array();
                    var verts = new NVector3[v.Length]; var normals = new NVector3[v.Length]; var uvs = new NVector2[v.Length];
                    for (var i = 0; i < v.Length; i++)
                    {
                        var p = xf * v[i];
                        verts[i] = new NVector3(p.X, p.Y, p.Z);
                        y0 = Mathf.Min(y0, p.Y); y1 = Mathf.Max(y1, p.Y);
                        var q = n == null ? Vector3.Up : (normalBasis * n[i]).Normalized();
                        normals[i] = new NVector3(q.X, q.Y, q.Z);
                        uvs[i] = uv == null ? default : new NVector2(uv[i].X, uv[i].Y);
                    }
                    surfaces.Add(new PackSurface(PackMaterialSlot(mi.GetActiveMaterial(s) as StandardMaterial3D), verts, normals, uvs, idx ?? Enumerable.Range(0, v.Length).ToArray()));
                }
            }
            return surfaces.Count == 0 ? null : new PackModelMesh { Name = name, HeightM = y1 - y0, Surfaces = surfaces };
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            PackWarn("load:" + name, $"pack trees: {name}: {e.Message}");
            return null;
        }
        finally { scene?.Free(); } // the meshes and materials are reference counted
    }

    /// <summary>
    /// The slot of the shader material that reproduces a pack material (albedo texture and colour, alpha scissor, cull mode, roughness, metallic, specular):
    /// one slot per distinct look, whatever the material is called, so the atlas trunk and the atlas crown of the models share a draw.
    /// </summary>
    int PackMaterialSlot(StandardMaterial3D? src)
    {
        var scissor = src == null || src.Transparency == BaseMaterial3D.TransparencyEnum.Disabled ? -1f : src.AlphaScissorThreshold; // alpha blending and hash are drawn as a scissor
        var key = src == null ? "plain" : string.Join('|', src.AlbedoTexture?.GetInstanceId() ?? 0, src.AlbedoColor.ToHtml(), scissor, (int)src.CullMode, src.Roughness, src.Metallic, src.MetallicSpecular);
        if (_packMaterialIds.TryGetValue(key, out var slot)) return slot;
        var cull = src?.CullMode switch { BaseMaterial3D.CullModeEnum.Front => "cull_front", BaseMaterial3D.CullModeEnum.Disabled => "cull_disabled", _ => "cull_back" };
        var mat = new ShaderMaterial { Shader = TreeCutShaders.Load("pack_tree.gdshader", cull) };
        if (src != null)
        {
            if (src.AlbedoTexture != null) mat.SetShaderParameter("albedo_tex", src.AlbedoTexture);
            mat.SetShaderParameter("albedo_colour", src.AlbedoColor);
            mat.SetShaderParameter("roughness", src.Roughness);
            mat.SetShaderParameter("metallic", src.Metallic);
            mat.SetShaderParameter("specular", src.MetallicSpecular);
        }
        mat.SetShaderParameter("scissor", scissor);
        SetPackLook(mat);
        _packMaterials.Add(mat);
        return _packMaterialIds[key] = _packMaterials.Count - 1;
    }

    /// <summary>The amounts every pack material shares (pack_trees.json colour, the undergrowth's grow-in); the per-tree tint is in the vertex colour (PackColourMath).</summary>
    void SetPackLook(ShaderMaterial mat)
    {
        var colour = _packCfg!.Colour;
        mat.SetShaderParameter("foliage_saturation", colour.Enabled ? colour.Saturation : 1f);
        mat.SetShaderParameter("foliage_gain", colour.Enabled ? colour.Gain : 1f);
        mat.SetShaderParameter("leaf_lo", colour.LeafLo);
        mat.SetShaderParameter("leaf_hi", colour.LeafHi);
        mat.SetShaderParameter("recolour", colour.Enabled ? 1f : 0f);
        mat.SetShaderParameter("leaf_ref", colour.LeafRef);
        mat.SetShaderParameter("shade_min", colour.ShadeMin);
        mat.SetShaderParameter("shade_max", colour.ShadeMax);
        mat.SetShaderParameter("under_grow", _packCfg.Undergrowth.Grow);
        mat.SetShaderParameter("under_sink", _packCfg.Undergrowth.SinkM);
    }

    // ---- cards: the atlas ----

    /// <summary>
    /// Starts painting the card atlas (<see cref="CardPainter"/>) of every model a tree or a ground object may take. The textures are read back here, on the main
    /// thread; the painting is a worker's. Until it is done the baked trees stand beyond the pack radius, as without cards.
    /// </summary>
    void StartCardAtlas(PackTreeConfig cfg)
    {
        var ids = new SortedSet<int>();
        void Add(PackStandModels set) { foreach (var c in set.Cone) ids.Add(c.ModelId); foreach (var c in set.Box) ids.Add(c.ModelId); }
        Add(_packSets!.Default);
        foreach (var set in _packSets.Stands.Values) Add(set);
        foreach (var kind in _packObjKinds.Values) foreach (var m in kind.Models) ids.Add(m.ModelId);
        if (ids.Count == 0) return;

        var textures = new Dictionary<ulong, CardTexture?>();
        var materials = new Dictionary<int, CardMaterial>();
        foreach (var id in ids)
            foreach (var surface in _packModels[id].Surfaces)
            {
                if (materials.ContainsKey(surface.Material)) continue;
                var mat = _packMaterials[surface.Material];
                CardTexture? tex = null;
                if (mat.GetShaderParameter("albedo_tex").As<Texture2D>() is { } t)
                {
                    if (!textures.TryGetValue(t.GetInstanceId(), out tex)) textures[t.GetInstanceId()] = tex = ReadCardTexture(t);
                }
                var col = mat.GetShaderParameter("albedo_colour").VariantType == Variant.Type.Nil ? Colors.White : mat.GetShaderParameter("albedo_colour").AsColor();
                materials[surface.Material] = new CardMaterial(tex, new System.Numerics.Vector4(col.R, col.G, col.B, col.A), mat.GetShaderParameter("scissor").AsSingle());
            }
        var models = _packModels.ToArray();
        var list = ids.ToList();
        var cards = cfg.Cards;
        _packCardJob = Task.Run(() => CardPainter.Build(models, list, materials, cards));
    }

    /// <summary>A texture's pixels as RGBA bytes (a copy: the texture is not touched); null when it cannot be read.</summary>
    CardTexture? ReadCardTexture(Texture2D texture)
    {
        try
        {
            var img = texture.GetImage();
            if (img == null || img.IsEmpty()) return null;
            if (img.IsCompressed() && img.Decompress() != Error.Ok) return null;
            if (img.GetFormat() != Image.Format.Rgba8) img.Convert(Image.Format.Rgba8);
            return new CardTexture(img.GetWidth(), img.GetHeight(), img.GetData());
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            PackWarn("card-texture", $"pack trees: a texture could not be read for the tree cards: {e.Message}");
            return null;
        }
    }

    /// <summary>Takes the painted atlas (main thread): the two textures and the one material every card wears. From then on card tiles are built.</summary>
    void FinishCardAtlas()
    {
        if (_packCardJob is not { IsCompleted: true } job) return;
        _packCardJob = null;
        if (job.IsFaulted) { PackWarn("cards", $"pack trees: the tree cards could not be painted: {job.Exception?.GetBaseException().Message}; far trees stay baked"); return; }
        var atlas = job.Result;
        if (atlas.Models.All(m => m == null)) return;
        var albedo = Image.CreateFromData(atlas.W, atlas.H, true, Image.Format.Rgba8, atlas.Albedo);
        var normal = Image.CreateFromData(atlas.W, atlas.H, true, Image.Format.Rgba8, atlas.Normal);
        if (albedo == null || albedo.IsEmpty() || normal == null || normal.IsEmpty())
        {
            PackWarn("cards", "pack trees: the engine did not take the card atlas (size or mipmaps); far trees stay baked");
            return;
        }
        var mat = new ShaderMaterial { Shader = TreeCutShaders.Load("pack_tree.gdshader", "cull_disabled") };
        mat.SetShaderParameter("albedo_tex", ImageTexture.CreateFromImage(albedo));
        mat.SetShaderParameter("normal_tex", ImageTexture.CreateFromImage(normal));
        mat.SetShaderParameter("albedo_colour", Colors.White);
        mat.SetShaderParameter("scissor", _packCfg!.Cards.Scissor);
        SetPackLook(mat);
        _packMaterials.Add(mat);
        _packCardMaterial = _packMaterials.Count - 1;
        _packCards = atlas.Models;
        GD.Print($"pack trees: cards of {atlas.Models.Count(m => m != null)} models in a {atlas.W} x {atlas.H} atlas, {_packCardTileSet.Count} card tiles of {_packCfg.Cards.TileM} m");
    }

    // ---- the baked side: the material of a props mesh ----

    static ShaderMaterial? _propsCutMaterial;

    /// <summary>The material of a props mesh that holds baked trees when pack trees are on: the flat one plus the cut (shared, see <see cref="FlatMaterial"/>).</summary>
    static ShaderMaterial PropsCutMaterial() => _propsCutMaterial ??= new ShaderMaterial { Shader = TreeCutShaders.Load("props_tree_cut.gdshader") };

    /// <summary>Whether a node gets <see cref="PropsCutMaterial"/>: pack trees are on for the map and it is a props mesh (a chunk's or a parcel's, not a textured fence) that carries the tree bases in UV2.</summary>
    bool TreeCutProps(string baseName, Mesh? mesh)
    {
        if (_packCfg == null || mesh == null || mesh.GetSurfaceCount() == 0) return false;
        if (baseName != "props" && PropsParcelId(baseName) == null) return false;
        if (mesh is not ArrayMesh am || ((ulong)am.SurfaceGetFormat(0) & (ulong)Mesh.ArrayFormat.FormatTexUV2) == 0) return false;
        if (_clutterWarned.Add("pack-trees:uv2-checked")) CheckBaseUv2(mesh, baseName);
        return true;
    }

    /// <summary>Warns once when the UV2 the engine hands the shader is not the base point the pipeline wrote (a compressed attribute loses metres in a big map): the first mesh with UV2 is read back.</summary>
    void CheckBaseUv2(Mesh mesh, string name)
    {
        var a = mesh.SurfaceGetArrays(0);
        if (a[(int)Mesh.ArrayType.TexUV2].VariantType == Variant.Type.Nil) return;
        var v = a[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var uv2 = a[(int)Mesh.ArrayType.TexUV2].AsVector2Array();
        for (var i = 0; i < uv2.Length && i < v.Length; i++)
        {
            if (uv2[i].X >= PackTreeMath.NoBase * 0.5f) continue;
            var off = new Vector2(uv2[i].X - v[i].X, uv2[i].Y - v[i].Z).Length();
            if (off > 60f) PackWarn("uv2-range", $"pack trees: UV2 of {name} is {off:F0} m from its vertex; the base points look compressed or wrong, trees will be cut in the wrong places");
            return;
        }
    }

    // ---- streaming (called every frame by the MeadowStreamer with the focus) ----

    /// <summary>
    /// One step around <paramref name="focus"/> (Godot axes): free the far tiles, start the nearest missing layers on the worker threads, hand finished
    /// surfaces to the rendering server within the budget, show what is in range and set the cuts. The trees (and ground objects) of a tile are built within
    /// <c>near_m + ahead_m</c>; the undergrowth of the same tile, meshes of their own, only within the shorter <c>undergrowth.near_m</c> (and <c>far_m</c> for the
    /// kinds marked far) plus its <c>ahead_m</c>; the cards (#362) on their own bigger tiles within <c>cards.far_m</c>, or over the whole map.
    /// All queue by distance, nearest first, on the same <c>max_in_flight</c> workers. "Distance" for building is to the stretch from the focus to where it will
    /// be in <c>lead_s</c> seconds (<see cref="PackTreeMath.Lead"/>), so a car or an aircraft finds its tiles built; what is shown and where the cuts lie is by the
    /// distance to the focus itself.
    /// </summary>
    void PackTreeStep(Vector3 focus, float dt)
    {
        if (_packCfg is not { } c || _packRoot == null) return;
        FinishCardAtlas();
        var u = c.Undergrowth;
        var underOn = _packUnder.Length > 0;
        var underFarOn = underOn && _packUnderFarOn;
        var cardsOn = _packCardMaterial >= 0;
        var cardFar = c.Cards.FarM > 0 ? c.Cards.FarM : float.MaxValue; // the whole map
        float fx = focus.X, fy = -focus.Z; // map metres
        var here = new NVector2(fx, fy);
        _packVelocity = _packLastFocus is { } last ? PackTreeMath.Velocity(_packVelocity, last, here, dt) : default;
        _packLastFocus = here;
        var lead = PackTreeMath.Lead(here, _packVelocity, c.LeadS, c.LeadMaxM);
        var leadLen = (lead - here).Length();
        _packFocus = new Vector2(focus.X, focus.Z);
        (float Pri, float D) DistTo((int Tx, int Ty) k, float tileM)
        {
            float x0 = k.Tx * tileM, y0 = k.Ty * tileM, x1 = x0 + tileM, y1 = y0 + tileM;
            var d = PackTreeMath.DistToBox(fx, fy, x0, y0, x1, y1);
            return (leadLen > 0.5f ? PackTreeMath.DistToBoxAlong(fx, fy, lead.X, lead.Y, x0, y0, x1, y1) : d, d);
        }
        float Radius(PackLayerKind kind) => kind switch { PackLayerKind.Trees => c.NearM, PackLayerKind.Under => u.NearM, PackLayerKind.UnderFar => u.FarM, _ => cardFar };
        PackLayer? LayerOf(PackLayerKind kind, (int, int) key, out PackTile? tile)
            => (kind == PackLayerKind.Cards ? _packCardTiles : _packTiles).TryGetValue(key, out tile) ? tile.Peek(kind) : null;

        _packWanted.Clear();
        foreach (var (tx, ty, _) in PackTreeMath.TilesNear(fx, fy, c.NearM + c.AheadM + leadLen, c.TileM))
        {
            if (!_packTileSet.Contains((tx, ty))) continue;
            var (pri, d) = DistTo((tx, ty), c.TileM);
            if (pri > c.NearM + c.AheadM) continue;
            _packWanted.Add((pri, d, PackLayerKind.Trees, tx, ty));
            if (underOn && pri <= u.NearM + u.AheadM) _packWanted.Add((pri, d, PackLayerKind.Under, tx, ty));
            if (underFarOn && pri <= u.FarM + u.AheadM) _packWanted.Add((pri, d, PackLayerKind.UnderFar, tx, ty));
        }
        if (cardsOn)
            foreach (var key in _packCardTileSet)
            {
                if (c.Cards.FarM <= 0 && _packCardTiles.TryGetValue(key, out var built) && built.Peek(PackLayerKind.Cards) is { Ready: true }) continue; // whole map: a built tile stays, nothing left to do for it
                var (pri, d) = DistTo(key, c.Cards.TileM);
                if (pri <= cardFar + c.AheadM) _packWanted.Add((pri, d, PackLayerKind.Cards, key.Tx, key.Ty));
            }
        _packWanted.Sort((a, b) => a.Pri != b.Pri ? a.Pri.CompareTo(b.Pri) : a.Kind.CompareTo(b.Kind)); // trees before the undergrowth of the same tile

        _packScratch.Clear();
        foreach (var (key, t) in _packTiles)
        {
            var pri = DistTo(key, c.TileM).Pri;
            if (pri > c.NearM + 2 * c.AheadM) { _packScratch.Add(key); continue; }
            // the tile stays, its undergrowth goes (and is built again when it is near)
            if (t.Peek(PackLayerKind.Under) is { Idle: false } near && pri > u.NearM + 2 * u.AheadM) FreePackLayer(near);
            if (t.Peek(PackLayerKind.UnderFar) is { Idle: false } far && pri > u.FarM + 2 * u.AheadM) FreePackLayer(far);
        }
        foreach (var key in _packScratch) FreePackTile(_packTiles, key);
        if (c.Cards.FarM > 0)
        {
            _packScratch.Clear();
            foreach (var key in _packCardTiles.Keys) if (DistTo(key, c.Cards.TileM).Pri > cardFar + 2 * c.AheadM) _packScratch.Add(key);
            foreach (var key in _packScratch) FreePackTile(_packCardTiles, key);
        }

        var inFlight = 0;
        foreach (var t in _packTiles.Values) foreach (var (_, l) in t.Layers()) if (l.Job != null) inFlight++;
        foreach (var t in _packCardTiles.Values) foreach (var (_, l) in t.Layers()) if (l.Job != null) inFlight++;
        foreach (var (_, _, kind, tx, ty) in _packWanted)
        {
            if (inFlight >= c.MaxInFlight) break;
            switch (kind)
            {
                case PackLayerKind.Trees:
                    if (_packTiles.ContainsKey((tx, ty))) continue;
                    StartPackTile(c, (tx, ty));
                    break;
                case PackLayerKind.Cards:
                    if (_packCardTiles.ContainsKey((tx, ty))) continue;
                    StartCardTile(c, (tx, ty));
                    break;
                default:
                    if (!_packTiles.TryGetValue((tx, ty), out var t) || !t.Of(kind).Idle) continue;
                    StartPackUndergrowth(c, t, (tx, ty), kind);
                    if (t.Of(kind).Job == null) continue; // nothing grows on this tile: ready at once, no worker
                    break;
            }
            inFlight++;
        }

        foreach (var t in _packTiles.Values) foreach (var (kind, l) in t.Layers()) SettlePackLayer(l, kind);
        foreach (var t in _packCardTiles.Values) foreach (var (kind, l) in t.Layers()) SettlePackLayer(l, kind);

        var sw = Stopwatch.StartNew(); // at least one surface per call, then until the budget is spent
        foreach (var (_, _, kind, tx, ty) in _packWanted)
        {
            if (LayerOf(kind, (tx, ty), out var tile) is not { } l || !l.State.Is(PackLayerState.Handing)) continue;
            while (l.Commit.Count > 0)
            {
                CommitPackSurface(tile!, l, kind, l.Commit.Dequeue());
                if (sw.Elapsed.TotalMilliseconds >= c.BudgetMs) break;
            }
            if (l.Commit.Count == 0) l.State.Enter(PackLayerState.Ready);
            if (sw.Elapsed.TotalMilliseconds >= c.BudgetMs) break;
        }

        foreach (var (key, t) in _packTiles)
        {
            var d = DistTo(key, c.TileM).D;
            foreach (var (kind, l) in t.Layers())
                if (l.Node != null) l.Node.Visible = l.Ready && d < Radius(kind); // tiles beyond the radius have nothing to show
        }
        foreach (var (key, t) in _packCardTiles)
            if (t.Peek(PackLayerKind.Cards) is { Node: { } node } l) node.Visible = l.Ready && DistTo(key, c.Cards.TileM).D < cardFar;

        // a cut never passes a layer that is not built yet: the baked trees would vanish before the pack trees (or cards) that replace them exist, and the undergrowth would pop in
        _packUnbuilt.Clear(); _packUnderUnbuilt.Clear(); _packUnderFarUnbuilt.Clear(); _packCardUnbuilt.Clear();
        foreach (var (_, d, kind, tx, ty) in _packWanted)
        {
            if (d >= Radius(kind) || LayerOf(kind, (tx, ty), out _) is { Ready: true }) continue;
            (kind switch { PackLayerKind.Trees => _packUnbuilt, PackLayerKind.Under => _packUnderUnbuilt, PackLayerKind.UnderFar => _packUnderFarUnbuilt, _ => _packCardUnbuilt }).Add(d);
        }
        _packCutR = PackTreeMath.CutRadius(c.NearM, _packUnbuilt);
        _packUnderCutR = PackTreeMath.CutRadius(u.NearM, _packUnderUnbuilt);
        _packUnderFarCutR = underFarOn ? PackTreeMath.CutRadius(u.FarM, _packUnderFarUnbuilt) : -1f;
        _packCardCutR = cardsOn ? PackTreeMath.CutRadius(MathF.Min(cardFar, CardWholeMapM), _packCardUnbuilt) : -1f;
        TreeCutShaders.Set(focus, _packCutR, c.FadeM, _packUnderCutR, u.FadeM, _packCardCutR, c.FadeM, _packUnderFarCutR, u.FarFadeM);
    }

    /// <summary>The cards' radius when they cover the whole map: beyond any map, small enough that the shader's float keeps the metres.</summary>
    const float CardWholeMapM = 1.0e6f;

    /// <summary>A finished job's buckets go to the layer's queue (Handing), or it is Ready at once when the worker made nothing or failed.</summary>
    void SettlePackLayer(PackLayer l, PackLayerKind kind)
    {
        if (l.Job is not { IsCompleted: true } job) return;
        l.Job = null;
        if (job.IsFaulted)
        {
            PackWarn("layer:" + kind, $"pack trees: a tile's {kind} could not be built: {job.Exception?.GetBaseException().Message}");
            l.State.Enter(PackLayerState.Ready);
            return;
        }
        l.Placed = job.Result.Placed; l.Objects = job.Result.Objects;
        foreach (var b in job.Result.Buckets) l.Commit.Enqueue(b);
        l.State.Enter(l.Commit.Count > 0 ? PackLayerState.Handing : PackLayerState.Ready);
    }

    /// <summary>What a worker needs to place the trees and ground objects of one tile of side <paramref name="tileM"/>: all copies or immutable, so a map reload cannot change it under the task.</summary>
    (TreeInstance[] Trees, PackTreeTag[] Tags, PackGroundObject[] Objs, NVector3 Centre) PackTileInput((int Tx, int Ty) key, float tileM,
        Dictionary<(int Tx, int Ty), List<int>> treeTiles, Dictionary<(int Tx, int Ty), List<int>> objectTiles)
    {
        var treeIds = treeTiles.TryGetValue(key, out var ti) ? ti : null;
        var trees = treeIds?.Select(i => _trees[i]).ToArray() ?? Array.Empty<TreeInstance>();
        var tags = treeIds?.Select(i => _packTags[i]).ToArray() ?? Array.Empty<PackTreeTag>();
        var objs = objectTiles.TryGetValue(key, out var oi) ? oi.Select(i => _packObjs[i]).ToArray() : Array.Empty<PackGroundObject>();
        return (trees, tags, objs, new NVector3((key.Tx + 0.5f) * tileM, 0, -(key.Ty + 0.5f) * tileM));
    }

    void StartPackTile(PackTreeConfig c, (int Tx, int Ty) key)
    {
        var (trees, tags, objs, centre) = PackTileInput(key, c.TileM, _packTreeTiles, _packObjectTiles);
        var model = TreeModel;
        var sets = _packSets!;
        var models = _packModels.ToArray();
        var objKinds = new Dictionary<string, GroundKindModels>(_packObjKinds);
        var groundModel = _packGround?.Model ?? new GroundModel();
        var tile = _packTiles[key] = new PackTile { Centre = new Vector3(centre.X, 0, centre.Z) };
        var l = tile.Of(PackLayerKind.Trees);
        l.Job = Task.Run(() => BuildPackTileData(c, trees, tags, model, sets, models, centre, objs, objKinds, groundModel));
        l.State.Enter(PackLayerState.Building);
    }

    /// <summary>Starts the cards of a card tile: the same placements as the pack tiles make (same model, scale and tint per tree), drawn as cards.</summary>
    void StartCardTile(PackTreeConfig c, (int Tx, int Ty) key)
    {
        var (trees, tags, objs, centre) = PackTileInput(key, c.Cards.TileM, _packCardTreeTiles, _packCardObjectTiles);
        var model = TreeModel;
        var sets = _packSets!;
        var models = _packModels.ToArray();
        var objKinds = new Dictionary<string, GroundKindModels>(_packObjKinds);
        var groundModel = _packGround?.Model ?? new GroundModel();
        var cards = _packCards;
        var material = _packCardMaterial;
        var tile = _packCardTiles[key] = new PackTile { Centre = new Vector3(centre.X, 0, centre.Z) };
        var l = tile.Of(PackLayerKind.Cards);
        l.Job = Task.Run(() => BuildCardTileData(c, trees, tags, model, sets, models, centre, objs, objKinds, groundModel, cards, material));
        l.State.Enter(PackLayerState.Building);
    }

    /// <summary>Starts the undergrowth of a tile, the near kinds or the far ones (the planning is the main thread's; a tile with no forest floor has no job and is ready at once).</summary>
    void StartPackUndergrowth(PackTreeConfig c, PackTile tile, (int Tx, int Ty) key, PackLayerKind kind)
    {
        var l = tile.Of(kind);
        if (PlanUndergrowth(c, key) is not { } input) { l.State.Enter(PackLayerState.Ready); return; }
        var models = _packModels.ToArray();
        var kinds = !_packUnderFarOn ? _packUnder : kind == PackLayerKind.UnderFar ? _packUnderFar : _packUnderNear;
        var centre = new NVector3(tile.Centre.X, 0, tile.Centre.Z);
        l.Job = Task.Run(() => BuildPackUndergrowthData(c, models, kinds, input, centre, key));
        l.State.Enter(PackLayerState.Building);
    }

    /// <summary>
    /// The main thread's part of a tile's undergrowth: the forest cells of the meadow raster (not thread safe), the trunks around the tile and the terrain
    /// as it is now. Null when no cell of the tile is of a mix that grows anything.
    /// </summary>
    UndergrowthInput? PlanUndergrowth(PackTreeConfig c, (int Tx, int Ty) key)
    {
        if (_meadow is not { } m) return null;
        var u = c.Undergrowth;
        float x0 = key.Tx * c.TileM, y0 = key.Ty * c.TileM, x1 = x0 + c.TileM, y1 = y0 + c.TileM, cell = m.CellM;
        int gx0 = (int)MathF.Floor(x0 / cell), gx1 = (int)MathF.Ceiling(x1 / cell), gy0 = (int)MathF.Floor(y0 / cell), gy1 = (int)MathF.Ceiling(y1 / cell);
        int w = gx1 - gx0, h = gy1 - gy0;
        var cellsOf = new Dictionary<int, bool[]>(); // mix -> its cells, made when the first one is seen
        var wanted = _packUnder.Select(x => x.Mix).ToHashSet();
        try
        {
            for (var gy = gy0; gy < gy1; gy++)
                for (var gx = gx0; gx < gx1; gx++)
                {
                    var mix = m.Zone(gx, gy) >> 4;
                    if (!wanted.Contains(mix)) continue;
                    var on = false; // on the raster at all: free ground (no building, road, water), see detail/meadow.py
                    for (var li = 0; li < m.Layers.Count && !on; li++) on = m.Density(li, gx, gy) > 0;
                    if (!on) continue;
                    if (!cellsOf.TryGetValue(mix, out var cells)) cellsOf[mix] = cells = new bool[w * h];
                    cells[(gy - gy0) * w + (gx - gx0)] = true;
                }
        }
        catch (FormatException) { return null; }
        if (cellsOf.Count == 0) return null;
        var margin = MathF.Max(u.FootM, 1f);
        var feet = new List<TreeFoot>();
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                if (_packTreeTiles.TryGetValue((key.Tx + dx, key.Ty + dy), out var l))
                    foreach (var i in l)
                    {
                        var t = _trees[i];
                        if (t.X >= x0 - margin && t.X < x1 + margin && t.Y >= y0 - margin && t.Y < y1 + margin) feet.Add(new TreeFoot(t.X, t.Y, t.Scale));
                    }
        var ground = GroundHeightSnapshot();
        var masks = cellsOf.OrderBy(kv => kv.Key).Select(kv => (kv.Key, new ForestMask { Gx0 = gx0, Gy0 = gy0, W = w, H = h, Cells = kv.Value })).ToList();
        return new UndergrowthInput(masks, cell, feet.ToArray(), (x, y) => ground(x, -y));
    }

    /// <summary>The trees and ground objects of a tile as placements: model choice by position, stand and height; the same for a pack tile and for the card tile over it.</summary>
    static (List<PackPlacement> Placed, List<PackPlacement> Objects) PlacePackTile(PackTreeConfig c, TreeInstance[] trees, PackTreeTag[] tags, TreeModel model, PackModelSets sets,
        PackModelMesh[] models, PackGroundObject[] objs, Dictionary<string, GroundKindModels> objKinds, GroundModel groundModel)
    {
        var placed = new List<PackPlacement>(trees.Length);
        for (var i = 0; i < trees.Length; i++)
            if (PackTreeMath.Place(trees[i], model, tags[i], sets, c) is { } p) placed.Add(p);
        var objects = new List<PackPlacement>(objs.Length);
        foreach (var o in objs)
        {
            var k = objKinds[o.Kind];
            if (o.Kind == "log") objects.AddRange(GroundObjectMath.PlaceLog(o, k.Models, models, k.RadiusM, k.YawDeg, c.GroundObjects.Fallen, GroundObjectMath.BoxOf(o, groundModel)));
            else
            {
                var id = k.Models[GroundObjectMath.Choose(k.Models, o.X, o.Y)].ModelId;
                objects.Add(GroundObjectMath.Place(o, models[id], id, k.RadiusM, k.YawDeg, false));
            }
        }
        return (placed, objects);
    }

    /// <summary>The worker's part of a tile: model choice, the merged triangles per material, as the arrays the mesh takes. Trees and ground objects share the buckets, so a tile costs one draw per material (the undergrowth is built apart, <see cref="BuildPackUndergrowthData"/>).</summary>
    static PackLayerResult BuildPackTileData(PackTreeConfig c, TreeInstance[] trees, PackTreeTag[] tags, TreeModel model, PackModelSets sets, PackModelMesh[] models, NVector3 centre,
        PackGroundObject[] objs, Dictionary<string, GroundKindModels> objKinds, GroundModel groundModel)
    {
        var (placed, objects) = PlacePackTile(c, trees, tags, model, sets, models, objs, objKinds, groundModel);
        var all = new List<PackPlacement>(placed.Count + objects.Count);
        all.AddRange(placed); all.AddRange(objects);
        return new PackLayerResult(ReadyBuckets(models, all, centre), placed.ToArray(), objects.ToArray());
    }

    /// <summary>The worker's part of a card tile: the same placements, three quads each, one bucket (one draw) for the whole tile.</summary>
    static PackLayerResult BuildCardTileData(PackTreeConfig c, TreeInstance[] trees, PackTreeTag[] tags, TreeModel model, PackModelSets sets, PackModelMesh[] models, NVector3 centre,
        PackGroundObject[] objs, Dictionary<string, GroundKindModels> objKinds, GroundModel groundModel, CardModel?[] cards, int material)
    {
        var (placed, objects) = PlacePackTile(c, trees, tags, model, sets, models, objs, objKinds, groundModel);
        var all = new List<PackPlacement>(placed.Count + objects.Count);
        all.AddRange(placed); all.AddRange(objects);
        return new PackLayerResult(PackCardMesher.Build(cards, all, centre, material), placed.ToArray(), objects.ToArray());
    }

    /// <summary>The worker's part of a tile's undergrowth: the scatter of every mix on its forest cells, tinted gently, as its own buckets (the tile's trees are not touched).</summary>
    static PackLayerResult BuildPackUndergrowthData(PackTreeConfig c, PackModelMesh[] models, UndergrowthMixKinds[] underKinds, UndergrowthInput input, NVector3 centre, (int Tx, int Ty) key)
    {
        float x0 = key.Tx * c.TileM, y0 = key.Ty * c.TileM;
        var kindsOf = underKinds.ToDictionary(m => m.Mix, m => m.Kinds);
        var sets = input.Mixes.Select(m => new UndergrowthSet(m.Cells, kindsOf[m.Mix], m.Mix * 10000)).ToList();
        var growth = UndergrowthGen.Tile(c.Undergrowth, sets, x0, y0, x0 + c.TileM, y0 + c.TileM, input.CellM, input.Feet, input.GroundZ, c.Undergrowth.TrisPerTile, out _)
            .Select(p => PackColourMath.Undergrowth(p, c.Colour)).ToList();
        return new PackLayerResult(ReadyBuckets(models, growth, centre), growth.ToArray(), Array.Empty<PackPlacement>());
    }

    static List<PackBucket> ReadyBuckets(PackModelMesh[] models, List<PackPlacement> all, NVector3 centre) => PackTileMesher.Build(models, all, centre);

    // The mesher's lists go to the renderer as they are (System.Numerics and Godot vectors are the same floats): no second copy in managed memory.
    static Variant Packed<T, TGodot>(List<T> l) where T : struct where TGodot : struct
        => PackedOf(System.Runtime.InteropServices.MemoryMarshal.Cast<T, TGodot>(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(l)));
    static Variant PackedOf<TGodot>(Span<TGodot> s) where TGodot : struct => s switch
    {
        _ when typeof(TGodot) == typeof(Vector3) => Variant.CreateFrom(System.Runtime.InteropServices.MemoryMarshal.Cast<TGodot, Vector3>(s)),
        _ when typeof(TGodot) == typeof(Vector2) => Variant.CreateFrom(System.Runtime.InteropServices.MemoryMarshal.Cast<TGodot, Vector2>(s)),
        _ when typeof(TGodot) == typeof(Color) => Variant.CreateFrom(System.Runtime.InteropServices.MemoryMarshal.Cast<TGodot, Color>(s)),
        _ => Variant.CreateFrom(System.Runtime.InteropServices.MemoryMarshal.Cast<TGodot, int>(s)),
    };

    void CommitPackSurface(PackTile tile, PackLayer layer, PackLayerKind kind, PackBucket b)
    {
        var node = layer.Node ??= NewPackTileNode(tile, kind);
        using var arrays = new Godot.Collections.Array(); // disposed here: left to the finalizer, the native copies would stay until a collection
        arrays.Resize((int)Mesh.ArrayType.Max);
        using (var v = Packed<NVector3, Vector3>(b.V)) arrays[(int)Mesh.ArrayType.Vertex] = v;
        using (var v = Packed<NVector3, Vector3>(b.N)) arrays[(int)Mesh.ArrayType.Normal] = v;
        using (var v = Packed<NVector2, Vector2>(b.Uv)) arrays[(int)Mesh.ArrayType.TexUV] = v;
        using (var v = Packed<NVector2, Vector2>(b.Uv2)) arrays[(int)Mesh.ArrayType.TexUV2] = v; // the base of the tree of each vertex, the shader's cut
        using (var v = Packed<System.Numerics.Vector4, Color>(b.Col)) arrays[(int)Mesh.ArrayType.Color] = v; // the tree's tint (rgb, halved) and what the piece is (a), read by pack_tree.gdshader
        using (var v = Packed<int, int>(b.I)) arrays[(int)Mesh.ArrayType.Index] = v;
        var mesh = (ArrayMesh)node.Mesh;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, _packMaterials[b.Material]);
        b.Return(); // the renderer has its own copy
    }

    MeshInstance3D NewPackTileNode(PackTile tile, PackLayerKind kind)
    {
        var node = new MeshInstance3D
        {
            Name = kind switch { PackLayerKind.Trees => "pack_tile", PackLayerKind.Under => "pack_under", PackLayerKind.UnderFar => "pack_under_far", _ => "pack_cards" },
            Mesh = new ArrayMesh(),
            Position = tile.Centre,
            Visible = false, // shown when the whole layer is in
            // only the pack trees may cast (pack_trees.json shadows); a card casts none: the baked tree under it does, in the sun's pass
            CastShadow = kind == PackLayerKind.Trees && _packCfg!.Shadows ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled, // UV2 is not a lightmap UV here
        };
        _packRoot!.AddChild(node);
        return node;
    }

    void FreePackTile(Dictionary<(int Tx, int Ty), PackTile> tiles, (int Tx, int Ty) key)
    {
        if (!tiles.Remove(key, out var tile)) return;
        foreach (var (_, l) in tile.Layers()) FreePackLayer(l);
    }

    /// <summary>
    /// Drops a layer: its node and vertex buffers, the surfaces still queued, its placements. A job still running is left to finish and its result dropped. The layer is
    /// Idle again, so it is asked for anew when the tile is near.
    /// </summary>
    static void FreePackLayer(PackLayer l)
    {
        if (l.Node != null && GodotObject.IsInstanceValid(l.Node)) { FreeTileMesh(l.Node); l.Node.GetParent()?.RemoveChild(l.Node); l.Node.QueueFree(); }
        l.Node = null;
        l.Job = null;
        l.Commit.Clear();
        l.Placed = Array.Empty<PackPlacement>(); l.Objects = Array.Empty<PackPlacement>();
        l.State.Enter(PackLayerState.Idle);
    }

    /// <summary>Frees a tile's vertex buffers now: the mesh is reference counted and a C# wrapper of it may hold it until the next garbage collection (hundreds of MB of freed tiles at a 220 m radius).</summary>
    static void FreeTileMesh(MeshInstance3D node)
    {
        if (node.Mesh is ArrayMesh mesh) { mesh.ClearSurfaces(); mesh.Dispose(); }
    }
}
