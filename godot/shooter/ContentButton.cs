using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// A button that holds a layout instead of a caption: a Life List tile, a table row. Its content (any control tree, ignoring the mouse) fills the button inside the
/// style's content margins and gives it its minimum size, so every state of the Theme (hover, pressed, focus ring, disabled) draws around it.
/// </summary>
public sealed partial class ContentButton : Button
{
    Control? _content;

    public ContentButton()
    {
        FocusMode = FocusModeEnum.All;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        ClipContents = true;
    }

    public void SetContent(Control content)
    {
        _content = content;
        AddChild(content);
        MouseIgnoreAll(content);
        Place();
        UpdateMinimumSize();
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

    public override Vector2 _GetMinimumSize()
    {
        if (_content == null) return Vector2.Zero;
        var box = Box;
        return _content.GetCombinedMinimumSize() + new Vector2(box.GetContentMargin(Side.Left) + box.GetContentMargin(Side.Right), box.GetContentMargin(Side.Top) + box.GetContentMargin(Side.Bottom));
    }

    public override void _Notification(int what)
    {
        if (what == NotificationThemeChanged) { Place(); UpdateMinimumSize(); }
    }
}

/// <summary>A name that opens that player's log: the accent, underlined (so it reads as a link without the colour).</summary>
public sealed partial class PlayerLink : Label
{
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
        var y = Size.Y / 2 + font.GetHeight(size) / 2 + 1;
        DrawLine(new Vector2(0, y), new Vector2(w, y), ShooterTheme.Accent, ShooterTheme.Border);
    }
}
