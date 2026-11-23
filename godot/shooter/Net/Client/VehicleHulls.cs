using Godot;
using Drycastle.Shooter.Vehicles;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Glass;
using Drycastle.ShooterCore.Vehicles;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// Where a vehicle's hull is on this client, for the things that meet it: the solid box a step on foot or a blast meets, and the hit box a shot is resolved against, as the server has them (the tilt
/// comes from the latest flight or drive record, a trailer's from its ground fit) without the tilt travelling on the wire.
/// </summary>
internal sealed class VehicleHulls
{
    readonly ClientContext _ctx;
    readonly FlightClient _flight;
    readonly DriveClient _drive;
    readonly Func<ushort, bool> _carThrown;
    readonly GlassConfig _glass;

    /// <param name="carThrown">The Bigfoot holds or threw this vehicle: it is level, as on the server.</param>
    public VehicleHulls(ClientContext ctx, FlightClient flight, DriveClient drive, Func<ushort, bool> carThrown, GlassConfig glass)
    {
        _ctx = ctx; _flight = flight; _drive = drive; _carThrown = carThrown; _glass = glass;
    }

    /// <summary>The pitch and roll a vehicle's hull is turned by on this client: the latest flight record's, the trailer's ground fit, or the latest drive record's slope; else none.</summary>
    public (float Pitch, float Roll) TiltOf(CarView v, VehicleDef def) =>
        !def.Tilts ? (0f, 0f)
        : v.Trailer is { Hull: { } h } ? (h.Pitch, h.Roll) // a trailer's own ground fit (TowClient.RefreshHull), the server's number
        : _flight.RecordTilt(v.Id) is { } tilt ? tilt
        : !_carThrown(v.Id) && _drive.RecordTilt(v.Id) is { } slope ? slope // a driven vehicle's slope, the latest drive record's (the server's Drive.Pitch/Roll); not the springs' lean
        : (0f, 0f); // (a thrown one is level, as on the server)

    /// <summary>Where a vehicle's hull frame has its origin: the record's place, or a trailer's origin turned about its axle (<see cref="TiltOf"/>).</summary>
    public static NVec3 FeetOf(CarView v) => v.Trailer is { Hull: { } h } ? h.Origin : v.Pos;

    /// <summary>Puts a vehicle's solid box where its latest records say: the server builds its own from the same numbers (<c>ServerCar.PlaceSolid</c>).</summary>
    public void PlaceSolid(CarView v)
    {
        if (v.Solid == null) return;
        var f = FeetOf(v);
        var p = new Vector3(f.X, f.Y, f.Z);
        var def = _ctx.Vehicles!.Vehicles[v.Kind];
        if (def.Tilts) { var (pitch, roll) = TiltOf(v, def); VehicleCollider.Place(v.Solid, p, v.Yaw, pitch, roll, !v.Destroyed); }
        else VehicleCollider.Place(v.Solid, p, v.Yaw, !v.Destroyed);
    }

    /// <summary>
    /// The nearest vehicle the ray (<paramref name="from"/>, <paramref name="dir"/>) enters within <paramref name="maxDistance"/>, on the
    /// vehicle's hit box (<see cref="VehicleBox"/>, the server's own collider) at its latest snapshot: the position the server resolved the shot against.
    /// As on the server (NetServer.BulletHit, docs/glass.md) a ray that goes in by a glass pane or an open side and out by another is not stopped
    /// by that vehicle, and one that goes in and finds no way out is stopped on the far side of the hit box, not where it met it.
    /// <paramref name="dir"/> is a unit vector.
    /// </summary>
    /// <param name="skip">A vehicle the ray passes through: the one the local player fires from, in an armed seat (the server leaves that hull out too).</param>
    public bool Along(Vector3 from, Vector3 dir, float maxDistance, out CarView car, out VehicleBoxHit hit, CarView? skip = null)
    {
        car = null!; hit = default;
        if (_ctx.Vehicles == null) return false;
        var best = float.MaxValue;
        foreach (var v in _ctx.Cars.All)
        {
            if (v.Destroyed || v == skip || v.Kind >= _ctx.Vehicles.Vehicles.Count) continue;
            var def = _ctx.Vehicles.Vehicles[v.Kind];
            var (pitch, roll) = TiltOf(v, def);
            if (VehicleBox.Ray(from.ToNumerics(), dir.ToNumerics(), maxDistance, FeetOf(v), v.Yaw, pitch, roll, def) is not { } h || h.Distance >= best) continue;
            if (def.GlassRects.Count > 0 || def.OpenSideRects.Count > 0)
            {
                var lo = GlassRules.ToVehicle(from.ToNumerics(), FeetOf(v), v.Yaw, pitch, roll); var ld = GlassRules.DirToVehicle(dir.ToNumerics(), v.Yaw, pitch, roll);
                var way = GlassRules.Through(def.GlassRects, def.OpenSideRects, lo, ld, h.Distance, _glass.VehicleReach);
                if (way.Enters)
                {
                    if (way.Leaves) continue; // through the glass and out again
                    var far = MathF.Max(VehicleBox.ExitDistance(lo, ld, def), way.EntryT);
                    if (far > maxDistance || far >= best) continue;
                    h = new VehicleBoxHit(far, lo + ld * far, -ld); // inside, on the far side, facing back along the ray
                }
            }
            best = h.Distance; hit = h; car = v;
        }
        return car != null;
    }

    /// <summary>A point in a vehicle's node frame (as <see cref="VehicleBoxHit"/>) in world space, where the car is drawn now.</summary>
    public static Vector3 ToWorld(CarView v, Vector3 local) => v.Node.GlobalTransform * local;

    /// <summary>
    /// Bullet hole for a hitscan shot that the server stopped at <paramref name="end"/>: on the vehicle the ray enters at the end (a car
    /// is hit at its hit box, and the hole rides its node), otherwise on the world.
    /// </summary>
    public void ShotHole(Vector3 from, Vector3 end)
    {
        var d = end - from;
        var len = d.Length();
        if (len < 0.05f) return;
        if (Along(from, d / len, len + 0.5f, out var car, out var hit) && hit.Distance >= len - 0.5f)
        {
            BulletHoles.PlaceOn(_ctx.Owned, GlassClient.HostOf(car.Node), hit.Local.ToGodot(), hit.LocalNormal.ToGodot());
            return;
        }
        BulletHoles.PlaceFromShot(_ctx.Owned, from, end);
    }
}
