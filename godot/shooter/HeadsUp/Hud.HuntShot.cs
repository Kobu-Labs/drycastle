using Godot;
using Drycastle.Shooter.Hunts;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.Shooter.HeadsUp;

/// <summary>
/// The hit diagram (GLOSSARY "Hit diagram"): after a sniper rifle kills a wild animal, a card on the right of the screen shows the species' side
/// view with the place the bullet entered marked by a ring and a cross (shape, not colour alone), the species' name above, and the distance and
/// the weapon below. The side view is always the same picture of the animal, standing, whatever it was doing; the mark is the hit moved onto
/// it (<see cref="AnimalShape.MapOnto"/>: a head hit on the head, a body hit on the same place of the body). An animal without a shape shows
/// the species' baked picture or a plain body. It stays <see cref="HuntShotSeconds"/>, fades out and ignores the mouse. Built once; a kill only sets its values.
/// </summary>
public partial class Hud
{
    const double HuntShotSeconds = 5, HuntShotFade = 0.25;
    PanelContainer _huntPanel = null!;
    Label _huntTitle = null!, _huntLine = null!;
    AnimalPicture _huntView = null!;
    double _huntUntil;

    void BuildHuntShot()
    {
        _huntPanel = Place(P("HudPanel"), 1, 0.5f, -ShooterTheme.Margin, 0, AnimalPicture.HitCardW + 2 * ShooterTheme.Space, AnimalPicture.HitCardH + 2 * ShooterTheme.FontSmall + 6 * ShooterTheme.Space);
        _huntPanel.Visible = false;
        _root.AddChild(_huntPanel);
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", ShooterTheme.Space / 2);
        _huntPanel.AddChild(v);
        _huntTitle = L("", "HudHead", HorizontalAlignment.Center);
        _huntLine = L("", "HudSmall", HorizontalAlignment.Center);
        _huntView = new AnimalPicture(new Vector2(AnimalPicture.HitCardW, AnimalPicture.HitCardH));
        v.AddChild(_huntTitle); v.AddChild(_huntView); v.AddChild(_huntLine);
    }

    /// <summary>
    /// Shows the hit diagram: the species' name and side view (<paramref name="art"/>: the hit shape and its picture, else the species' picture, else a plain
    /// body), the bullet's entry (<paramref name="along"/> 0 rear to 1 front, <paramref name="height"/> 0 ground to 1 top, of what is drawn) and the line
    /// under it. The animal always faces left.
    /// </summary>
    public void ShowHuntShot(string species, AnimalArt art, float along, float height, string line)
    {
        Set(_huntTitle, species);
        Set(_huntLine, line);
        _huntView.SetHit(art, true, along, height);
        _huntPanel.Visible = true;
        Fade(_huntPanel, 1);
        _huntUntil = Now + HuntShotSeconds;
    }

    void UpdateHuntShot(double now)
    {
        var left = _huntUntil - now;
        if (left <= 0) { _huntUntil = 0; _huntPanel.Visible = false; return; }
        Fade(_huntPanel, Settings.ReduceMotion ? 1 : (float)Math.Clamp(left / HuntShotFade, 0, 1));
    }

    void ClearHuntShot() { _huntUntil = 0; if (_huntPanel.Visible) _huntPanel.Visible = false; }
}
