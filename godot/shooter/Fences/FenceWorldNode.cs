using System.Text.Json;
using Godot;
using Drycastle.Shooter.Buildings.Destruction;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Fences;
using MapLoaderT = Drycastle.MapLoader.MapLoader;

namespace Drycastle.Shooter.Fences;

/// <summary>
/// The client's destructible thin fences (docs/fence-destruction.md, WP-V): fences.json's sections, a <see cref="FenceState"/> of which pieces are broken (masks only, the server
/// holds the hit points) and the same collider rebuild the server does (<see cref="FenceColliders"/>), so predicted movement meets the same gaps. Drawn, an undamaged fence keeps
/// the map's textured material; at the first break every thin textured props node (picket, wire, plain: <see cref="MapLoaderT.PropsTexturedNodes"/>) gets the discard shader
/// (fence_break.gdshader) with one R8 mask texture, a texel a piece (column piece + 64 * (section / 16384), row section % 16384, 255 = broken), re-uploaded at most once a frame.
/// A broken piece falls as a cosmetic body (<see cref="FenceDebris"/>). 224 (<see cref="OnBreak"/>) breaks pieces, 225 (<see cref="OnSync"/>) sets masks. A map loaded
/// without visuals (the server, a headless client) has the colliders only. One per map, client only: <see cref="Current"/>.
/// </summary>
public partial class FenceWorldNode : Node3D
{
    public const string ShaderPath = "res://shaders/fence_break.gdshader";
    /// <summary>The mask's columns a section block takes (a section has at most this many pieces) and rows a block has.</summary>
    const int MaskColumns = 64, MaskRows = 16384;
    static readonly string[] ThinTextures = ["picket", "wire", "plain"];
    public static FenceWorldNode? Current { get; private set; }
    static Shader? _shader;

    /// <summary>A thin textured props node, the shader material it gets and the one it had.</summary>
    sealed record Look(MeshInstance3D Node, ShaderMaterial Material, Material? Original);

    readonly record struct Fall(int Section, int Piece, Vector2 Dir, float Speed);

    MapLoaderT _map = null!;
    FenceDestructionConfig _cfg = null!;
    IReadOnlyList<FenceSectionInfo> _sections = [];
    FenceState _state = null!;
    FenceColliders _colliders = null!;
    bool _visual, _warmed, _swapped, _maskDirty, _collidersDirty;
    int _columns, _rows;
    byte[] _bytes = [];
    Image? _image;
    ImageTexture? _maskTexture;
    readonly List<Look> _looks = new();
    readonly List<Fall> _falls = new();

    /// <summary>The falling pieces' node (made by the net client, under its owned node, so a leave frees it); null = none.</summary>
    public FenceDebris? Debris { get; set; }

    /// <summary>fence_destruction.json as this map was loaded with it.</summary>
    public FenceDestructionConfig Config => _cfg;

    /// <summary>The map this node belongs to (the debris stands in its frame).</summary>
    public MapLoaderT Map => _map;

