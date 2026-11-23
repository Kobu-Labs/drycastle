using Godot;
using Drycastle.ShooterCore.Hunts;

namespace Drycastle.Shooter;

/// <summary>Shapes the pieces of a collection log share: sampled curves and strokes with round ends. All sizes are reference pixels (the HUD scales the root).</summary>
static class LogDraw
{
    public static Vector2[] Quad(Vector2 a, Vector2 c, Vector2 b, int steps = 10)
    {
        var pts = new Vector2[steps + 1];
        for (var i = 0; i <= steps; i++)
        {
            var t = i / (float)steps; var u = 1f - t;
            pts[i] = u * u * a + 2f * u * t * c + t * t * b;
        }
        return pts;
    }

    public static Vector2[] Cubic(Vector2 a, Vector2 c1, Vector2 c2, Vector2 b, int steps = 16)
    {
        var pts = new Vector2[steps + 1];
        for (var i = 0; i <= steps; i++)
        {
            var t = i / (float)steps; var u = 1f - t;
            pts[i] = u * u * u * a + 3f * u * u * t * c1 + 3f * u * t * t * c2 + t * t * t * b;
        }
        return pts;
    }

    /// <summary>A stroke with round ends.</summary>
    public static void Stroke(CanvasItem on, Vector2[] pts, Color color, float width)
    {
        on.DrawPolyline(pts, color, width, true);
        on.DrawCircle(pts[0], width / 2f, color);
        on.DrawCircle(pts[^1], width / 2f, color);
    }

    /// <summary>A closed outline (the first point is repeated so the last joint is drawn).</summary>
    public static void Outline(CanvasItem on, Vector2[] closed, Color color, float width)
    {
        var pts = new Vector2[closed.Length + 2];
        closed.CopyTo(pts, 0);
        pts[^2] = closed[0]; pts[^1] = closed[1];
        on.DrawPolyline(pts, color, width, true);
    }

    /// <summary>A disc with a darker ring: a screw, a rivet, a tack.</summary>
    public static void Stud(CanvasItem on, Vector2 at, float radius, Color fill, Color edge)
    {
        on.DrawCircle(at, radius, edge);
        on.DrawCircle(at, radius - 2f, fill);
    }

    /// <summary>A rarity medal: two ribbon tails and a disc with an inner ring. <paramref name="at"/> is the disc's centre.</summary>
    public static void Medal(CanvasItem on, Vector2 at, float r, Color metal, Color edge)
    {
        foreach (var side in new[] { -1f, 1f })
        {
            var tail = new[] { at + new Vector2(side * r * 0.5f, r * 0.6f), at + new Vector2(side * r * 0.9f, r * 2.1f), at + new Vector2(side * r * 0.2f, r * 1.6f) };
            on.DrawColoredPolygon(tail, metal);
            Outline(on, tail, edge, 1.5f);
        }
        on.DrawCircle(at, r, edge);
        on.DrawCircle(at, r - 2f, metal);
        on.DrawArc(at, r * 0.55f, 0, Mathf.Tau, 24, new Color(0, 0, 0, 0.4f), 1.5f, true);
    }
}

/// <summary>
/// The board of a collection log: its surface (planks with a stepped grain, or courses of masonry with a mottle), the lighter header band, the dark frame and a metal
/// bracket in each corner. Drawn once per size (a seeded pattern, so it is the same every time); nothing moves.
/// </summary>
public sealed partial class LogBoard : Control
{
    const float PlankH = 102, CourseH = 170;
    readonly LogSkin _skin;

