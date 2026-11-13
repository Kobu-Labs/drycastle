using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Own statuses in the HUD (Holy Mass reward): up to three chips above the breath meter, bottom left, "BLESSED", "CURSED" and "BURNING".
/// Each has a fixed slot that fades in and out (nothing shifts), a word (never colour alone) and tooltip-free text from the string table.
/// A blessing that saves the player also flashes the screen gold for a moment (none under reduce motion; the toast says it too).
/// Written on the status event, not per frame.
/// </summary>
public partial class Hud
{
    const float StatusChipWidth = 136, StatusChipHeight = 40, StatusFlashSeconds = 0.6f;

    readonly PanelContainer[] _statusChip = new PanelContainer[3];
    ColorRect _statusFlash = null!;
    Tween? _statusFlashTween;

    void BuildStatus()
    {
        var row = Place(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 0, 1, ShooterTheme.Margin, -ShooterTheme.Margin - 16 - ShooterTheme.Space - 64 - ShooterTheme.Space, 3 * StatusChipWidth + 2 * ShooterTheme.Space, StatusChipHeight);
        row.AddThemeConstantOverride("separation", ShooterTheme.Space);
        _root.AddChild(row);
        var words = new[] { ("status.blessed", "HudAccent"), ("status.cursed", "HudWarn"), ("status.burning", "HudWarn") };
        for (var i = 0; i < words.Length; i++)
        {
            var chip = _statusChip[i] = P("HudPanel");
            chip.CustomMinimumSize = new Vector2(StatusChipWidth, StatusChipHeight);
            chip.Modulate = new Color(1, 1, 1, 0);
            chip.AddChild(L(Strings.T(words[i].Item1), words[i].Item2, HorizontalAlignment.Center));
            row.AddChild(chip);
        }
        _statusFlash = new ColorRect { Color = new Color(ShooterTheme.Blessing, 0f), MouseFilter = Control.MouseFilterEnum.Ignore };
        _statusFlash.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_statusFlash);
    }

    /// <summary>Shows which of the local player's statuses are on: the blessing and the curse (the mask), and burning.</summary>
    public void SetStatus(byte mask, bool burning)
    {
        Fade(_statusChip[0], (mask & (1 << (int)StatusKind.Blessing)) != 0 ? 1 : 0);
        Fade(_statusChip[1], (mask & (1 << (int)StatusKind.Curse)) != 0 ? 1 : 0);
        Fade(_statusChip[2], burning ? 1 : 0);
    }

    /// <summary>The golden screen flash of a blessing that saved the player. Reduce motion: no flash.</summary>
    public void FlashBlessing()
    {
        if (Settings.ReduceMotion) return;
        _statusFlashTween?.Kill();
        _statusFlash.Color = new Color(ShooterTheme.Blessing, 0.55f);
        _statusFlashTween = _statusFlash.CreateTween();
        _statusFlashTween.TweenProperty(_statusFlash, "color:a", 0f, StatusFlashSeconds);
    }
}
