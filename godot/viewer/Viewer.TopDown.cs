using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// `--topdown PNG`: renders the whole loaded map straight down (orthographic, north up) into one PNG plus a sidecar
/// `PNG.json` with its ground extent, so the tactical map can use the world's real colours (terrain, roads, regions, ground
/// patches, buildings, trees). The picture is cut into square tiles that are rendered one by one and stitched, so the size is not
/// limited by the GPU texture size. Options: `--topdown-mpp M` metres per pixel (default 1), `--topdown-tile PX` (default 2048).
/// The light is neutral (no shadows, no sky): an up-facing surface shows its flat vertex colour. Outside the map it is transparent.
/// The viewer has no water surface (that is the shooter's), so water shows as the ground type's colour.
/// </summary>
public partial class Viewer
{
    const float TopDownAmbient = 0.6f, TopDownSun = 0.4f; // ambient + sun at 75 degrees is ~1.0 on level ground: the colour as authored; the slope of a roof only shades it a little

    void AddTopDownEnvironment()
    {
        AddChild(new DirectionalLight3D { ShadowEnabled = false, LightEnergy = TopDownSun, RotationDegrees = new Vector3(-75, -35, 0) });
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0, 0, 0, 0),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = Colors.White,
                AmbientLightEnergy = TopDownAmbient,
                TonemapMode = Godot.Environment.ToneMapper.Linear,
            }
        });
    }

    async Task RunTopDown()
    {
        _player.InputEnabled = false;
        _player.Visible = false;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        await Physics(3);

        var mpp = _opts.TryGetValue("topdown-mpp", out var m) && float.TryParse(m, System.Globalization.CultureInfo.InvariantCulture, out var mf) && mf > 0 ? mf : 1f;
        var tilePx = _opts.TryGetValue("topdown-tile", out var t) && int.TryParse(t, out var ti) && ti >= 256 ? ti : 2048;

        // the extent is the terrain's (every chunk's terrain mesh), north-west corner = (min x, min z)
        var any = false;
        var box = default(Aabb);
        foreach (var mi in _map.TerrainVisuals)
        {
            if (!GodotObject.IsInstanceValid(mi) || mi.Mesh == null) continue;
            var b = mi.GlobalTransform * mi.GetAabb();
            box = any ? box.Merge(b) : b;
            any = true;
        }
        if (!any) { GD.PrintErr("topdown: the map has no terrain"); GetTree().Quit(4); return; }
        var widthPx = (int)Math.Ceiling(box.Size.X / mpp);
        var heightPx = (int)Math.Ceiling(box.Size.Z / mpp);
        if (widthPx > 16384 || heightPx > 16384) { GD.PrintErr($"topdown: {widthPx}x{heightPx} px is too large, raise --topdown-mpp"); GetTree().Quit(5); return; }

        var vp = new SubViewport
        {
            Size = new Vector2I(tilePx, tilePx),
            World3D = GetViewport().World3D,
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        };
        AddChild(vp);
        var cam = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Size = tilePx * mpp, Near = 1f, Far = box.Size.Y + 600f };
        vp.AddChild(cam);
        var camY = box.End.Y + 200f;
        // looks down -Y with image up = -Z (north), image right = +X (east)
        var basis = new Basis(Vector3.Right, new Vector3(0, 0, -1), Vector3.Up);

        var full = Image.CreateEmpty(widthPx, heightPx, false, Image.Format.Rgba8);
        var first = true;
        var t0 = Time.GetTicksMsec();
        for (var py = 0; py < heightPx; py += tilePx)
            for (var px = 0; px < widthPx; px += tilePx)
            {
                cam.GlobalTransform = new Transform3D(basis, new Vector3(box.Position.X + (px + tilePx / 2f) * mpp, camY, box.Position.Z + (py + tilePx / 2f) * mpp));
                for (var i = 0; i < (first ? 10 : 2); i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); // the first tile also waits for the meshes and shaders to be ready
                first = false;
                vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var tile = vp.GetTexture().GetImage();
                tile.Convert(Image.Format.Rgba8);
                full.BlitRect(tile, new Rect2I(0, 0, Math.Min(tilePx, widthPx - px), Math.Min(tilePx, heightPx - py)), new Vector2I(px, py));
            }

        var path = Path.GetFullPath(_opts["topdown"]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var err = full.SavePng(path);
        File.WriteAllText(path + ".json", JsonSerializer.Serialize(new
        {
            map = _map.MapVersion,
            origin_x = Math.Round(box.Position.X, 3), // ground x of the west edge
            origin_z = Math.Round(box.Position.Z, 3), // ground z of the north edge (north is -z, row v is z = origin_z + v * metres_per_px)
            width_px = widthPx,
            height_px = heightPx,
            metres_per_px = mpp,
        }, new JsonSerializerOptions { WriteIndented = true }));
        GD.Print($"topdown: {(err == Error.Ok ? "saved" : "FAILED " + err)} {path} {widthPx}x{heightPx} px at {mpp} m/px in {(Time.GetTicksMsec() - t0) / 1000.0:F1}s gpu='{RenderingServer.GetVideoAdapterName()}'");
        GetTree().Quit(err == Error.Ok ? 0 : 1);
    }
}
