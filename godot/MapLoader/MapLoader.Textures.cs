using System.Text.Json;
using Godot;

namespace Drycastle.MapLoader;

/// <summary>
/// Textured map nodes (map 0.7.0+). A node named &lt;node&gt;_tex_&lt;texture id&gt; holds the triangles of
/// &lt;node&gt; that are drawn with a texture of the library: assets/manifest.json "textures" maps the id to
/// a PNG under assets/. The node carries UVs and a vertex colour that tints the texture (white = as drawn);
/// no image is in the map files. Nothing here knows about fences: any node can have textured siblings.
/// Typed fences are the first user: parcel_&lt;ID&gt;_props_tex_&lt;id&gt; next to parcel_&lt;ID&gt;_props.
/// </summary>
public sealed partial class MapLoader
{
    public const string TexNodeSeparator = "_tex_";

    // Textured siblings of parcel_<ID>_props, by parcel ID: hidden and shown together with it.
    readonly Dictionary<string, List<MeshInstance3D>> _propsTexVisuals = new();
    Dictionary<string, string>? _textureFiles;  // texture id -> file, relative to the assets directory
    readonly HashSet<string> _missingTextures = new();

    // One material per image file, shared by every node and every loader in the process.
    static readonly Dictionary<string, StandardMaterial3D> _textureMaterials = new();

    /// <summary>Textured props visuals by parcel ID (the siblings of <see cref="PropsVisuals"/>).</summary>
    public IReadOnlyDictionary<string, List<MeshInstance3D>> PropsTexturedVisuals => _propsTexVisuals;

    /// <summary>Splits "&lt;node&gt;_tex_&lt;id&gt;" into the node it belongs to and the texture id (null: an ordinary node).</summary>
    public static (string Base, string? Texture) SplitTexNode(string name)
    {
        var i = name.IndexOf(TexNodeSeparator, StringComparison.Ordinal);
        return i < 0 ? (name, null) : (name[..i], name[(i + TexNodeSeparator.Length)..]);
    }

    /// <summary>The parcel ID of a props visual ("parcel_&lt;ID&gt;_props", textured or not); null for any other node.</summary>
    static string? PropsParcelId(string baseName)
        => baseName.StartsWith("parcel_") && baseName.EndsWith("_props") ? baseName["parcel_".Length..^"_props".Length] : null;

    /// <summary>Flat colour for an ordinary node, the library texture for a "_tex_" node.</summary>
    void ApplyMaterial(MeshInstance3D mi, string name)
    {
        var (_, texture) = SplitTexNode(name);
        if (texture != null && TextureMaterial(texture) is { } mat) mi.MaterialOverride = mat;
        else FixMaterials(mi);  // an unknown texture falls back to the tint as a flat colour
    }

    void ReadTextureManifest()
    {
        _textureFiles = new();
        var dir = AssetsDir;
        var path = dir == null ? null : Path.Combine(dir, "manifest.json");
        if (path == null || !File.Exists(path)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("textures", out var list)) return;
        foreach (var t in list.EnumerateArray())
            if (t.TryGetProperty("id", out var id) && t.TryGetProperty("file", out var file))
                _textureFiles[id.GetString()!] = file.GetString()!;
    }

    /// <summary>
    /// The material of a library texture: the image as albedo (repeating, mipmapped), multiplied by the vertex
    /// colour, fully rough like the flat map material. An image with transparent pixels is alpha-tested (cut-outs
    /// such as wire mesh and pickets). Null when the id or the file is unknown (warned once).
    /// </summary>
    StandardMaterial3D? TextureMaterial(string id)
    {
        if (_textureFiles == null) ReadTextureManifest();
        if (!_textureFiles!.TryGetValue(id, out var rel) || AssetsDir == null)
        {
            if (_missingTextures.Add(id)) Warn($"texture '{id}' is not in the assets manifest (assets dir: {AssetsDir ?? "none"}); drawn flat");
            return null;
        }
        var file = Path.GetFullPath(Path.Combine(AssetsDir, rel));
        if (_textureMaterials.TryGetValue(file, out var cached)) return cached;
        var image = File.Exists(file) ? Image.LoadFromFile(file) : null;
        if (image == null || image.IsEmpty())
        {
            if (_missingTextures.Add(id)) Warn($"texture '{id}': cannot load {file}; drawn flat");
            return null;
        }
        var cutOut = image.DetectAlpha() != Image.AlphaMode.None;
        image.GenerateMipmaps();
        var mat = new StandardMaterial3D
        {
            AlbedoTexture = ImageTexture.CreateFromImage(image),
            VertexColorUseAsAlbedo = true,
            VertexColorIsSrgb = true,
            AlbedoColor = Colors.White,
            Roughness = 1f,
            Metallic = 0f,
            MetallicSpecular = 0f,
            TextureRepeat = true,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };
        if (cutOut)
        {
            // Alpha test, not blending: it sorts correctly and casts shadows. Coverage keeps thin wires from
            // breaking up when multisampling is on.
            mat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
            mat.AlphaScissorThreshold = 0.4f;
            mat.AlphaAntialiasingMode = BaseMaterial3D.AlphaAntiAliasing.AlphaToCoverage;
        }
        return _textureMaterials[file] = mat;
    }
}
