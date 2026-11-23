using Drycastle.Shooter.Characters;
using Drycastle.ShooterCore.Camo;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// Camouflage on the client (ticket #140): the server's appearance broadcasts are kept per player id and applied to that
/// player's character when it exists (a look can arrive before the first snapshot). <see cref="Save"/> sends the
/// player's own change; the server answers with a broadcast, or an error.
/// </summary>
public sealed class CamoClient
{
    readonly ClientContext _ctx;
    readonly Action<ushort, CamoSpec> _dressCorpses;
    readonly Dictionary<ushort, CamoSpec> _looks = new();

    /// <param name="data">camo.json as loaded by the host.</param>
    /// <param name="dressCorpses">A look that arrives after a corpse of that player spawned (#203) is put on it.</param>
    public CamoClient(ClientContext ctx, CamoFile data, Action<ushort, CamoSpec> dressCorpses)
    {
        _ctx = ctx;
        Data = data;
        _dressCorpses = dressCorpses;
    }

    public CamoFile Data { get; }

    /// <summary>The local player's own Camouflage as the server last announced it, or null before that (the Equipment screen's figure).</summary>
    public CamoSpec? OwnLook => _looks.TryGetValue(_ctx.MyId, out var look) ? look : null;

    /// <summary>Puts the player's look on a character, if it is known (it may arrive before the character is made).</summary>
    public void Dress(CharacterView view, ushort player)
    {
        if (_looks.TryGetValue(player, out var look)) view.SetCamo(look, Data);
    }

    /// <summary>
    /// Asks the server to replace one loadout slot's camouflage (#141). Answered with the new <see cref="ProfileMsg"/>
    /// (its <c>Camos</c> carry the saved look), and, for the active slot, an appearance broadcast; or an error.
    /// </summary>
    public bool Save(int slot, CamoSpec spec) => _ctx.SendJoined(new SaveCamoMsg(slot, spec));

    public void OnEvent(EventKind kind, byte[] payload)
    {
        if (kind != EventKind.Appearance || AppearanceCodec.Decode(payload) is not { } a) return;
        _looks[a.PlayerId] = a.Spec;
        if (_ctx.Remotes.TryGetValue(a.PlayerId, out var r) && r.View != null) r.View.SetCamo(a.Spec, Data);
        _dressCorpses(a.PlayerId, a.Spec);
    }
}