    public LogBoard(LogSkin skin)
    {
        _skin = skin;
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    public override void _Notification(int what) { if (what == NotificationResized) QueueRedraw(); }

    public override void _Draw()
    {
        var s = _skin; var w = Size.X; var h = Size.Y;
        var rng = new Random(s.Seed);
        DrawRect(new Rect2(0, 0, w, h), s.Board);
        if (s.Surface == LogSurface.Planks) Planks(rng, w, h); else Masonry(rng, w, h);

        // the header band, with its own few streaks, and the joint under it
        DrawRect(new Rect2(0, 0, w, LogSkin.HeaderH), s.Header);
        for (var i = 0; i < 9; i++) Streak(rng, 0, w, 6, LogSkin.HeaderH - 8, 0.10f);
        DrawRect(new Rect2(0, LogSkin.HeaderH - 2, w, 4), s.Seam);

        // the frame and the brackets
        DrawRect(new Rect2(LogSkin.Frame / 2f, LogSkin.Frame / 2f, w - LogSkin.Frame, h - LogSkin.Frame), s.Edge, false, LogSkin.Frame);
        Bracket(new Vector2(-4, -4), 1, 1);
        Bracket(new Vector2(w + 4, -4), -1, 1);
        Bracket(new Vector2(-4, h + 4), 1, -1);
        Bracket(new Vector2(w + 4, h + 4), -1, -1);
    }

    void Planks(Random rng, float w, float h)
    {
        var s = _skin;
        var tones = new[] { s.BoardAlt, s.Board, s.BoardAlt2, s.Board };
        var row = 0;
        for (var y = (float)LogSkin.HeaderH; y < h; y += PlankH, row++)
        {
            var ph = Math.Min(PlankH, h - y);
            DrawRect(new Rect2(0, y, w, ph), tones[row % tones.Length]);
            for (var i = 0; i < 16; i++) Streak(rng, 0, w, y + 4, ph - 10, i % 3 == 0 ? 0.20f : 0.10f);
            DrawRect(new Rect2(0, y + ph - 3, w, 3), s.Seam);
        }
    }

    /// <summary>One dark streak of grain: long, thin, along the plank.</summary>
    void Streak(Random rng, float x0, float x1, float y0, float room, float alpha)
    {
        var len = 60f + (float)rng.NextDouble() * 340f;
        var x = x0 + (float)rng.NextDouble() * Math.Max(1f, x1 - x0 - len);
        var y = y0 + (float)rng.NextDouble() * Math.Max(1f, room);
        DrawRect(new Rect2(x, y, len, 2f + rng.Next(3)), new Color(0, 0, 0, alpha));
    }

    void Masonry(Random rng, float w, float h)
    {
        var s = _skin;
        var tones = new[] { s.BoardAlt, s.Board, s.BoardAlt2, s.Board };
        var row = 0;
        for (var y = (float)LogSkin.HeaderH; y < h; y += CourseH, row++)
        {
            var ch = Math.Min(CourseH, h - y);
            DrawRect(new Rect2(0, y, w, ch), tones[row % tones.Length]);
            // the mottle: a few darker and lighter patches, irregular four-sided
            for (var i = 0; i < 26; i++)
            {
                var c = new Vector2((float)rng.NextDouble() * w, y + (float)rng.NextDouble() * ch);
                var r = 14f + (float)rng.NextDouble() * 46f;
                var quad = new Vector2[4];
                for (var k = 0; k < 4; k++)
                {
                    var a = k * Mathf.Pi / 2f + (float)rng.NextDouble() * 0.9f;
                    quad[k] = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a) * 0.6f) * r * (0.6f + (float)rng.NextDouble() * 0.5f);
                    quad[k].Y = Math.Clamp(quad[k].Y, y, y + ch);
                }
                DrawColoredPolygon(quad, i % 4 == 0 ? new Color(1, 1, 1, 0.05f) : new Color(0, 0, 0, 0.10f));
            }
            // the joints: one under the course, upright ones staggered from course to course
            DrawRect(new Rect2(0, y + ch - 3, w, 3), s.Seam);
            var block = 380f + rng.Next(80);
            for (var x = (row % 2 == 0 ? 0.55f : 0.2f) * block; x < w - 40; x += block) DrawRect(new Rect2(x, y, 3, ch), s.Seam);
        }
    }

    /// <summary>An L-shaped plate in a corner, with three rivets. (<paramref name="sx"/>, <paramref name="sy"/>) point into the board.</summary>
    void Bracket(Vector2 corner, float sx, float sy)
    {
        const float L = LogSkin.Bracket, T = 14;
        Vector2 P(float x, float y) => corner + new Vector2(x * sx, y * sy);
        var pts = new[] { P(0, 0), P(L, 0), P(L, T), P(T + 12, T), P(T, T + 12), P(T, L), P(0, L) };
        // two convex parts (the L itself is not convex)
        DrawColoredPolygon(new[] { pts[0], pts[1], pts[2], pts[3], pts[4], P(0, T + 12) }, _skin.Metal);
        DrawColoredPolygon(new[] { P(0, T), pts[4], pts[5], pts[6] }, _skin.Metal);
        LogDraw.Outline(this, pts, _skin.MetalEdge, 2);
        DrawCircle(P(9, 9), 3.5f, _skin.MetalEdge);
        DrawCircle(P(L - 14, 7), 3f, _skin.MetalEdge);
        DrawCircle(P(7, L - 14), 3f, _skin.MetalEdge);
    }
}

