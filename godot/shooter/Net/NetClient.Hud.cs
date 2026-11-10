using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>Feeds the in-match <see cref="Hud"/> from client state: events push changes, the per-frame hook only forwards health.</summary>
public partial class NetClient
{
    Hud? _hud;
    readonly SpreadEstimator _spread = new();
    int _hudRestarts;

    WeaponDef? CurrentWeapon => Ammo is { } a && Weapons != null && a.Slot >= 1 && a.Slot <= Weapons.Weapons.Count ? Weapons.Weapons[a.Slot - 1] : null;

    void HudWelcome()
    {
        if (!ShotFx.Enabled || _hud != null) return; // headless runs have no HUD
        _hud = new Hud { MyId = _myId, Spread = () => CurrentWeapon is { } w ? _spread.Degrees(w, Now) : 0f };
        AddChild(_hud);
        if (Weapons != null) _hud.SetWeapons(Weapons.Weapons.Select(w => WeaponName(w.Id)).ToList());
    }

    static string WeaponName(string id)
    {
        var k = "weapon." + id;
        var t = Strings.T(k);
        return t == k ? id : t;
    }

    void HudAmmo()
    {
        if (_hud == null || Ammo is not { } a || Weapons == null || a.Slot < 1 || a.Slot > Weapons.Weapons.Count) return;
        var w = Weapons.Weapons[a.Slot - 1];
        var all = new List<(int, int, bool)>();
        for (var i = 0; i < Weapons.Weapons.Count && i < a.Slots.Count; i++)
            all.Add((a.Slots[i].Mag, a.Slots[i].Reserve, Weapons.Weapons[i].MagazineSize == 0));
        var (mag, reserve) = a.Slots[a.Slot - 1];
        _hud.SetAmmo(a.Slot, w.MagazineSize == 0 ? -1 : mag, reserve, all, a.ReloadRemaining, w.ReloadTime);
    }

    void HudHit(HitMsg h)
    {
        if (_hud == null) return;
        if (h.Attacker == _myId && h.Victim != _myId) _hud.Hit(h.Headshot, h.Kill);
        if (h.Victim == _myId && h.Attacker != _myId && _me != null && _remotes.TryGetValue(h.Attacker, out var r))
        {
            var me = _me.GlobalPosition; var src = r.Node.GlobalPosition;
            _hud.Damage(HudLogic.DamageAngle(me.X, me.Z, _me.Yaw, src.X, src.Z));
        }
    }

    void UpdateHud() => _hud?.SetHealth(Mathf.CeilToInt(_me?.Health ?? 100));
}
