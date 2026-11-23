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
internal sealed class MissileClient
{
    readonly ClientContext _ctx;
    readonly BlastDustClient _blastDust;
    readonly EnemyChestPoints _markers;

    public MissileClient(ClientContext ctx, BlastDustClient blastDust, EnemyChestPoints markers)
    {
        _ctx = ctx; _blastDust = blastDust; _markers = markers;
        // registers the type, follows the missile entities and builds the POV overlay
        AttackMissileRules.Register(WorldEntityRegistry.Shared);
        ctx.WorldEntities.Spawned += OnSpawned;
        ctx.WorldEntities.Despawned += OnDespawned;
        _pov = new MissilePovOverlay();
        ctx.Root.AddChild(_pov);
    }

    /// <summary>The owner steers a missile: the view is the missile's.</summary>
    public bool Controlling => _controlling;

    /// <summary>The missile's POV camera is the current one.</summary>
    public bool PovOn => _povCam is { Current: true };

    /// <summary>The missile's heading the POV camera uses this frame (the minimap follows it, ticket #150).</summary>
    public float ViewYaw => _missileViewYaw;

    /// <summary>Leaving: the missiles go and the camera with the owned node.</summary>
    public void Clear() { _missiles.Clear(); _povCam = null; }


    /// <summary>True from StreakUsed(attack_missile) to its StreakEnded: the owner steers a missile.</summary>
    bool _controlling;
    Camera3D? _povCam;
    /// <summary>The missile's heading the POV camera uses this frame (the minimap follows it, ticket #150).</summary>
    float _missileViewYaw;
    MissilePovOverlay? _pov;
    readonly Dictionary<ushort, RocketView> _missiles = new();
    double _povSince;

    void OnSpawned(WorldReplica.Entity e)
    {
        if (e.Type != AttackMissileRules.TypeId) return;
        _missiles[e.Id] = new RocketView { Node = RocketFx.Make(_ctx.Owned), Born = _ctx.Now, At = _ctx.Now };
    }

    void OnDespawned(ushort id)
    {
        if (!_missiles.Remove(id, out var r)) return;
        r.Snd?.Release();
        RocketFx.Retire(_ctx.Owned, r.Node);
    }

    /// <summary>Owner-only: the streak was used. The attack missile hands the owner its point of view.</summary>
    public void OnStreakUsed(string streakId)
    {
        if (streakId != AttackMissileStreak.StreakId) return;
        _controlling = true;
        _povSince = _ctx.Now;
    }

    /// <summary>Owner-only: the streak ended (impact, timeout, or death). The point of view goes back to the body.</summary>
    public void OnStreakEnded(string streakId)
    {
        if (streakId == AttackMissileStreak.StreakId) _controlling = false;
    }

    /// <summary>
    /// The owner died: the server has ended every streak of theirs and sent StreakEnded, but the view must not depend on that one event arriving
    /// (a lost or reordered one left the missile's point of view stuck on). Idempotent with the real StreakEnded.
    /// </summary>
    public void OnOwnerDied()
    {
        _controlling = false;
    }

    /// <summary>Places the missiles from the replicated state (dead-reckoning is not needed: the state is the full pose), with their flight sound.</summary>
    public void RenderMissiles()
    {
        foreach (var (id, r) in _missiles)
        {
            if (r.Node == null || !_ctx.WorldEntities.Entities.TryGetValue(id, out var e)) continue;
            var s = e.State;
            var p = s.Position;
            var at = new Vector3(p.X, p.Y, p.Z);
            var d = HitMath.Forward(s.YawRad, s.PitchRad);
            r.Node.GlobalPosition = at;
            if (Mathf.Abs(d.Y) < 0.99f) r.Node.LookAt(at + new Vector3(d.X, d.Y, d.Z));
            // the owner sees the view, not the model
            r.Node.Visible = s.Owner != _ctx.MyId;
            r.PlayFlightLoop(at);
        }
    }

    /// <summary>The owner's missile, if it is up (the one the point of view follows).</summary>
    ushort? OwnMissile()
    {
        foreach (var (id, e) in _ctx.WorldEntities.Entities)
            if (e.Type == AttackMissileRules.TypeId && e.State.Owner == _ctx.MyId && _missiles.ContainsKey(id)) return id;
        return null;
    }

