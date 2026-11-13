using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// The headgear chip (issue #261): what the local player wears in the headgear slot, bottom left beside the health bar. A small panel with the
/// word "Headgear" over the item's name, so it reads without colour or an icon. It is hidden while nothing is worn (it takes no space, the
/// health bar does not move) and written only when the name changes.
/// </summary>
public partial class Hud
{
    PanelContainer _headgear = null!;
    Label _headgearName = null!;

    void BuildHeadgear()
    {
        // right of the 320 px health bar, on the same bottom edge; the air meter sits above the bar, so the two never meet
        _headgear = Place(P("HudPanel"), 0, 1, ShooterTheme.Margin + 320 + ShooterTheme.Space, -ShooterTheme.Margin, 240, 0);
        _headgear.GrowVertical = Control.GrowDirection.Begin; // its height is its content's: it grows up from the bottom edge
        _headgear.Visible = false;
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", 0);
        _headgear.AddChild(v);
        v.AddChild(L(Strings.T("hud.headgear"), "HudSmall"));
        _headgearName = L("", "HudAccent");
        v.AddChild(_headgearName);
        _root.AddChild(_headgear);
    }

    /// <summary>Shows the item worn in the headgear slot, or hides the chip for null.</summary>
    public void SetHeadgear(string? name)
    {
        if (_headgear.Visible == (name != null) && (name == null || _headgearName.Text == name)) return;
        _headgear.Visible = name != null;
        if (name != null) Set(_headgearName, name);
    }
}
