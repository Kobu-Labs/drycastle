using Godot;
using Drycastle.Shooter.Characters;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Hud;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The ragdoll phase of each player a Bigfoot picked up (#360) or a vehicle threw alive (#411): held in its fist, flying, lying, getting up (<see cref="BodyFlingTracker"/>).
/// The Bigfoot says who it holds and lets go, the remote figures and the own body ask for the phase. Another player in a phase is posed here; the own body only needs
/// the phase (the eye and the hidden arms).
/// </summary>
internal sealed class BodyFlings
{
    /// <summary>How long a player who landed alive stays a ragdoll turning upright (the tumble's own settle is quicker; this ends it for the own body, which is not drawn).</summary>
    const float GetUpSeconds = 0.4f;

    readonly ClientContext _ctx;
    /// <summary>The ragdoll phase of each player the Bigfoot picked up or a vehicle threw. Absent or None: the normal figure.</summary>
    readonly Dictionary<ushort, BodyFlingTracker> _flings = new();
    /// <summary>The giant that holds each player now: the hand's shake the body hangs in.</summary>
    readonly Dictionary<ushort, BigfootView> _holders = new();
    Vector3 _ownLast;

    public BodyFlings(ClientContext ctx) { _ctx = ctx; }

    /// <summary>This client's own body is in the fist or flying: its arms and weapon are not drawn.</summary>
    public bool OwnFlung => _ctx.MyId != 0 && _flings.TryGetValue(_ctx.MyId, out var f) && f.Phase is BodyFling.Held or BodyFling.Flying or BodyFling.Lying;

    public BodyFlingTracker Of(ushort id)
    {
        if (!_flings.TryGetValue(id, out var f)) _flings[id] = f = new BodyFlingTracker();
        return f;
    }

    /// <summary>The phase of a player who is in one (held, flying or getting up); false for the normal figure.</summary>
    public bool TryActive(ushort id, out BodyFlingTracker flung) => _flings.TryGetValue(id, out flung!) && flung.Active;

    /// <summary>The giant takes a player into its hand.</summary>
    public void Grab(ushort player, BigfootView giant)
    {
        _holders[player] = giant;
        Of(player).Grabbed();
    }

    /// <summary>The hold is over: a launch throws the player, anything else (the hold cut short, the giant dead or gone) lets them drop.</summary>
    public void Release(ushort player, bool thrown)
    {
        _holders.Remove(player);
        if (thrown) Of(player).Flung(); else Of(player).Ended();
    }

    /// <summary>A killed, respawned or departed player is no longer a ragdoll of this machine (a corpse is its own entity).</summary>
    public void End(ushort id)
    {
        if (_flings.TryGetValue(id, out var f) && f.Active) { f.Ended(); _holders.Remove(id); }
    }

    /// <summary>The own body only needs its phase (the eye and the hidden arms): the landing is read from the predicted body's speed.</summary>
    public void TickOwn(double delta)
    {
        if (_ctx.MyId == 0 || _ctx.Me == null || !_flings.TryGetValue(_ctx.MyId, out var f) || !f.Active) { if (_ctx.Me != null) _ownLast = _ctx.Me.GlobalPosition; return; }
        if (_ctx.Dead) { f.Ended(); return; }
        var dt = (float)delta;
        var at = _ctx.Me.GlobalPosition;
        f.Tick(dt, dt > 0 ? (at - _ownLast).Length() / dt : 0, _ctx.Me.State.OnFloor);
        _ownLast = at;
        if (f.Phase == BodyFling.Landing && f.Elapsed >= GetUpSeconds) f.StoodUp();
    }

    /// <summary>
    /// Another player the Bigfoot holds or has thrown (#360): a ragdoll the whole time. In the fist it hangs in the hand's shake (the same the held player's
    /// own eye rides) with its limbs flailing; thrown it tumbles about its middle (<see cref="RagdollTumble"/>) until it lands, then turns upright and goes
    /// slack, and the normal figure takes over without a pop (a turn is a whole circle, the pivot height at upright is the body's middle).
    /// </summary>
    public void PoseRemote(ushort id, RemotePlayer r, BodyFlingTracker f, Vector3 at, float yaw, float dt)
    {
        var t = r.Tumble;
        var speed = dt > 0 ? (at - r.TumbleLast).Length() / dt : 0;
        r.TumbleLast = at;
        f.Tick(dt, speed, _ctx.Map is not { } map || at.Y - map.GroundHeight(at.X, at.Z) < 0.35f);
        t.Thrown = true; t.StandUp = f.Phase == BodyFling.Landing; // lying on the ground first, then it turns upright // the tumble owns the ragdoll flag: when the machine is over, TumbleRemote's normal path puts the figure back
        var middle = Vector3.Up * RagdollTumble.Half;
        if (f.Phase == BodyFling.Held && _holders.TryGetValue(id, out var h))
        {
            var local = h.Node.ToLocal(at);
            var tm = (float)(_ctx.Now - h.Since);
            var hand = h.Node.GlobalBasis;
            var shaken = h.Pivot.Transform.Basis * local + new Vector3(0, 0.45f * MathF.Sin(tm * 22f), 0.3f * MathF.Sin(tm * 11f + 1.3f));
            var tilt = h.Pivot.Rotation.Z * 1.6f + 0.1f * MathF.Sin(tm * 17f);
            t.Amount = 1f; t.Spin = 0;
            r.View?.SetRagdoll(true, 1f, false);
            if (r.View != null) r.View.Position = -middle;
            r.Node.GlobalPosition = at + hand * (shaken - local) + middle;
            r.Node.Basis = new Basis(hand.Z.Normalized(), tilt) * new Basis(hand.X.Normalized(), h.Pivot.Rotation.X * 2f) * new Basis(Vector3.Up, yaw);
            return;
        }
        t.Update(at, f.Phase == BodyFling.Flying, dt);
        if (f.Phase == BodyFling.Landing && t.AtRest) f.StoodUp();
        r.View?.SetRagdoll(true, t.Amount, t.AtRest);
        if (r.View != null) r.View.Position = -middle;
        r.Node.GlobalPosition = at + Vector3.Up * t.PivotHeight;
        r.Node.Basis = t.BasisFor(yaw);
    }
}
