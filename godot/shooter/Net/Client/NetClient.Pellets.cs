using Godot;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Casings;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// Shotgun pellet fan (client side). The server's <see cref="EventKind.PelletFan"/> carries only origin, aim and seed; this
/// rebuilds the same pellet directions and draws each one that lands in the world as a bullet hole and a spark burst,
/// with one tracer along the first landing pellet. The sound and flash are skipped for the own shot when prediction already played them.
/// </summary>
public partial class NetClient
{
    void OnPelletFan(PelletFanMsg pf)
    {
        if (Weapons == null || pf.Weapon >= Weapons.Weapons.Count) return;
        var w = Weapons.Weapons[pf.Weapon];
        var range = w.Range > 0 ? w.Range : 300f;
        var echo = EchoOfPredicted(pf.Shooter);
        if (pf.Shooter == Ctx.MyId) _spread.Shot(w, Ctx.Now);
        if (Ctx.Remotes.TryGetValue(pf.Shooter, out var shooter)) shooter.FireUntil = Ctx.Now + 0.45;

        var from = G(pf.Origin);
        var aim = pf.Aim;
        Vector3? first = null;
        foreach (var dir in HitMath.PelletDirections(aim, pf.Cone, w.Pellets, pf.Seed))
        {
            var ray = G(dir);
            var q = PhysicsRayQueryParameters3D.Create(from, from + ray * range, CollisionLayers.WorldShots);
            var hit = World.GetWorld3D().DirectSpaceState.IntersectRay(q);
            var worldDist = hit.Count == 0 ? float.MaxValue : from.DistanceTo(hit["position"].AsVector3());
            if (VehicleAlong(from, ray, MathF.Min(range, worldDist), out var car, out var vh))
            {
                // a pellet that stops on a car: the hole rides the car's node, the spark is where the car is drawn
                BulletHoles.PlaceOn(Ctx.Owned, GlassClient.HostOf(car.Node), G(vh.Local), G(vh.LocalNormal));
                var vat = ToWorld(car, G(vh.Local));
                ShotFx.Spark(Ctx.Owned, vat, car.Node.GlobalTransform.Basis * G(vh.LocalNormal));
                first ??= vat;
                continue;
            }
            if (hit.Count == 0) continue;
            var at = hit["position"].AsVector3();
            var normal = hit["normal"].AsVector3();
            BulletHoles.Place(Ctx.Owned, at, normal);
            ShotFx.Spark(Ctx.Owned, at, normal);
            first ??= at;
        }

        var end = first ?? from + G(aim) * range;
        if (!echo)
        {
            SfxShot(new ShotMsg(pf.Shooter, pf.Weapon, pf.Origin, new NVec3(end.X, end.Y, end.Z)), false);
            if (pf.Shooter == Ctx.MyId) _viewmodel.Flash(0.4f); else if (Ctx.Remotes.TryGetValue(pf.Shooter, out var flasher)) flasher.View?.Flash();
        }
        var muzzle = MuzzleOf(pf.Shooter, from, end);
        ShotFx.Spawn(Ctx.Owned, muzzle, end, false, impact: false);
        if (!echo) CasingFx.Eject(Ctx.Owned, muzzle, end - muzzle, CasingKind.Shotgun);
    }
}
