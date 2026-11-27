using Drycastle.ShooterCore.Editor;
using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>One opening of a building's detail file as the form shows it.</summary>
public sealed record OpeningRow(string Id, string Kind, int Edge, double At, double Width, double Height, double Sill, string Confidence)
{
    public OpeningSpec Spec => new(Id, Edge, At, Width, Height, Sill);
}

/// <summary>The part of a building's detail file the form edits (everything else stays in the file untouched).</summary>
public sealed record DetailForm(
    double Overhang, string Frame, string Surround, string Trim, double? PlinthHeight, bool RidgeCaps, bool RoofTiles, bool Gutter, bool Corners, bool Bands, bool Interior,
    IReadOnlyList<OpeningRow> Openings);

/// <summary>
/// A building as the worker's <c>state</c> op describes it (pipeline edits/building_edit.py): the override entry's colours and form, the detail file's form and openings,
/// the footprint's walls (to draw an opening and to click one) and the palettes of the pipeline's colour names.
/// </summary>
public sealed class BuildingForm
{
    public string Building = "", Parcel = "";
    public bool HasOverride, HasDetail, Measured;
    /// <summary>The detail file exists but does not parse (the worker says why); the openings cannot be shown then.</summary>
    public string DetailError = "";
    public string Roof = "gabled";
    public double WallTop, Top;
    public string? WallColour, RoofColour;
    public DetailForm? Detail;
    public List<WallEdge> Edges = new();
    public double BaseZ;
    public Dictionary<string, List<(string Name, string Hex)>> Palettes = new();

