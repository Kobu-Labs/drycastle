using Godot;
using Drycastle.Shooter.Menu;

namespace Drycastle.Shooter.HeadsUp.Vision;

/// <summary>
/// The flash of the "Job done" cinematic (docs/cartel.md "Job done"): one full-screen rectangle in the theme's near-white under the HUD's widgets, its opacity set per frame by the client (<c>job_done_cinematic.flash_peak</c>,
/// at most 0.6: soft, short, never more than 3 a second, none with reduce motion: the timeline has no flash then). Plain colour, no text, no controls; hidden while it is clear. Nothing is built or allocated per frame.
/// </summary>
internal sealed class FlashVeil
{
    readonly ColorRect _rect;

    public FlashVeil(Control root)
    {
        _rect = new ColorRect { Color = new Color(ShooterTheme.Text, 0f), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(_rect);
    }

    /// <summary>The flash's opacity (0 off).</summary>
    public void Set(float alpha)
    {
        var on = alpha > 0.002f;
        if (_rect.Visible != on) _rect.Visible = on;
        if (on) _rect.Color = new Color(ShooterTheme.Text, Mathf.Clamp(alpha, 0f, 0.6f));
    }
}
