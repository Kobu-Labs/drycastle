using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Hud;

namespace Drycastle.Shooter.HeadsUp.Vitals;

/// <summary>Health: one bar, no panel, no text. It turns the danger colour when low.</summary>
internal sealed class HealthBar
{
    readonly ProgressBar _bar;
    int _hp = -1;

    public HealthBar(Control root)
    {
        _bar = HudUi.Place(new ProgressBar { MaxValue = 100, Value = 100, ShowPercentage = false, ThemeTypeVariation = "HealthBar", MouseFilter = Control.MouseFilterEnum.Ignore }, 0, 1, ShooterTheme.Margin, -ShooterTheme.Margin, 320, 16);
        root.AddChild(_bar);
    }

    public void Set(int hp)
    {
        hp = Math.Clamp(hp, 0, 100);
        if (hp == _hp) return;
        _hp = hp;
        _bar.Value = hp;
        _bar.ThemeTypeVariation = hp <= HudLogic.LowHealth ? "HealthBarLow" : "HealthBar";
    }
}
