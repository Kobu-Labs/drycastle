using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Effects;
using Drycastle.Shooter.Scenery;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Effects;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The dust of a blast as this player meets it (#345). <see cref="BlastFelt"/> is the sound and the shake of every blast: at once for a small one, and for a
/// large one when its Blast wave gets here (<see cref="DustRules.FeltDelay"/>), so the wave is seen coming and then hits. <see cref="UpdateDust"/> lays the
/// veil over the screen from the dust at the camera (<see cref="DustFx.Field"/>): a wall between the camera and the blast keeps most of it out.
/// A friend's name tag is hidden behind dust (<see cref="DustHides"/>). Visual only: nothing here is sent or changes a hit.
/// </summary>
public partial class NetClient
{
    /// <summary>Share of a blast's dust that gets to a camera with a wall between it and the blast.</summary>
    const float DustBehindWall = 0.3f;

    readonly List<(double At, Vector3 Pos, float Radius, string? Sound)> _blastsDue = new();
    EarRingFx? _earRing;
    PhysicsRayQueryParameters3D? _dustRay;
    Func<DustBlast, float>? _dustReach;
    Vector3 _dustEye;

    /// <summary>The sound (none when null) and the camera shake of a blast: now, or when its Blast wave (leaving at <paramref name="waveSpeed"/>, the dust's own when zero) reaches the camera.</summary>
    void BlastFelt(Vector3 at, float radius, string? sound, float waveSpeed = 0f)
    {
        var eye = Sfx.ListenerPosition(_me?.GlobalPosition ?? at);
        var wait = DustRules.FeltDelay(radius, new Vector2(eye.X - at.X, eye.Z - at.Z).Length(), DustRules.SpeedOr(waveSpeed));
        if (wait > 0.03f && _blastsDue.Count < 32) { _blastsDue.Add((Now + wait, at, radius, sound)); return; }
        FeelBlast(at, radius, sound);
    }

    void FeelBlast(Vector3 at, float radius, string? sound)
    {
        if (sound != null && Sfx.Enabled) Sfx.Play3D(sound, at);
        ShakeFor(at, radius);
        RingFrom(at, radius);
    }

    /// <summary>The ear ringing (#481) when this player stands within the blast's damage radius (<paramref name="radius"/> is the one the server used). Only the local player hears it.</summary>
    void RingFrom(Vector3 at, float radius)
    {
        if (_me == null || !Sfx.Enabled) return; // a kill by the blast still rings through the death view; respawn clears it
        if (_earRing == null) { _earRing = new EarRingFx { Name = "EarRing" }; AddChild(_earRing); }
        _earRing.Blast(at, _me.GlobalPosition, radius);
    }

    /// <summary>Every frame: the blasts whose wave has arrived are heard and felt, and the veil follows the dust at the camera.</summary>
    void UpdateDust()
    {
        for (var i = _blastsDue.Count - 1; i >= 0; i--)
        {
            var b = _blastsDue[i];
            if (b.At > Now) continue;
            _blastsDue.RemoveAt(i);
            FeelBlast(b.Pos, b.Radius, b.Sound);
        }
        var night = DayNightCycle.Current?.NightFactor ?? 0f;
        DustFx.SetNight(night);
        if (_hud == null) return;
        var field = DustFx.Field;
        if (field.Count == 0 || World.GetViewport()?.GetCamera3D() is not { } cam) { _hud.SetDust(0f, 0f); return; }
        field.Prune(Now);
        _dustEye = cam.GlobalPosition;
        _dustReach ??= DustReach;
        // the waves follow the ground: the camera's height over the ground under it (one ray), not over the blast
        _dustRay ??= new PhysicsRayQueryParameters3D { CollisionMask = CollisionLayers.World };
        _dustRay.From = _dustEye; _dustRay.To = _dustEye + Vector3.Down * 60f;
        var under = World.GetWorld3D().DirectSpaceState.IntersectRay(_dustRay);
        float? above = under.Count == 0 ? null : _dustEye.Y - under["position"].AsVector3().Y;
        _hud.SetDust(field.Veil(new NVec3(_dustEye.X, _dustEye.Y, _dustEye.Z), Now, _dustReach, above), night);
    }

    /// <summary>How much of a blast's dust gets to the camera: all of it in the open, <see cref="DustBehindWall"/> with the world (ground, a building) between them. One ray, only for a blast with dust at the camera.</summary>
    float DustReach(DustBlast b)
    {
        var to = new Vector3(b.At.X, b.At.Y + 1f, b.At.Z);
        if (_dustEye.DistanceSquaredTo(to) < 1f) return 1f;
        _dustRay ??= new PhysicsRayQueryParameters3D { CollisionMask = CollisionLayers.World };
        _dustRay.From = _dustEye; _dustRay.To = to;
        return World.GetWorld3D().DirectSpaceState.IntersectRay(_dustRay).Count == 0 ? 1f : DustBehindWall;
    }

    /// <summary>True when dust stands between the camera and <paramref name="point"/> (or around either): what is there cannot be made out, so its tag is not drawn.</summary>
    bool DustHides(Vector3 point)
    {
        var field = DustFx.Field;
        if (field.Count == 0 || World.GetViewport()?.GetCamera3D() is not { } cam) return false;
        var e = cam.GlobalPosition;
        return field.Sight(new NVec3(e.X, e.Y, e.Z), new NVec3(point.X, point.Y, point.Z), Now) >= DustRules.SightBlocked;
    }
}
