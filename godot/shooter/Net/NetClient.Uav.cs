using Godot;
using Suchohrad.ShooterCore;
using Vector2 = Godot.Vector2;

namespace Suchohrad.Shooter;

/// <summary>
/// The client side of the UAV (issue #121). The owner gets a <see cref="EventKind.UavPing"/> every second with the enemy
/// positions the server allows (the Ghost perk is left out there) and the seconds left. Pings go on the minimap and the
/// countdown on the HUD. The StreakEnded event for the UAV clears both.
/// </summary>
public partial class NetClient
{
    /// <summary>When the owner's UAV ends, on the Time.GetTicksMsec()/1000 clock; 0 when none is up.</summary>
    double _uavEndsAt;

    void UavEvent(EventKind kind, byte[] payload)
    {
        if (_hud == null) return;
        var now = Time.GetTicksMsec() / 1000.0;
        switch (kind)
        {
            case EventKind.UavPing when UavEvents.Decode(payload) is { } p:
                _uavEndsAt = now + p.SecondsLeft;
                var dots = new Vector2[p.Contacts.Count];
                for (var i = 0; i < dots.Length; i++) dots[i] = new Vector2(p.Contacts[i].X, p.Contacts[i].Y);
                // the pings outlive one pulse by a little, so a late pulse does not blink them off
                _hud.Minimap.SetEnemyPings(dots, now + UavRules.PulseSeconds + 0.5);
                if (Sfx.Enabled) Sfx.Ui("uav.ping");
                _hud.SetUavSeconds(p.SecondsLeft);
                break;
            case EventKind.StreakEnded when KillstreakEvents.DecodeEnded(payload) is { } e && e.StreakId == UavStreak.StreakId:
                _uavEndsAt = 0;
                _hud.Minimap.ClearEnemyPings();
                _hud.SetUavSeconds(0);
                break;
        }
    }

    /// <summary>Per frame: counts the HUD's UAV countdown down between pulses (the label changes once a second at most).</summary>
    void UavTick()
    {
        if (_hud == null || _uavEndsAt == 0) return;
        _hud.SetUavSeconds((float)(_uavEndsAt - Time.GetTicksMsec() / 1000.0));
    }
}
