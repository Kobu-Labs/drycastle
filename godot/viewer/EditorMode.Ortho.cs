using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

/// <summary>
/// The aerial photo layer: the orthophoto the pipeline downloads (`make ortho`, data/AREA/ortho) draped on the terrain
/// under everything the editor draws, to paint regions and place things by eye. A reference only: it is read from the
/// gitignored data folder, never written to the map or exported, and its licence forbids redistributing or altering it.
/// O toggles it, the View page has the switch and an opacity slider; both are remembered (editor_ui.cfg).
/// </summary>
public partial class EditorMode
{
    const float OrthoStep = 8f;          // metres between the vertices of the draped mesh (the terrain's own cells are 4 m)
    const float OrthoLift = 0.25f;       // above the terrain, so the 4 m terrain triangles never cut through it
    const float OrthoDefaultOpacity = 0.85f;

    MeshInstance3D? _orthoMesh;
    bool _orthoOn, _orthoRestore;
    float _orthoOpacity = OrthoDefaultOpacity;
    CheckBox _orthoCheck = null!;
    HSlider _orthoSlider = null!;
    Label _orthoNote = null!;

    public bool OrthoShown => _orthoOn;

    /// <summary>The folder `make ortho` writes, or null when the editor does not know the area (no watcher).</summary>
    string? OrthoDir => Watcher == null || Watcher.PipelineDir == ""
        ? null
        : Path.GetFullPath(Path.Combine(Watcher.PipelineDir, "..", "data", Watcher.Area, "ortho"));

    public void ToggleOrtho() => SetOrtho(!_orthoOn);

    /// <summary>Show or hide the aerial photo. The first time it loads the picture, which takes a moment.</summary>
    public void SetOrtho(bool on, bool save = true)
    {
        if (on && _orthoMesh == null && !LoadOrtho())
            on = false;
        _orthoOn = on;
        if (_orthoMesh != null) _orthoMesh.Visible = on;
        if (_orthoCheck != null) _orthoCheck.SetPressedNoSignal(on);
        if (on) Say("aerial photo on (reference only, O hides it)");
        if (save) SaveUiConfig();
    }

    void SetOrthoOpacity(float opacity, bool save)
    {
        _orthoOpacity = Mathf.Clamp(opacity, 0.1f, 1f);
        if (_orthoMesh?.MaterialOverride is StandardMaterial3D m) m.AlbedoColor = new Color(1, 1, 1, _orthoOpacity);
        if (_orthoSlider != null && !Mathf.IsEqualApprox((float)_orthoSlider.Value, _orthoOpacity * 100f))
            _orthoSlider.SetValueNoSignal(_orthoOpacity * 100f);
        if (save) SaveUiConfig();
    }

    /// <summary>Restore the remembered state once the map is up (called when the editor first opens).</summary>
    void RestoreOrtho()
    {
        if (!_orthoRestore) return;
        _orthoRestore = false;
        SetOrtho(true, save: false);
    }