    public static BuildingForm Parse(JsonElement r)
    {
        var f = new BuildingForm();
        string Str(JsonElement e, string k, string d = "") => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : d;
        double Num(JsonElement e, string k, double d = 0) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : d;
        bool Flag(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
        f.Building = Str(r, "building"); f.Parcel = Str(r, "parcel");
        f.DetailError = Str(r, "detail_error");
        f.HasOverride = Flag(r, "has_override"); f.HasDetail = Flag(r, "has_detail"); f.Measured = Flag(r, "measured");
        if (r.TryGetProperty("form", out var form)) { f.Roof = Str(form, "roof", "gabled"); f.WallTop = Num(form, "wall_top_m"); f.Top = Num(form, "top_m"); }
        if (r.TryGetProperty("override", out var ov) && ov.ValueKind == JsonValueKind.Object)
        {
            f.WallColour = ov.TryGetProperty("wall_colour", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;
            f.RoofColour = ov.TryGetProperty("roof_colour", out var rc) && rc.ValueKind == JsonValueKind.String ? rc.GetString() : null;
        }
        if (r.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.Object)
        {
            var ops = new List<OpeningRow>();
            if (d.TryGetProperty("openings", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var o in arr.EnumerateArray())
                    ops.Add(new OpeningRow(Str(o, "id"), Str(o, "kind", "window"), (int)Num(o, "edge"), Num(o, "at_m"), Num(o, "width_m", 1), Num(o, "height_m", 1), Num(o, "sill_m"), Str(o, "confidence", "medium")));
            double? plinth = d.TryGetProperty("plinth", out var p) && p.ValueKind == JsonValueKind.Object ? Num(p, "height_m", 0.3) : null;
            f.Detail = new DetailForm(Num(d, "overhang_m"), Str(d, "frame_colour"), Str(d, "surround_colour"), Str(d, "trim_colour"), plinth,
                Flag(d, "ridge_caps"), Flag(d, "roof_tiles"), Flag(d, "gutter"), Flag(d, "corners"), Flag(d, "bands"), Flag(d, "interior"), ops);
        }
        if (r.TryGetProperty("edges", out var edges) && edges.ValueKind == JsonValueKind.Array)
            foreach (var e in edges.EnumerateArray())
            {
                var p = e.GetProperty("p"); var q = e.GetProperty("q"); var n = e.GetProperty("normal");
                f.Edges.Add(new WallEdge((int)Num(e, "i"), p[0].GetDouble(), p[1].GetDouble(), q[0].GetDouble(), q[1].GetDouble(), n[0].GetDouble(), n[1].GetDouble()));
            }
        f.BaseZ = Num(r, "base_z");
        if (r.TryGetProperty("palettes", out var pal) && pal.ValueKind == JsonValueKind.Object)
            foreach (var kv in pal.EnumerateObject())
                f.Palettes[kv.Name] = kv.Value.EnumerateObject().Select(c => (c.Name, c.Value.GetString() ?? "#000000")).ToList();
        return f;
    }

    /// <summary>The compass side a wall faces (N, NE, ...), from its outward normal in map axes (y north).</summary>
    public static string SideOf(WallEdge e)
    {
        var bearing = (Math.Atan2(e.Nx, e.Ny) * 180 / Math.PI + 360) % 360;
        return PhotoFiles.Sides[(int)Math.Round(bearing / 45) % 8];
    }
}

/// <summary>A Street View capture of a building (<c>data/village/photos/&lt;id&gt;/&lt;n&gt;.png</c> and <c>.json</c>, photo_capture.py).</summary>
public sealed record CaptureInfo(int N, string Png, double Heading, double DistanceM, IReadOnlyList<int> Walls);

/// <summary>A photo of the user's own, copied under the building's photo folder with the place it was taken from in a sidecar (<c>side</c> or <c>point</c>).</summary>
public sealed class OwnPhoto
{
    public string Png = "", Json = "", Name = "";
    public string? Side, Point;
    public bool Ticked;
    public string Where => Side != null ? $"from the {Side}" : Point != null ? $"from point {Point}" : "";
    public bool HasPlace => Side != null || Point != null;
}

/// <summary>The photo files of a building on disk: captures, own photos with their sidecars, and the saved run of the build.</summary>
public static class PhotoFiles
{
    public static readonly string[] Sides = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
    public static readonly string[] SideNames = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };

    public static string Dir(string repo, string building) => Path.Combine(repo, "data", "village", "photos", building);
    public static string RunDir(string repo, string building) => Path.Combine(Dir(repo, building), "run");

    public static List<CaptureInfo> Captures(string repo, string building)
    {
        var list = new List<CaptureInfo>();
        var dir = Dir(repo, building);
        if (!Directory.Exists(dir)) return list;
        foreach (var json in Directory.EnumerateFiles(dir, "*.json"))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(json), out var n)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                var r = doc.RootElement;
                var png = Path.Combine(dir, r.TryGetProperty("photo", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : n + ".png");
                if (!File.Exists(png)) continue;
                var walls = r.TryGetProperty("edges_facing", out var w) && w.ValueKind == JsonValueKind.Array ? w.EnumerateArray().Select(e => e.GetInt32()).ToList() : new List<int>();
                list.Add(new CaptureInfo(n, png, r.TryGetProperty("heading", out var h) ? h.GetDouble() : 0, r.TryGetProperty("distance_m", out var dm) ? dm.GetDouble() : 0, walls));
            }
            catch (Exception e) when (e is JsonException or IOException) { GD.PushWarning($"photo capture {json} unreadable: {e.Message}"); }
        }
        return list.OrderBy(c => c.N).ToList();
    }

    public static string OwnDir(string repo, string building) => Path.Combine(Dir(repo, building), "own");

