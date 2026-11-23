using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Flight;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Waters;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// What a flying vehicle shows and sounds like beyond its attitude: the rotors turn at the rotor speed of its flight record (they spool up when a
/// pilot sits down and run down on the ground without one), a little faster and louder under load (climbing, tilted hard); at speed a faint disc
/// stands in for the blur of the blades; the generated <see cref="RotorSound"/> follows the same two numbers; and in a low hover the downwash
/// throws up dust, or spray over a water body. All of it is the client's own: nothing here is on the wire beyond the flight record.
/// </summary>
public partial class NetClient
{

    Godot.Collections.Array<Rid>? _washExclude;

    /// <summary>The frame's rotor, sound and downwash of one flying vehicle, drawn at <paramref name="p"/> with attitude <paramref name="rot"/>.</summary>
    void StepFlightFx(CarView v, VehicleDef def, Vector3 p, Vector3 rot, float dt)
    {
        if (v.Rotor == null || FlightOf(v) is not { } s) return;
        var f = def.Flight!;
        var tilt = Mathf.Max(Mathf.Abs(rot.X), Mathf.Abs(rot.Z)) / (f.MaxPitchDeg * FlightMath.Deg2Rad);
        var load = Mathf.Clamp(0.45f + 0.45f * s.Velocity.Y / f.ClimbRate + 0.3f * tilt, 0f, 1f);
        var distance = Sfx.ListenerDistance(p);
        (Vector3, float, bool)? wash = null;
        if (s.Rotor > 0.6f && distance < RotorFx.WashRange && !GameSettings.Current.ReduceMotion && World != null)
        {
            // what is under it: the ground (or a roof), or the surface of a water body above that
            var q = PhysicsRayQueryParameters3D.Create(p + Vector3.Up, p + Vector3.Down * (RotorFx.WashHeight + 1f));
            q.CollisionMask = CollisionLayers.World;
            if (_me != null) q.Exclude = _washExclude ??= new Godot.Collections.Array<Rid> { _me.GetRid() };
            var hit = World.GetWorld3D().DirectSpaceState.IntersectRay(q);
            var surface = Water.BodyAt(p.X, p.Z)?.SurfaceY;
            float? under = hit.Count > 0 ? hit["position"].AsVector3().Y : null;
            var wet = surface is { } sy && (under == null || sy > under);
            if (wet) under = surface;
            if (under is { } y && p.Y - y < RotorFx.WashHeight)
                wash = (new Vector3(p.X, y, p.Z), s.Rotor * (1f - Mathf.Clamp((p.Y - y) / RotorFx.WashHeight, 0f, 1f)), wet);
        }
        v.Rotor.Step(dt, s.Rotor, load, distance, wash);
    }
}
