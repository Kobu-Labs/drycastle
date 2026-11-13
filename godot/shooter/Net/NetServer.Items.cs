using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Items and equipment on the server (issue #261): what a player wears (<see cref="Client.Worn"/>, items.json), the item a corpse carries and the
/// pickup of it. The rules and the data are in <c>ShooterCore/Items</c> and <see cref="CorpseBook"/>; this file sends the state and answers the
/// pickup. What lasts: the worn item drops on the corpse at death (<see cref="SpawnCorpse"/>), a new round keeps it, leaving the match loses it,
/// and none of it is stored in the profile (ADR 0003 stays the profile's only store).
/// </summary>
public partial class NetServer
{
    /// <summary>items.json: the equippable items. Empty when the data directory has none.</summary>
    public ItemsFile Items = new();

    /// <summary>
    /// Tells a player what they wear now (the HUD's headgear chip) and everyone else too, so their clients draw it on that player's character
    /// (<see cref="EventKind.WornItems"/>, issue #266). The pickup count stays on the server in both.
    /// </summary>
    void SendEquipment(Client c)
    {
        c.Worn.Changed = false;
        Send(c, new EventMsg((byte)EventKind.Equipment, ItemEvents.EncodeEquipment(c.Worn)));
        var worn = WornEvent(c);
        foreach (var o in _byId.Values)
            if (o != c) Send(o, worn);
    }

    EventMsg WornEvent(Client c) => new((byte)EventKind.WornItems, ItemEvents.EncodeWorn(c.Id, c.Worn));

    /// <summary>A joiner learns what everyone already in the match wears (nobody else needs to be told: the joiner wears nothing).</summary>
    void SendWornOnJoin(Client c)
    {
        foreach (var o in _byId.Values)
            if (o != c && o.Worn.Slots.Any(s => s != null)) Send(c, WornEvent(o));
    }

    /// <summary>Tells everyone what a corpse carries now (nothing, after a pickup that emptied it or an item that crumbled).</summary>
    void BroadcastCorpseItem(ushort corpse) =>
        BroadcastEvent(EventKind.CorpseItem, ItemEvents.EncodeCorpseItem(corpse, _corpses.Get(corpse)?.Item?.Item));

    /// <summary>A joiner learns which corpses carry an item (the corpses themselves arrive as world entities).</summary>
    void SendCorpseItemsOnJoin(Client c)
    {
        foreach (var (id, body) in _corpses.WithIds)
            if (body.Item is { } item) Send(c, new EventMsg((byte)EventKind.CorpseItem, ItemEvents.EncodeCorpseItem(id, item.Item)));
    }

    /// <summary>
    /// A client asks to put on the item a corpse carries. Same conditions as taking its weapon (alive, on foot, in reach). What
    /// <see cref="CorpseBook.TryPickUpItem"/> decides is sent: the wearer's equipment and everyone's view of the corpse, and a notice
    /// when the item crumbled. A refused attempt (gone, too far, nothing to take) is only logged, as for the weapon.
    /// </summary>
    void OnPickUpCorpseItem(Client c, byte[] payload)
    {
        if (!ItemEvents.TryDecodePickUp(payload, out var corpse)) return;
        if (!c.Joined || c.W == null || c.Car != null || _match.Get(c.Id)?.Life != PlayerLife.Alive) return;
        var result = _corpses.TryPickUpItem(corpse, N(c.Body.GlobalPosition), c.Worn, Items);
        if (result is not (ItemPickUp.Ok or ItemPickUp.Crumbled))
        {
            GD.Print($"server: {c.Name} corpse item pickup refused: {result}");
            return;
        }
        BroadcastCorpseItem(corpse);
        if (c.Worn.Changed) SendEquipment(c);
        if (result == ItemPickUp.Crumbled) Send(c, new EventMsg((byte)EventKind.ItemNotice, ItemEvents.EncodeNotice(ItemNoticeKind.Crumbled)));
        GD.Print($"server: {c.Name} corpse item pickup: {result}");
    }
}
