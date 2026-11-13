using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>Feeds the in-match <see cref="Hud"/> from client state: events push changes, the per-frame hook only forwards health.</summary>
public partial class NetClient
{
    Hud? _hud;
    readonly SpreadEstimator _spread = new();
    int _hudRestarts;
    /// <summary>The aim button of the last input tick; the crosshair spread uses it as the server uses the input's Aim bit.</summary>
    bool _aimHeld;

    WeaponDef? CurrentWeapon => Ammo is { } a && Weapons != null && a.Slot >= 1 && a.Slot <= Weapons.Weapons.Count ? Weapons.Weapons[a.Slot - 1] : null;

    /// <summary>Spread the next shot would get, for the crosshair gap: the same rule as the server's shot (stance, aim, movement).</summary>
    float CrosshairSpread()
    {
        if (CurrentWeapon is not { } w) return 0f;
        var own = HitMath.OwnSpread(w, _spread.Degrees(w, Now));
        // in an armed seat the vehicle's speed is what widens it (the server's rule for a shot from the seat)
        if (InArmedSeat && MySeat() is { } m) return SeatAimRules.ShotSpread(own, w, _aimHeld, RideSpeed(m.Car, DriveOf(m.Car)), m.Seat);
        return _me is { } me ? HitMath.ShotSpread(own, w, me.State, _aimHeld) : own;
    }

    void HudWelcome()
    {
        if (!ShotFx.Enabled || _hud != null) return; // headless runs have no HUD
        _hud = new Hud { MyId = _myId, KillWeaponName = id => Weapons != null && id < Weapons.Weapons.Count ? WeaponName(Weapons.Weapons[id].Id) : null, Scoped = () => _me is { ZoomFov: > 0 }, Aim = () => _viewmodel?.AimAmount ?? 0f, Breath = BreathView, Spread = CrosshairSpread };
        AddChild(_hud); // the hotbar names are set at each spawn (ApplySpawnLoadout)
        _hud.SetSandbox(SandboxGranted, SandboxHeldStreak, SandboxTakesDamage); // the SANDBOX badge (#156)
        WireLoadoutHud(_hud);
        _hud.AttachEquipment(this); // F (#268)
        _hud.Minimap.Build(Map); // once per connection; the texture comes from the map's parcels and roads (ticket #119)
    }

    /// <summary>The loaded map (set by the host): the minimap's texture source.</summary>
    public Drycastle.MapLoader.MapLoader? Map;
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
        // a gunner's hit marker is the turret overlay's while the HUD is hidden (ticket #152); the hit event reaches it either way
        if (h.Attacker == _myId && h.Victim != _myId && _gunship != null) _gunOverlay.Hit(h.Headshot, h.Kill);
        if (_hud == null) return;
        if (h.Attacker == _myId && h.Victim != _myId) _hud.Hit(h.Headshot, h.Kill);
        if (h.Victim == _myId && h.Attacker != _myId && _me != null && _remotes.TryGetValue(h.Attacker, out var r))
        {
            var me = _me.GlobalPosition; var src = r.Node.GlobalPosition;
            _hud.Damage(HudLogic.DamageAngle(me.X, me.Z, _me.Yaw, src.X, src.Z));
        }
    }

    readonly List<Vector2> _friendDots = new();

    void UpdateHud()
    {
        _hud?.SetControlMode(_gunship != null || _controlling); // one mode switch: a turret or the missile owns the screen (ticket #152)
        _hud?.SetHealth(Mathf.CeilToInt(_me?.Health ?? 100));
        if (_me != null) _me.MoveWithFreeMouse = _hud?.EquipmentOpen ?? false; // the Equipment screen frees the mouse, not the legs (#268)
        if (_hud is { EquipmentOpen: true } && (MenuOpen?.Invoke() == true || ChooserOpen)) _hud.CloseEquipment(); // a menu or the corpse chooser takes the screen
        UavTick();
        if (_me != null) Rozhlas.Current?.SetListener(_me.GlobalPosition); // the loudspeaker is heard where the player is, not where a vehicle's orbiting camera is
        if (_me != null) _hud?.SetBreath(Breath.AirLeft(_me.State), _me.HeadUnder);
        if (_me != null && _hud != null)
        {
            var g = _me.GlobalPosition;
            var src = MinimapView.Select(missilePov: _povCam is { Current: true }, turret: _gunship != null, driving: _driving);
            _hud.Minimap.SetPlayer(new Vector2(g.X, g.Z), MinimapView.YawFor(src, _me.Yaw, _carYaw, _turretViewYaw, _missileViewYaw));
            _carDots.Clear();
            foreach (var c in _cars.Values)
                if (!c.Destroyed) _carDots.Add(new Vector2(c.Node.GlobalPosition.X, c.Node.GlobalPosition.Z));
            _hud.Minimap.SetVehicleDots(_carDots);
            _friendDots.Clear();
            if (Match.IsTeamMode)
                foreach (var (id, r) in _remotes)
                    if (r.State != EntityState.Dead && Match.AreFriends(_myId, id) && GodotObject.IsInstanceValid(r.Node))
                        _friendDots.Add(new Vector2(r.Node.GlobalPosition.X, r.Node.GlobalPosition.Z));
            _hud.Minimap.SetFriendDots(_friendDots);
        }
    }
}
