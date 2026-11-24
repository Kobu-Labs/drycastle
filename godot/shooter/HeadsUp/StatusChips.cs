using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Status;

namespace Drycastle.Shooter.HeadsUp;

/// <summary>
/// Own statuses in the HUD (Holy Mass reward): up to three chips above the breath meter, bottom left, "BLESSED", "CURSED" and "BURNING".
/// Each has a fixed slot that fades in and out (nothing shifts), a word (never colour alone) and tooltip-free text from the string table.
/// A blessing that saves the player also flashes the screen gold for a moment (none under reduce motion; the toast says it too).
/// Written on the status event, not per frame.
/// </summary>
internal sealed class StatusChips
{
    const float ChipWidth = 136, ChipHeight = 40, FlashSeconds = 0.6f;

    readonly PanelContainer[] _chip = new PanelContainer[3];
    readonly ColorRect _flash;
    Tween? _flashTween;

    public StatusChips(Control root)
    {
        var row = HudUi.Place(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 0, 1, ShooterTheme.Margin, -ShooterTheme.Margin - 16 - ShooterTheme.Space - 64 - ShooterTheme.Space, 3 * ChipWidth + 2 * ShooterTheme.Space, ChipHeight);
        row.AddThemeConstantOverride("separation", ShooterTheme.Space);
        root.AddChild(row);
        var words = new[] { ("status.blessed", "HudAccent"), ("status.cursed", "HudWarn"), ("status.burning", "HudWarn") };
        for (var i = 0; i < words.Length; i++)
        {
            var chip = _chip[i] = HudUi.P("HudPanel");
            chip.CustomMinimumSize = new Vector2(ChipWidth, ChipHeight);
            chip.Modulate = new Color(1, 1, 1, 0);
            chip.AddChild(HudUi.L(Strings.T(words[i].Item1), words[i].Item2, HorizontalAlignment.Center));
            row.AddChild(chip);
        }
        _flash = new ColorRect { Color = new Color(ShooterTheme.Blessing, 0f), MouseFilter = Control.MouseFilterEnum.Ignore };
        _flash.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(_flash);
    }

    /// <summary>Shows which of the local player's statuses are on: the blessing and the curse (the mask), and burning.</summary>
    public void Set(byte mask, bool burning)
    {
        HudUi.Fade(_chip[0], (mask & (1 << (int)StatusKind.Blessing)) != 0 ? 1 : 0);
        HudUi.Fade(_chip[1], (mask & (1 << (int)StatusKind.Curse)) != 0 ? 1 : 0);
        HudUi.Fade(_chip[2], burning ? 1 : 0);
    }

    /// <summary>The golden screen flash of a blessing that saved the player. Reduce motion: no flash.</summary>
    public void FlashBlessing()
    {
        if (GameSettings.Current.ReduceMotion) return;
        _flashTween?.Kill();
        _flash.Color = new Color(ShooterTheme.Blessing, 0.55f);
        _flashTween = _flash.CreateTween();
        _flashTween.TweenProperty(_flash, "color:a", 0f, FlashSeconds);
    }
}
