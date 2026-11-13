using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Camouflage on the client (ticket #140): the server's appearance broadcasts are kept per player id and applied to that
/// player's character when it exists (a look can arrive before the first snapshot). <see cref="SaveCamo"/> sends the
/// player's own change; the server answers with a broadcast, or an error.
/// </summary>
public partial class NetClient
{
    /// <summary>camo.json as loaded by the host.</summary>
    public CamoFile CamoData = null!;

    /// <summary>The latest look per player id.</summary>
    public readonly Dictionary<ushort, CamoSpec> Appearances = new();

    /// <summary>The local player's own Camouflage as the server last announced it, or null before that (the Equipment screen's figure).</summary>
    public CamoSpec? OwnLook => Appearances.TryGetValue(_myId, out var look) ? look : null;

    /// <summary>Fired when a player's look arrives or changes (including this player's own).</summary>
    public event Action<ushort, CamoSpec>? AppearanceChanged;

    /// <summary>
    /// Asks the server to replace one loadout slot's camouflage (#141). Answered with the new <see cref="ProfileMsg"/>
    /// (its <c>Camos</c> carry the saved look), and, for the active slot, an appearance broadcast; or an error.
    /// </summary>
    public bool SaveCamo(int slot, CamoSpec spec)
    {
        if (_peer == null || !_welcomed) return false;
        Tx(_peer, Wire.ChanReliable, Wire.Encode(new SaveCamoMsg(slot, spec)), (int)ENetPacketPeer.FlagReliable);
        _conn.Flush();
        return true;
    }

    void OnAppearanceEvent(EventKind kind, byte[] payload)
    {
        if (kind != EventKind.Appearance || AppearanceCodec.Decode(payload) is not { } a) return;
        Appearances[a.PlayerId] = a.Spec;
        if (_remotes.TryGetValue(a.PlayerId, out var r) && r.View != null) r.View.SetCamo(a.Spec, CamoData);
        foreach (var (id, c) in _corpses) // a look that arrives after the corpse spawned (#203)
            if (WorldEntities.Entities.TryGetValue(id, out var e) && e.State.Owner == a.PlayerId) c.View?.SetCamo(a.Spec, CamoData);
        AppearanceChanged?.Invoke(a.PlayerId, a.Spec);
    }
}
