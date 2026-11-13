using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Items on the server (issues #261, #267, ADR 0016 and 0018): what a player wears (<see cref="Client.Worn"/>) and carries in the bag
/// (<see cref="Client.Bag"/>), the items a corpse carries, the items on the ground, and the requests that move them. The rules and the data are
/// in <c>ShooterCore/Items</c> and <see cref="CorpseBook"/>; this file checks who may ask, sends the state and answers. What lasts: a death puts
/// everything worn and bagged on the corpse (<see cref="SpawnCorpse"/>), a new round keeps it, leaving the match loses it, and none of it is
/// stored in the profile (ADR 0003 stays the profile's only store). The client sends wishes only; every one is checked here.
/// </summary>
public partial class NetServer
{
    /// <summary>items.json: the equippable items. Empty when the data directory has none.</summary>
    public ItemsFile Items = new();

    /// <summary>How far above the player's feet the ray for the terrain height under a drop starts, and how far down it looks (a helicopter's drop looks much further).</summary>
    const float DropRayLiftMetres = 0.5f, DropRayFootMetres = 100f, DropRayAirMetres = 2000f;

    /// <summary>
    /// Tells a player what they wear and carry now (the Equipment screen) and tells everyone else what they wear, so their clients draw it on
    /// that player's character (<see cref="EventKind.WornItems"/>, issue #266). The pickup counts and the bag stay between the server and the owner.
    /// </summary>
    void SendEquipment(Client c)
    {
        c.Worn.Changed = false;
        c.Bag.Changed = false;
        Send(c, new EventMsg((byte)EventKind.Equipment, ItemEvents.EncodeEquipment(c.Worn, c.Bag)));
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

    /// <summary>Sends a player their equipment and bag when either changed since the last send.</summary>
    void SendEquipmentIfChanged(Client c)
    {
        if (c.Worn.Changed || c.Bag.Changed) SendEquipment(c);
    }

    void SendItemNotice(Client c, ItemNoticeKind kind) => Send(c, new EventMsg((byte)EventKind.ItemNotice, ItemEvents.EncodeNotice(kind)));

    /// <summary>Tells everyone what a corpse carries now (an empty list after a pickup that emptied it or an item that crumbled).</summary>
    void BroadcastCorpseItems(ushort corpse) =>
        BroadcastEvent(EventKind.CorpseItems, ItemEvents.EncodeCorpseItems(corpse, _corpses.Get(corpse)?.Items ?? Array.Empty<ItemStack>()));

    /// <summary>A joiner learns which corpses carry items (the corpses themselves arrive as world entities, and so do the items on the ground).</summary>
    void SendCorpseItemsOnJoin(Client c)
    {
        foreach (var (id, body) in _corpses.WithIds)
            if (body.Items.Count > 0) Send(c, new EventMsg((byte)EventKind.CorpseItems, ItemEvents.EncodeCorpseItems(id, body.Items)));
    }

    /// <summary>The player is alive and on foot or seated: the state in which they may handle their items. A dead or unjoined one may not.</summary>
    bool MayHandleItems(Client c) => c.Joined && c.W != null && _match.Get(c.Id)?.Life == PlayerLife.Alive;

    /// <summary>
    /// A client asks to take the item at a position of a corpse's list. Same conditions as taking its weapon (alive, on foot, in reach). What
    /// <see cref="CorpseBook.TryPickUpItem"/> decides is sent: the taker's equipment and bag and everyone's view of the corpse, and a notice
    /// when the item crumbled. A refused attempt (gone, too far, nothing to take) is only logged, as for the weapon.
    /// </summary>
    void OnPickUpCorpseItem(Client c, byte[] payload)
    {
        if (!ItemEvents.TryDecodePickUp(payload, out var corpse, out var index)) return;
        if (!MayHandleItems(c) || c.Car != null) return;
        var result = _corpses.TryPickUpItem(corpse, index, N(c.Body.GlobalPosition), c.Worn, c.Bag, Items, out var placed);
        if (result is not (ItemPickUp.Ok or ItemPickUp.Crumbled))
        {
            GD.Print($"server: {c.Name} corpse item pickup refused: {result}");
            return;
        }
        BroadcastCorpseItems(corpse);
        SendEquipmentIfChanged(c);
        if (result == ItemPickUp.Crumbled) SendItemNotice(c, ItemNoticeKind.Crumbled);
        GD.Print($"server: {c.Name} corpse item pickup: {result} {placed}");
    }

    /// <summary>The item requests of the bag and the ground (<see cref="EventKind.EquipItem"/> to <see cref="EventKind.TakeGroundItem"/>).</summary>
    void OnItemRequest(Client c, EventKind kind, byte[] payload)
    {
        if (!MayHandleItems(c)) return;
        switch (kind)
        {
            case EventKind.EquipItem when ItemEvents.TryDecodeEquip(payload, out var cell):
                if (ItemRules.Equip(Items, c.Worn, c.Bag, cell) == ItemMove.Ok) SendEquipmentIfChanged(c);
                break;
            case EventKind.UnequipItem when ItemEvents.TryDecodeUnequip(payload, out var slot):
                var moved = ItemRules.Unequip(c.Worn, c.Bag, slot);
                if (moved == ItemMove.BagFull) SendItemNotice(c, ItemNoticeKind.BagFull);
                else if (moved == ItemMove.Ok) SendEquipmentIfChanged(c);
                break;
            case EventKind.DropItem when ItemEvents.TryDecodeDrop(payload, out var home, out var index):
                DropItem(c, home, index);
                break;
            case EventKind.TakeGroundItem when ItemEvents.TryDecodeTakeGround(payload, out var id):
                TakeGroundItem(c, id);
                break;
        }
    }

    /// <summary>
    /// Puts a worn or bagged item on the ground at the dropper's feet, or under their vehicle (<see cref="DropPoint"/>): no throw. If the world refuses
    /// the entity (its cap) the item goes back where it was.
    /// </summary>
    void DropItem(Client c, ItemHome home, int index)
    {
        var slot = (EquipSlot)index;
        ItemStack stack;
        ItemMove moved;
        if (home == ItemHome.Worn)
        {
            if (!Enum.IsDefined(slot)) return;
            moved = ItemRules.DropWorn(c.Worn, slot, out stack);
        }
        else moved = ItemRules.DropBagged(c.Bag, index, out stack);
        if (moved != ItemMove.Ok) return;
        if (_corpses.Drop(c.Id, DropPoint(c), stack) == null)
        {
            if (home == ItemHome.Worn) c.Worn.Wear(slot, stack); else c.Bag.Set(index, stack);
            GD.Print($"server: {c.Name} item drop refused: the world is full");
        }
        SendEquipmentIfChanged(c);
        GD.Print($"server: {c.Name} dropped {Items.At(stack.Item)?.Id}");
    }

    /// <summary>A client asks to take the item lying on the ground. Alive, on foot and in reach; both the slot and the bag full refuses it with a notice.</summary>
    void TakeGroundItem(Client c, ushort id)
    {
        if (c.Car != null) return;
        var result = _corpses.TryTakeGround(id, N(c.Body.GlobalPosition), c.Worn, c.Bag, Items, out var placed);
        if (result == ItemPickUp.NoRoom) { SendItemNotice(c, ItemNoticeKind.BagFull); return; }
        if (result is not (ItemPickUp.Ok or ItemPickUp.Crumbled))
        {
            GD.Print($"server: {c.Name} ground item pickup refused: {result}");
            return;
        }
        SendEquipmentIfChanged(c);
        if (result == ItemPickUp.Crumbled) SendItemNotice(c, ItemNoticeKind.Crumbled);
        GD.Print($"server: {c.Name} ground item pickup: {result} {placed}");
    }

    /// <summary>
    /// Where a drop lands: the terrain height under the dropper's feet, or under their vehicle (any seat, a helicopter in the air included). A
    /// vehicle on the ground (a car, the transport afloat) is where its feet are; a player on foot and a helicopter look down for the ground, and
    /// keep the height they started from when nothing is below.
    /// </summary>
    NVec3 DropPoint(Client c)
    {
        var from = c.Car != null ? PresentPose(c.Car).Feet : N(c.Body.GlobalPosition);
        if (c.Car is { Flies: false }) return from;
        var down = c.Car is { Flies: true } ? DropRayAirMetres : DropRayFootMetres;
        var q = PhysicsRayQueryParameters3D.Create(new Vector3(from.X, from.Y + DropRayLiftMetres, from.Z), new Vector3(from.X, from.Y - down, from.Z), CollisionLayers.World);
        var hit = World.GetWorld3D().DirectSpaceState.PerfRay(q);
        return hit.Count == 0 ? from : new NVec3(from.X, hit["position"].AsVector3().Y, from.Z);
    }
}
