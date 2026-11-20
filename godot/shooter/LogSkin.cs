using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>What a collection log's board is made of.</summary>
public enum LogSurface { Planks, Masonry }

/// <summary>What sits on the top edge of a collection log's board.</summary>
public enum LogCrest { Antlers, Crystals }

/// <summary>
/// The look of one collection log (docs/standards/ui-ux.md "Collection logs"): the tokens of its board, metal, paper and inks, and the Theme made of them. A log's
/// screen is built on a <see cref="LogFrame"/> that carries the skin's Theme, so every control under it (buttons, tabs, labels, rows, the scroll bar, the
/// drop-down lists) takes the skin without a colour in the screen's own code. The Hunter's Log is the lodge (walnut planks, brass, parchment, antlers); the Mineral
/// Log is the stone (masonry, iron, slate, a crystal cluster). Text is only ever put on a plate, a paper or the plain board colour, never where the grain is the
/// background: ink on paper and on metal is over 7:1, light text on the board over 9:1.
/// </summary>
public sealed class LogSkin
{
    public string Id { get; init; } = "";
    public LogSurface Surface { get; init; }
    public LogCrest Crest { get; init; }
    public int Seed { get; init; }

    // the board: three tones of its surface, the joints, the dark edge every shape is outlined with, and the lighter header band with its burnt-in title
    public Color Board { get; init; }
    public Color BoardAlt { get; init; }
    public Color BoardAlt2 { get; init; }
    public Color Seam { get; init; }
    public Color Edge { get; init; }
    public Color Header { get; init; }
    public Color HeaderInk { get; init; }
    /// <summary>Text straight on the board (section titles, captions of the filters, the state boxes).</summary>
    public Color OnBoard { get; init; }
    public Color OnBoardDim { get; init; }

    // metal: plates, brackets, tacks, the close button, the scroll bar's grabber
    public Color Metal { get; init; }
    public Color MetalEdge { get; init; }
    /// <summary>A plate that carries ink (a stat, a name): the metal itself where ink reads on it (brass), else a slab of the paper's stone (slate).</summary>
    public Color Plate { get; init; }

    // paper: cards and the table; ink on paper and on metal
    public Color Paper { get; init; }
    public Color PaperShade { get; init; }
    public Color PaperMine { get; init; }
    public Color PaperLine { get; init; }
    public Color Ink { get; init; }
    public Color InkDim { get; init; }

    // controls on the board: a button and a tab sign (normal, hover, pressed/current)
    public Color Control { get; init; }
    public Color ControlHover { get; init; }
    public Color ControlActive { get; init; }

    // a collection tile (a plaque, a niche): collected, not yet, covered; the text and the picture on it
    public Color Tile { get; init; }
    public Color TileTodo { get; init; }
    public Color Cover { get; init; }
    public Color OnTile { get; init; }
    public Color OnTileDim { get; init; }
    public Color Chalk { get; init; }

    /// <summary>The seal of a record (a filled pill with words in it) and its text.</summary>
    public Color Seal { get; init; }
    public Color OnSeal { get; init; }
    /// <summary>The focus ring: light on the dark board, and 3 px wide so it shows on paper too.</summary>
    public Color Focus { get; init; }
    /// <summary>The crest's two tones (bone and its shadow, or a crystal's light and dark facet).</summary>
    public Color CrestLight { get; init; }
    public Color CrestDark { get; init; }

    // ---- shared by every skin ----

    /// <summary>Sizes of the frame, in reference pixels: the header band, the side margin of the content, the outline, a plate's screw, a tack.</summary>
    public const int HeaderH = 76, Side = 4 * ShooterTheme.Space, Frame = 6, Bracket = 60, Screw = 3, ScrewInset = 9, Tack = 8, RopeH = 16, TabDrop = 4, CrestH = 78;

    static readonly Color Bronze = new(0xc0783cff), Silver = new(0xd5dae0ff), Gold = new(0xf2c230ff);

