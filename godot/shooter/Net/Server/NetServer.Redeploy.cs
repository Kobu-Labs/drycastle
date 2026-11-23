using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Progression;

namespace Drycastle.Shooter.Net.Server;

/// <summary>The player's own Respawn (ticket #284): selects the named loadout slot, then kills the player as a suicide; the normal respawn applies the loadout.</summary>
public partial class NetServer
{
    void OnRedeploy(Client c, byte[] payload)
    {
        if (!Redeploy.TryDecode(payload, out var slot)) return;
        if (c.Car == null && _tick <= c.HeldUntilTick) return; // in the Bigfoot's hand (the demon's pounce is the match's Held)
        if (!_match.CanForceDeath(c.Id)) return;
        if (slot is { } s)
        {
            if (LoadoutRules.ValidateSlot(s) is { } err) { Send(c, new ErrorMsg(err)); return; }
            if (s != c.Profile!.Active && !AllowSave(c)) return; // a changed slot is a write: the same rate limit as selecting it
            OnSelectSlot(c, s);
        }
        _match.ForceDeath(c.Id);
    }
}
