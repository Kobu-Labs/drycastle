using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Hud;

namespace Drycastle.Shooter.HeadsUp;

/// <summary>What every HUD widget shares: the clock, the layout helpers and the text helpers. Sizes are reference pixels at 1080p.</summary>
internal static class HudUi
{
    /// <summary>Screenshot mode freezes the clock so transient elements (hit marker, indicators, feed, countdowns) stay as set.</summary>
    public static double? FrozenNow { get; set; }

    public static double Now => FrozenNow ?? Time.GetTicksMsec() / 1000.0;

    /// <summary>Anchors the control at (ax, ay) of the screen and offsets it so a w x h box is aligned to that point (0 = left/top, 1 = right/bottom).</summary>
    public static T Place<T>(T c, float ax, float ay, float ox, float oy, float w, float h) where T : Control
    {
        c.AnchorLeft = c.AnchorRight = ax; c.AnchorTop = c.AnchorBottom = ay;
        c.OffsetLeft = ox - w * ax; c.OffsetRight = ox + w * (1 - ax);
        c.OffsetTop = oy - h * ay; c.OffsetBottom = oy + h * (1 - ay);
        return c;
    }

    public static Label L(string text, string? variation = null, HorizontalAlignment align = HorizontalAlignment.Left, float minW = 0)
    {
        var l = new Label { Text = text, HorizontalAlignment = align, MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(minW, 0) };
        if (variation != null) l.ThemeTypeVariation = variation;
        return l;
    }

    public static PanelContainer P(string variation) => new() { ThemeTypeVariation = variation, MouseFilter = Control.MouseFilterEnum.Ignore };

    public static void Set(Label l, string text) { if (l.Text != text) l.Text = text; }

    public static void Fade(CanvasItem c, float a) { if (!Mathf.IsEqualApprox(c.Modulate.A, a)) c.Modulate = new Color(1, 1, 1, a); }

    /// <summary>The label's text colour for a team relation; null restores the theme colour.</summary>
    public static void Tint(Label l, Color? c) { if (c is { } col) l.AddThemeColorOverride("font_color", col); else l.RemoveThemeColorOverride("font_color"); }

    /// <summary>Viewport height / 1080 (never under 0.8) times the UI scale setting.</summary>
    public static float Scale(Node n) => HudLogic.ScreenScale(n.GetViewport().GetVisibleRect().Size.Y, GameSettings.Current.UiScale);

    /// <summary>Root size is the viewport divided by the scale, so anchored children fill the real screen at any size and aspect.</summary>
    public static void Fit(Control c)
    {
        var s = Scale(c);
        c.Scale = new Vector2(s, s);
        c.Size = c.GetViewport().GetVisibleRect().Size / s;
    }
}
