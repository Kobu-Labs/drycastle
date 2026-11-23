using Godot;
using Drycastle.ShooterCore.Mining;

namespace Drycastle.Shooter;

/// <summary>
/// The Mineral Log (N, rebindable action "mineral_log", also in the pause menu; crystals.md): every mineral a player can find, in one grid in a fixed shuffled order
/// (<see cref="Minerals.LogOrder"/>). A collection log like the Hunter's Log, on the same <see cref="LogFrame"/> with the stone skin: non-pausing, the world live around
/// it, the mouse released, the legs working. A card is a rock niche holding the mineral's crystals in its own colours with its name under it, always shown; a found one
/// is clear, has a tick and says which finder the player was ("7th found"); one not found yet shows the same crystals blurred behind frosted glass. It has one page, so
/// no tabs; Esc closes it. Nothing can be found yet: the log shows what it is given (<see cref="SetFinds"/>), which is nothing, so every card is behind glass.
/// </summary>
public sealed partial class MineralLogScreen : Control
{
    /// <summary>A card, and the niche in it, in reference pixels.</summary>
    const int CardW = 15 * ShooterTheme.Space, NicheH = 12 * ShooterTheme.Space;

    readonly LogFrame _frame;
    readonly VBoxContainer _host;
    IReadOnlyDictionary<string, int>? _ranks;

    static LogSkin Skin => LogSkin.Stone;
    public bool IsOpen => _frame.IsOpen;

