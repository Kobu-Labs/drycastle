using Godot;
using Drycastle.Shooter.Menu;

namespace Drycastle.Shooter.HeadsUp.Prompts;

/// <summary>The context prompt under the crosshair (enter / exit a vehicle), and the hold ring just above it (care package open, #151; taking the rozhlas set) with its label.</summary>
internal sealed class ContextPrompt
{
    readonly Label _prompt;
    readonly RadialProgress _ring;

    public ContextPrompt(Control root)
    {
        _prompt = HudUi.Place(HudUi.L("", "HudHead", HorizontalAlignment.Center), 0.5f, 0.7f, -200, 0, 400, 32);
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

    /// <summary>A fill from 0 to 1, or null to hide the ring.</summary>
    public void SetRing(float? fraction)
    {
        _ring.Visible = fraction != null;
        if (fraction is { } f) _ring.Value = f;
    }
}
