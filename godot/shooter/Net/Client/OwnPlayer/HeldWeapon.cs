using Godot;
using Drycastle.Shooter.Net.Client.Aviation;
using Drycastle.Shooter.Net.Client.Gear;
using Drycastle.Shooter.Net.Client.Incidents;
using Drycastle.Shooter.Net.Client.Parish;
using Drycastle.Shooter.Net.Client.Peers;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Client.OwnPlayer;

/// <summary>The weapon in the local player's hand: the slot keys and the wheel, the viewmodel shown for it, the scope and the iron sights, and its movement speed.</summary>
internal sealed class HeldWeapon
{
    readonly ClientContext _ctx;
    readonly OwnAim _aim;
    readonly LoadoutClient _loadouts;
    readonly BinocularsClient _binoculars;
    readonly ThrowClient _thrower;
    readonly BodyFlings _flings;
    readonly AircraftClient _aircraft;
    readonly ScudClient _scud;
    readonly PounceClient _pounce;
    readonly InteractTargets _targets;

    public HeldWeapon(ClientContext ctx, OwnAim aim, LoadoutClient loadouts, BinocularsClient binoculars, ThrowClient thrower, BodyFlings flings, AircraftClient aircraft, ScudClient scud, PounceClient pounce, InteractTargets targets)
    {
        _ctx = ctx; _aim = aim; _loadouts = loadouts; _binoculars = binoculars; _thrower = thrower; _flings = flings; _aircraft = aircraft; _scud = scud; _pounce = pounce; _targets = targets;
    }

    /// <summary>Shows the held weapon of the current hotbar slot (the server-confirmed one once known) low on the right; hidden while dead or driving.</summary>
    public void ShowViewmodel()
    {
        var slot = _ctx.Ammo?.Slot ?? _ctx.Hotbar.Want;
        var id = _thrower.HeldId() ?? (_ctx.Weapons != null && slot >= 1 && slot <= _ctx.Weapons.Weapons.Count ? _ctx.Weapons.Weapons[slot - 1].Id : ""); // the throw key held: the grenade, whatever was selected
        _ctx.Viewmodel.Show(id);
        _ctx.Viewmodel.Visible = _ctx.View.Mode.ShowsViewmodel && !_flings.OwnFlung && _ctx.Me is not { ZoomFov: > 0 }; // an armed seat's rider holds their weapon (ADR 0011)
    }

    /// <summary>Movement speed multiplier of the weapon in <paramref name="slot"/> (0: none, 1.0), times the run speed of <see cref="Perks"/>. The server applies the same value.</summary>
    public float SpeedFor(byte slot) =>
        (_ctx.Weapons != null && slot >= 1 && slot <= _ctx.Weapons.Weapons.Count ? _ctx.Weapons.Weapons[slot - 1].MoveSpeedMultiplier : 1f) * _loadouts.Perks.RunSpeed;

    /// <summary>This player's perk values, mirroring the server's copy for prediction. Neutral until the loadout ticket sets them.</summary>

    /// <summary>Holding zoom with a scoped weapon (sniper) narrows the camera; never while dead or with the mouse released.</summary>
    public void ApplyScope()
    {
        if (_ctx.Me == null || _aircraft.AtTurret || _scud.ScreenOn) return; // at a turret the zoom is the gunship's; on a Scud screen it is the screen's
        if (_ctx.View.Mode.ScopeSuppressed)
        {
            // seated (roof/door gun, turret, any seat) or steering a missile: the held weapon's scope and iron-sight zoom stay off (#241)
            _ctx.Me.ZoomFov = 0f; _ctx.Me.IronSightZoom = false;
            _ctx.Viewmodel.Aiming = false;
            _binoculars.Lower();
            return;
        }
        if (_binoculars.Tick()) return; // raised binoculars are the zoom: no scope, no iron sights
        var slot = _ctx.Ammo?.Slot ?? _ctx.Hotbar.Want;
        var zoom = _ctx.Weapons != null && slot >= 1 && slot <= _ctx.Weapons.Weapons.Count ? _ctx.Weapons.Weapons[slot - 1].ZoomFov : 0f;
        var on = zoom > 0 && !_ctx.Dead && !_pounce.Pinned && Input.MouseMode == Input.MouseModeEnum.Captured && Input.IsActionPressed("zoom");
        _ctx.Me.ZoomFov = on ? zoom : 0f;
        // iron sights: any non-scoped, non-melee weapon while zoom is held
        var kind = _ctx.Weapons != null && slot >= 1 && slot <= _ctx.Weapons.Weapons.Count ? _ctx.Weapons.Weapons[slot - 1].Kind : WeaponKind.Melee;
        _ctx.Viewmodel.Aiming = zoom <= 0 && kind != WeaponKind.Melee && !_ctx.Dead && (!_ctx.Driving && _ctx.View.MyState != EntityState.InVehicle || _ctx.View.InArmedSeat)
                && Input.MouseMode == Input.MouseModeEnum.Captured && Input.IsActionPressed("zoom");
        _ctx.Me.IronSightZoom = _ctx.Viewmodel.Aiming;
    }

    /// <summary>At a turret the slot sent is the key held (1-5, 0 none), not the body's hotbar weapon: the turret reads it as its weapon number (the AC-130's 1-3).</summary>
    public byte TurretKey()
    {
        if (_ctx.Dead || Input.MouseMode != Input.MouseModeEnum.Captured) return 0;
        for (var k = 0; k < 5; k++)
            if (Input.IsActionPressed($"slot_{k + 1}")) return (byte)(k + 1);
        return 0;
    }

    public void ReadSlotKeys()
    {
        if (!_ctx.View.Mode.HotbarKeysLive || Input.MouseMode != Input.MouseModeEnum.Captured || _targets.ChooserOpen()) return; // seated, the number keys are the seats' (HandleSeatInput)
        for (var k = 0; k < Math.Min(5, _ctx.Hotbar.Slots.Length); k++)
            if (Input.IsActionPressed($"slot_{k + 1}")) _ctx.Hotbar.Want = _ctx.Hotbar.Slots[k] + 1;
    }

    /// <summary>The mouse wheel: with a scope that has bullet drop up it is the zero dial, else it cycles the hotbar.</summary>
    public void HandleInput(InputEvent e)
    {
        if (_ctx.Bot || _ctx.Weapons == null || _ctx.Dead || _targets.ChooserOpen() || Input.MouseMode != Input.MouseModeEnum.Captured) return;
        if (e is InputEventMouseButton { Pressed: true } b && b.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            if (_aim.DialZero(b.ButtonIndex == MouseButton.WheelUp ? 1 : -1)) return; // a scope with bullet drop is up: the wheel is its zero dial
            _ctx.Hotbar.Want = _ctx.Hotbar.Cycle(_ctx.Hotbar.Want, b.ButtonIndex == MouseButton.WheelDown ? 1 : -1);
        }
    }
}
