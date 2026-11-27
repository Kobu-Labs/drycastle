using System.Text.Json;

namespace Drycastle.ShooterCore.Editor;

/// <summary>The Photo to building tool of the map editor: what its panel is doing. See <see cref="EditorMachines.PhotoBuildSpec"/>.</summary>
public enum PhotoBuildState { Idle, Building, Cancelling, Review, Failed }

/// <summary>One line of what <c>facade_pipeline auto</c> prints (docs/photo-to-building.md): its stage, a problem, the made-up list, the result, an error, or other text.</summary>
public enum PhotoLineKind { Other, Stage, Problem, Made, Result, Error }

public sealed record PhotoLine(PhotoLineKind Kind, string Text, string Stage = "", int Index = 0, int Count = 0, PhotoResult? Result = null);

/// <summary>The <c>RESULT:</c> line of a finished build: where the run's pictures are and what the tool noticed.</summary>
public sealed record PhotoResult(
    string Building, string Parcel, string Method, string Run, IReadOnlyList<string> Overlays, IReadOnlyList<string> Compares,
    IReadOnlyList<string> Problems, IReadOnlyList<string> Invented, string Roof, double EaveM, double TopM, int Openings, bool CanRedoEnds, bool Replaced);

/// <summary>Reads the progress lines of the build process. Pure: the viewer feeds it each line of the process's output.</summary>
public static class PhotoBuildLog
{
    public static PhotoLine Parse(string line)
    {
        var t = line.TrimEnd();
        if (t.StartsWith("STAGE:", StringComparison.Ordinal))
        {
            var rest = t[6..].Trim();
            var slash = rest.LastIndexOf(' ');
            if (slash > 0 && rest[(slash + 1)..].Split('/') is [var i, var n] && int.TryParse(i, out var index) && int.TryParse(n, out var count))
                return new PhotoLine(PhotoLineKind.Stage, rest, rest[..slash], index, count);
            return new PhotoLine(PhotoLineKind.Stage, rest, rest);
        }
        if (t.StartsWith("PROBLEM:", StringComparison.Ordinal)) return new PhotoLine(PhotoLineKind.Problem, t[8..].Trim());
        if (t.StartsWith("MADE UP BY RULE", StringComparison.Ordinal)) return new PhotoLine(PhotoLineKind.Made, t[(t.IndexOf(':') + 1)..].Trim());
        if (t.StartsWith("ERROR:", StringComparison.Ordinal)) return new PhotoLine(PhotoLineKind.Error, t[6..].Trim());
        if (t.StartsWith("RESULT:", StringComparison.Ordinal))
        {
            try { return new PhotoLine(PhotoLineKind.Result, "", Result: ParseResult(t[7..].Trim())); }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return new PhotoLine(PhotoLineKind.Error, "the build's result line is unreadable: " + e.Message); }
        }
        return new PhotoLine(PhotoLineKind.Other, t);
    }

    public static PhotoResult ParseResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        double Num(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        IReadOnlyList<string> List(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(e => e.GetString() ?? "").ToList() : Array.Empty<string>();
        bool Flag(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
        return new PhotoResult(Str("building"), Str("parcel"), Str("method"), Str("run"), List("overlays"), List("compares"), List("problems"), List("invented"),
            Str("roof"), Num("eave_m"), Num("top_m"), (int)Num("openings"), Flag("can_redo_ends"), Flag("replaced"));
    }

    /// <summary>What the panel says for a stage name of the build ("read ends" is a model call).</summary>
    public static string StageText(string stage) => stage switch
    {
        "ends" => "Finding the wall ends in the photos",
        "read ends" => "Asking the model where each wall stops",
        "walls" => "Straightening the walls",
        "read walls" => "Asking the model what is on each wall",
        "apply" => "Measuring, writing the building",
        "prep" => "Preparing your photo",
        "read sheet" => "Asking the model to read your photo",
        _ => stage,
    };
}

/// <summary>A wall of the footprint in map metres (x east, y north), numbered as the building detail file numbers it; the normal points outward.</summary>
public readonly record struct WallEdge(int Index, double Px, double Py, double Qx, double Qy, double Nx, double Ny)
{
    public double Length => Math.Sqrt((Qx - Px) * (Qx - Px) + (Qy - Py) * (Qy - Py));
}

/// <summary>An opening as the detail file says it: its wall, where along it (from the wall's first corner to the opening's left side), size, and height of its bottom above the ground.</summary>
public readonly record struct OpeningSpec(string Id, int Edge, double At, double Width, double Height, double Sill);

/// <summary>The geometry of openings on walls: where one is in the world, and which one a ray hits. Pure (map metres, z up).</summary>
public static class BuildingGeom
{
    /// <summary>The four corners of an opening (bottom left, bottom right, top right, top left, seen from outside), <paramref name="lift"/> metres off the wall.</summary>
    public static (double X, double Y, double Z)[] Corners(WallEdge e, OpeningSpec o, double baseZ, double lift = 0.0)
    {
        var len = Math.Max(e.Length, 1e-9);
        double ux = (e.Qx - e.Px) / len, uy = (e.Qy - e.Py) / len;
        (double, double, double) At(double s, double z) => (e.Px + ux * s + e.Nx * lift, e.Py + uy * s + e.Ny * lift, baseZ + z);
        var z0 = o.Sill; var z1 = o.Sill + o.Height;
        return new[] { At(o.At, z0), At(o.At + o.Width, z0), At(o.At + o.Width, z1), At(o.At, z1) };
    }

    /// <summary>The id of the opening the ray hits first (nearest wall plane crossing from outside), within <paramref name="margin"/> metres of its edge; null for none.</summary>
    public static string? Pick(IReadOnlyList<WallEdge> edges, IReadOnlyList<OpeningSpec> openings, double baseZ, (double X, double Y, double Z) origin, (double X, double Y, double Z) dir, double margin = 0.12)
    {
        string? best = null;
        var bestT = double.MaxValue;
        foreach (var o in openings)
        {
            var e = edges.FirstOrDefault(x => x.Index == o.Edge);
            if (e.Length <= 0) continue;
            var dn = dir.X * e.Nx + dir.Y * e.Ny;
            if (dn >= -1e-6) continue; // the ray runs along the wall or away from it: seen from inside or behind
            var t = ((e.Px - origin.X) * e.Nx + (e.Py - origin.Y) * e.Ny) / dn;
            if (t <= 0 || t >= bestT) continue;
            double hx = origin.X + dir.X * t, hy = origin.Y + dir.Y * t, hz = origin.Z + dir.Z * t;
            var s = ((hx - e.Px) * (e.Qx - e.Px) + (hy - e.Py) * (e.Qy - e.Py)) / e.Length;
            if (s < o.At - margin || s > o.At + o.Width + margin) continue;
            if (hz < baseZ + o.Sill - margin || hz > baseZ + o.Sill + o.Height + margin) continue;
            best = o.Id;
            bestT = t;
        }
        return best;
    }
}

/// <summary>What the tool ticks by default: the newest captures, as many as the pipeline uses (facade_pipeline.MAX_CAPTURES).</summary>
public static class PhotoDefaults
{
    public const int Captures = 3;
}

/// <summary>The arrow-key step of a number field: Shift is five steps.</summary>
public static class Nudge
{
    public static double Step(double step, bool shift) => shift ? step * 5 : step;

    /// <summary>The next free opening id of a kind, as the pipeline names them (w1, d2, g1).</summary>
    public static string NextId(string kind, IEnumerable<string> taken)
    {
        var prefix = kind switch { "door" or "glass_door" => "d", "garage_door" => "g", "recess" => "r", _ => "w" };
        var set = taken.ToHashSet(StringComparer.Ordinal);
        for (var n = 1; ; n++) if (!set.Contains(prefix + n)) return prefix + n;
    }
}
