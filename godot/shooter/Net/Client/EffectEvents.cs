using Godot;
using Drycastle.Shooter.Scenery;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Flight;
using Drycastle.ShooterCore.Glass;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Killstreaks.Helicopter;
using Drycastle.ShooterCore.Killstreaks.Moab;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Scenery;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The server's one-off effects, each to the feature that draws it: the ammo, shots, launches and hits, the blasts and what they do to vehicles and glass, the gunship's coalesced
/// effects, a body thrown by a vehicle, and the street lamps and sign plates the server switches. It decodes and routes; the features do the drawing.
/// </summary>
internal sealed class EffectEvents
{
    readonly ClientContext _ctx;
    readonly OwnWeapon _weapon;
    readonly HudClient _hud;
    readonly BlipsClient _blips;
    readonly VehicleGunsClient _guns;
    readonly ShotsClient _shots;
    readonly RocketsClient _rockets;
    readonly HuntsClient _hunts;
    readonly MoabClient _moab;
    readonly MissileClient _missile;
    readonly ImpactsClient _impacts;
    readonly AircraftClient _aircraft;
    readonly RemotesClient _remotes;
    readonly VehiclesClient _vehicles;
    readonly GlassClient _glass;

    public EffectEvents(ClientContext ctx, OwnWeapon weapon, HudClient hud, BlipsClient blips, VehicleGunsClient guns, ShotsClient shots, RocketsClient rockets, HuntsClient hunts, MoabClient moab, MissileClient missile, ImpactsClient impacts, AircraftClient aircraft, RemotesClient remotes, VehiclesClient vehicles, GlassClient glass)
    {
        _ctx = ctx; _weapon = weapon; _hud = hud; _blips = blips; _guns = guns; _shots = shots; _rockets = rockets; _hunts = hunts; _moab = moab; _missile = missile; _impacts = impacts; _aircraft = aircraft; _remotes = remotes; _vehicles = vehicles; _glass = glass;
    }

    /// <summary>One reliable event, to the features that draw effects: the fire and its answers first, then the blasts and the other effects, then the street (a kind is only one of them).</summary>
    public void OnEvent(EventKind kind, byte[] payload)
    {
        OnFireEvent(kind, payload);
        OnBlastEvent(kind, payload);
        OnStreetEvent(kind, payload);
    }

    /// <summary>The ammo, the shots, the launches and the hits: the sounds, the HUD and the fire's own effects.</summary>
    void OnFireEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.Ammo when WeaponCodec.DecodeAmmo(payload) is { } a:
                _ctx.Ammo = a;
                _weapon.OnAmmo(a);
                _ctx.Sound.Ammo(a, _weapon.ReloadUntil);
                _hud.Ammo();
                break;
            case EventKind.Shot when WeaponCodec.DecodeShot(payload) is { } sh:
                _blips.Add(sh.Shooter, sh.Weapon, sh.Origin);
                if (!_guns.OnDoorGunShot(sh)) _shots.OnShot(sh); // a door gun's round leaves its muzzle on the model
                break;
            case EventKind.PelletFan when WeaponCodec.DecodePelletFan(payload) is { } pf:
                _blips.Add(pf.Shooter, pf.Weapon, pf.Origin);
                _shots.OnPelletFan(pf);
                break;
            case EventKind.Launch when WeaponCodec.DecodeLaunch(payload) is { } l:
                _rockets.OnLaunch(l);
                break;
            case EventKind.HuntShot when HuntShot.Decode(payload) is { } hs:
                _hunts.OnShot(hs);
                break;
            case EventKind.Hit when WeaponCodec.DecodeHit(payload) is { } h:
                _ctx.Logs?.Hits.Add((_ctx.Now, h));
                _ctx.Sound.Hit(h);
                _hud.OnHit(h);
                break;
        }
    }


    /// <summary>The blasts, the gunship's coalesced effects and the vehicles' and the world's reaction.</summary>
    void OnBlastEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.Explosion when WeaponCodec.DecodeExplosion(payload) is { } ex:
                _rockets.OnExplosion(ex);
                break;
            case EventKind.VehicleExplosion when WeaponCodec.DecodeVehicleExplosion(payload) is { } vx:
                _vehicles.OnExplosion(vx);
                break;
            case EventKind.Moab when MoabEvents.Decode(payload) is { } mo:
                _moab.OnMoab(mo);
                break;
            case EventKind.MissileBlast when MissileCodec.DecodeBlast(payload) is { } mb:
                _missile.OnBlast(mb);
                break;
            case EventKind.GunshipShots when GunshipFxCodec.DecodeShots(payload) is { } gs:
                _shots.OnGunshipShots(gs);
                break;
            case EventKind.GunshipImpacts when GunshipFxCodec.DecodeImpacts(payload) is { } gi:
                _shots.OnGunshipImpacts(gi);
                break;
            case EventKind.GunshipBlasts when GunshipFxCodec.DecodeBlasts(payload) is { } gb:
                _shots.OnGunshipBlasts(gb);
                break;
            case EventKind.ImpactFx when ImpactCodec.Decode(payload) is { } imp:
                _impacts.OnImpact(imp);
                break;
            case EventKind.HelperWarning when HelperWarningCodec.Decode(payload) is { } hw:
                _aircraft.OnHelperWarning(hw);
                break;
            case EventKind.PlayerFlung when PlayerFlungCodec.Decode(payload) is { } flung:
                _remotes.OnPlayerFlung(flung);
                break;
            case EventKind.VehicleCrash when VehicleCrashCodec.Decode(payload) is { } crash:
                _ctx.Cars.Sounds.OnCrash(crash.Car, crash.Sound);
                break;
            case EventKind.GlassPane when GlassCodec.DecodePane(payload) is { } gp:
                _glass.OnPane(gp);
                break;
            case EventKind.GlassSync when GlassCodec.DecodeSync(payload) is { } gsync:
                _glass.OnSync(gsync);
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