    /// <summary>The metal of a rarity tier's rim and medal (the tile also says the tier in words); null for common: no rim.</summary>
    public static Color? TierColor(HuntTier tier) => tier switch { HuntTier.Gold => Gold, HuntTier.Silver => Silver, HuntTier.Bronze => Bronze, _ => null };

    /// <summary>The Hunter's Log: a hunting lodge's wall board.</summary>
    public static readonly LogSkin Lodge = new()
    {
        Id = "lodge", Surface = LogSurface.Planks, Crest = LogCrest.Antlers, Seed = 7,
        Board = new(0x432816ff), BoardAlt = new(0x4b2e1aff), BoardAlt2 = new(0x503320ff), Seam = new(0x24150bff), Edge = new(0x1e120aff),
        Header = new(0xa87644ff), HeaderInk = new(0x1e120aff), OnBoard = new(0xf0e2c0ff), OnBoardDim = new(0xd6c39cff),
        Metal = new(0xc9a24bff), MetalEdge = new(0x6b4e17ff), Plate = new(0xc9a24bff),
        Paper = new(0xe9dab4ff), PaperShade = new(0xdccb9fff), PaperMine = new(0xdcc68fff), PaperLine = new(0xb7a377ff), Ink = new(0x1e120aff), InkDim = new(0x5a4630ff),
        Control = new(0xb98a55ff), ControlHover = new(0xcda06aff), ControlActive = new(0xe8cb9aff),
        Tile = new(0x6b4526ff), TileTodo = new(0x4a2f1cff), Cover = new(0x6f6a58ff), OnTile = new(0xf3e6c8ff), OnTileDim = new(0xd6c39cff), Chalk = new(0xe8e2d0ff),
        Seal = new(0x9e2b22ff), OnSeal = new(0xfff3e0ff), Focus = new(0xffe08aff), CrestLight = new(0xe6d6aeff), CrestDark = new(0x1e120aff),
    };

    /// <summary>The Mineral Log: a slab of cave masonry.</summary>
    public static readonly LogSkin Stone = new()
    {
        Id = "stone", Surface = LogSurface.Masonry, Crest = LogCrest.Crystals, Seed = 11,
        Board = new(0x3a3e46ff), BoardAlt = new(0x41454eff), BoardAlt2 = new(0x353940ff), Seam = new(0x14161aff), Edge = new(0x14161aff),
        Header = new(0x8d939cff), HeaderInk = new(0x14161aff), OnBoard = new(0xf0f2f5ff), OnBoardDim = new(0xc9ced6ff),
        Metal = new(0x7a808aff), MetalEdge = new(0x14161aff), Plate = new(0x5b616bff),
        Paper = new(0x5b616bff), PaperShade = new(0x4a4f58ff), PaperMine = new(0x666d78ff), PaperLine = new(0x14161aff), Ink = new(0xf0f2f5ff), InkDim = new(0xd5dae0ff),
        Control = new(0x8d939cff), ControlHover = new(0xa7adb6ff), ControlActive = new(0xc9ced6ff),
        Tile = new(0x2a2d33ff), TileTodo = new(0x2a2d33ff), Cover = new(0xc8d7e6ff), OnTile = new(0xe8ecf0ff), OnTileDim = new(0xc9ced6ff), Chalk = new(0xe8ecf0ff),
        Seal = new(0x9e2b22ff), OnSeal = new(0xfff3e0ff), Focus = new(0xbfe9ffff), CrestLight = new(0xb58cffff), CrestDark = new(0x7c4fe0ff),
    };

    Theme? _theme;

    /// <summary>The skin's Theme, made once. It only holds what differs from the game's Theme; everything else (font, spacing) is looked up further up the tree.</summary>
    public Theme Theme => _theme ??= Build();

