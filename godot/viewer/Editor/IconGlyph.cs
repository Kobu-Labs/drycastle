using Godot;
using System.Collections.Generic;

namespace Drycastle.Viewer.Editor;

/// <summary>
/// Vector icon drawn on top of a Button (the one editor icon set: 24-unit grid, 2-unit strokes, no fills but the tree and eye).
/// Colour follows the owner's state: normal, hover, pressed, disabled.
/// </summary>
public partial class IconGlyph : Control
{
    public string Kind { get; set; } = "";
    public Button Owner { get; set; } = null!;
    int _last = -1;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        Owner.Resized += QueueRedraw; // the parent is laid out after this node is ready; draw again at the real size
        Resized += QueueRedraw;
    }

    int State() => Owner.Disabled ? 3 : Owner.ButtonPressed ? 2 : Owner.IsHovered() ? 1 : 0;

    public override void _Process(double delta)
    {
        var s = State();
        if (s == _last) return;
        _last = s;
        QueueRedraw();
    }

    // set at the start of each _Draw, shared by the stroke helpers below
    Color _col;
    float _u, _w;
    Vector2 _o;

    public override void _Draw()
    {
        var st = State();
        _col = st switch { 3 => new Color(EditorTheme.Dim, 0.45f), 2 => EditorTheme.Accent, 1 => Colors.White, _ => EditorTheme.Text };
        _u = Mathf.Min(Owner.Size.X, Owner.Size.Y) * 0.6f / 24f;
        _o = (Owner.Size - new Vector2(24, 24) * _u) / 2f;
        _w = 2f * _u;

        switch (Kind)
        {
            case "select":
                Poly(true, (6, 3), (6, 19), (10.5f, 14.5f), (14, 21), (16.5f, 19.8f), (13, 13.5f), (19, 13.5f));
                break;
            case "group":
                Rect(3, 8, 11, 11);
                Rect(10, 4, 11, 11);
                break;
            case "fence":
                Line(5, 6, 5, 20); Line(12, 6, 12, 20); Line(19, 6, 19, 20);
                Line(3, 10, 21, 10); Line(3, 16, 21, 16);
                break;
            case "zone":
                Poly(true, (4, 9), (11, 4), (20, 8), (18, 19), (7, 20));
                foreach (var (cx, cy) in new[] { (4f, 9f), (20f, 8f), (7f, 20f) }) DrawCircle(P(cx, cy), 2.2f * _u, _col);
                break;
            case "ground":
                Poly(false, (3, 9), (8, 6), (13, 9), (18, 6), (21, 8));
                Poly(false, (3, 14), (8, 11), (13, 14), (18, 11), (21, 13));
                Poly(false, (3, 19), (8, 16), (13, 19), (18, 16), (21, 18));
                break;
            case "poles":
                Line(12, 4, 12, 21);
                Line(5, 7.5f, 19, 7.5f);
                Line(7, 7.5f, 7, 4.5f); Line(17, 7.5f, 17, 4.5f);
                Poly(false, (5, 7.5f), (2.5f, 10), (0.5f, 10.5f));
                Poly(false, (19, 7.5f), (21.5f, 10), (23.5f, 10.5f));
                break;
            case "objects":
                Rect(4, 10, 10, 10); Rect(9, 5, 10, 10);
                Line(4, 10, 9, 5); Line(14, 10, 19, 5); Line(14, 20, 19, 15);
                break;
            case "view": DrawEye(); break;
            case "sketch":
                Poly(false, (4, 19), (10, 10), (15, 15), (20, 6));
                foreach (var (cx, cy) in new[] { (4f, 19f), (20f, 6f), (6f, 6f) }) DrawCircle(P(cx, cy), 2.2f * _u, _col);
                break;
            case "undo":
            case "redo":
            {
                var m = Kind == "redo"; // redo is undo mirrored
                float X(float x) => m ? 24 - x : x;
                Arc(12, 13, 6.5f, -2.6f, 1.1f, m);
                Poly(false, (X(4.5f), 6), (X(6.4f), 10.2f), (X(11), 8.6f));
                break;
            }
            case "help": DrawHelp(); break;
            case "reset":
                Arc(12, 12, 7f, -2.4f, 2.9f, false);
                Poly(false, (4.5f, 3.5f), (5.6f, 8.2f), (10.4f, 7.2f));
                break;
            case "walk":
                DrawCircle(P(12, 5), 2.5f * _u, _col);
                Line(12, 8, 12, 15); Line(12, 15, 8, 22); Line(12, 15, 16, 22); Line(7, 11, 17, 11);
                break;
            case "photo":
                // a camera: body, the bump over the lens, the lens
                Rect(3, 8, 18, 12);
                Poly(false, (8, 8), (9.5f, 4.5f), (14.5f, 4.5f), (16, 8));
                Arc(12, 14, 3.6f, 0f, Mathf.Tau, false);
                break;
            case "close":
                Line(6, 6, 18, 18); Line(18, 6, 6, 18);
                break;
        }
    }

    Vector2 P(float x, float y) => _o + new Vector2(x, y) * _u;
    void Line(float x1, float y1, float x2, float y2) => DrawLine(P(x1, y1), P(x2, y2), _col, _w);
    void Rect(float x, float y, float rw, float rh) => DrawRect(new Rect2(P(x, y), new Vector2(rw, rh) * _u), _col, false, _w);
    void Poly(bool closed, params (float x, float y)[] pts)
    {
        var v = new Vector2[pts.Length + (closed ? 1 : 0)];
        for (var i = 0; i < pts.Length; i++) v[i] = P(pts[i].x, pts[i].y);
        if (closed) v[^1] = v[0];
        DrawPolyline(v, _col, _w);
    }

    /// <summary>Circle arc in 16 steps, x mirrored across the 24 grid when <paramref name="mirror"/>.</summary>
    void Arc(float cx, float cy, float r, float from, float to, bool mirror)
    {
        var pts = new List<Vector2>();
        for (var i = 0; i <= 16; i++)
        {
            var a = Mathf.Lerp(from, to, i / 16f);
            var x = cx + r * Mathf.Cos(a);
            pts.Add(P(mirror ? 24 - x : x, cy + r * Mathf.Sin(a)));
        }
        DrawPolyline(pts.ToArray(), _col, _w);
    }

    void DrawEye()
    {
        var pts = new Vector2[25];
        for (var i = 0; i <= 24; i++) { var a = i / 24f * Mathf.Tau; pts[i] = P(12 + 9.5f * Mathf.Cos(a), 12 + 5.5f * Mathf.Sin(a)); }
        DrawPolyline(pts, _col, _w);
        DrawCircle(P(12, 12), 3f * _u, _col);
    }

    void DrawHelp()
    {
        var f = GetThemeDefaultFont();
        var fs = (int)(18 * _u * 24f / 18f * 0.8f);
        DrawString(f, P(0, 18), "?", HorizontalAlignment.Center, 24 * _u, Mathf.Max(fs, 12), _col);
    }
}
