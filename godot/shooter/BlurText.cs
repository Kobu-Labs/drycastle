using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// A placeholder name drawn blurred: the text laid over itself at small offsets in a ring, each faint, so nothing reads as a word. It is the look of a Mystery
/// creature's name for a viewer who has not caught it (the server sends no name, only a hidden id; the placeholder is a fixed string). Sized and coloured by a Label
/// theme variation; redraws only when its text or variation changes.
/// </summary>
public sealed partial class BlurText : Control
{
    const int Taps = 8;
    /// <summary>How far the copies spread, in tenths of the font size, and how faint each is.</summary>
    const float SpreadOfFont = 0.14f, TapAlpha = 0.16f;

    string _text = "", _variation;

    public BlurText(string text, string variation = "Label")
    {
        _text = text; _variation = variation;
        MouseFilter = MouseFilterEnum.Ignore;
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
    }

    public void SetVariation(string variation)
    {
        if (_variation == variation) return;
        _variation = variation;
        UpdateMinimum();
        QueueRedraw();
    }

    public override void _Ready() => UpdateMinimum();

    Font TextFont => GetThemeFont("font", "Label");
    int FontPx => GetThemeFontSize("font_size", _variation) is > 0 and var s ? s : GetThemeFontSize("font_size", "Label");
    float Spread => FontPx * SpreadOfFont;

    void UpdateMinimum()
    {
        var size = TextFont.GetStringSize(_text, HorizontalAlignment.Left, -1, FontPx);
        CustomMinimumSize = new Vector2(size.X + 2 * Spread, TextFont.GetHeight(FontPx) + 2 * Spread);
    }

    public override void _Draw()
    {
        var font = TextFont; var px = FontPx; var spread = Spread;
        var colour = GetThemeColor("font_color", _variation);
        var at = new Vector2(spread, spread + font.GetAscent(px));
        DrawString(font, at, _text, HorizontalAlignment.Left, -1, px, colour with { A = TapAlpha * 2 });
        for (var i = 0; i < Taps; i++)
        {
            var a = i * Mathf.Tau / Taps;
            DrawString(font, at + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * spread, _text, HorizontalAlignment.Left, -1, px, colour with { A = TapAlpha });
        }
    }
}
