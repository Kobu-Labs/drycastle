using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

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
        foreach (var o in _clients.Values.Where(o => o.Joined))
        {
            Send(c, AppearanceEvent(o));
            if (o != c) Send(o, AppearanceEvent(c));
        }
    }

    void OnSaveCamo(Client c, CamoSpec spec)
    {
        var p = c.Profile!;
        if (spec.Validate(CamoData) is { } err)
        {
            Send(c, new ErrorMsg(err));
            return;
        }
        p.Camo = spec;
        if (p.Persist) _ = Progression!.Camo.SaveAsync(p.DbId, spec);
        var ev = AppearanceEvent(c);
        foreach (var o in _clients.Values.Where(o => o.Joined)) Send(o, ev);
    }

    EventMsg AppearanceEvent(Client c) => new((byte)EventKind.Appearance, AppearanceCodec.Encode(c.Id, c.Profile!.Camo));
}
