using Godot;

namespace Drycastle.Shooter.HeadsUp.Vision;

/// <summary>
/// The demon's pounce on the HUD (docs/events/README.md, "Pounce"): one full-screen plain red rectangle under the HUD's widgets that the client fades in a pulse on each bite and out when the
/// victim is let go. No text, no controls, no theme tokens needed: a colour wash only (the client softens it under reduce motion; it is not the only cue,
/// the camera is on the floor under the demon). Written per frame as one colour value; nothing is built or allocated.
/// </summary>
internal sealed class PounceWash
{
    static readonly Color Red = new(0.62f, 0.02f, 0.02f);

    readonly ColorRect _rect;

    public PounceWash(Control root)
    {
        _rect = new ColorRect { Color = new Color(Red, 0f), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(_rect);
    }

    /// <summary>The red wash's opacity (0 off).</summary>
    public void Set(float alpha)
    {
        var on = alpha > 0.002f;
        if (_rect.Visible != on) _rect.Visible = on;
        if (on) _rect.Color = new Color(Red, Mathf.Clamp(alpha, 0f, 0.9f));
    }
}