    Theme Build()
    {
        var t = new Theme();
        const int S = ShooterTheme.Space;

        // text on the board, and ink on paper and metal; no outline (the log is opaque, nothing shows through behind the letters)
        Text(t, "Label", 0, OnBoard);
        Text(t, "HudSmall", ShooterTheme.FontSmall, OnBoardDim);
        Text(t, "HudDim", ShooterTheme.FontBody, OnBoardDim);
        Text(t, "HudHead", ShooterTheme.FontHead, OnBoard);
        Text(t, "HudBig", ShooterTheme.FontBig, OnBoard);
        Text(t, "HudDisplay", ShooterTheme.FontDisplay, OnBoard);
        Text(t, "HudTextSmall", ShooterTheme.FontSmall, OnBoard);
        Text(t, "HudWarn", ShooterTheme.FontBody, ShooterTheme.Warn);
        Text(t, "HudAccent", ShooterTheme.FontBody, Ink);
        Text(t, "HudAccentSmall", ShooterTheme.FontSmall, Ink); // a player's name in a row: ink, underlined (PlayerLink)
        Text(t, "LogCaption", ShooterTheme.FontSmall, InkDim);
        Text(t, "LogTitle", ShooterTheme.FontBig, HeaderInk);
        Text(t, "LogSection", ShooterTheme.FontBody, OnBoard);
        Text(t, "InkSmall", ShooterTheme.FontSmall, Ink);
        Text(t, "InkDimSmall", ShooterTheme.FontSmall, InkDim);
        Text(t, "InkBody", ShooterTheme.FontBody, Ink);
        Text(t, "InkHead", ShooterTheme.FontHead, Ink);
        Text(t, "InkBig", ShooterTheme.FontBig, Ink);
        Text(t, "InkDisplay", ShooterTheme.FontDisplay, Ink);
        Text(t, "InkDimDisplay", ShooterTheme.FontDisplay, InkDim);
        Text(t, "TileName", ShooterTheme.FontSmall, Ink);
        Text(t, "TileText", ShooterTheme.FontSmall, OnTile);
        Text(t, "TileDim", ShooterTheme.FontSmall, OnTileDim);
        Text(t, "TileMark", ShooterTheme.FontDisplay, OnTile);
        Text(t, "TileMarkDim", ShooterTheme.FontDisplay, OnTileDim);

        // panels: plates of metal, papers, the darker box a picture sits in
        Panel(t, "LogStat", Box(Plate, S * 2, MetalEdge, 2).With(b => { b.ContentMarginTop = b.ContentMarginBottom = S; }));
        Panel(t, "LogName", Box(Plate, 0, MetalEdge, 1, 2).With(b => { b.ContentMarginLeft = b.ContentMarginRight = S + S / 2; }));
        Panel(t, "LogNameTodo", Box(Chalk.Darkened(0.2f), 0, Edge, 1, 2).With(b => { b.ContentMarginLeft = b.ContentMarginRight = S + S / 2; }));
        var paper = Box(Paper, S * 2, Edge, 2, 3).With(b => { b.ShadowColor = new Color(0, 0, 0, 0.35f); b.ShadowSize = 1; b.ShadowOffset = new Vector2(0, 4); });
        Panel(t, "LogCard", paper);
        Panel(t, "LogHit", paper);
        Panel(t, "LogLedger", Box(Paper, 0, Edge, 2, 3).With(b => { b.ShadowColor = new Color(0, 0, 0, 0.35f); b.ShadowSize = 1; b.ShadowOffset = new Vector2(0, 4); b.SetContentMarginAll(2); }));
        Panel(t, "LogCover", Box(Cover, S * 2, Edge, 2, 3));
        Panel(t, "LogFigure", Box(PaperShade, S / 2, PaperLine, 1, 0));
        Panel(t, "LogNotice", Box(Edge, S, ShooterTheme.Warn, 2));

        // buttons on the board (and the hunter button on the hit card): every state its own fill, the pressed and current one a thicker edge as well
        const int pad = 12;
        foreach (var type in new[] { "Button", "OptionButton" })
        {
            t.SetStylebox("normal", type, Box(Control, pad, Edge, 2));
            t.SetStylebox("hover", type, Box(ControlHover, pad, Edge, 2));
            t.SetStylebox("pressed", type, Box(ControlActive, pad, Edge, ShooterTheme.BorderStrong));
            t.SetStylebox("hover_pressed", type, Box(ControlActive, pad, Edge, ShooterTheme.BorderStrong));
            t.SetStylebox("disabled", type, Box(new Color(Control, 0.45f), pad, new Color(Edge, 0.6f), 2));
            t.SetStylebox("focus", type, Ring(pad));
            ButtonInk(t, type, HeaderInk);
            t.SetFontSize("font_size", type, ShooterTheme.FontBody);
        }
        t.SetConstant("modulate_arrow", "OptionButton", 1);
        Button(t, "MenuPrimary", Box(ControlActive, pad, Edge, 2), Box(ControlHover, pad, Edge, ShooterTheme.BorderStrong), Box(Control, pad, Edge, ShooterTheme.BorderStrong), pad, HeaderInk);

        // a tab: a sign hung from the header (LogSign draws the ropes and the tack of the current one); the left margin keeps room for the tack in every state, so nothing moves
        StyleBoxFlat Sign(Color bg, int border) => Box(bg, pad, Edge, border).With(b => { b.ContentMarginLeft = pad + S * 2; b.ContentMarginRight = pad + S; });
        Button(t, "LogTab", Sign(Control, 2), Sign(ControlHover, 2), Sign(ControlActive, ShooterTheme.BorderStrong), pad, HeaderInk);

        // the close button: a round metal plate holding the drawn cross
        Button(t, "LogClose", Box(Metal, S / 2, MetalEdge, 2, 64), Box(Metal.Lightened(0.15f), S / 2, MetalEdge, 2, 64), Box(Metal.Lightened(0.3f), S / 2, MetalEdge, ShooterTheme.BorderStrong, 64), S / 2, HeaderInk);
        t.SetStylebox("focus", "LogClose", Ring(S / 2, 64));

        // a collection tile: the plaque is drawn by the tile itself (PlaqueArt reads the button's state), the styles only keep the margins
        var tile = Margins(S * 2, S + S / 2, S * 2, S * 2);
        Button(t, "LogTile", tile, tile, tile, 0, OnTile);
        Button(t, "LogTileTodo", tile, tile, tile, 0, OnTileDim);
        foreach (var n in new[] { "LogTile", "LogTileTodo" })
        {
            t.SetStylebox("focus", n, new StyleBoxEmpty());
            t.SetStylebox("disabled", n, tile);
        }

        // the table on paper: ruled rows, the own row tinted with a stripe on its left (and "(you)" in words), a frame on hover, a thick frame on the selected one
        StyleBoxFlat Row(Color bg, Color? border, int width) => Box(bg, S / 2, border, width, 0).With(b => { b.ContentMarginLeft = S * 2; b.ContentMarginRight = S + S / 2; });
        var ruled = Row(Colors.Transparent, null, 0).With(b => { b.BorderColor = PaperLine; b.BorderWidthBottom = 1; });
        var mine = Row(PaperMine, null, 0).With(b => { b.BorderColor = Tile; b.BorderWidthLeft = 5; });
        var hover = Row(PaperShade, Ink, 1);
        var selected = Row(PaperShade, Ink, ShooterTheme.BorderStrong);
        Button(t, "LogRow", ruled, hover, selected, S / 2, Ink);
        Button(t, "LogRowMine", mine, hover, selected, S / 2, Ink);
        foreach (var n in new[] { "LogRow", "LogRowMine" }) t.SetStylebox("disabled", n, ruled);

        var head = Row(Colors.Transparent, null, 0).With(b => { b.BorderColor = Ink; b.BorderWidthBottom = 3; });
        foreach (var n in new[] { "LogHead", "LogHeadSorted" })
        {
            Button(t, n, head, Row(PaperShade, Ink, 1), head, S / 2, n == "LogHead" ? InkDim : Ink);
            t.SetFontSize("font_size", n, ShooterTheme.FontSmall);
            foreach (var st in new[] { "icon_normal_color", "icon_hover_color", "icon_pressed_color", "icon_focus_color", "icon_hover_pressed_color" }) t.SetColor(st, n, Ink);
            t.SetConstant("icon_max_width", n, S * 2);
            t.SetConstant("h_separation", n, S / 2);
        }

        // the drop-down lists of the filters, the scroll bar
        t.SetStylebox("panel", "PopupMenu", Box(Paper, S, Edge, 2));
        t.SetStylebox("hover", "PopupMenu", Box(PaperShade, S / 2, Ink, 1));
        foreach (var c in new[] { "font_color", "font_hover_color" }) t.SetColor(c, "PopupMenu", Ink);
        t.SetColor("font_disabled_color", "PopupMenu", InkDim);
        t.SetFontSize("font_size", "PopupMenu", ShooterTheme.FontBody);
        t.SetStylebox("scroll", "VScrollBar", Box(Edge, 0, null, 0, 4).With(b => b.ContentMarginLeft = b.ContentMarginRight = 4));
        t.SetStylebox("grabber", "VScrollBar", Box(Metal, 0, MetalEdge, 1, 4));
        t.SetStylebox("grabber_highlight", "VScrollBar", Box(Metal.Lightened(0.2f), 0, MetalEdge, 1, 4));
        t.SetStylebox("grabber_pressed", "VScrollBar", Box(Metal.Lightened(0.35f), 0, MetalEdge, 1, 4));
        return t;
    }