    public static List<OwnPhoto> Own(string repo, string building)
    {
        var list = new List<OwnPhoto>();
        var dir = OwnDir(repo, building);
        if (!Directory.Exists(dir)) return list;
        foreach (var json in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var stem = Path.GetFileNameWithoutExtension(json);
            var png = Directory.EnumerateFiles(dir, stem + ".*").FirstOrDefault(f => !f.EndsWith(".json", StringComparison.Ordinal));
            if (png == null) continue;
            var o = new OwnPhoto { Png = png, Json = json, Name = stem };
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(json));
                o.Side = node?["side"]?.GetValue<string>(); o.Point = node?["point"]?.GetValue<string>();
                o.Name = node?["name"]?.GetValue<string>() ?? stem;
            }
            catch (Exception e) when (e is JsonException or IOException or InvalidOperationException) { GD.PushWarning($"own photo {json} unreadable: {e.Message}"); }
            list.Add(o);
        }
        return list;
    }

    /// <summary>Copy a photo of the user's under the building's folder (own/&lt;n&gt;.&lt;ext&gt;) with an empty sidecar; null and a message when the copy fails.</summary>
    public static OwnPhoto? AddOwn(string repo, string building, string source, out string? error)
    {
        error = null;
        try
        {
            var dir = OwnDir(repo, building);
            Directory.CreateDirectory(dir);
            var n = 1;
            while (File.Exists(Path.Combine(dir, n + ".json"))) n++;
            var png = Path.Combine(dir, n + Path.GetExtension(source).ToLowerInvariant());
            File.Copy(source, png, true);
            var o = new OwnPhoto { Png = png, Json = Path.Combine(dir, n + ".json"), Name = Path.GetFileName(source), Ticked = true };
            SaveOwn(o);
            return o;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = $"could not copy the photo: {e.Message}";
            return null;
        }
    }

    public static void SaveOwn(OwnPhoto o)
    {
        var node = new JsonObject { ["name"] = o.Name };
        if (o.Side != null) node["side"] = o.Side;
        if (o.Point != null) node["point"] = o.Point;
        File.WriteAllText(o.Json, node.ToJsonString());
    }

    public static void RemoveOwn(OwnPhoto o)
    {
        try { File.Delete(o.Png); File.Delete(o.Json); } catch (IOException e) { GD.PushWarning($"could not remove {o.Png}: {e.Message}"); }
    }

    /// <summary>The run the last capture build left (result.json, overlays, comparisons): null when there is none.</summary>
    public static PhotoResult? LastRun(string repo, string building)
    {
        var run = RunDir(repo, building);
        var res = Path.Combine(run, "result.json");
        if (!File.Exists(res)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(res));
            var r = doc.RootElement;
            string[] Glob(string pat) => Directory.EnumerateFiles(run, pat).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            IReadOnlyList<string> List(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(e => e.GetString() ?? "").ToList() : Array.Empty<string>();
            return new PhotoResult(building, "", "captures", run, Glob("overlay_*.png"), Glob("compare_*.png"), List("problems"), List("invented"),
                r.TryGetProperty("roof", out var roof) ? roof.GetString() ?? "" : "", r.TryGetProperty("eave_m", out var e) ? e.GetDouble() : 0,
                r.TryGetProperty("top_m", out var t) ? t.GetDouble() : 0, 0, File.Exists(Path.Combine(run, "ends_ask.md")), true);
        }
        catch (Exception e) when (e is JsonException or IOException) { GD.PushWarning($"{res} unreadable: {e.Message}"); return null; }
    }

    /// <summary>A picture file as a texture, at most <paramref name="maxWidth"/> px wide (0: full size); null when it cannot be read.</summary>
    public static ImageTexture? Load(string path, int maxWidth)
    {
        var img = Image.LoadFromFile(path);
        if (img == null || img.IsEmpty()) return null;
        if (maxWidth > 0 && img.GetWidth() > maxWidth) img.Resize(maxWidth, Math.Max(1, img.GetHeight() * maxWidth / img.GetWidth()), Image.Interpolation.Lanczos);
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>Whether ANTHROPIC_API_KEY is set in the environment or in the repo-root <c>.env</c> (the build process reads it the same way).</summary>
    public static bool HasApiKey(string repo)
    {
        if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))) return true;
        try
        {
            var env = Path.Combine(repo, ".env");
            if (!File.Exists(env)) return false;
            foreach (var raw in File.ReadLines(env))
            {
                var line = raw.Trim();
                if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
                if (!line.StartsWith("ANTHROPIC_API_KEY", StringComparison.Ordinal)) continue;
                var eq = line.IndexOf('=');
                if (eq > 0 && line[..eq].Trim() == "ANTHROPIC_API_KEY" && line[(eq + 1)..].Trim().Trim('"', '\'') != "") return true;
            }
        }
        catch (IOException) { /* unreadable: the build says so */ }
        return false;
    }
}