    /// <summary>
    /// The thin fences of the map's fences.json (manifest files.fences), or null when destruction is off, the map has no file, no thin section or the file is broken (logged).
    /// A visual map also loads the shader and makes the mask and the materials (a loading step, not the first break).
    /// </summary>
    public static FenceWorldNode? Create(MapLoaderT map, FenceDestructionConfig cfg)
    {
        if (!cfg.Enabled || FencesPath(map) is not { } path) return null;
        IReadOnlyList<FenceSectionInfo> sections;
        try { sections = FenceFile.Parse(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException or JsonException)
        {
            GD.PrintErr($"client: {path}: {e.Message}; fences are not destroyed");
            return null;
        }
        if (!sections.Any(s => s.Thin)) return null;
        var node = new FenceWorldNode { Name = "FenceDestruction", _map = map, _cfg = cfg, _sections = sections, _state = new FenceState(sections, cfg), _colliders = new FenceColliders(map, sections) };
        node._visual = !map.CollisionOnly && ShotFx.Enabled;
        if (node._visual) node.MakeLook();
        return node;
    }

    /// <summary>fences.json of the loaded map (manifest files.fences), or null.</summary>
    static string? FencesPath(MapLoaderT map)
    {
        if (map.MapDir == "") return null;
        var manifest = Path.Combine(map.MapDir, "manifest.json");
        if (!File.Exists(manifest)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(manifest));
        if (!doc.RootElement.TryGetProperty("files", out var files) || !files.TryGetProperty("fences", out var f) || f.GetString() is not { } rel) return null;
        var path = Path.Combine(map.MapDir, rel);
        return File.Exists(path) ? path : null;
    }

    /// <summary>The mask (all standing), the shader and a material per thin texture, each thin textured props node noted with the material it has now.</summary>
    void MakeLook()
    {
        _shader ??= GD.Load<Shader>(ShaderPath);
        _columns = MaskColumns * ((_sections.Count + MaskRows - 1) / MaskRows);
        _rows = Math.Min(_sections.Count, MaskRows);
        _bytes = new byte[_columns * _rows];
        _image = Image.CreateFromData(_columns, _rows, false, Image.Format.R8, _bytes);
        _maskTexture = ImageTexture.CreateFromImage(_image);
        var materials = new Dictionary<string, ShaderMaterial?>();
        foreach (var (node, texture) in _map.PropsTexturedNodes)
        {
            if (!GodotObject.IsInstanceValid(node) || Array.IndexOf(ThinTextures, texture) < 0) continue;
            if (!materials.TryGetValue(texture, out var mat)) materials[texture] = mat = MakeMaterial(texture);
            if (mat != null) _looks.Add(new Look(node, mat, node.MaterialOverride));
        }
    }

    ShaderMaterial? MakeMaterial(string texture)
    {
        if (_shader == null || _map.LibraryMaterial(texture)?.AlbedoTexture is not { } albedo) return null;
        var mat = new ShaderMaterial { Shader = _shader };
        mat.SetShaderParameter("albedo_tex", albedo);
        mat.SetShaderParameter("mask", _maskTexture!);
        return mat;
    }

    public override void _EnterTree() => Current = this;

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
    }

    FenceSectionInfo? Thin(int section) => (uint)section < (uint)_sections.Count && _sections[section].Thin ? _sections[section] : null;

    /// <summary>The bits of a section's pieces that exist.</summary>
    static ulong Valid(FenceSectionInfo s) => s.Pieces >= 64 ? ulong.MaxValue : (1UL << s.Pieces) - 1;

    /// <summary>224: pieces of one section broke now. They are hidden, the collider is rebuilt and a body falls for each.</summary>
    public void OnBreak(FenceBreakMsg m)
    {
        if (Thin(m.Section) is not { } s) return;
        var old = _state.Broken(m.Section);
        var fresh = m.Pieces & Valid(s) & ~old;
        if (fresh == 0) return;
        Apply(s, old | fresh);
        if (!_visual || Debris == null) return;
        var dir = new Vector2(m.PushX, m.PushZ) / 127f;
        var speed = m.Speed10 > 0 ? m.Speed10 / 10f : _cfg.Debris.PushSpeed;
        for (var j = 0; j < s.Pieces; j++)
            if ((fresh >> j & 1) != 0) _falls.Add(new Fall(m.Section, j, dir, speed));
    }

    /// <summary>225: the broken pieces of the sections it lists (a reset first clears everything), no bodies.</summary>
    public void OnSync(FenceSyncMsg m)
    {
        if (m.Reset) ResetAll();
        foreach (var (section, broken) in m.Sections)
            if (Thin(section) is { } s) Apply(s, broken & Valid(s));
        if (!_state.Damaged) RestoreLook();
    }

