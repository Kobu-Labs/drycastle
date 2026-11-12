using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// The sandbox badge in the HUD corner (#156): "SANDBOX" in the warning colour with the held killstreak under it; the badge
/// says so when the player has switched damage on, so godmode is never assumed from the badge alone. It is text
/// (never colour alone) and ignores the mouse. The state is kept until the labels exist, so the client may set it early.
/// </summary>
public partial class Hud
{
    Label _sandboxBadge = null!, _sandboxHeld = null!;
    bool _sandboxOn, _sandboxDamage;
    string _sandboxHeldId = "";

    void BuildSandbox()
    {
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", ShooterTheme.Space / 2);
        _sandboxBadge = L(Strings.T("sandbox.badge"), "HudWarn", HorizontalAlignment.Right);
        _sandboxHeld = L("", "HudSmall", HorizontalAlignment.Right);
        box.AddChild(_sandboxBadge);
        box.AddChild(_sandboxHeld);
        _root.AddChild(Place(box, 1, 0, -ShooterTheme.Margin - 360, ShooterTheme.Margin, 360, 56));
        ApplySandbox();
    }

    /// <summary>Shows or hides the badge; <paramref name="held"/> is the held killstreak id ("" = none); <paramref name="takesDamage"/> is the panel's damage switch.</summary>
    public void SetSandbox(bool on, string held, bool takesDamage = false)
    {
        _sandboxOn = on;
        _sandboxDamage = takesDamage;
        _sandboxHeldId = held;
        if (_sandboxBadge != null) ApplySandbox();
    }

    void ApplySandbox()
    {
        _sandboxBadge.Visible = _sandboxOn;
        _sandboxBadge.Text = Strings.T(_sandboxDamage ? "sandbox.badge.damage" : "sandbox.badge");
        _sandboxHeld.Visible = _sandboxOn;
        _sandboxHeld.Text = _sandboxHeldId == ""
            ? Strings.T("sandbox.held.none")
            : Strings.F("sandbox.held", SandboxStrings.StreakName(_sandboxHeldId));
    }
}
