using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Camouflage on the server (ticket #140). The join sends every joined player's appearance to the joiner and the joiner's
/// to everyone else. A client's change is validated against camo.json (an error reply and no state change when invalid),
/// kept on the profile, saved through <c>ICamoRepository</c> for a persisted player, and broadcast to all clients.
/// </summary>
public partial class NetServer
{
    /// <summary>camo.json as loaded by the host (set in <c>Shooter.BuildServer</c>).</summary>
    public CamoFile CamoData = null!;

    /// <summary>Sends each joined player's look to the joiner, and the joiner's look to every other player.</summary>
    void SendAppearancesOnJoin(Client c)
    {
        foreach (var o in _byId.Values)
        {
            Send(c, AppearanceEvent(o));
            if (o != c) Send(o, AppearanceEvent(c));
        }
    }

    /// <summary>Saves one loadout slot's look (#141). Only the active slot is broadcast: it is the look others see.</summary>
    void OnSaveCamo(Client c, int slot, CamoSpec spec)
    {
        var p = c.Profile!;
        if (LoadoutRules.ValidateSlot(slot) is { } slotErr)
        {
            Send(c, new ErrorMsg(slotErr));
            return;
        }
        if (spec.Validate(CamoData) is { } err)
        {
            Send(c, new ErrorMsg(err));
            return;
        }
        p.Camos[slot] = spec;
        if (p.Persist) AfterWrite(Progression!.Camo.SaveAsync(p.DbId, slot, spec), ok => { if (!ok) Send(c, new ErrorMsg("Could not save your camouflage, try again in a moment.")); }, c);
        SendProfile(c);
        if (slot == p.Active) BroadcastAppearance(c);
    }

    void BroadcastAppearance(Client c)
    {
        var ev = AppearanceEvent(c);
        foreach (var o in _byId.Values) Send(o, ev);
    }

    EventMsg AppearanceEvent(Client c) => new((byte)EventKind.Appearance, AppearanceCodec.Encode(c.Id, c.Profile!.Camo));
}
