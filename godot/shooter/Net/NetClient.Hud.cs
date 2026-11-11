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
        _hud = new Hud { MyId = _myId, Scoped = () => _me is { ZoomFov: > 0 }, Aim = () => _viewmodel?.AimAmount ?? 0f, Breath = BreathView, Spread = () => CurrentWeapon is { } w ? HitMath.StanceSpread(_spread.Degrees(w, Now), w, _me?.State.Stance ?? Stance.Stand) : 0f, StanceOf = () => _me?.State.Stance ?? Stance.Stand };
        AddChild(_hud); // the hotbar names are set at each spawn (ApplySpawnLoadout)
        WireLoadoutHud(_hud);
        _hud.Minimap.Build(Map); // once per connection; the texture comes from the map's parcels and roads (ticket #119)
    }

    /// <summary>The loaded map (set by the host): the minimap's texture source.</summary>
    public Suchohrad.MapLoader.MapLoader? Map;
    readonly List<Vector2> _carDots = new();

    static string WeaponName(string id)
    {
        var k = "weapon." + id;
        var t = Strings.T(k);
        return t == k ? id : t;
    }

    /// <summary>Ammo of the hotbar: the selected hotbar cell, and every hotbar weapon's magazine in hotbar order.</summary>
    void HudAmmo()
    {
        if (_hud == null || Ammo is not { } a || Weapons == null || a.Slot < 1 || a.Slot > Weapons.Weapons.Count) return;
        var w = Weapons.Weapons[a.Slot - 1];
        var all = new List<(int, int, bool)>();
        foreach (var gi in _hotbar)
            if (gi < a.Slots.Count) all.Add((a.Slots[gi].Mag, a.Slots[gi].Reserve, Weapons.Weapons[gi].MagazineSize == 0));
        var (mag, reserve) = a.Slots[a.Slot - 1];
        _hud.SetAmmo(Math.Max(1, HotbarPos(a.Slot) + 1), w.MagazineSize == 0 ? -1 : mag, reserve, all, a.ReloadRemaining, w.ReloadTime);
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

    void UpdateHud()
    {
        _hud?.SetHealth(Mathf.CeilToInt(_me?.Health ?? 100));
        UavTick();
        if (_me != null) _hud?.SetBreath(Breath.AirLeft(_me.State), _me.HeadUnder);
        if (_me != null && _hud != null)
        {
            var g = _me.GlobalPosition;
            _hud.Minimap.SetPlayer(new Vector2(g.X, g.Z), _me.Yaw);
            _carDots.Clear();
            foreach (var c in _cars.Values)
                if (!c.Destroyed) _carDots.Add(new Vector2(c.Node.GlobalPosition.X, c.Node.GlobalPosition.Z));
            _hud.Minimap.SetVehicleDots(_carDots);
        }
    }
}
