using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// A drawn padlock or star for menu rows and tabs (ticket #147). Drawn rather than taken from a font, so it renders the same
/// on every system. It ignores the mouse and always sits beside a text label, so it is never the only cue.
/// The Equipment screen (issue #268) adds the pictograms of its hint row (<see cref="Kind.Close"/>, <see cref="Kind.Swap"/>, <see cref="Kind.Drop"/>), which
/// stand beside a key cap, and the ghost of an empty headgear slot (<see cref="Kind.Headgear"/>).
/// </summary>
public sealed partial class Glyph : Control
{
    public enum Kind { Padlock, Star, Close, Swap, Drop, Headgear }

    readonly Kind _kind;
    readonly Color _color;

    public Glyph(Kind kind, Color color, int size = ShooterTheme.Space * 2)
    {
        _kind = kind;
        _color = color;
        CustomMinimumSize = new Vector2(size, size);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw()
    {
        switch (_kind)
        {
            case Kind.Padlock: DrawPadlock(Size); break;
            case Kind.Star: DrawStar(Size); break;
            case Kind.Close: DrawClose(Size); break;
            case Kind.Swap: DrawSwap(Size); break;
            case Kind.Drop: DrawDrop(Size); break;
            default: DrawHeadgear(Size); break;
        }
    }

    float Stroke(Vector2 s) => Mathf.Max(2f, Mathf.Min(s.X, s.Y) * 0.1f);

    void DrawClose(Vector2 s)
    {
        var a = s * 0.2f; var b = s * 0.8f;
        DrawLine(a, b, _color, Stroke(s), true);
        DrawLine(new Vector2(a.X, b.Y), new Vector2(b.X, a.Y), _color, Stroke(s), true);
    }

    /// <summary>Two arrows the other way round: what is worn goes to the bag, what is bagged is put on.</summary>
    void DrawSwap(Vector2 s)
    {
        var w = Stroke(s);
        var top = s.Y * 0.32f; var bottom = s.Y * 0.68f; var head = s.X * 0.18f;
        DrawLine(new Vector2(s.X * 0.12f, top), new Vector2(s.X * 0.88f, top), _color, w, true);
        DrawColoredPolygon(new[] { new Vector2(s.X * 0.92f, top), new Vector2(s.X * 0.92f - head, top - head), new Vector2(s.X * 0.92f - head, top + head) }, _color);
        DrawLine(new Vector2(s.X * 0.88f, bottom), new Vector2(s.X * 0.12f, bottom), _color, w, true);
        DrawColoredPolygon(new[] { new Vector2(s.X * 0.08f, bottom), new Vector2(s.X * 0.08f + head, bottom - head), new Vector2(s.X * 0.08f + head, bottom + head) }, _color);
    }

    /// <summary>An arrow down onto the ground line.</summary>
    void DrawDrop(Vector2 s)
    {
        var w = Stroke(s);
        var mid = s.X * 0.5f; var head = s.X * 0.2f;
        DrawLine(new Vector2(mid, s.Y * 0.1f), new Vector2(mid, s.Y * 0.62f), _color, w, true);
        DrawColoredPolygon(new[] { new Vector2(mid, s.Y * 0.74f), new Vector2(mid - head, s.Y * 0.74f - head * 1.2f), new Vector2(mid + head, s.Y * 0.74f - head * 1.2f) }, _color);
        DrawLine(new Vector2(s.X * 0.1f, s.Y * 0.9f), new Vector2(s.X * 0.9f, s.Y * 0.9f), _color, w, true);
    }

    /// <summary>A helmet seen from the side: a dome over a brim.</summary>
    void DrawHeadgear(Vector2 s)
    {
        var points = new List<Vector2>();
        const int steps = 16;
        var centre = new Vector2(s.X * 0.5f, s.Y * 0.62f);
        var rx = s.X * 0.36f; var ry = s.Y * 0.44f;
        for (var i = 0; i <= steps; i++)
        {
            var a = Mathf.Pi + i * Mathf.Pi / steps;
            points.Add(centre + new Vector2(Mathf.Cos(a) * rx, Mathf.Sin(a) * ry));
        }
        DrawColoredPolygon(points.ToArray(), _color);
        DrawRect(new Rect2(s.X * 0.12f, s.Y * 0.62f, s.X * 0.76f, s.Y * 0.12f), _color);
    }

    void DrawPadlock(Vector2 s)
    {
        var body = new Rect2(s.X * 0.15f, s.Y * 0.45f, s.X * 0.7f, s.Y * 0.55f);
        DrawRect(body, _color);
        DrawArc(new Vector2(s.X * 0.5f, s.Y * 0.45f), s.X * 0.28f, Mathf.Pi, Mathf.Tau, 16, _color, Mathf.Max(2f, s.X * 0.12f));
    }

    void DrawStar(Vector2 s)
    {
        var centre = s / 2f;
        var outer = Mathf.Min(s.X, s.Y) / 2f;
        var points = new Vector2[10];
        for (var i = 0; i < points.Length; i++)
        {
            var angle = -Mathf.Pi / 2f + i * Mathf.Pi / 5f;
            var radius = i % 2 == 0 ? outer : outer * 0.45f;
            points[i] = centre + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
        }
        DrawColoredPolygon(points, _color);
    }
}
