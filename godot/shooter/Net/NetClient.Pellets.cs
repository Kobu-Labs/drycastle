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
    PhysicsRayQueryParameters3D? _pelletQuery;

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
        // the whole fan out of the effect range of the camera draws nothing: no ray per pellet, no hole, no spark, no flash (the sound still plays)
        var far = !ShotFx.InRange(Owned, from, from + G(aim) * range);
        if (!far)
        foreach (var dir in HitMath.PelletDirections(aim, pf.Cone, w.Pellets, pf.Seed))
        {
            var ray = G(dir);
            _pelletQuery ??= PhysicsRayQueryParameters3D.Create(from, from, CollisionLayers.WorldShots); // one query object, refilled per pellet
            _pelletQuery.From = from; _pelletQuery.To = from + ray * range;
            var hit = World.GetWorld3D().DirectSpaceState.IntersectRay(_pelletQuery);
            var worldDist = hit.Count == 0 ? float.MaxValue : from.DistanceTo(hit["position"].AsVector3());
            if (VehicleAlong(from, ray, MathF.Min(range, worldDist), out var car, out var vh))
            {
                // a pellet that stops on a car: the hole rides the car's node, the spark is where the car is drawn
                var vat = ToWorld(car, G(vh.Local));
                if (HoleVisible(vat)) BulletHoles.PlaceOn(Owned, GlassHost(car.Node), G(vh.Local), G(vh.LocalNormal));
                ShotFx.Spark(Owned, vat, car.Node.GlobalTransform.Basis * G(vh.LocalNormal));
                first ??= vat;
                continue;
            }
            if (hit.Count == 0) continue;
            var at = hit["position"].AsVector3();
            var normal = hit["normal"].AsVector3();
            if (HoleVisible(at)) BulletHoles.Place(Owned, at, normal);
            ShotFx.Spark(Owned, at, normal);
            first ??= at;
        }

        var end = first ?? from + G(aim) * range;
        if (!echo)
        {
            SfxShot(new ShotMsg(pf.Shooter, pf.Weapon, pf.Origin, new NVec3(end.X, end.Y, end.Z)), false);
            if (far) return;
            if (pf.Shooter == _myId) _viewmodel?.Flash(0.4f); else if (_remotes.TryGetValue(pf.Shooter, out var flasher)) flasher.View?.Flash();
        }
        if (far) return;
        var muzzle = MuzzleOf(pf.Shooter, from, end);
        ShotFx.Spawn(Owned, muzzle, end, false, impact: false);
        if (!echo) CasingFx.Eject(Owned, muzzle, end - muzzle, CasingKind.Shotgun);
    }
}
