using Godot;
using Drycastle.Shooter.Menu;

namespace Drycastle.Shooter.HeadsUp.Prompts;

/// <summary>The context prompt under the crosshair (enter / exit a vehicle), and the hold ring just above it (care package open, #151; taking the rozhlas set; the Harvest's fill) with the label of its own job.</summary>
internal sealed class ContextPrompt
{
    readonly Label _prompt;
    readonly RadialProgress _ring;
    /// <summary>The key of the label the ring shows now: the text is looked up only when the job changes.</summary>
    string _ringKey = "hud.crate.opening";

    public ContextPrompt(Control root)
    {
        _prompt = HudUi.Place(HudUi.L("", "HudHead", HorizontalAlignment.Center), 0.5f, 0.7f, -400, 0, 800, 32); // wide: two key hints share a line at an open boot
        _prompt.Visible = false;
        root.AddChild(_prompt);
        _ring = new RadialProgress { Text = Strings.T("hud.crate.opening"), Visible = false };
        _ring.AnchorLeft = _ring.AnchorRight = 0.5f;
        _ring.AnchorTop = _ring.AnchorBottom = 0.7f;
        _ring.OffsetLeft = -RadialProgress.Width / 2;
        _ring.OffsetRight = RadialProgress.Width / 2;
        _ring.OffsetBottom = -ShooterTheme.Space * 4;
        _ring.OffsetTop = _ring.OffsetBottom - RadialProgress.Height;
        root.AddChild(_ring);
    }

    /// <summary>E.g. "E  Enter car"; null hides it.</summary>
    public void SetPrompt(string? text)
    {
        _prompt.Visible = text != null;
        if (text != null) HudUi.Set(_prompt, text);
    }

    /// <summary>A fill from 0 to 1, or null to hide the ring; <paramref name="labelKey"/> is the string key of what the ring is for ("Opening...", "Filling..."; null keeps the last).</summary>
    public void SetRing(float? fraction, string? labelKey = null)
    {
        _ring.Visible = fraction != null;
        if (labelKey != null && labelKey != _ringKey) { _ringKey = labelKey; _ring.Text = Strings.T(labelKey); }
        if (fraction is { } f) _ring.Value = f;
    }
}
