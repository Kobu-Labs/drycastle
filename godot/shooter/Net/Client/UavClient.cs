using Drycastle.Shooter.Audio;
using Drycastle.ShooterCore.Killstreaks.Uav;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Net;
using Vector2 = Godot.Vector2;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The client side of the UAV (issue #121). The owner gets a <see cref="EventKind.UavPing"/> every second with the enemy
/// positions the server allows (the Ghost perk is left out there) and the seconds left. Pings go on the minimap and the
/// countdown on the HUD. The StreakEnded event for the UAV clears both.
/// </summary>
public sealed class UavClient
{
    readonly ClientContext _ctx;
    /// <summary>When the owner's UAV ends, on the <see cref="ClientContext.Now"/> clock; 0 when none is up.</summary>
    double _endsAt;

    public UavClient(ClientContext ctx) { _ctx = ctx; }

    public void OnEvent(EventKind kind, byte[] payload)
    {
        if (_ctx.Hud is not { } hud) return;
        switch (kind)
        {
            case EventKind.UavPing when UavEvents.Decode(payload) is { } p:
                var now = _ctx.Now;
                _endsAt = now + p.SecondsLeft;
                var dots = new Vector2[p.Contacts.Count];
                for (var i = 0; i < dots.Length; i++) dots[i] = new Vector2(p.Contacts[i].X, p.Contacts[i].Y);
                // the pings outlive one pulse by a little, so a late pulse does not blink them off
                hud.Minimap.SetEnemyPings(dots, now + UavRules.PulseSeconds + 0.5);
                if (Sfx.Enabled) Sfx.Ui("uav.ping");
                hud.SetUavSeconds(p.SecondsLeft);
                break;
            case EventKind.StreakEnded when KillstreakEvents.DecodeEnded(payload) is { } e && e.StreakId == UavStreak.StreakId:
                Ended();
                break;
        }
    }

    /// <summary>The owner's UAV is over (StreakEnded, or the owner died): no countdown, no pings.</summary>
    public void Ended()
    {
        _endsAt = 0;
        _ctx.Hud?.Minimap.ClearEnemyPings();
        _ctx.Hud?.SetUavSeconds(0);
    }

    /// <summary>Per frame: counts the HUD's UAV countdown down between pulses (the label changes once a second at most).</summary>
    public void Tick()
    {
        if (_ctx.Hud == null || _endsAt == 0) return;
        _ctx.Hud.SetUavSeconds((float)(_endsAt - _ctx.Now));
    }
}
