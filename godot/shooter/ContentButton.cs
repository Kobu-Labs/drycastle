using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// A button that holds a layout instead of a caption: a Life List tile, a table row. Its content (any control tree, ignoring the mouse) fills the button inside the
/// style's content margins and gives it its minimum size (<see cref="Control.CustomMinimumSize"/>, kept up to date when the content's own minimum changes), so every
/// state of the Theme (hover, pressed, focus ring, disabled) draws around it. <see cref="MinSize"/> is the floor a caller wants on top (a row's fixed height).
/// </summary>
public sealed partial class ContentButton : Button
{
    Control? _content;
    Vector2 _floor;

    public ContentButton()
    {
        FocusMode = FocusModeEnum.All;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        ClipContents = true;
    }

    /// <summary>The smallest size the caller wants (a tile's width, a row's height); the content's own minimum and the style's margins can only make it larger.</summary>
    public Vector2 MinSize
    {
        get => _floor;
        set { _floor = value; FitMinimum(); }
    }

    public void SetContent(Control content)
    {
        if (_content != null) _content.MinimumSizeChanged -= FitMinimum;
        _content = content;
        AddChild(content);
        MouseIgnoreAll(content);
        content.MinimumSizeChanged += FitMinimum;
        Place();
        FitMinimum();
    }

    static void MouseIgnoreAll(Control c)
    {
        c.MouseFilter = MouseFilterEnum.Ignore;
        foreach (var child in c.GetChildren()) if (child is Control cc) MouseIgnoreAll(cc);
    }

    StyleBox Box => GetThemeStylebox("normal");

    void Place()
    {
        if (_content == null) return;
        var box = Box;
        _content.SetAnchorsPreset(LayoutPreset.FullRect);
        _content.OffsetLeft = box.GetContentMargin(Side.Left);
        _content.OffsetTop = box.GetContentMargin(Side.Top);
        _content.OffsetRight = -box.GetContentMargin(Side.Right);
        _content.OffsetBottom = -box.GetContentMargin(Side.Bottom);
    }

    /// <summary>The button's minimum: the content's combined minimum plus the style's margins, at least <see cref="MinSize"/>. A Button never asks a script for its minimum size, so it is set here.</summary>
    void FitMinimum()
    {
        var min = _floor;
        if (_content != null)
        {
            var box = Box;
            var need = _content.GetCombinedMinimumSize() + new Vector2(box.GetContentMargin(Side.Left) + box.GetContentMargin(Side.Right), box.GetContentMargin(Side.Top) + box.GetContentMargin(Side.Bottom));
            min = new Vector2(Math.Max(min.X, need.X), Math.Max(min.Y, need.Y));
        }
        if (CustomMinimumSize != min) CustomMinimumSize = min;
    }

    public override void _Notification(int what)
    {
        if (what is (int)NotificationEnterTree or (int)NotificationThemeChanged) { Place(); FitMinimum(); }
    }
}

/// <summary>A name that opens that player's log: the theme's link colour, underlined (so it reads as a link without the colour); under the pointer (<see cref="Hot"/>) it gets a border like every other clickable.</summary>
public sealed partial class PlayerLink : Label
{
    bool _hot;

    /// <summary>The pointer is over the name (the row tells it: the name itself takes no mouse events, the row does).</summary>
    public bool Hot
    {
        get => _hot;
        set { if (_hot == value) return; _hot = value; QueueRedraw(); }
    }

    /// <summary>The name's own area in this label's coordinates (the text plus a little air, as the hover box draws it): the only place a click opens the log.</summary>
    public Rect2 NameRect()
    {
        if (Text.Length == 0) return default;
        var w = Math.Min(Size.X, GetThemeFont("font").GetStringSize(Text, HorizontalAlignment.Left, -1, GetThemeFontSize("font_size")).X);
        return new Rect2(-ShooterTheme.Space / 2f, 0, w + ShooterTheme.Space, Size.Y);
    }

    public PlayerLink()
    {
        ThemeTypeVariation = "HudAccentSmall";
        MouseFilter = MouseFilterEnum.Ignore;
        ClipText = true;
    }

    public override void _Draw()
    {
        if (Text.Length == 0) return;
        var font = GetThemeFont("font");
        var size = GetThemeFontSize("font_size");
        var w = Math.Min(Size.X, font.GetStringSize(Text, HorizontalAlignment.Left, -1, size).X);
        var ink = GetThemeColor("font_color"); // the accent on the HUD, the ink of a skinned log
        if (_hot) DrawStyleBox(ShooterTheme.Box(new Color(ink, 0.18f), 0, ink, ShooterTheme.Border), NameRect()); // a border only: the row's own hover fill is behind it, the text stays readable either way
        var y = Size.Y / 2 + font.GetHeight(size) / 2 + 1;
        DrawLine(new Vector2(0, y), new Vector2(w, y), ink, ShooterTheme.Border);
    }
}

/// <summary>
/// The record badge of a table row: a winner's medal on a ribbon with a measuring tape (the longest shot) or a weight marked "kg" (the heaviest), in the skin's seal colours. It carries no words;
/// the tooltip does. It is a picture only, so it floats over a cell without taking part in its layout.
/// </summary>
public sealed partial class RecordBadge : Control
{
    RecordMark _mark;
    readonly LogSkin _skin;

    public RecordBadge(LogSkin skin)
    {
        _skin = skin;
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = new Vector2(ShooterTheme.LogBadge, ShooterTheme.LogBadge);
        TextureFilter = TextureFilterEnum.LinearWithMipmaps;
    }

    public void Set(RecordMark mark)
    {
        if (_mark == mark) return;
        _mark = mark;
        Visible = mark != RecordMark.None;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_mark == RecordMark.None) return;
        var r = new Rect2(Vector2.Zero, Size);
        DrawTextureRect(HuntArt.BadgeRibbon, r, false, _skin.Seal.Darkened(.35f));
        DrawTextureRect(HuntArt.BadgeDisc, r, false, _skin.Seal);
        DrawTextureRect(HuntArt.BadgeRing, r, false, _skin.OnSeal);
        DrawTextureRect(_mark == RecordMark.Weight ? HuntArt.BadgeWeight : HuntArt.BadgeTape, r, false, _skin.OnSeal);
    }
}
