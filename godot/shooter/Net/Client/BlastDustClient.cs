using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The dust of a blast as this player meets it (#345). <see cref="BlastFelt"/> is the sound and the shake of every blast: at once for a small one, and for a
/// large one when its Blast wave gets here (<see cref="DustRules.FeltDelay"/>), so the wave is seen coming and then hits. <see cref="Update"/> lays the
/// veil over the screen from the dust at the camera (<see cref="DustFx.Field"/>): a wall between the camera and the blast keeps most of it out.
/// A friend's name tag is hidden behind dust (<see cref="Hides"/>). Visual only: nothing here is sent or changes a hit.
/// </summary>
public sealed class BlastDustClient
{
    /// <summary>Share of a blast's dust that gets to a camera with a wall between it and the blast.</summary>
    const float BehindWall = 0.3f;

    readonly ClientContext _ctx;
    readonly List<(double At, Vector3 Pos, float Radius, string? Sound)> _due = new();
    EarRingFx? _earRing;
    PhysicsRayQueryParameters3D? _ray;
    Func<DustBlast, float>? _reach;
    Vector3 _eye;

    public BlastDustClient(ClientContext ctx) { _ctx = ctx; }

    /// <summary>The sound (none when null) and the camera shake of a blast: now, or when its Blast wave (leaving at <paramref name="waveSpeed"/>, the dust's own when zero) reaches the camera.</summary>
    public void BlastFelt(Vector3 at, float radius, string? sound, float waveSpeed = 0f)
    {
        var eye = Sfx.ListenerPosition(_ctx.Me?.GlobalPosition ?? at);
        var wait = DustRules.FeltDelay(radius, new Vector2(eye.X - at.X, eye.Z - at.Z).Length(), DustRules.SpeedOr(waveSpeed));
        if (wait > 0.03f && _due.Count < 32) { _due.Add((_ctx.Now + wait, at, radius, sound)); return; }
        Feel(at, radius, sound);
    }

    void Feel(Vector3 at, float radius, string? sound)
    {
        if (sound != null && Sfx.Enabled) Sfx.Play3D(sound, at);
        _ctx.Shake.At(at, radius);
        RingFrom(at, radius);
    }

    /// <summary>The ear ringing (#481) when this player stands within the blast's damage radius (<paramref name="radius"/> is the one the server used). Only the local player hears it.</summary>
    public void RingFrom(Vector3 at, float radius)
    {
        if (_ctx.Me == null || !Sfx.Enabled) return; // a kill by the blast still rings through the death view; respawn clears it
        if (_earRing == null) { _earRing = new EarRingFx { Name = "EarRing" }; _ctx.Root.AddChild(_earRing); }
        _earRing.Blast(at, _ctx.Me.GlobalPosition, radius);
    }

    /// <summary>A respawn ends the ringing.</summary>
    public void ClearRinging() => _earRing?.Clear();

    /// <summary>Every frame: the blasts whose wave has arrived are heard and felt, and the veil follows the dust at the camera.</summary>
    public void Update()
    {
        var now = _ctx.Now;
        for (var i = _due.Count - 1; i >= 0; i--)
        {
            var b = _due[i];
            if (b.At > now) continue;
            _due.RemoveAt(i);
            Feel(b.Pos, b.Radius, b.Sound);
        }
        var night = DayNightCycle.Current?.NightFactor ?? 0f;
        DustFx.SetNight(night);
        if (_ctx.Hud is not { } hud) return;
        var field = DustFx.Field;
        if (field.Count == 0 || _ctx.World.GetViewport()?.GetCamera3D() is not { } cam) { hud.SetDust(0f, 0f); return; }
        field.Prune(now);
        _eye = cam.GlobalPosition;
        _reach ??= Reach;
        // the waves follow the ground: the camera's height over the ground under it (one ray), not over the blast
        _ray ??= new PhysicsRayQueryParameters3D { CollisionMask = CollisionLayers.World };
        _ray.From = _eye; _ray.To = _eye + Vector3.Down * 60f;
        var under = _ctx.World.GetWorld3D().DirectSpaceState.IntersectRay(_ray);
        float? above = under.Count == 0 ? null : _eye.Y - under["position"].AsVector3().Y;
        hud.SetDust(field.Veil(new NVec3(_eye.X, _eye.Y, _eye.Z), now, _reach, above), night);
    }

    /// <summary>How much of a blast's dust gets to the camera: all of it in the open, <see cref="BehindWall"/> with the world (ground, a building) between them. One ray, only for a blast with dust at the camera.</summary>
    float Reach(DustBlast b)
    {
        var to = new Vector3(b.At.X, b.At.Y + 1f, b.At.Z);
        if (_eye.DistanceSquaredTo(to) < 1f) return 1f;
        _ray ??= new PhysicsRayQueryParameters3D { CollisionMask = CollisionLayers.World };
        _ray.From = _eye; _ray.To = to;
        return _ctx.World.GetWorld3D().DirectSpaceState.IntersectRay(_ray).Count == 0 ? 1f : BehindWall;
    }

    /// <summary>True when dust stands between the camera and <paramref name="point"/> (or around either): what is there cannot be made out, so its tag is not drawn.</summary>
    public bool Hides(Vector3 point)
    {
        var field = DustFx.Field;
        if (field.Count == 0 || _ctx.World.GetViewport()?.GetCamera3D() is not { } cam) return false;
        var e = cam.GlobalPosition;
        return field.Sight(new NVec3(e.X, e.Y, e.Z), new NVec3(point.X, point.Y, point.Z), _ctx.Now) >= DustRules.SightBlocked;
    }
}
