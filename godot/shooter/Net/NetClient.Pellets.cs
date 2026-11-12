using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

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
        if (pf.Shooter == _myId) _spread.Shot(w, Now);
        if (_remotes.TryGetValue(pf.Shooter, out var shooter)) shooter.FireUntil = Now + 0.45;

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
                BulletHoles.PlaceOn(Owned, car.Node, G(vh.Local), G(vh.LocalNormal));
                var vat = ToWorld(car, G(vh.Local));
                ShotFx.Spark(Owned, vat, car.Node.GlobalTransform.Basis * G(vh.LocalNormal));
                first ??= vat;
                continue;
            }
            if (hit.Count == 0) continue;
            var at = hit["position"].AsVector3();
            var normal = hit["normal"].AsVector3();
            BulletHoles.Place(Owned, at, normal);
            ShotFx.Spark(Owned, at, normal);
            first ??= at;
        }

        var end = first ?? from + G(aim) * range;
        if (!echo)
        {
            SfxShot(new ShotMsg(pf.Shooter, pf.Weapon, pf.Origin, new NVec3(end.X, end.Y, end.Z)), false);
            if (pf.Shooter == _myId) _viewmodel?.Flash(0.4f); else if (_remotes.TryGetValue(pf.Shooter, out var flasher)) flasher.View?.Flash();
        }
        ShotFx.Spawn(Owned, MuzzleOf(pf.Shooter, from, end), end, false, impact: false);
    }
}
