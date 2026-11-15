using System;
using System.IO;
using System.Text.Json;
using Godot;

/// <summary>
/// Puts the character where a Street View tab says you are. A small program outside the game
/// (<c>python -m drycastle_pipeline.streetview_follow</c>) watches the address of a Chrome tab and writes
/// <c>streetview-position.json</c> next to editor-state.json; this node polls that file four times a second and, when it
/// has a position newer than the viewer's start, moves the character there (S-JTSK metres to the map's frame, onto the
/// terrain) and turns it the way the camera looks. What the file held when the viewer started is ignored, so an old
/// position never moves the character by itself. No model, no MCP server.
/// </summary>
public partial class StreetViewFollow : Node
{
    const double PollSeconds = 0.25;

    public Drycastle.MapLoader.MapLoader Map { get; init; } = null!;
    public EditorMode Editor { get; init; } = null!;
    public string FilePath { get; init; } = "";

    long _stamp = -1, _seq = long.MinValue;
    double _t;
    bool _warnedOrigin;

    public override void _Ready()
    {
        // the baseline: whatever is there now is old
        if (TryRead(out var seq, out _, out _, out _)) _seq = seq;
        _stamp = Stamp();
        GD.Print($"street view follow: watching {FilePath}");
    }

    long Stamp() => File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath).Ticks : -1;

    public override void _Process(double delta)
    {
        _t += delta;
        if (_t < PollSeconds) return;
        _t = 0;
        var stamp = Stamp();
        if (stamp == _stamp) return;
        _stamp = stamp;
        if (!TryRead(out var seq, out var x, out var y, out var heading) || seq <= _seq) return;
        _seq = seq;
        if (Map.OriginEpsg5514 is not { } o)
        {
            if (!_warnedOrigin) { Editor.Say("Street View follow needs a map exported with origin_epsg5514 (0.4.0+)", true); _warnedOrigin = true; }
            return;
        }
        var lx = (float)(x - o.X);
        var lz = (float)-(y - o.Y);
        var feet = new Vector3(lx, Map.GroundHeightOrMean(lx, lz) + 1.0f, lz); // above the ground like a spawn: physics settles it
        // heading_grid is clockwise from the map's north (-z); a turn about +Y is counter-clockwise seen from above
        Editor.PlacePlayer(feet, heading is { } h ? -(float)h : null);
        Editor.Say($"Street View: character at x {x:F0}  y {y:F0}" + (heading is { } hh ? $"  facing {hh:F0} deg" : ""));
    }

    bool TryRead(out long seq, out double x, out double y, out double? heading)
    {
        seq = 0; x = y = 0; heading = null;
        try
        {
            if (!File.Exists(FilePath)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("seq", out var s) || !s.TryGetInt64(out seq)
                || !r.TryGetProperty("x", out var xe) || !xe.TryGetDouble(out x)
                || !r.TryGetProperty("y", out var ye) || !ye.TryGetDouble(out y)) return false;
            if (r.TryGetProperty("heading_grid", out var h) && h.ValueKind == JsonValueKind.Number && h.TryGetDouble(out var hv)) heading = hv;
            return true;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return false; // being written right now, or not ours: the next change is looked at again
        }
    }
}