/// <summary>
/// What sits on the top edge of the board, above it: a rack of antlers on a small mount (the Hunter's Log) or a cluster of crystals on a lump of rock (the Mineral
/// Log). Its origin is the middle of the board's top edge; it rises <see cref="LogSkin.CrestH"/> above it, inside the screen's margin over the panel.
/// </summary>
public sealed partial class LogCrestArt : Control
{
    readonly LogSkin _skin;

    public LogCrestArt(LogSkin skin)
    {
        _skin = skin;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw()
    {
        if (_skin.Crest == LogCrest.Antlers) Antlers(); else Crystals();
    }

    void Antlers()
    {
        var s = _skin;
        const float k = 0.82f;
        foreach (var side in new[] { 1f, -1f })
        {
            Vector2 P(float x, float y) => new Vector2(side * (14 + x * k), -4 + y * k);
            var beam = LogDraw.Cubic(P(0, 0), P(60, 6), P(150, 2), P(200, -28), 14);
            var curl = LogDraw.Cubic(P(200, -28), P(250, -58), P(262, -62), P(236, -72), 10);
            var tines = new[]
            {
                LogDraw.Quad(P(58, 3), P(64, -28), P(46, -50)),
                LogDraw.Quad(P(118, 1), P(128, -34), P(110, -60)),
                LogDraw.Quad(P(172, -14), P(190, -44), P(178, -70)),
            };
            LogDraw.Stroke(this, beam, s.CrestDark, 18 * k); LogDraw.Stroke(this, curl, s.CrestDark, 18 * k);
            foreach (var t in tines) LogDraw.Stroke(this, t, s.CrestDark, 14 * k);
            LogDraw.Stroke(this, beam, s.CrestLight, 11 * k); LogDraw.Stroke(this, curl, s.CrestLight, 11 * k);
            foreach (var t in tines) LogDraw.Stroke(this, t, s.CrestLight, 7.5f * k);
        }
        // the mount: a small shield over the board's edge, with a stud
        var mount = new List<Vector2> { new(-30, -18), new(30, -18), new(30, 2) };
        mount.AddRange(LogDraw.Quad(new(30, 2), new(30, 16), new(0, 24), 6)[1..]);
        mount.AddRange(LogDraw.Quad(new(0, 24), new(-30, 16), new(-30, 2), 6)[1..]);
        var pts = mount.ToArray();
        DrawColoredPolygon(pts, s.Tile);
        LogDraw.Outline(this, pts, s.Edge, 4);
        LogDraw.Stud(this, Vector2.Zero, 5, s.Metal, s.MetalEdge);
    }

    void Crystals()
    {
        var s = _skin;
        void Shard(float x, float deg, float scale)
        {
            var rot = Transform2D.Identity.Rotated(Mathf.DegToRad(deg));
            Vector2 P(float px, float py) => new Vector2(x, -10) + rot * (new Vector2(px, py) * scale);
            var left = new[] { P(-12, 0), P(-9, -40), P(0, -60), P(0, 0) };
            var right = new[] { P(0, -60), P(9, -40), P(12, 0), P(0, 0) };
            DrawColoredPolygon(left, s.CrestLight);
            DrawColoredPolygon(right, s.CrestDark);
            LogDraw.Outline(this, new[] { P(-12, 0), P(-9, -40), P(0, -60), P(9, -40), P(12, 0) }, s.Edge, 2);
        }
        Shard(-46, -62, 0.5f); Shard(46, 62, 0.5f);
        Shard(-34, -40, 0.72f); Shard(34, 40, 0.72f);
        Shard(-18, -18, 0.95f); Shard(18, 18, 0.95f);
        Shard(0, 0, 1.12f);
        var rock = new[] { new Vector2(-74, 12), new Vector2(-60, -8), new Vector2(-30, -14), new Vector2(30, -14), new Vector2(60, -8), new Vector2(74, 12), new Vector2(42, 24), new Vector2(-42, 24) };
        DrawColoredPolygon(rock, s.BoardAlt.Lightened(0.08f));
        LogDraw.Outline(this, rock, s.Edge, 3);
    }
}

/// <summary>
/// A tab of a collection log: a sign hung from the header by two cords. The current one is lighter with a thicker edge (the Theme), hangs a little lower and has a
/// tack in it, so it is told apart by shape as well as colour. The room for the tack is in every state's margin: nothing moves when the tab changes.
/// </summary>
public sealed partial class LogSign : Button
{
    readonly LogSkin _skin;

