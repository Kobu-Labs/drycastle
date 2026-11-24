using Godot;
using Drycastle.Shooter.Menu;

namespace Drycastle.Shooter.HeadsUp;

/// <summary>
/// Spawn protection: a border round the screen (the first thing in the panels root, behind every other element) and a chip at the bottom centre with the seconds left
/// (reserved space, faded in and out; <see cref="AddChip"/> puts it in the draw order of the bottom elements). Written when the tenth of a second changes.
/// </summary>
internal sealed class SpawnProtection
{
    readonly PanelContainer _outline;
    PanelContainer _chip = null!;
    Label _text = null!;
    double _until;
    int _shown = -1;

    public SpawnProtection(Control root)
    {
        _outline = HudUi.P("HudProtection");
        _outline.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(_outline);
        _outline.Modulate = new Color(1, 1, 1, 0);
    }

    public void AddChip(Control root)
    {
        _chip = HudUi.Place(HudUi.P("HudPanel"), 0.5f, 1, 0, -ShooterTheme.Margin, 320, 48);
        var h = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        h.AddThemeConstantOverride("separation", ShooterTheme.Space);
        _chip.AddChild(h);
        h.AddChild(new ShieldIcon { CustomMinimumSize = new Vector2(24, 24), MouseFilter = Control.MouseFilterEnum.Ignore });
        _text = HudUi.L("", "HudAccent"); h.AddChild(_text);
        root.AddChild(_chip);
        _chip.Modulate = new Color(1, 1, 1, 0);
    }

    /// <summary>Seconds remaining (0 hides the chip and the outline).</summary>
    public void Set(float seconds)
    {
        _until = seconds > 0 ? HudUi.Now + seconds : 0;
        _shown = -1;
        if (seconds <= 0) { HudUi.Fade(_chip, 0); HudUi.Fade(_outline, 0); }
        else Update();
    }

    public void Tick(double now)
    {
        if (_until <= 0) return;
        if (now >= _until) Set(0); else Update();
    }

    void Update()
    {
        var tenths = (int)Math.Ceiling(Math.Max(0, _until - HudUi.Now) * 10);
        if (tenths == _shown) return;
        _shown = tenths;
        HudUi.Set(_text, Strings.F("hud.protection", $"{tenths / 10.0:0.0}s"));
        HudUi.Fade(_chip, 1); HudUi.Fade(_outline, 1);
    }
}

/// <summary>Small shield icon for spawn protection, drawn once.</summary>
internal sealed partial class ShieldIcon : Control
{
    public override void _Draw()
    {
        var w = Size.X; var h = Size.Y;
        var pts = new[] { new Vector2(w * 0.5f, 0), new Vector2(w, h * 0.2f), new Vector2(w * 0.88f, h * 0.65f), new Vector2(w * 0.5f, h), new Vector2(w * 0.12f, h * 0.65f), new Vector2(0, h * 0.2f) };
        DrawColoredPolygon(pts, ShooterTheme.Accent);
        DrawPolyline(new[] { pts[0], pts[1], pts[2], pts[3], pts[4], pts[5], pts[0] }, ShooterTheme.Outline, 1.5f);
    }
}
