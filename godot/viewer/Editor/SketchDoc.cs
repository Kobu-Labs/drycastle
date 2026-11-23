using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Drycastle.Viewer.Editor;

public enum SketchKind { Point, Line }

public sealed record SketchPoint(string Id, double X, double Y, string Sketch = SketchDoc.DefaultSketch);

public sealed record SketchLine(string Id, bool Closed, IReadOnlyList<(double x, double y)> Points, string Sketch = SketchDoc.DefaultSketch)
{
    public double LengthM
    {
        get
        {
            double len = 0;
            var n = Closed ? Points.Count : Points.Count - 1;
            for (var i = 0; i < n; i++) { var p = Points[i]; var q = Points[(i + 1) % Points.Count]; len += Math.Sqrt((q.x - p.x) * (q.x - p.x) + (q.y - p.y) * (q.y - p.y)); }
            return len;
        }
    }

    public double AreaM2
    {
        get
        {
            if (!Closed) return 0;
            double a = 0;
            for (var i = 0; i < Points.Count; i++) { var p = Points[i]; var q = Points[(i + 1) % Points.Count]; a += p.x * q.y - q.x * p.y; }
            return Math.Abs(a) / 2;
        }
    }
}

/// <summary>
/// One state of the sketch (the marks of the Sketch tool, see <see cref="Tools.SketchTool"/>). Never changed in place: every edit makes a new one, so undo keeps the old.
/// Also the sketch.json format: <see cref="ToJson"/> and <see cref="Parse"/>.
/// </summary>
public sealed record SketchDoc(IReadOnlyList<SketchPoint> Points, IReadOnlyList<SketchLine> Lines, int NextPoint, int NextLine, IReadOnlyList<string> Names)
{
    public const string DefaultSketch = "DEFAULT";

    public static readonly SketchDoc Empty = new(Array.Empty<SketchPoint>(), Array.Empty<SketchLine>(), 0, 0, Array.Empty<string>());
    public int Count => Points.Count + Lines.Count;
    public int CountIn(string sketch) => Points.Count(p => p.Sketch == sketch) + Lines.Count(l => l.Sketch == sketch);

    /// <summary>A sketch name: 1 to 24 letters, digits, _ or - (the MCP server checks the same).</summary>
    public static bool ValidName(string n) => n.Length is >= 1 and <= 24 && n.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-');

    /// <summary>Point names: A to Z, then AA, AB, ...</summary>
    public static string PointLabel(int index)
    {
        var s = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s;
        return s;
    }

    static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>sketch.json: format_version 1, a version that goes up on every write, the sketch names and the marks (see pipeline/src/drycastle_pipeline/edits/sketchfile.py).</summary>
    public string ToJson(int version = 0)
    {
        var sb = new StringBuilder("{\"format_version\":1,\"lines\":[");
        for (var i = 0; i < Lines.Count; i++)
        {
            var l = Lines[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"closed\":").Append(l.Closed ? "true" : "false").Append(",\"id\":").Append(JsonSerializer.Serialize(l.Id)).Append(",\"points\":[");
            for (var j = 0; j < l.Points.Count; j++)
            {
                if (j > 0) sb.Append(',');
                sb.Append('[').Append(Num(l.Points[j].x)).Append(',').Append(Num(l.Points[j].y)).Append(']');
            }
            sb.Append("],\"sketch\":").Append(JsonSerializer.Serialize(l.Sketch)).Append('}');
        }
        sb.Append("],\"next_line\":").Append(NextLine).Append(",\"next_point\":").Append(NextPoint).Append(",\"points\":[");
        for (var i = 0; i < Points.Count; i++)
        {
            var p = Points[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(JsonSerializer.Serialize(p.Id)).Append(",\"sketch\":").Append(JsonSerializer.Serialize(p.Sketch))
              .Append(",\"x\":").Append(Num(p.X)).Append(",\"y\":").Append(Num(p.Y)).Append('}');
        }
        sb.Append("],\"sketches\":[").Append(string.Join(",", Names.Select(n => JsonSerializer.Serialize(n)))).Append("],\"version\":").Append(version).Append('}');
        return sb.ToString();
    }

    /// <summary>The version of a sketch.json document (0 when it has none).</summary>
    public static int VersionOf(JsonElement root) => root.TryGetProperty("version", out var v) && v.TryGetInt32(out var i) ? i : 0;

    /// <summary>The sketch of a sketch.json document (or of the `sketch` value an older editor-state.json held: its marks join DEFAULT).</summary>
    public static SketchDoc Parse(JsonElement s, out int version)
    {
        version = VersionOf(s);
        var points = new List<SketchPoint>();
        var lines = new List<SketchLine>();
        var names = new List<string>();
        string SketchOf(JsonElement m) => m.TryGetProperty("sketch", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } t ? t : DefaultSketch;
        if (s.TryGetProperty("sketches", out var ns) && ns.ValueKind == JsonValueKind.Array)
            foreach (var n in ns.EnumerateArray()) if (n.GetString() is { } t && ValidName(t) && !names.Contains(t)) names.Add(t);
        if (s.TryGetProperty("points", out var ps))
            foreach (var p in ps.EnumerateArray())
                points.Add(new SketchPoint(p.GetProperty("id").GetString()!, p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble(), SketchOf(p)));
        if (s.TryGetProperty("lines", out var ls))
            foreach (var l in ls.EnumerateArray())
            {
                var pts = l.GetProperty("points").EnumerateArray().Select(q => (q[0].GetDouble(), q[1].GetDouble())).ToList();
                if (pts.Count >= 2) lines.Add(new SketchLine(l.GetProperty("id").GetString()!, l.GetProperty("closed").GetBoolean() && pts.Count >= 3, pts, SketchOf(l)));
            }
        foreach (var n in points.Select(p => p.Sketch).Concat(lines.Select(l => l.Sketch))) if (!names.Contains(n)) names.Add(n);
        var np = s.TryGetProperty("next_point", out var a) && a.TryGetInt32(out var ai) ? ai : 0;
        var nl = s.TryGetProperty("next_line", out var b) && b.TryGetInt32(out var bi) ? bi : 0;
        return new SketchDoc(points, lines, Math.Max(np, points.Count), Math.Max(nl, lines.Count), names);
    }
}