    bool LoadOrtho()
    {
        var dir = OrthoDir;
        var info = dir == null ? null : Path.Combine(dir, "ortho.json");
        if (info == null || !File.Exists(info))
        {
            OrthoFail($"no aerial photo yet: run  make ortho AREA={Watcher?.Area ?? "village"}  and open the editor again");
            return false;
        }
        if (Map.OriginEpsg5514 == null) { OrthoFail("this map has no origin_epsg5514 (export it again, map 0.4.0+); the photo cannot be placed"); return false; }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(info));
            var root = doc.RootElement;
            var b = root.GetProperty("bounds_epsg5514");
            double minX = b[0].GetDouble(), minY = b[1].GetDouble(), maxX = b[2].GetDouble(), maxY = b[3].GetDouble();
            var image = Image.LoadFromFile(Path.Combine(dir!, root.GetProperty("file").GetString() ?? "ortho.jpg"));
            if (image == null || image.IsEmpty()) { OrthoFail("the aerial photo could not be read (ortho.jpg); run make ortho again"); return false; }
            image.GenerateMipmaps(); // the photo is seen at a slant from far away: without mipmaps it shimmers
            var material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoTexture = ImageTexture.CreateFromImage(image),
                AlbedoColor = new Color(1, 1, 1, _orthoOpacity),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            _orthoMesh = new MeshInstance3D
            {
                Name = "OrthoLayer",
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = material,
                Mesh = BuildOrthoMesh(minX, minY, maxX, maxY),
                Visible = false,
            };
            OutlineRoot.AddChild(_orthoMesh); // follows the editor's visibility: gone in walk mode
            _orthoNote.Text = $"{image.GetWidth()} x {image.GetHeight()} px, {root.GetProperty("pixel_m").GetDouble():F2} m per pixel. {root.GetProperty("attribution").GetString()}. Reference only.";
            return true;
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            OrthoFail("the aerial photo could not be loaded: " + e.Message);
            return false;
        }
    }

    void OrthoFail(string message)
    {
        _orthoNote.Text = message;
        Say(message, true);
    }

    /// <summary>A grid over the photo's S-JTSK bounds, each vertex on the terrain (or the mean height where it has none).</summary>
    ArrayMesh BuildOrthoMesh(double minX, double minY, double maxX, double maxY)
    {
        var o = Map.OriginEpsg5514!.Value;
        int cols = Math.Max(1, (int)Math.Ceiling((maxX - minX) / OrthoStep)), rows = Math.Max(1, (int)Math.Ceiling((maxY - minY) / OrthoStep));
        var verts = new Vector3[(cols + 1) * (rows + 1)];
        var uvs = new Vector2[verts.Length];
        for (var r = 0; r <= rows; r++)
        {
            var v = (float)r / rows; // 0 at the north edge, where the image starts
            var north = maxY - v * (maxY - minY);
            for (var c = 0; c <= cols; c++)
            {
                var u = (float)c / cols;
                var x = (float)(minX + u * (maxX - minX) - o.X);
                var z = (float)-(north - o.Y);
                verts[r * (cols + 1) + c] = new Vector3(x, Map.GroundHeightOrMean(x, z) + OrthoLift, z);
                uvs[r * (cols + 1) + c] = new Vector2(u, v);
            }
        }
        var indices = new int[cols * rows * 6];
        var i = 0;
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
            {
                int a = r * (cols + 1) + c, b = a + 1, d = a + cols + 1, e = d + 1;
                indices[i++] = a; indices[i++] = b; indices[i++] = d;
                indices[i++] = b; indices[i++] = e; indices[i++] = d;
            }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    // ---- the View page section ----

    void BuildOrthoSection(VBoxContainer page)
    {
        page.AddChild(new HSeparator());
        page.AddChild(new Label { Text = "Layers", ThemeTypeVariation = "Header" });
        BuildLayersToggle(page);
        _orthoCheck = new CheckBox
        {
            Text = "Aerial photo  (O)", FocusMode = Control.FocusModeEnum.None,
            TooltipText = "Lay the orthophoto on the ground to edit by eye (O). A reference only: it is never exported.",
        };
        _orthoCheck.Toggled += on => SetOrtho(on);
        page.AddChild(_orthoCheck);
        page.AddChild(new Label { Text = "Aerial photo opacity", ThemeTypeVariation = "Dim" });
        _orthoSlider = new HSlider
        {
            MinValue = 10, MaxValue = 100, Step = 5, Value = _orthoOpacity * 100f, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Scrollable = false, // the mouse wheel must scroll the sidebar, never move the slider
            TooltipText = "How much of the map shows through the photo.",
        };
        _orthoSlider.ValueChanged += v => SetOrthoOpacity((float)v / 100f, save: false);
        _orthoSlider.DragEnded += _ => SaveUiConfig();
        page.AddChild(_orthoSlider);
        _orthoNote = new Label
        {
            Text = "Download it once with make ortho. Source: GKU Bratislava, NLC; not to be redistributed or altered.",
            ThemeTypeVariation = "Dim", AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        page.AddChild(_orthoNote);
    }
}