    /// <summary>Every fence whole: masks clear, original materials and colliders back, no falling pieces (round reset, leaving the server).</summary>
    public void ResetAll()
    {
        _state.Restore();
        _colliders.RestoreAll();
        _collidersDirty = false;
        _falls.Clear();
        Debris?.ResetAll();
        if (!_visual) return;
        Array.Clear(_bytes);
        _maskDirty = true;
        RestoreLook();
    }

    /// <summary>Sets a section's broken pieces and notes what follows: its collider, its mask row and the shader.</summary>
    void Apply(FenceSectionInfo s, ulong mask)
    {
        _state.SetBroken(s.Index, mask);
        _colliders.Mark(s.Index);
        _collidersDirty = true;
        if (!_visual) return;
        var row = s.Index % MaskRows * _columns + MaskColumns * (s.Index / MaskRows);
        for (var j = 0; j < MaskColumns; j++) _bytes[row + j] = (byte)((mask >> j & 1) != 0 ? 255 : 0);
        _maskDirty = true;
        if (mask != 0) SwapLook();
    }

    /// <summary>The first break: the discard shader on every thin textured node.</summary>
    void SwapLook()
    {
        if (_swapped) return;
        _swapped = true;
        foreach (var l in _looks)
            if (GodotObject.IsInstanceValid(l.Node)) l.Node.MaterialOverride = l.Material;
    }

    /// <summary>Whole again: the map's materials back.</summary>
    void RestoreLook()
    {
        if (!_swapped) return;
        _swapped = false;
        foreach (var l in _looks)
            if (GodotObject.IsInstanceValid(l.Node)) l.Node.MaterialOverride = l.Original;
    }

    public override void _Process(double delta)
    {
        if (_visual && !_warmed) WarmUp();
        Debris?.SetRubble(HouseWorldNode.Current?.RubbleStay ?? false);
        if (_collidersDirty)
        {
            _collidersDirty = false;
            _colliders.Flush(_state);
        }
        if (_maskDirty && _image != null && _maskTexture != null)
        {
            _maskDirty = false;
            _image.SetData(_columns, _rows, false, Image.Format.R8, _bytes);
            _maskTexture.Update(_image);
        }
        // the bodies after the collider has its gap, so they do not start inside the old one
        if (_falls.Count == 0) return;
        foreach (var f in _falls) Debris?.Fall(_sections[f.Section], f.Piece, f.Dir, f.Speed);
        _falls.Clear();
    }

    /// <summary>Compiles the discard shader before the first break: a speck with each material (and the vertex format of a fence mesh) in front of the camera for half a second.</summary>
    void WarmUp()
    {
        if (_looks.Count == 0) { _warmed = true; return; }
        if (GetViewport()?.GetCamera3D() is not { } cam) return;
        _warmed = true;
        var mesh = new ArrayMesh();
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new[] { new Vector3(0, 0, 0), new Vector3(0.002f, 0, 0), new Vector3(0, 0.002f, 0) };
        arrays[(int)Mesh.ArrayType.Normal] = new[] { Vector3.Back, Vector3.Back, Vector3.Back };
        arrays[(int)Mesh.ArrayType.Color] = new[] { Colors.White, Colors.White, Colors.White };
        arrays[(int)Mesh.ArrayType.TexUV] = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1) };
        arrays[(int)Mesh.ArrayType.TexUV2] = new[] { new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 1) };
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        var specks = new List<MeshInstance3D>();
        foreach (var mat in _looks.Select(l => l.Material).Distinct())
        {
            var speck = new MeshInstance3D { Name = "FenceWarm", Mesh = mesh, MaterialOverride = mat, Position = new Vector3(0, 0, -0.3f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            cam.AddChild(speck);
            specks.Add(speck);
        }
        GetTree().CreateTimer(0.5).Timeout += () => { foreach (var s in specks) if (GodotObject.IsInstanceValid(s)) s.QueueFree(); };
    }
}