    public LogSign(LogSkin skin)
    {
        _skin = skin;
        ThemeTypeVariation = "LogTab";
        ToggleMode = true;
        FocusMode = FocusModeEnum.All;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        Toggled += _ => QueueRedraw();
    }

    public override void _Draw()
    {
        var cord = new Color(_skin.OnBoardDim, 1f);
        const float inset = 18;
        DrawRect(new Rect2(inset, -LogSkin.RopeH, 3, LogSkin.RopeH), cord);
        DrawRect(new Rect2(Size.X - inset - 3, -LogSkin.RopeH, 3, LogSkin.RopeH), cord);
        if (ButtonPressed) LogDraw.Stud(this, new Vector2(ShooterTheme.Space * 2 + 2, Size.Y / 2f), 6, _skin.Metal, _skin.MetalEdge);
    }
}

/// <summary>A metal plate with a screw in each corner (a stat of a log). The plate itself is the Theme's panel; this draws the screws on it.</summary>
public sealed partial class LogPlate : PanelContainer
{
    readonly LogSkin _skin;

    public LogPlate(LogSkin skin, string variation = "LogStat")
    {
        _skin = skin;
        ThemeTypeVariation = variation;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw()
    {
        const float i = LogSkin.ScrewInset;
        foreach (var at in new[] { new Vector2(i, i), new Vector2(Size.X - i, i), new Vector2(i, Size.Y - i), new Vector2(Size.X - i, Size.Y - i) })
            DrawCircle(at, LogSkin.Screw, _skin.MetalEdge);
    }
}

/// <summary>A paper pinned to the board (a record card, the hit card): the Theme's panel with a tack at the middle of its top edge.</summary>
public sealed partial class LogPaper : PanelContainer
{
    readonly LogSkin _skin;

    public LogPaper(LogSkin skin, string variation = "LogCard")
    {
        _skin = skin;
        ThemeTypeVariation = variation;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw() => LogDraw.Stud(this, new Vector2(Size.X / 2f, 0), LogSkin.Tack, _skin.Metal, _skin.MetalEdge);
}

/// <summary>A thin carved line that fills the row beside a section's title.</summary>
public sealed partial class LogRule : Control
{
    readonly LogSkin _skin;

    public LogRule(LogSkin skin)
    {
        _skin = skin;
        MouseFilter = MouseFilterEnum.Ignore;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        CustomMinimumSize = new Vector2(ShooterTheme.Space * 4, 3);
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(0, 0, Size.X, 2), _skin.Seam);
        DrawRect(new Rect2(0, 2, Size.X, 1), new Color(1, 1, 1, 0.10f));
    }
}

/// <summary>A rarity medal as a small picture (the legend beside the Life List's title).</summary>
public sealed partial class MedalGlyph : Control
{
    readonly Color _metal, _edge;

    public MedalGlyph(Color metal, Color edge, int size = ShooterTheme.Space * 3)
    {
        _metal = metal; _edge = edge;
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = new Vector2(size, size);
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
    }

    public override void _Draw() => LogDraw.Medal(this, new Vector2(Size.X / 2f, Size.Y * 0.36f), Size.Y * 0.32f, _metal, _edge);
}

/// <summary>What a plaque holds, which decides how it is drawn.</summary>
public enum PlaqueKind
{
    /// <summary>A hunted species: the plaque's wood, a rim in its rarity's metal and a medal for a rare one.</summary>
    Hunted,
    /// <summary>Not hunted yet: a duller plaque.</summary>
    Todo,
    /// <summary>A Mystery creature the viewer has not caught: a plaque under a cloth.</summary>
    Covered,
    /// <summary>A caught Mystery creature: framed in its own style, whatever the rarity.</summary>
    Mystery,
}

/// <summary>What a caught Mystery creature's plaque has on its corners besides the rim: nothing, four-pointed stars (the Unicorn) or tongues of flame (the Phoenix). A shape, so the style is not colour alone.</summary>
public enum MysteryDecor { None, Sparkles, Flames }

/// <summary>How a caught Mystery creature's plaque is framed: its field, its rim (one colour, or several run along the rim) and its corner decoration.</summary>
public sealed record MysteryFrame(Color Field, Color[] Rim, MysteryDecor Decor)
{
    static readonly Color[] Iris = { new(0xff9ecfff), new(0xffe08aff), new(0x9ef0c0ff), new(0x8fd0ffff), new(0xc9a6ffff) };

