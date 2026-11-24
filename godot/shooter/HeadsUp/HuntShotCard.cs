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
/// the species' baked picture or a plain body. It stays <see cref="Seconds"/>, fades out and ignores the mouse. Built once; a kill only sets its values.
/// </summary>
internal sealed class HuntShotCard
{
    const double Seconds = 5, FadeSeconds = 0.25;

    readonly PanelContainer _panel;
    readonly Label _title, _line;
    readonly AnimalPicture _view;
    double _until;

    public HuntShotCard(Control root)
    {
        _panel = HudUi.Place(HudUi.P("HudPanel"), 1, 0.5f, -ShooterTheme.Margin, 0, AnimalPicture.HitCardW + 2 * ShooterTheme.Space, AnimalPicture.HitCardH + 2 * ShooterTheme.FontSmall + 6 * ShooterTheme.Space);
        _panel.Visible = false;
        root.AddChild(_panel);
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", ShooterTheme.Space / 2);
        _panel.AddChild(v);
        _title = HudUi.L("", "HudHead", HorizontalAlignment.Center);
        _line = HudUi.L("", "HudSmall", HorizontalAlignment.Center);
        _view = new AnimalPicture(new Vector2(AnimalPicture.HitCardW, AnimalPicture.HitCardH));
        v.AddChild(_title); v.AddChild(_view); v.AddChild(_line);
    }

    /// <summary>
    /// Shows the card: the species' name and side view (<paramref name="art"/>: the hit shape and its picture, else the species' picture, else a plain
    /// body), the bullet's entry (<paramref name="along"/> 0 rear to 1 front, <paramref name="height"/> 0 ground to 1 top, of what is drawn) and the line
    /// under it. The animal always faces left.
    /// </summary>
    public void Show(string species, AnimalArt art, float along, float height, string line)
    {
        HudUi.Set(_title, species);
        HudUi.Set(_line, line);
        _view.SetHit(art, true, along, height);
        _panel.Visible = true;
        HudUi.Fade(_panel, 1);
        _until = HudUi.Now + Seconds;
    }

    public void Tick(double now)
    {
        if (_until <= 0) return;
        var left = _until - now;
        if (left <= 0) { _until = 0; _panel.Visible = false; return; }
        HudUi.Fade(_panel, GameSettings.Current.ReduceMotion ? 1 : (float)Math.Clamp(left / FadeSeconds, 0, 1));
    }

    public void Clear() { _until = 0; if (_panel.Visible) _panel.Visible = false; }
}
