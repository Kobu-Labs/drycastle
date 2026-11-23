using Godot;
using Drycastle.Shooter.HeadsUp;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Client;

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
    /// <summary>The missile's heading the POV camera uses this frame (the minimap follows it, ticket #150).</summary>
    float _missileViewYaw;
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
        _missiles[e.Id] = new Rocket { Node = RocketFx.Make(Owned), Born = Now, At = Now };
    }

    void OnMissileDespawned(ushort id)
    {
        if (!_missiles.Remove(id, out var r)) return;
        r.Snd?.Release();
        RocketFx.Retire(Owned, r.Node);
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

    /// <summary>
    /// The owner died: the server has ended every streak of theirs (OwnerDied) and sent StreakEnded, but the views must not depend on that one event arriving
    /// (a lost or reordered one left the missile's point of view, or the AC-130's turret def, stuck on). Idempotent with the real StreakEnded.
    /// </summary>
    void ClearStreakViewsOnDeath()
    {
        _controlling = false;
        Ac130Ended();
        HeliStreakEnded(AttackHelicopterStreak.StreakId); // the gunner's clock and kill count (also marks the overlay dirty)
        _uav.Ended();
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

    /// <summary>The POV camera sits on the missile and looks along the owner's aim (kept near the missile's heading), and the overlay reads altitude and time. Back to the body's camera when control ends.</summary>
    void RenderMissilePov()
    {
        if (_me == null || _pov == null) return;
        var id = _controlling ? OwnMissile() : null;
        if (id is not { } mid || !WorldEntities.Entities.TryGetValue(mid, out var e))
        {
            if (_povCam is { Current: true })
            {
                _povCam.Current = false; _me.Camera.Current = true;
                if (_lookBeforeMissile is { } look) { _me.Yaw = look.Yaw; _me.Pitch = look.Pitch; _lookBeforeMissile = null; }
            }
            _pov.SetOn(false);
            _pov.SetMarkers(null, null);
            return;
        }
        _povCam ??= new Camera3D { Fov = 75, Far = 2000 };
        if (!_povCam.IsInsideTree()) Owned.AddChild(_povCam);
        var s = e.State;
        var p = s.Position;
        var tuning = Killstreaks?.Find(AttackMissileStreak.StreakId)?.Missile;
        if (!_povCam.Current)
        {
            // control starts: the aim starts on the missile's heading (not the body's look, which would turn it at once); the body's look comes back at the end
            _lookBeforeMissile = (_me.Yaw, _me.Pitch);
            _me.Yaw = s.YawRad; _me.Pitch = s.PitchRad;
        }
        ClampMissileAim(s.YawRad, tuning);
        // the view is the aim: the mouse turns it at once and the missile turns after it, so the view moves only when the mouse does.
        // The position is the interpolated pose (the state alone steps at the snapshot rate).
        var at = e.Buf.At(RenderTickNow())?.Pos ?? p;
        _povCam.GlobalPosition = new Vector3(at.X, at.Y, at.Z);
        _missileViewYaw = _me.Yaw;
        _povCam.Rotation = new Vector3(_me.Pitch, _me.Yaw, 0); // Godot's YXZ order: the same heading HitMath.Forward gives
        _me.Camera.Current = false;
        _povCam.Current = true;
        _pov.SetOn(true);
        _pov.SetMarkers(_povCam, EnemyMarkerPoints());
        // the estimate: height over the speed's downward part, on flat ground at the body's height (the ground under it is not sent)
        var alt = p.Y - _me.GlobalPosition.Y;
        var missile = Killstreaks?.Find(AttackMissileStreak.StreakId)?.Missile;
        var sink = missile == null ? 0f : missile.SpeedMps * MathF.Sin(-s.PitchRad);
        var eta = sink > 0 ? MathF.Max(0, alt) / sink : float.NaN;
        _pov.SetReadout(alt, Now - _povSince, eta);
    }

    /// <summary>The owner's aim leads the missile by at most this much (yaw), radians: the mouse steers a bounded lead, it never winds up beyond what the missile can follow.</summary>
    const float MissileMaxLeadRad = 20f * MathF.PI / 180f;
    /// <summary>The body's look when the missile took the view, given back when control ends.</summary>
    (float Yaw, float Pitch)? _lookBeforeMissile;

    /// <summary>
    /// Keeps the aim the server steers by (the body's look) near the missile: pitch inside the dive cone, yaw within <see cref="MissileMaxLeadRad"/> of the heading.
    /// Without it the mouse winds the aim up past the turn rate and the dive cone, and the missile keeps turning long after the mouse stopped.
    /// </summary>
    void ClampMissileAim(float missileYaw, MissileTuning? t)
    {
        if (_me == null) return;
        if (t != null) _me.Pitch = Mathf.Clamp(_me.Pitch, t.SteepPitchRad, t.ShallowPitchRad);
        var lead = Mathf.Wrap(_me.Yaw - missileYaw, -Mathf.Pi, Mathf.Pi);
        _me.Yaw = missileYaw + Mathf.Clamp(lead, -MissileMaxLeadRad, MissileMaxLeadRad);
    }

    /// <summary>The blast: the rpg's blast look at the missile's own radius, the shake, and the static on the owner's screen.</summary>
    void OnMissileBlast(MissileBlastInfo b)
    {
        var at = new Vector3(b.Position.X, b.Position.Y, b.Position.Z);
        if (_missiles.Remove(b.Id, out var r)) { r.Snd?.Release(); RocketFx.Retire(Owned, r.Node); }
        RocketFx.Explode(Owned, at, b.Radius);
        _blastDust.BlastFelt(at, b.Radius, SoundRules.Event("explosion", "rpg")); // the rpg's blast sound until the missile has its own
        if (b.Owner == _myId) _pov?.Impact();
    }
}
