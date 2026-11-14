using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// The demon's pounce on the HUD (docs/events/README.md, "Pounce"): one full-screen plain red rectangle under the HUD's widgets that the client fades in a pulse on each bite and out when the
/// victim is let go, and the crosshair hidden for the pin. No text, no controls, no theme tokens needed: a colour wash only (the client softens it under reduce motion; it is not the only cue,
/// the camera is on the floor under the demon). Written per frame as one colour value; nothing is built or allocated.
/// </summary>
public partial class Hud
{
    static readonly Color PounceRed = new(0.62f, 0.02f, 0.02f);
    ColorRect _pounceWash = null!;
    bool _pounceHide;

    void BuildPounce()
    {
        _pounceWash = new ColorRect { Color = new Color(PounceRed, 0f), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _pounceWash.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_pounceWash);
    }

    /// <summary>The red wash's opacity (0 off).</summary>
    public void SetPounceWash(float alpha)
    {
        var on = alpha > 0.002f;
        if (_pounceWash.Visible != on) _pounceWash.Visible = on;
        if (on) _pounceWash.Color = new Color(PounceRed, Mathf.Clamp(alpha, 0f, 0.9f));
    }

    /// <summary>The crosshair is hidden while the player lies pinned under the demon.</summary>
    public void SetPounceHidesCrosshair(bool hide)
    {
        if (_pounceHide == hide) return;
        _pounceHide = hide;
        _crossRoot.Visible = !hide && Settings.ShowCrosshair;
    }
}