    public MineralLogScreen()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        _frame = new LogFrame(Skin, Strings.T("mineral.title"));
        AddChild(_frame);
        _frame.BackPressed += () => { Sfx.Ui("ui.back"); Close(); };
        _frame.CloseRequested += Close;
        _host = _frame.NewHost(0);
        Rebuild();
    }

    /// <summary>What the player has found: mineral id to their place among its finders (1: the first). Redraws the cards.</summary>
    public void SetFinds(IReadOnlyDictionary<string, int>? ranks)
    {
        _ranks = ranks;
        Rebuild();
    }

    public void Open()
    {
        if (IsOpen) return;
        _frame.Open();
        Callable.From(_frame.FocusTab).CallDeferred();
    }

    public void Close() => _frame.Close();

    /// <summary>Esc closes it. True when it was open (the Esc is used).</summary>
    public bool HandleBack()
    {
        if (!IsOpen) return false;
        Sfx.Ui("ui.back");
        Close();
        return true;
    }

    void Rebuild()
    {
        foreach (var c in _host.GetChildren()) { _host.RemoveChild(c); c.QueueFree(); }
        var tiles = MineralTileModel.For(_ranks);

        var stats = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        stats.AddThemeConstantOverride("separation", ShooterTheme.Space + ShooterTheme.Space / 2);
        stats.AddChild(Stat($"{tiles.Count(t => t.Found)} / {tiles.Count}", Strings.T("mineral.stat.found"), Strings.T("mineral.stat.found.of")));
        stats.AddChild(Stat(tiles.Count(t => t.Rank == 1).ToString(), Strings.T("mineral.stat.first"), Strings.T("mineral.stat.first.of")));
        _host.AddChild(stats);

        var grid = new HFlowContainer { Alignment = FlowContainer.AlignmentMode.Center, SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        grid.AddThemeConstantOverride("h_separation", ShooterTheme.Space * 2);
        grid.AddThemeConstantOverride("v_separation", ShooterTheme.Space + ShooterTheme.Space / 2);
        foreach (var t in tiles) grid.AddChild(Card(t));
        _host.AddChild(grid);
    }

    Control Stat(string value, string caption, string what)
    {
        var p = new LogPlate(Skin) { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var h = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        h.AddThemeConstantOverride("separation", ShooterTheme.Space * 2);
        h.AddChild(LogFrame.Text(value, "InkBig"));
        var v = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        v.AddThemeConstantOverride("separation", 0);
        var cap = LogFrame.Text(caption, "LogCaption");
        cap.Uppercase = true;
        v.AddChild(cap);
        v.AddChild(LogFrame.Text(what, "InkSmall"));
        h.AddChild(v);
        p.AddChild(h);
        return p;
    }

    /// <summary>One mineral: the niche with its crystals, its name, and (once found) the player's place among its finders. Not a button: there is nowhere to go from a card yet.</summary>
    Control Card(MineralTileModel m)
    {
        var card = new MineralCard(Skin, m.Mineral, m.Found, NicheH) { CustomMinimumSize = new Vector2(CardW, 0) };
        var v = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", 0);
        v.AddChild(new Control { CustomMinimumSize = new Vector2(0, NicheH + ShooterTheme.Space), MouseFilter = MouseFilterEnum.Ignore }); // the niche is drawn here by the card
        var name = LogFrame.Text(Strings.T("mineral." + m.Mineral.Id), m.Found ? "TileText" : "TileDim", HorizontalAlignment.Center);
        name.ClipText = true;
        v.AddChild(name);
        v.AddChild(LogFrame.Text(m.Found ? Strings.F("mineral.rank", MineralFormat.Ordinal(m.Rank)) : " ", "TileDim", HorizontalAlignment.Center));
        card.AddChild(v);
        card.TooltipText = m.Found ? "" : Strings.T("mineral.todo");
        return card;
    }
}

/// <summary>The colours a mineral's crystals are drawn in on its card: three facets, the rock they stand on, and the light they give (none: no glow).</summary>
public readonly record struct MineralPaint(Color Light, Color Mid, Color Dark, Color Rock, Color? Glow)
{
    static MineralPaint P(uint light, uint mid, uint dark, uint rock, uint glow = 0) => new(new Color(light << 8 | 0xff), new Color(mid << 8 | 0xff), new Color(dark << 8 | 0xff), new Color(rock << 8 | 0xff), glow == 0 ? null : new Color(glow << 8 | 0xff));

    const uint Stone = 0x4a4e57;

    /// <summary>By mineral id; the fantasy ones follow their tints in crystals.json.</summary>
    static readonly Dictionary<string, MineralPaint> ById = new()
    {
        ["emerald"] = P(0x2fbf71, 0x1e9455, 0x156b3d, Stone, 0x3ce08a), ["sapphire"] = P(0x5b95ff, 0x2f62de, 0x173b9c, Stone, 0x5b95ff),
        ["amethyst"] = P(0xc9a6ff, 0x8a5cf0, 0x5f36b8, Stone, 0xb58cff), ["opal"] = P(0xf3f0ff, 0xbde8f0, 0xf7c6e0, Stone, 0xe8f4ff),
        ["agate"] = P(0xe08a4a, 0xb5622c, 0x8a4418, Stone, 0xe0a070), ["diamond"] = P(0xffffff, 0xd6ecff, 0xa9cff2, Stone, 0xe6f4ff),
        ["obsidian"] = P(0x4a4a58, 0x26262f, 0x101016, Stone), ["labradorite"] = P(0x5fd0c8, 0x3f7fb0, 0x2e4a6e, Stone, 0x6fe0e0),
        ["smetiskomite"] = P(0xc4a22e, 0xa8501c, 0x6f8f2a, 0x4a4338), ["pheonixite"] = P(0xffd466, 0xff7410, 0xff1f00, 0x3b2c27, 0xff5a1a),
        ["frostbite"] = P(0xffffff, 0xa9e4ff, 0x6fc0ee, 0x56616e, 0x7fe3ff), ["thundernite"] = P(0xfff2b0, 0xffe14a, 0xd9b520, 0x5a6070, 0xffd84a),
        ["zombite"] = P(0x7d8468, 0x6f7a5a, 0x5f6a52, 0x3d362c), ["bloodstone"] = P(0xb01020, 0x7a0612, 0x5c040e, 0x3a2626, 0xff2030),
        ["magmorite"] = P(0xff6a10, 0x2a201c, 0x14110f, 0x221c1a, 0xff6a1a), ["venomite"] = P(0xe6ffb0, 0x7dff3a, 0x0a5a10, 0x3a4030, 0x6dff2a),
        ["rosaline"] = P(0xf7b6cd, 0xff8fb4, 0xe0507e, 0x5d6b4a, 0xff8fb8), ["nazium"] = P(0x8a8a84, 0x5a6130, 0x4b5320, 0x7c7c76),
        ["holystone"] = P(0xfffdf2, 0xfff3c4, 0xffe08a, 0xb8b0a0, 0xffe6a8), ["brothers_stone"] = P(0x85868a, 0x77787a, 0x6a6b6e, 0x7d7d7a),
        ["futbalitrine"] = P(0xeeeee6, 0x0b0b0d, 0xbdbdb5, 0x1f6b22), ["senin"] = P(0xf2dc8c, 0xe6c264, 0xc79a3a, 0x4a3f2c, 0xffb24a),
        ["povodnovite"] = P(0xebffff, 0x35b39e, 0x053a4d, 0x2c3436, 0x4fd6c8), ["dubovite"] = P(0x6b4a2c, 0x5a3d24, 0x4a3220, 0x4a3d2e, 0x9fd45a),
        ["hovnovite"] = P(0x55501e, 0x4a3a18, 0x3f3312, 0x3a2a14), ["micin"] = P(0xffffff, 0xf5f2ea, 0xbdb8ad, 0x8a8780, 0xffffff),
        ["marihuanite"] = P(0x17702c, 0x0f5a22, 0x0b4a1c, 0x4a4a3a, 0x5fd06a), ["kulturite"] = P(0xffe9b0, 0xc9a6ff, 0x8fd0ff, 0x9e6633, 0xffe9b0),
    };

    public static MineralPaint Of(string id) => ById.GetValueOrDefault(id, P(0xd5dae0, 0x9aa0a8, 0x6e747d, Stone));
}

/// <summary>
/// A mineral's card: a slab with an arched niche at its top and the mineral's crystals standing in it (three shards on a lump of rock, in the mineral's colours, with
/// its light behind them). Found: clear, a tick in the corner, and a fantasy mineral's slab is edged in its light. Not found: the same crystals smeared (drawn several
/// times a little apart, each faint) behind a pane of frosted glass with slanted streaks, so the shape of the state reads without the colour. The name and the rank are
/// the card's children (labels), laid out under the niche. Redrawn only when resized.
/// </summary>
public sealed partial class MineralCard : MarginContainer
{
    readonly LogSkin _skin;
    readonly MineralPaint _paint;
    readonly bool _found, _fantasy;
    readonly float _nicheH;

    public MineralCard(LogSkin skin, Mineral mineral, bool found, float nicheH)
    {
        _skin = skin; _found = found; _fantasy = mineral.Fantasy; _nicheH = nicheH;
        _paint = MineralPaint.Of(mineral.Id);
        MouseFilter = MouseFilterEnum.Pass; // a tooltip, no click
        AddThemeConstantOverride("margin_left", ShooterTheme.Space / 2);
        AddThemeConstantOverride("margin_right", ShooterTheme.Space / 2);
        AddThemeConstantOverride("margin_top", ShooterTheme.Space / 2);
        AddThemeConstantOverride("margin_bottom", ShooterTheme.Space / 2);
    }

    public override void _Notification(int what) { if (what == NotificationResized) QueueRedraw(); }

    public override void _Draw()
    {
        var s = _skin; float w = Size.X, h = Size.Y;
        // the slab; a found fantasy mineral's is edged in its light, inside the dark edge
        DrawRect(new Rect2(0, 0, w, h), s.Tile);
        DrawRect(new Rect2(1.5f, 1.5f, w - 3, h - 3), s.Edge, false, 3);
        if (_found && _fantasy && _paint.Glow is { } edge) DrawRect(new Rect2(4f, 4f, w - 8, h - 8), edge, false, 2);

        // the niche: an arch
        float pad = ShooterTheme.Space, left = pad, right = w - pad, top = pad, bottom = pad + _nicheH, r = (right - left) / 2f;
        var arch = new List<Vector2> { new(left, bottom), new(left, top + r) };
        for (var i = 1; i < 16; i++)
        {
            var a = Mathf.Pi + i * Mathf.Pi / 16f;
            arch.Add(new Vector2(left + r + Mathf.Cos(a) * r, top + r + Mathf.Sin(a) * r));
        }
        arch.Add(new(right, top + r)); arch.Add(new(right, bottom));
        var pts = arch.ToArray();
        DrawColoredPolygon(pts, s.Edge.Darkened(0.2f));
        LogDraw.Outline(this, pts, s.Seam, 2);

        // the crystals, on the niche's floor
        var scale = Math.Min((right - left - 6) / 90f, (_nicheH - r * 0.25f) / 70f);
        var origin = new Vector2(left + r - 45f * scale, bottom - 70f * scale - 1);
        if (_found) Cluster(origin, scale, 1f, true);
        else
        {
            for (var i = 0; i < 8; i++)
            {
                var a = i * Mathf.Tau / 8f;
                Cluster(origin + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 3.5f, scale, 0.16f, false);
            }
            Cluster(origin, scale, 0.3f, false);
            // the frosted pane: a pale wash and slanted streaks, inside the arch's upright part and its crown
            DrawColoredPolygon(pts, new Color(s.Cover, 0.22f));
            for (var x = left + 10; x < right - 22; x += 22)
            {
                var y0 = top + r - MathF.Sqrt(MathF.Max(0, r * r - MathF.Pow(x + 18 - left - r, 2))) + 3; // under the crown at the streak's top end
                DrawLine(new Vector2(x + 18, y0), new Vector2(x, bottom - 2), new Color(1, 1, 1, 0.14f), 4, true);
            }
        }

        // found: a tick on a small disc in the corner
        if (_found)
        {
            var at = new Vector2(w - 15, 15);
            DrawCircle(at, 11, s.Edge);
            DrawCircle(at, 9, s.OnTile);
            DrawPolyline(new[] { at + new Vector2(-5, 0.5f), at + new Vector2(-1.5f, 4), at + new Vector2(5, -3.5f) }, s.Edge, 3, true);
        }
    }

    /// <summary>The cluster in a 90 by 70 box at <paramref name="o"/>: the light behind, three shards of two facets each, the rock in front.</summary>
    void Cluster(Vector2 o, float k, float alpha, bool lines)
    {
        Vector2 P(float x, float y) => o + new Vector2(x, y) * k;
        Color A(Color c) => new(c, alpha);
        if (_paint.Glow is { } glow)
        {
            DrawCircle(P(45, 34), 40 * k, new Color(glow, 0.14f * alpha));
            DrawCircle(P(45, 34), 26 * k, new Color(glow, 0.18f * alpha));
        }
        void Facet(Color c, params Vector2[] pts)
        {
            DrawColoredPolygon(pts, A(c));
            if (lines) LogDraw.Outline(this, pts, _skin.Edge, 1.5f);
        }
        Facet(_paint.Dark, P(22, 54), P(14, 30), P(20, 20), P(24, 54));
        Facet(_paint.Mid, P(20, 20), P(30, 28), P(33, 54), P(24, 54));
        Facet(_paint.Light, P(58, 54), P(62, 30), P(72, 22), P(66, 54));
        Facet(_paint.Dark, P(72, 22), P(78, 34), P(71, 54), P(66, 54));
        Facet(_paint.Mid, P(37, 52), P(34, 18), P(45, 4), P(45, 52));
        Facet(_paint.Light, P(45, 4), P(56, 18), P(53, 52), P(45, 52));
        Facet(_paint.Rock, P(6, 68), P(13, 54), P(30, 47), P(60, 47), P(79, 55), P(86, 68));
    }
}
