using Godot;
using Drycastle.Shooter.Menu;

namespace Drycastle.Shooter.HeadsUp;

/// <summary>
/// The sandbox badge in the HUD corner (#156): "SANDBOX" in the warning colour with the held killstreak under it; the badge
/// says so when the player has switched damage on, so godmode is never assumed from the badge alone. It is text
/// (never colour alone) and ignores the mouse. The state is kept until <see cref="Build"/> made the labels, so the client may set it early.
/// </summary>
internal sealed class SandboxBadge
{
    Label? _badge, _held;
    bool _damage;
    string _heldId = "";

    /// <summary>The badge is on (the tactical map offers its teleport then).</summary>
    public bool On { get; private set; }

    public void Build(Control root)
    {
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", ShooterTheme.Space / 2);
        _badge = HudUi.L(Strings.T("sandbox.badge"), "HudWarn", HorizontalAlignment.Right);
        _held = HudUi.L("", "HudSmall", HorizontalAlignment.Right);
        box.AddChild(_badge);
        box.AddChild(_held);
        root.AddChild(HudUi.Place(box, 1, 0, -ShooterTheme.Margin - 360, ShooterTheme.Margin, 360, 56));
        Apply();
    }

    /// <summary>Shows or hides the badge; <paramref name="held"/> is the held killstreak id ("" = none); <paramref name="takesDamage"/> is the panel's damage switch.</summary>
    public void Set(bool on, string held, bool takesDamage)
    {
        On = on;
        _damage = takesDamage;
        _heldId = held;
        if (_badge != null) Apply();
    }

    void Apply()
    {
        _badge!.Visible = On;
        _badge.Text = Strings.T(_damage ? "sandbox.badge.damage" : "sandbox.badge");
        _held!.Visible = On;
        _held.Text = _heldId == ""
            ? Strings.T("sandbox.held.none")
            : Strings.F("sandbox.held", SandboxStrings.StreakName(_heldId));
    }
}
