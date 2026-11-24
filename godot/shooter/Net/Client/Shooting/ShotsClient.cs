using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Flight;
using Drycastle.Shooter.Net.Client.Aviation;
using Drycastle.Shooter.Net.Client.CarPool;
using Drycastle.Shooter.Net.Client.OwnPlayer;
using Drycastle.Shooter.Net.Client.Peers;
using Drycastle.Shooter.Vehicles;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Casings;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Flight;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client.Shooting;

/// <summary>
/// The shots of everybody as drawn: a tracer from a shot event or a gunship tick (the muzzle, the tracer, the hole and the sound, the prediction's echo skipped), the shotgun's pellet fan
/// (the server's <see cref="EventKind.PelletFan"/> carries only origin, aim and seed; this rebuilds the same pellet directions and draws each one that lands in the world as a bullet hole
/// and a spark burst, with one tracer along the first landing pellet) and the gunship's tick of tracers, impacts and blasts (ticket #159, coalesced by the server, kinds 121-123), each drawn
/// through the same code as the single events. The own shot's sound and flash are skipped when <see cref="OwnWeapon"/> already played them.
/// </summary>
internal sealed class ShotsClient
{
    readonly ClientContext _ctx;
    readonly OwnWeapon _weapon;
    readonly VehicleHulls _hulls;
    readonly WaterClient _water;
    readonly VehicleGunsClient _guns;
    readonly AircraftClient _aircraft;
    readonly ImpactsClient _impacts;

    public ShotsClient(ClientContext ctx, OwnWeapon weapon, VehicleHulls hulls, WaterClient water, VehicleGunsClient guns, AircraftClient aircraft, ImpactsClient impacts)
    {
        _ctx = ctx; _weapon = weapon; _hulls = hulls; _water = water; _guns = guns; _aircraft = aircraft; _impacts = impacts;
    }

    public void OnPelletFan(PelletFanMsg pf)
    {
        if (_ctx.Weapons == null || pf.Weapon >= _ctx.Weapons.Weapons.Count) return;
        var w = _ctx.Weapons.Weapons[pf.Weapon];
        var range = w.Range > 0 ? w.Range : 300f;
        var echo = _weapon.EchoOfPredicted(pf.Shooter);
        if (pf.Shooter == _ctx.MyId) _weapon.Spread.Shot(w, _ctx.Now);
        if (_ctx.Remotes.TryGetValue(pf.Shooter, out var shooter)) shooter.FireUntil = _ctx.Now + 0.45;

        var from = pf.Origin.ToGodot();
        var aim = pf.Aim;
        Vector3? first = null;
        foreach (var dir in HitMath.PelletDirections(aim, pf.Cone, w.Pellets, pf.Seed))
        {
            var ray = dir.ToGodot();
            var q = PhysicsRayQueryParameters3D.Create(from, from + ray * range, CollisionLayers.WorldShots);
            var hit = _ctx.World.GetWorld3D().DirectSpaceState.IntersectRay(q);
            var worldDist = hit.Count == 0 ? float.MaxValue : from.DistanceTo(hit["position"].AsVector3());
            if (_hulls.Along(from, ray, MathF.Min(range, worldDist), out var car, out var vh))
            {
                // a pellet that stops on a car: the hole rides the car's node, the spark is where the car is drawn
                BulletHoles.PlaceOn(_ctx.Owned, GlassClient.HostOf(car.Node), vh.Local.ToGodot(), vh.LocalNormal.ToGodot());
                var vat = VehicleHulls.ToWorld(car, vh.Local.ToGodot());
                ShotFx.Spark(_ctx.Owned, vat, car.Node.GlobalTransform.Basis * vh.LocalNormal.ToGodot());
                first ??= vat;
                continue;
            }
            if (hit.Count == 0) continue;
            var at = hit["position"].AsVector3();
            var normal = hit["normal"].AsVector3();
            BulletHoles.Place(_ctx.Owned, at, normal);
            ShotFx.Spark(_ctx.Owned, at, normal);
            first ??= at;
        }

        var end = first ?? from + aim.ToGodot() * range;
        if (!echo)
        {
            _ctx.Sound.Shot(new ShotMsg(pf.Shooter, pf.Weapon, pf.Origin, new NVec3(end.X, end.Y, end.Z)), false);
            if (pf.Shooter == _ctx.MyId) _ctx.Viewmodel.Flash(0.4f); else if (_ctx.Remotes.TryGetValue(pf.Shooter, out var flasher)) flasher.View?.Flash();
        }
        var muzzle = _weapon.MuzzleOf(pf.Shooter, from, end);
        ShotFx.Spawn(_ctx.Owned, muzzle, end, false, impact: false);
        if (!echo) CasingFx.Eject(_ctx.Owned, muzzle, end - muzzle, CasingKind.Shotgun);
    }