    static StyleBoxFlat Box(Color bg, int pad, Color? border = null, int width = ShooterTheme.Border, int radius = ShooterTheme.Radius) => ShooterTheme.Box(bg, pad, border, width, radius);

    StyleBoxFlat Ring(int pad, int radius = ShooterTheme.Radius) =>
        new StyleBoxFlat { DrawCenter = false, BorderColor = Focus }.With(b => { b.SetBorderWidthAll(ShooterTheme.BorderStrong); b.SetCornerRadiusAll(radius); b.SetContentMarginAll(pad); b.SetExpandMarginAll(2); });

    static StyleBoxEmpty Margins(int left, int top, int right, int bottom) => new() { ContentMarginLeft = left, ContentMarginTop = top, ContentMarginRight = right, ContentMarginBottom = bottom };

    static void Text(Theme t, string name, int size, Color color)
    {
        if (name != "Label") t.SetTypeVariation(name, "Label");
        if (size > 0) t.SetFontSize("font_size", name, size);
        t.SetColor("font_color", name, color);
        t.SetConstant("outline_size", name, 0);
    }

    static void Panel(Theme t, string name, StyleBox box)
    {
        t.SetTypeVariation(name, "PanelContainer");
        t.SetStylebox("panel", name, box);
    }

    void Button(Theme t, string name, StyleBox normal, StyleBox hover, StyleBox pressed, int ringPad, Color ink)
    {
        t.SetTypeVariation(name, "Button");
        t.SetStylebox("normal", name, normal);
        t.SetStylebox("hover", name, hover);
        t.SetStylebox("pressed", name, pressed);
        t.SetStylebox("hover_pressed", name, pressed);
        t.SetStylebox("disabled", name, normal);
        t.SetStylebox("focus", name, Ring(ringPad));
        ButtonInk(t, name, ink);
    }

    void ButtonInk(Theme t, string name, Color ink)
    {
        foreach (var c in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color", "font_focus_color" }) t.SetColor(c, name, ink);
        t.SetColor("font_disabled_color", name, new Color(ink, 0.6f));
    }
}

static class StyleBoxExtensions
{
    public static T With<T>(this T box, Action<T> f) where T : StyleBox { f(box); return box; }
}
