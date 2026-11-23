using Godot;
using Drycastle.Shooter.Scenery;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Flight;
using Drycastle.ShooterCore.Glass;
using Drycastle.ShooterCore.Killstreaks.Helicopter;
using Drycastle.ShooterCore.Killstreaks.Moab;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Scenery;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.Shooter.Net.Client;

/// <summary>Weapons: the slot keys, the scope, the viewmodel and the weapon events (ammo, shots, launches, explosions, hits).</summary>
public partial class NetClient
{
    /// <summary>Shows the held weapon of the current hotbar slot (the server-confirmed one once known) low on the right; hidden while dead or driving.</summary>
    void UpdateViewmodel()
    {
        var slot = Ammo?.Slot ?? Ctx.Hotbar.Want;
        var id = ThrowHeldId() ?? (Weapons != null && slot >= 1 && slot <= Weapons.Weapons.Count ? Weapons.Weapons[slot - 1].Id : ""); // the throw key held: the grenade, whatever was selected
        _viewmodel.Show(id);
        _viewmodel.Visible = Mode.ShowsViewmodel && !OwnBodyFlung && Ctx.Me is not { ZoomFov: > 0 }; // an armed seat's rider holds their weapon (ADR 0011)
    }

    /// <summary>Movement speed multiplier of the weapon in <paramref name="slot"/> (0: none, 1.0), times the run speed of <see cref="Perks"/>. The server applies the same value.</summary>
    float SpeedFor(byte slot) =>
        (Weapons != null && slot >= 1 && slot <= Weapons.Weapons.Count ? Weapons.Weapons[slot - 1].MoveSpeedMultiplier : 1f) * Loadouts.Perks.RunSpeed;

    /// <summary>This player's perk values, mirroring the server's copy for prediction. Neutral until the loadout ticket sets them.</summary>

    /// <summary>Holding zoom with a scoped weapon (sniper) narrows the camera; never while dead or with the mouse released.</summary>
    void ApplyScope()
    {
        if (Ctx.Me == null || _gunship != null || ScudScreenOn) return; // at a turret the zoom is the gunship's; on a Scud screen it is the screen's
        if (Mode.ScopeSuppressed)
        {
            // seated (roof/door gun, turret, any seat) or steering a missile: the held weapon's scope and iron-sight zoom stay off (#241)
            Ctx.Me.ZoomFov = 0f; Ctx.Me.IronSightZoom = false;
            _viewmodel.Aiming = false;
            LowerBinoculars();
            return;
        }
        if (TickBinoculars()) return; // raised binoculars are the zoom: no scope, no iron sights
        var slot = Ammo?.Slot ?? Ctx.Hotbar.Want;
        var zoom = Weapons != null && slot >= 1 && slot <= Weapons.Weapons.Count ? Weapons.Weapons[slot - 1].ZoomFov : 0f;
        var on = zoom > 0 && !Dead && !_pinned && Input.MouseMode == Input.MouseModeEnum.Captured && Input.IsActionPressed("zoom");
        Ctx.Me.ZoomFov = on ? zoom : 0f;
        // iron sights: any non-scoped, non-melee weapon while zoom is held
        var kind = Weapons != null && slot >= 1 && slot <= Weapons.Weapons.Count ? Weapons.Weapons[slot - 1].Kind : WeaponKind.Melee;
        _viewmodel.Aiming = zoom <= 0 && kind != WeaponKind.Melee && !Dead && (!Ctx.Driving && Ctx.View.MyState != EntityState.InVehicle || Ctx.View.InArmedSeat)
                && Input.MouseMode == Input.MouseModeEnum.Captured && Input.IsActionPressed("zoom");
        Ctx.Me.IronSightZoom = _viewmodel.Aiming;
    }

    /// <summary>At a turret the slot sent is the key held (1-5, 0 none), not the body's hotbar weapon: the turret reads it as its weapon number (the AC-130's 1-3).</summary>
    byte TurretKey()
    {
        if (Dead || Input.MouseMode != Input.MouseModeEnum.Captured) return 0;
        for (var k = 0; k < 5; k++)
            if (Input.IsActionPressed($"slot_{k + 1}")) return (byte)(k + 1);
        return 0;
    }