    static readonly Dictionary<string, MysteryFrame> ById = new()
    {
        [Mysteries.Unicorn] = new(new(0x3a2a5cff), Iris, MysteryDecor.Sparkles),
        [Mysteries.Bigfoot] = new(new(0x2f3b24ff), new[] { new Color(0x8a6a3aff), new Color(0x6f8f4aff) }, MysteryDecor.None),
        [Mysteries.Scylla] = new(new(0x123a46ff), new[] { new Color(0x4fd6c8ff), new Color(0x2f8fb0ff) }, MysteryDecor.None),
        [Mysteries.Phoenix] = new(new(0x3b1410ff), new[] { new Color(0xffc247ff), new Color(0xff6a1fff), new Color(0xd8321aff) }, MysteryDecor.Flames), // a night field of embers, a rim of gold through orange to red
    };

    static readonly MysteryFrame Plain = new(new(0x2b2b33ff), new[] { new Color(0xd5dae0ff) }, MysteryDecor.None);

    public static MysteryFrame Of(string species) => ById.GetValueOrDefault(species) ?? Plain;
}

/// <summary>
/// A Life List tile's plaque: a shield-shaped board the animal is mounted on, drawn behind the tile's content and filling the tile's button. It draws the button's
/// states itself (the button's own styles are empty): hover lightens the wood, pressed lightens it more and thickens the rim, focus adds a ring around the shield.
/// A rare weight gives the rim its metal and a medal on the corner (the tile also says the tier in words); a caught Mystery creature has its own frame; one the
/// viewer has not caught is a plaque under a cloth.
/// </summary>
public sealed partial class PlaqueArt : Control
{
    readonly LogSkin _skin;
    readonly PlaqueKind _kind;
    readonly HuntTier _tier;
    readonly MysteryFrame? _mystery;
    BaseButton? _button;

