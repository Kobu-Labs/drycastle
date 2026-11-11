using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// The attack missile on the client (killstreak "attack_missile"): the missile in flight for everyone (rocket model and
/// flight sound, from the replicated world entity), the owner's point-of-view camera while they control it, and the blast.
/// Control is learnt from the owner-only streak events (StreakUsed starts it, StreakEnded ends it), so no body prediction
/// runs while the owner steers: the server holds the body still.
/// </summary>
public partial class NetClient
{
    /// <summary>True from StreakUsed(attack_missile) to its StreakEnded: the owner steers a missile.</summary>
    bool _controlling;
    Camera3D? _povCam;
    MissilePovOverlay? _pov;
    readonly Dictionary<ushort, Rocket> _missiles = new();
    double _povSince;

    /// <summary>Called once from <see cref="_Ready"/>: registers the type, follows the missile entities and builds the POV overlay.</summary>
    void InitMissile()
    {
        AttackMissileRules.Register(WorldEntityRegistry.Shared);
        WorldEntities.Spawned += OnMissileSpawned;
        WorldEntities.Despawned += OnMissileDespawned;
        _pov = new MissilePovOverlay();
        AddChild(_pov);
    }

    void OnMissileSpawned(WorldReplica.Entity e)
    {
        if (e.Type != AttackMissileRules.TypeId) return;
        _missiles[e.Id] = new Rocket { Node = RocketFx.Make(World), Born = Now, At = Now };
    }

    void OnMissileDespawned(ushort id)
    {
        if (!_missiles.Remove(id, out var r)) return;
        r.Snd?.Release();
        RocketFx.Retire(World, r.Node);
    }

    /// <summary>Owner-only: the streak was used. The attack missile hands the owner its point of view.</summary>
    void OnStreakUsed(string streakId)
    {
        if (streakId == Ac130Streak.StreakId) Ac130Used();
        if (streakId != AttackMissileStreak.StreakId) return;
        _controlling = true;
        _povSince = Now;
    }

    /// <summary>Owner-only: the streak ended (impact, timeout, or death). The point of view goes back to the body.</summary>
    void OnStreakEnded(string streakId)
    {
        if (streakId == Ac130Streak.StreakId) Ac130Ended();
        if (streakId == AttackMissileStreak.StreakId) _controlling = false;
    }

    /// <summary>Places the missiles from the replicated state (dead-reckoning is not needed: the state is the full pose), with their flight sound.</summary>
    void RenderMissiles()
    {
        foreach (var (id, r) in _missiles)
        {
            if (r.Node == null || !WorldEntities.Entities.TryGetValue(id, out var e)) continue;
            var s = e.State;
            var p = s.Position;
            var at = new Vector3(p.X, p.Y, p.Z);
            var d = HitMath.Forward(s.YawRad, s.PitchRad);
            r.Node.GlobalPosition = at;
            if (Mathf.Abs(d.Y) < 0.99f) r.Node.LookAt(at + new Vector3(d.X, d.Y, d.Z));
            // the owner sees the view, not the model
            r.Node.Visible = s.Owner != _myId;
            SfxRocket(r, at);
        }
    }

    /// <summary>The owner's missile, if it is up (the one the point of view follows).</summary>
    ushort? OwnMissile()
    {
        foreach (var (id, e) in WorldEntities.Entities)
            if (e.Type == AttackMissileRules.TypeId && e.State.Owner == _myId && _missiles.ContainsKey(id)) return id;
        return null;
    }

    /// <summary>The POV camera follows the missile's real pose (the turn rate shows), and the overlay reads altitude and time. Back to the body's camera when control ends.</summary>
    void RenderMissilePov()
    {
        if (_me == null || _pov == null) return;
        var id = _controlling ? OwnMissile() : null;
        if (id is not { } mid || !WorldEntities.Entities.TryGetValue(mid, out var e))
        {
            if (_povCam is { Current: true }) { _povCam.Current = false; _me.Camera.Current = true; }
            _pov.SetOn(false);
            return;
        }
        _povCam ??= new Camera3D { Fov = 75, Far = 2000 };
        if (!_povCam.IsInsideTree()) World.AddChild(_povCam);
        var s = e.State;
        var p = s.Position;
        _povCam.GlobalPosition = new Vector3(p.X, p.Y, p.Z);
        _povCam.Rotation = new Vector3(s.PitchRad, s.YawRad, 0); // Godot's YXZ order: the same heading HitMath.Forward gives
        _me.Camera.Current = false;
        _povCam.Current = true;
        _pov.SetOn(true);
        _pov.SetReadout(p.Y - _me.GlobalPosition.Y, Now - _povSince);
    }

    /// <summary>The blast: the bazooka's blast look at the missile's own radius, the shake, and the static on the owner's screen.</summary>
    void OnMissileBlast(MissileBlastInfo b)
    {
        var at = new Vector3(b.Position.X, b.Position.Y, b.Position.Z);
        if (_missiles.Remove(b.Id, out var r)) { r.Snd?.Release(); RocketFx.Retire(World, r.Node); }
        if (Sfx.Enabled) Sfx.Play3D(SoundRules.Event("explosion", "bazooka"), at); // the bazooka's blast sound until the missile has its own
        RocketFx.Explode(World, at, b.Radius);
        ShakeFor(at, b.Radius);
        if (b.Owner == _myId) _pov?.Impact();
    }
}