    /// <summary>The POV camera sits on the missile and looks along the owner's aim (kept near the missile's heading), and the overlay reads altitude and time. Back to the body's camera when control ends.</summary>
    public void RenderPov()
    {
        if (_ctx.Me == null || _pov == null) return;
        var id = _controlling ? OwnMissile() : null;
        if (id is not { } mid || !_ctx.WorldEntities.Entities.TryGetValue(mid, out var e))
        {
            if (_povCam is { Current: true })
            {
                _povCam.Current = false; _ctx.Me.Camera.Current = true;
                if (_lookBeforeMissile is { } look) { _ctx.Me.Yaw = look.Yaw; _ctx.Me.Pitch = look.Pitch; _lookBeforeMissile = null; }
            }
            _pov.SetOn(false);
            _pov.SetMarkers(null, null);
            return;
        }
        _povCam ??= new Camera3D { Fov = 75, Far = 2000 };
        if (!_povCam.IsInsideTree()) _ctx.Owned.AddChild(_povCam);
        var s = e.State;
        var p = s.Position;
        var tuning = _ctx.Killstreaks?.Find(AttackMissileStreak.StreakId)?.Missile;
        if (!_povCam.Current)
        {
            // control starts: the aim starts on the missile's heading (not the body's look, which would turn it at once); the body's look comes back at the end
            _lookBeforeMissile = (_ctx.Me.Yaw, _ctx.Me.Pitch);
            _ctx.Me.Yaw = s.YawRad; _ctx.Me.Pitch = s.PitchRad;
        }
        ClampMissileAim(s.YawRad, tuning);
        // the view is the aim: the mouse turns it at once and the missile turns after it, so the view moves only when the mouse does.
        // The position is the interpolated pose (the state alone steps at the snapshot rate).
        var at = e.Buf.At(_ctx.RenderTick)?.Pos ?? p;
        _povCam.GlobalPosition = new Vector3(at.X, at.Y, at.Z);
        _missileViewYaw = _ctx.Me.Yaw;
        _povCam.Rotation = new Vector3(_ctx.Me.Pitch, _ctx.Me.Yaw, 0); // Godot's YXZ order: the same heading HitMath.Forward gives
        _ctx.Me.Camera.Current = false;
        _povCam.Current = true;
        _pov.SetOn(true);
        _pov.SetMarkers(_povCam, _markers.Points());
        // the estimate: height over the speed's downward part, on flat ground at the body's height (the ground under it is not sent)
        var alt = p.Y - _ctx.Me.GlobalPosition.Y;
        var missile = _ctx.Killstreaks?.Find(AttackMissileStreak.StreakId)?.Missile;
        var sink = missile == null ? 0f : missile.SpeedMps * MathF.Sin(-s.PitchRad);
        var eta = sink > 0 ? MathF.Max(0, alt) / sink : float.NaN;
        _pov.SetReadout(alt, _ctx.Now - _povSince, eta);
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
        if (_ctx.Me == null) return;
        if (t != null) _ctx.Me.Pitch = Mathf.Clamp(_ctx.Me.Pitch, t.SteepPitchRad, t.ShallowPitchRad);
        var lead = Mathf.Wrap(_ctx.Me.Yaw - missileYaw, -Mathf.Pi, Mathf.Pi);
        _ctx.Me.Yaw = missileYaw + Mathf.Clamp(lead, -MissileMaxLeadRad, MissileMaxLeadRad);
    }

    /// <summary>The blast: the rpg's blast look at the missile's own radius, the shake, and the static on the owner's screen.</summary>
    public void OnBlast(MissileBlastInfo b)
    {
        var at = new Vector3(b.Position.X, b.Position.Y, b.Position.Z);
        if (_missiles.Remove(b.Id, out var r)) { r.Snd?.Release(); RocketFx.Retire(_ctx.Owned, r.Node); }
        RocketFx.Explode(_ctx.Owned, at, b.Radius);
        _blastDust.BlastFelt(at, b.Radius, SoundRules.Event("explosion", "rpg")); // the rpg's blast sound until the missile has its own
        if (b.Owner == _ctx.MyId) _pov?.Impact();
    }
}