    public PlaqueArt(LogSkin skin, PlaqueKind kind, HuntTier tier = HuntTier.Common, string mystery = "")
    {
        _skin = skin; _kind = kind; _tier = tier;
        _mystery = kind == PlaqueKind.Mystery ? MysteryFrame.Of(mystery) : null;
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    public override void _Ready()
    {
        // the button redraws on every change of state (hover, press, focus): so does the plaque
        _button = GetParent() as BaseButton;
        if (_button != null) _button.Draw += QueueRedraw;
    }

    public override void _Notification(int what) { if (what == NotificationResized) QueueRedraw(); }

    Vector2[] Shield(float grow = 0)
    {
        float w = Size.X, h = Size.Y, m = 7 - grow, c = 9;
        var pts = new List<Vector2> { new(m + c, m), new(w - m - c, m), new(w - m, m + c), new(w - m, h * 0.68f) };
        pts.AddRange(LogDraw.Quad(new(w - m, h * 0.68f), new(w - m, h * 0.93f + grow), new(w / 2f, h - m), 10)[1..]);
        pts.AddRange(LogDraw.Quad(new(w / 2f, h - m), new(m, h * 0.93f + grow), new(m, h * 0.68f), 10)[1..]);
        pts.Add(new(m, m + c));
        return pts.ToArray();
    }

    public override void _Draw()
    {
        var s = _skin;
        var mode = _button?.GetDrawMode() ?? BaseButton.DrawMode.Normal;
        var lift = mode switch { BaseButton.DrawMode.Hover => 0.10f, BaseButton.DrawMode.Pressed or BaseButton.DrawMode.HoverPressed => 0.20f, _ => 0f };
        var pressed = mode is BaseButton.DrawMode.Pressed or BaseButton.DrawMode.HoverPressed;
        var shield = Shield();

        var field = _kind switch { PlaqueKind.Todo => s.TileTodo, PlaqueKind.Covered => s.Cover, PlaqueKind.Mystery => _mystery!.Field, _ => s.Tile };
        LogDraw.Outline(this, shield, s.Edge, 10);
        DrawColoredPolygon(shield, field.Lightened(lift));
        if (_button is { } b && b.HasFocus()) LogDraw.Outline(this, Shield(5), s.Focus, ShooterTheme.BorderStrong); // on the outer half of the dark edge, inside the tile's own rectangle

        if (_kind == PlaqueKind.Covered)
        {
            // the folds of the cloth
            float w = Size.X, h = Size.Y;
            foreach (var x in new[] { 0.22f, 0.5f, 0.76f })
                DrawPolyline(LogDraw.Quad(new(w * x, 6), new(w * (x + 0.05f), h * 0.45f), new(w * (x - 0.01f), h * (x == 0.5f ? 0.94f : 0.82f)), 8), new Color(0, 0, 0, 0.22f), 3, true);
            return;
        }

        // the grain of the plaque
        if (_kind != PlaqueKind.Mystery)
        {
            float w = Size.X, h = Size.Y;
            var grain = new Color(0, 0, 0, 0.14f);
            DrawPolyline(LogDraw.Quad(new(14, h * 0.18f), new(w * 0.5f, h * 0.12f), new(w - 14, h * 0.17f), 8), grain, 2, true);
            DrawPolyline(LogDraw.Quad(new(14, h * 0.55f), new(w * 0.5f, h * 0.61f), new(w - 14, h * 0.57f), 8), grain, 2, true);
        }

        // the rim: the rarity's metal, the creature's own colours, or none (the dark edge alone)
        var rimWidth = pressed ? 6f : 4f;
        if (_mystery is { } frame)
        {
            var closed = new Vector2[shield.Length + 1];
            shield.CopyTo(closed, 0); closed[^1] = shield[0];
            var colors = new Color[closed.Length];
            for (var i = 0; i < colors.Length; i++)
            {
                var at = i / (float)(colors.Length - 1) * frame.Rim.Length;
                var a = frame.Rim[(int)at % frame.Rim.Length]; var c = frame.Rim[((int)at + 1) % frame.Rim.Length];
                colors[i] = a.Lerp(c, at - MathF.Floor(at));
            }
            DrawPolylineColors(closed, colors, rimWidth + 2, true);
            if (frame.Decor != MysteryDecor.None)
                foreach (var (x, y, r) in new[] { (0.13f, 0.14f, 7f), (0.86f, 0.28f, 9f), (0.2f, 0.4f, 5f), (0.78f, 0.1f, 5f), (0.14f, 0.66f, 6f), (0.86f, 0.62f, 5f) })
                {
                    if (frame.Decor == MysteryDecor.Sparkles) Sparkle(new Vector2(Size.X * x, Size.Y * y), r);
                    else Flame(new Vector2(Size.X * x, Size.Y * y), r);
                }
        }
        else if (LogSkin.TierColor(_kind == PlaqueKind.Hunted ? _tier : HuntTier.Common) is { } metal)
        {
            LogDraw.Outline(this, shield, metal, rimWidth);
            LogDraw.Medal(this, new Vector2(Size.X - 26, 20), 10, metal, s.Edge);
        }
        else if (pressed) LogDraw.Outline(this, shield, s.OnTileDim, 2);
    }

    /// <summary>A tongue of flame, tip up, <paramref name="r"/> wide: an outer orange one and a smaller gold one inside it (two kites, so each polygon is convex).</summary>
    void Flame(Vector2 at, float r)
    {
        DrawColoredPolygon(FlameShape(at, r), new Color(0xff7a22ff));
        DrawColoredPolygon(FlameShape(at + new Vector2(0, r * 0.35f), r * 0.55f), new Color(0xffd36aff));
    }

    static Vector2[] FlameShape(Vector2 at, float r) => new[]
    {
        at + new Vector2(0, -r * 1.7f), at + new Vector2(r * 0.9f, -r * 0.1f), at + new Vector2(r * 0.55f, r * 0.9f), at + new Vector2(0, r * 1.1f), at + new Vector2(-r * 0.55f, r * 0.9f), at + new Vector2(-r * 0.9f, -r * 0.1f),
    };

    /// <summary>A four-pointed star.</summary>
    void Sparkle(Vector2 at, float r)
    {
        var pts = new Vector2[8];
        for (var i = 0; i < 8; i++)
        {
            var a = i * Mathf.Pi / 4f;
            pts[i] = at + new Vector2(Mathf.Sin(a), -Mathf.Cos(a)) * (i % 2 == 0 ? r : r * 0.28f);
        }
        // a star is not convex: four kites from the centre
        for (var i = 0; i < 8; i += 2) DrawColoredPolygon(new[] { at, pts[(i + 7) % 8], pts[i], pts[i + 1] }, Colors.White);
    }
}