    /// <summary>A tracer, from a shot event or a gunship tick: the muzzle, the tracer, the hole and the sound (prediction echo skipped).</summary>
    /// <param name="exactMuzzle">The origin is the real muzzle (a flak barrel): the tracer starts there, not at the shooter's hands, and the shooter's own flash and hole are skipped.</param>
    public void OnShot(ShotMsg sh, float? speed = null, float? length = null, bool impact = true, bool exactMuzzle = false, float width = 1f, bool? holes = null)
    {
        _ctx.Logs?.Shots.Add((_ctx.Now, sh));
        if (_ctx.Remotes.TryGetValue(sh.Shooter, out var shooter)) shooter.FireUntil = _ctx.Now + 0.45;
        if (sh.Shooter == _ctx.MyId && _ctx.Weapons != null && sh.Weapon < _ctx.Weapons.Weapons.Count) _weapon.Spread.Shot(_ctx.Weapons.Weapons[sh.Weapon], _ctx.Now);
        var melee = _ctx.Weapons != null && sh.Weapon < _ctx.Weapons.Weapons.Count && _ctx.Weapons.Weapons[sh.Weapon].Kind == WeaponKind.Melee;
        if (_weapon.EchoOfPredicted(sh.Shooter)) return; // already played and drawn by prediction
        var endV = new Vector3(sh.End.X, sh.End.Y, sh.End.Z);
        _ctx.Sound.Shot(sh, melee);
        if (!melee && !exactMuzzle) { if (sh.Shooter == _ctx.MyId) _ctx.Viewmodel.Flash(0.4f); else if (_ctx.Remotes.TryGetValue(sh.Shooter, out var flasher)) flasher.View?.Flash(); }
        var originV = new Vector3(sh.Origin.X, sh.Origin.Y, sh.Origin.Z);
        // a gunship's impact is its own message (OnGunshipImpacts), so its tracer does not draw one too: no 300 m cut-off for a long round
        var muzzle = exactMuzzle ? originV : _weapon.MuzzleOf(sh.Shooter, originV, endV);
        ShotFx.Spawn(_ctx.Owned, muzzle, endV, melee, impact: impact, speed: speed, length: length, width: width);
        if (!melee && !exactMuzzle && CasingKindOf(sh.Weapon) is { } ck) CasingFx.Eject(_ctx.Owned, muzzle, endV - muzzle, ck);
        var marks = !melee && (holes ?? !exactMuzzle);
        if (marks) _hulls.ShotHole(originV, endV);
        if (marks) _water.BulletSurfaceHit(originV, endV);
    }

    /// <summary>The casing a weapon byte ejects: a vehicle machine gun a big one, a hand weapon by its category, none for melee, launchers and the rest.</summary>
    CasingKind? CasingKindOf(byte weapon) => weapon == SpecialWeapons.VehicleGun ? CasingKind.Heavy : _ctx.Weapons != null && weapon < _ctx.Weapons.Weapons.Count ? CasingFx.KindOf(_ctx.Weapons.Weapons[weapon]) : null;

    /// <summary>One gunner's tracers this tick: each one drawn as its own shot.</summary>
    public void OnGunshipShots(GunshipShotsMsg m)
    {
        var flak = _guns.FlakCarOf(m.Owner); // a flak gunner's shells leave the barrels: the tracer starts at the muzzle, and the barrel kicks
        foreach (var t in m.Tracers)
        {
            OnShot(new ShotMsg(m.Owner, t.Weapon, t.Origin, t.End), t.SpeedMps > 0 ? t.SpeedMps : null, t.LengthM > 0 ? t.LengthM : null, impact: false, exactMuzzle: true, width: flak != null ? VehicleGunsClient.FlakTracerWidth : 1f, holes: flak == null);
            flak?.Flak?.Kick();
            if (flak != null) CasingFx.Eject(_ctx.Owned, t.Origin.ToGodot() - (t.End.ToGodot() - t.Origin.ToGodot()).Normalized() * 1.2f, t.End.ToGodot() - t.Origin.ToGodot(), CasingKind.Shell, VehicleGunsClient.CarVelocity(flak)); // a 35 mm case leaves the breech, well behind the muzzle
        }
    }

    /// <summary>The tick's gunship impacts (kills included): each one drawn as an ImpactFx.</summary>
    public void OnGunshipImpacts(IReadOnlyList<GunshipImpactFx> impacts)
    {
        foreach (var i in impacts) _impacts.OnImpact(i.Impact);
    }

    /// <summary>
    /// One gunner's blasts this tick: each one the explosion look at its radius and the shake, the sound once for the batch. The owner's
    /// hit marker follows the flags.
    /// </summary>
    public void OnGunshipBlasts(GunshipBlastsMsg m)
    {
        for (var i = 0; i < m.Blasts.Count; i++)
        {
            var b = m.Blasts[i];
            var at = new Vector3(b.At.X, b.At.Y, b.At.Z);
            if (i == 0 && Sfx.Enabled) Sfx.Play3D(SoundRules.Event("explosion", "rpg"), at);
            if (Mathf.Abs(b.Radius - _guns.FlakPuffRadius) < 0.05f) FlakFx.Burst(_ctx.Owned, at, b.Radius); // an airburst: black flak smoke, not a fireball
            else RocketFx.Explode(_ctx.Owned, at, b.Radius);
            _ctx.Shake.At(at, b.Radius);
        }
        if (m.Owner == _ctx.MyId && m.Hits != GunshipHitFlags.None) OnGunnerHits(m.Hits);
    }

    /// <summary>The owner's hit marker from a gunship tick: the turret overlay while at a gun, the HUD's marker, and the hit sound once.</summary>
    void OnGunnerHits(GunshipHitFlags flags)
    {
        var head = flags.HasFlag(GunshipHitFlags.Headshot);
        var kill = flags.HasFlag(GunshipHitFlags.Kill);
        _aircraft.HitMarker(head, kill);
        _ctx.Hud?.Hit(head, kill);
        if (Sfx.Enabled) Sfx.Play2D(kill ? "kill.confirm" : "hit.tick");
    }
}