    void ReadSlotKeys()
    {
        if (!Mode.HotbarKeysLive || Input.MouseMode != Input.MouseModeEnum.Captured || ChooserOpen) return; // seated, the number keys are the seats' (HandleSeatInput)
        for (var k = 0; k < Math.Min(5, Ctx.Hotbar.Slots.Length); k++)
            if (Input.IsActionPressed($"slot_{k + 1}")) Ctx.Hotbar.Want = Ctx.Hotbar.Slots[k] + 1;
    }

    void OnWeaponEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.Ammo when WeaponCodec.DecodeAmmo(payload) is { } a:
                Ctx.Ammo = a;
                PredictOnAmmo(a);
                Ctx.Sound.Ammo(a, _pReloadUntil);
                HudAmmo();
                break;
            case EventKind.Shot when WeaponCodec.DecodeShot(payload) is { } sh:
                _blips.Add(sh.Shooter, sh.Weapon, sh.Origin);
                if (!OnDoorGunShot(sh)) OnShot(sh); // a door gun's round leaves its muzzle on the model
                break;
            case EventKind.PelletFan when WeaponCodec.DecodePelletFan(payload) is { } pf:
                _blips.Add(pf.Shooter, pf.Weapon, pf.Origin);
                OnPelletFan(pf);
                break;
            case EventKind.Launch when WeaponCodec.DecodeLaunch(payload) is { } l:
                OnLaunch(l);
                break;
            case EventKind.Explosion when WeaponCodec.DecodeExplosion(payload) is { } ex:
                OnExplosion(ex);
                break;
            case EventKind.VehicleExplosion when WeaponCodec.DecodeVehicleExplosion(payload) is { } vx:
                OnVehicleExplosion(vx);
                break;
            case EventKind.Moab when MoabEvents.Decode(payload) is { } mo:
                _moab.OnMoab(mo);
                break;
            case EventKind.MissileBlast when MissileCodec.DecodeBlast(payload) is { } mb:
                OnMissileBlast(mb);
                break;
            case EventKind.GunshipShots when GunshipFxCodec.DecodeShots(payload) is { } gs:
                OnGunshipShots(gs);
                break;
            case EventKind.GunshipImpacts when GunshipFxCodec.DecodeImpacts(payload) is { } gi:
                OnGunshipImpacts(gi);
                break;
            case EventKind.GunshipBlasts when GunshipFxCodec.DecodeBlasts(payload) is { } gb:
                OnGunshipBlasts(gb);
                break;
            case EventKind.ImpactFx when ImpactCodec.Decode(payload) is { } imp:
                _impacts.OnImpact(imp);
                break;
            case EventKind.HelperWarning when HelperWarningCodec.Decode(payload) is { } hw:
                OnHelperWarning(hw);
                break;
            case EventKind.PlayerFlung when PlayerFlungCodec.Decode(payload) is { } flung:
                OnPlayerFlung(flung);
                break;
            case EventKind.VehicleCrash when VehicleCrashCodec.Decode(payload) is { } crash:
                Ctx.Cars.Sounds.OnCrash(crash.Car, crash.Sound);
                break;
            case EventKind.GlassPane when GlassCodec.DecodePane(payload) is { } gp:
                _glass.OnPane(gp);
                break;
            case EventKind.GlassSync when GlassCodec.DecodeSync(payload) is { } gsync:
                _glass.OnSync(gsync);
                break;
            case EventKind.HuntShot when HuntShot.Decode(payload) is { } hs:
                _hunts.OnShot(hs);
                break;
            case EventKind.Hit when WeaponCodec.DecodeHit(payload) is { } h:
                if (KeepEventLogs) _logs.Hits.Add((Ctx.Now, h));
                Ctx.Sound.Hit(h);
                HudHit(h);
                break;
        }
    }

    /// <summary>The street lamps and the sign plates the server switches (their full state at join, then each change).</summary>
    void OnStreetEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.LampState when LampCodec.DecodeState(payload) is { } lamp:
                StreetLamps.Current?.SetPoleLamp(lamp.Index, lamp.State);
                break;
            case EventKind.LampSync when LampCodec.DecodeSync(payload) is { } lamps:
                StreetLamps.Current?.SyncPoleLamps(lamps);
                break;
            case EventKind.SignPlateState when SignPlateCodec.DecodeState(payload) is { } plate:
                SignPlates.Current?.SetPlate(plate.Index, plate.State);
                break;
            case EventKind.SignPlateSync when SignPlateCodec.DecodeSync(payload) is { } plates:
                SignPlates.Current?.SyncPlates(plates);
                break;
        }
    }
}
