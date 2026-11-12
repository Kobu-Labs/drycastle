using System.Collections.Generic;
using Godot;


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

    public override void _Draw()
    {
        var st = State();
        var col = st switch { 3 => new Color(EditorTheme.Dim, 0.45f), 2 => EditorTheme.Accent, 1 => Colors.White, _ => EditorTheme.Text };
        var u = Mathf.Min(Owner.Size.X, Owner.Size.Y) * 0.6f / 24f;
        var o = (Owner.Size - new Vector2(24, 24) * u) / 2f;
        var w = 2f * u;
        Vector2 P(float x, float y) => o + new Vector2(x, y) * u;
        void Line(float x1, float y1, float x2, float y2) => DrawLine(P(x1, y1), P(x2, y2), col, w);
        void Poly(bool closed, params (float x, float y)[] pts)
        {
            var v = new Vector2[pts.Length + (closed ? 1 : 0)];
            for (var i = 0; i < pts.Length; i++) v[i] = P(pts[i].x, pts[i].y);
            if (closed) v[^1] = v[0];
            DrawPolyline(v, col, w);
        }
        void Rect(float x, float y, float rw, float rh) => DrawRect(new Rect2(P(x, y), new Vector2(rw, rh) * u), col, false, w);

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
            case "region":
                Poly(true, (4, 9), (11, 4), (20, 8), (18, 19), (7, 20));
                foreach (var (cx, cy) in new[] { (4f, 9f), (20f, 8f), (7f, 20f) }) DrawCircle(P(cx, cy), 2.2f * u, col);
                break;
            case "poles":
                Line(12, 4, 12, 21);
                Line(5, 7.5f, 19, 7.5f);
                Line(7, 7.5f, 7, 4.5f); Line(17, 7.5f, 17, 4.5f);
                Poly(false, (5, 7.5f), (2.5f, 10), (0.5f, 10.5f));
                Poly(false, (19, 7.5f), (21.5f, 10), (23.5f, 10.5f));
                break;
            case "trees":
                DrawCircle(P(12, 9), 6.5f * u, col);
                Line(12, 15, 12, 21);
                break;
            case "objects":
                Rect(4, 10, 10, 10); Rect(9, 5, 10, 10);
                Line(4, 10, 9, 5); Line(14, 10, 19, 5); Line(14, 20, 19, 15);
                break;
            case "view":
            {
                var pts = new Vector2[25];
                for (var i = 0; i <= 24; i++) { var a = i / 24f * Mathf.Tau; pts[i] = P(12 + 9.5f * Mathf.Cos(a), 12 + 5.5f * Mathf.Sin(a)); }
                DrawPolyline(pts, col, w);
                DrawCircle(P(12, 12), 3f * u, col);
                break;
            }
            case "sketch":
                Poly(false, (4, 19), (10, 10), (15, 15), (20, 6));
                foreach (var (cx, cy) in new[] { (4f, 19f), (20f, 6f), (6f, 6f) }) DrawCircle(P(cx, cy), 2.2f * u, col);
                break;
            case "undo":
            case "redo":
            {
                var m = Kind == "redo";
                float X(float x) => m ? 24 - x : x;
                var pts = new List<Vector2>();
                for (var i = 0; i <= 16; i++)
                {
                    var a = Mathf.Lerp(-2.6f, 1.1f, i / 16f);
                    pts.Add(P(X(12 + 6.5f * Mathf.Cos(a)), 13 + 6.5f * Mathf.Sin(a)));
                }
                DrawPolyline(pts.ToArray(), col, w);
                Poly(false, (X(4.5f), 6), (X(6.4f), 10.2f), (X(11), 8.6f));
                break;
            }
            case "help":
            {
                var f = GetThemeDefaultFont();
                var fs = (int)(18 * u * 24f / 18f * 0.8f);
                DrawString(f, P(0, 18), "?", HorizontalAlignment.Center, 24 * u, Mathf.Max(fs, 12), col);
                break;
            }
            case "reset":
            {
                var pts = new List<Vector2>();
                for (var i = 0; i <= 16; i++)
                {
                    var a = Mathf.Lerp(-2.4f, 2.9f, i / 16f);
                    pts.Add(P(12 + 7f * Mathf.Cos(a), 12 + 7f * Mathf.Sin(a)));
                }
                DrawPolyline(pts.ToArray(), col, w);
                Poly(false, (4.5f, 3.5f), (5.6f, 8.2f), (10.4f, 7.2f));
                break;
            }
            case "walk":
                DrawCircle(P(12, 5), 2.5f * u, col);
                Line(12, 8, 12, 15); Line(12, 15, 8, 22); Line(12, 15, 16, 22); Line(7, 11, 17, 11);
                break;
            case "close":
                Line(6, 6, 18, 18); Line(18, 6, 6, 18);
                break;
        }
    }
}
