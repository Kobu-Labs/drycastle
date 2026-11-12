using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// A drawn padlock or star for menu rows and tabs (ticket #147). Drawn rather than taken from a font, so it renders the same
/// on every system. It ignores the mouse and always sits beside a text label, so it is never the only cue.
/// </summary>
public sealed partial class Glyph : Control
{
    public enum Kind { Padlock, Star }

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
        if (_kind == Kind.Padlock) DrawPadlock(Size);
        else DrawStar(Size);
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
