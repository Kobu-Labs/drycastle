using Godot;
using Drycastle.Shooter.Menu;

namespace Drycastle.Shooter.HeadsUp;

/// <summary>Weapon name and ammo only, bottom right: no panel, no border.</summary>
internal sealed class AmmoReadout
{
    const int Slots = 4;

    readonly VBoxContainer _box;
    readonly Label _name, _mag, _reserve;
    readonly string[] _weaponNames = { "", "", "", "" };
    int _slot;

    public AmmoReadout(Control root)
    {
        _box = HudUi.Place(new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 1, 1, -ShooterTheme.Margin, -ShooterTheme.Margin, 320, 88);
        _box.AddThemeConstantOverride("separation", 0);
        root.AddChild(_box);
        _name = HudUi.L("", "HudHead", HorizontalAlignment.Right); _box.AddChild(_name);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End, MouseFilter = Control.MouseFilterEnum.Ignore };
        _box.AddChild(row);
        _mag = HudUi.L("", "HudDisplay", HorizontalAlignment.Right, 96);
        _reserve = HudUi.L("", "HudHead", HorizontalAlignment.Left, 88); _reserve.ThemeTypeVariation = "HudDim";
        row.AddChild(_mag); row.AddChild(_reserve);
    }

    public bool Visible { set => _box.Visible = value; }

    /// <summary>Display names in slot order (the selected one is named beside the ammo).</summary>
    public void SetWeapons(IReadOnlyList<string> names)
    {
        for (var i = 0; i < Slots; i++) _weaponNames[i] = i < names.Count ? names[i] : "";
        HudUi.Set(_name, _weaponNames[_slot]);
    }

    /// <summary>Selected slot (1-based) and its mag/reserve (mag &lt; 0: no ammo, melee).</summary>
    public void Set(int slot, int mag, int reserve)
    {
        _slot = Math.Clamp(slot, 1, Slots) - 1;
        HudUi.Set(_name, _weaponNames[_slot]);
        HudUi.Set(_mag, mag < 0 ? Strings.T("hud.ammo.none") : mag.ToString());
        HudUi.Set(_reserve, mag < 0 ? "" : Strings.F("hud.ammo.reserve", reserve));
        _mag.ThemeTypeVariation = mag == 0 && reserve == 0 && mag >= 0 ? "HudDisplayDanger" : "HudDisplay";
    }
}
