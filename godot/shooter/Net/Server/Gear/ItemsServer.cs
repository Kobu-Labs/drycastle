using Godot;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Net;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Gear;

/// <summary>
/// Items on the server (issues #261, #267, ADR 0016 and 0018): what a player wears (<see cref="ServerClient.Worn"/>) and carries in the bag
/// (<see cref="ServerClient.Bag"/>), the items a corpse carries, the items on the ground, and the requests that move them. The rules and the data are
/// in <c>ShooterCore/Items</c> and <see cref="CorpseBook"/>; this class checks who may ask, sends the state and answers. What lasts: a death puts
/// everything worn and bagged on the corpse, a new round keeps it, leaving the match loses it, and none of it is
/// stored in the profile (ADR 0003 stays the profile's only store). The client sends wishes only; every one is checked here.
/// </summary>
internal sealed class ItemsServer
{
    /// <summary>How far above the player's feet the ray for the terrain height under a drop starts, and how far down it looks (a helicopter's drop looks much further).</summary>
    const float DropRayLiftMetres = 0.5f, DropRayFootMetres = 100f, DropRayAirMetres = 2000f;

    readonly ServerContext _ctx;
    readonly ItemsFile _items;
    readonly CorpseBook _corpses;
    readonly Func<WorldQuery> _query;
    readonly Func<ServerCar, NVec3> _carFeet;

    /// <param name="carFeet">Where a vehicle stands now.</param>
    public ItemsServer(ServerContext ctx, ItemsFile items, CorpseBook corpses, Func<WorldQuery> query, Func<ServerCar, NVec3> carFeet)
    {
        _ctx = ctx; _items = items; _corpses = corpses; _query = query; _carFeet = carFeet;
    }

    /// <summary>
    /// Tells a player what they wear and carry now (the Equipment screen) and tells everyone else what they wear, so their clients draw it on
    /// that player's character (<see cref="EventKind.WornItems"/>, issue #266). The pickup counts and the bag stay between the server and the owner.
    /// </summary>
    public void SendEquipment(ServerClient c)
    {
        c.Worn.Changed = false;
        c.Bag.Changed = false;
        _ctx.SendEvent(c, EventKind.Equipment, ItemEvents.EncodeEquipment(c.Worn, c.Bag));
        var worn = WornEvent(c);
        foreach (var o in _ctx.Players.Values)
            if (o != c) _ctx.Send(o, worn);
    }

    static EventMsg WornEvent(ServerClient c) => new((byte)EventKind.WornItems, ItemEvents.EncodeWorn(c.Id, c.Worn));

    /// <summary>Sends a player their equipment and bag when either changed since the last send.</summary>
    public void SendEquipmentIfChanged(ServerClient c)
    {
        if (c.Worn.Changed || c.Bag.Changed) SendEquipment(c);
    }

    /// <summary>A joiner learns what everyone already in the match wears (nobody else needs to be told: the joiner wears nothing).</summary>
    public void SendWornOnJoin(ServerClient c)
    {
        foreach (var o in _ctx.Players.Values)
            if (o != c && o.Worn.Slots.Any(s => s != null)) _ctx.Send(c, WornEvent(o));
    }

    public bool CarriesBinoculars(ServerClient c) => HeldBinoculars(c) >= 0;

    /// <summary>The item index of the pair the player raises (<see cref="Binoculars.Held"/>), -1 with none in the bag.</summary>
    int HeldBinoculars(ServerClient c)
    {
        var carried = new List<int>();
        for (var cell = 0; cell < Bag.Cells; cell++)
            if (c.Bag.Get(cell) is { } stack) carried.Add(stack.Item);
        return Binoculars.Held(_items, carried);
    }

    /// <summary>The player's binoculars are up or down now: everyone else is told when it changed, so their clients pose that player's character.</summary>
    public void SetBinoculars(ServerClient c, bool up)
    {
        if (c.BinocularsUp == up) return;
        c.BinocularsUp = up;
        var msg = new EventMsg((byte)EventKind.BinocularsRaised, Binoculars.EncodeRaised(c.Id, up, HeldBinoculars(c)));
        foreach (var o in _ctx.Players.Values)
            if (o != c) _ctx.Send(o, msg);
    }

    /// <summary>A joiner learns who holds the binoculars up.</summary>
    public void SendBinocularsOnJoin(ServerClient c)
    {
        foreach (var o in _ctx.Players.Values)
            if (o != c && o.BinocularsUp) _ctx.SendEvent(c, EventKind.BinocularsRaised, Binoculars.EncodeRaised(o.Id, true, HeldBinoculars(o)));
    }

    /// <summary>The player's head lamp is on or off now (<see cref="HeadLamp"/>): everyone, the owner too, is told when it changed.</summary>
    public void SetHeadLamp(ServerClient c, bool on)
    {
        if (c.HeadLampOn == on) return;
        c.HeadLampOn = on;
        _ctx.Broadcast(EventKind.HeadLampState, HeadLamp.Encode(c.Id, on));
    }

    /// <summary>A joiner learns whose head lamp is on.</summary>
    public void SendHeadLampsOnJoin(ServerClient c)
    {
        foreach (var o in _ctx.Players.Values)
            if (o != c && o.HeadLampOn) _ctx.SendEvent(c, EventKind.HeadLampState, HeadLamp.Encode(o.Id, true));
    }

    void SendNotice(ServerClient c, ItemNoticeKind kind) => _ctx.SendEvent(c, EventKind.ItemNotice, ItemEvents.EncodeNotice(kind));

    /// <summary>Tells everyone what a corpse carries now (an empty list after a pickup that emptied it or an item that crumbled).</summary>
    public void BroadcastCorpseItems(ushort corpse) =>
        _ctx.Broadcast(EventKind.CorpseItems, ItemEvents.EncodeCorpseItems(corpse, _corpses.Get(corpse)?.Items ?? Array.Empty<ItemStack>()));

    /// <summary>A joiner learns which corpses carry items (the corpses themselves arrive as world entities, and so do the items on the ground).</summary>
    public void SendCorpseItemsOnJoin(ServerClient c)
    {
        foreach (var (id, body) in _corpses.WithIds)
            if (body.Items.Count > 0) _ctx.SendEvent(c, EventKind.CorpseItems, ItemEvents.EncodeCorpseItems(id, body.Items));
    }

    /// <summary>The player is alive and on foot or seated: the state in which they may handle their items. A dead or unjoined one may not.</summary>
    bool MayHandle(ServerClient c) => c.Joined && c.W != null && _ctx.IsAlive(c.Id);

    /// <summary>
    /// A client asks to take the item at a position of a corpse's list. Same conditions as taking its weapon (alive, on foot, in reach). What
    /// <see cref="CorpseBook.TryPickUpItem"/> decides is sent: the taker's equipment and bag and everyone's view of the corpse, and a notice
    /// when the item crumbled. A refused attempt (gone, too far, nothing to take) is only logged, as for the weapon.
    /// </summary>
    public void OnPickUpCorpseItem(ServerClient c, byte[] payload)
    {
        if (!ItemEvents.TryDecodePickUp(payload, out var corpse, out var index)) return;
        if (!MayHandle(c) || c.Car != null) return;
        var result = _corpses.TryPickUpItem(corpse, index, c.Body.GlobalPosition.ToNumerics(), c.Worn, c.Bag, _items, out var placed);
        if (result == ItemPickUp.NoRoom) { SendNotice(c, ItemNoticeKind.BagFull); return; }
        if (result is not (ItemPickUp.Ok or ItemPickUp.Crumbled))
        {
            GD.Print($"server: {c.Name} corpse item pickup refused: {result}");
            return;
        }
        BroadcastCorpseItems(corpse);
        SendEquipmentIfChanged(c);
        if (result == ItemPickUp.Crumbled) SendNotice(c, ItemNoticeKind.Crumbled);
        GD.Print($"server: {c.Name} corpse item pickup: {result} {placed}");
    }

    /// <summary>The item requests of the bag and the ground (<see cref="EventKind.EquipItem"/> to <see cref="EventKind.TakeGroundItem"/>).</summary>
    public void OnItemRequest(ServerClient c, EventKind kind, byte[] payload)
    {
        if (!MayHandle(c)) return;
        switch (kind)
        {
            case EventKind.EquipItem when ItemEvents.TryDecodeEquip(payload, out var cell):
                if (ItemRules.Equip(_items, c.Worn, c.Bag, cell) == ItemMove.Ok) SendEquipmentIfChanged(c);
                break;
            case EventKind.UnequipItem when ItemEvents.TryDecodeUnequip(payload, out var slot):
                var moved = ItemRules.Unequip(c.Worn, c.Bag, slot);
                if (moved == ItemMove.BagFull) SendNotice(c, ItemNoticeKind.BagFull);
                else if (moved == ItemMove.Ok) SendEquipmentIfChanged(c);
                break;
            case EventKind.DropItem when ItemEvents.TryDecodeDrop(payload, out var home, out var index):
                Drop(c, home, index);
                break;
            case EventKind.TakeGroundItem when ItemEvents.TryDecodeTakeGround(payload, out var id):
                TakeGround(c, id);
                break;
        }
    }

    /// <summary>
    /// Puts a worn or bagged item on the ground at the dropper's feet, or under their vehicle (<see cref="DropPoint"/>): no throw. If the world refuses
    /// the entity (its cap) the item goes back where it was.
    /// </summary>
    void Drop(ServerClient c, ItemHome home, int index)
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
        GD.Print($"server: {c.Name} dropped {_items.At(stack.Item)?.Id}");
    }

    /// <summary>A client asks to take the item lying on the ground. Alive, on foot and in reach; both the slot and the bag full refuses it with a notice.</summary>
    void TakeGround(ServerClient c, ushort id)
    {
        if (c.Car != null) return;
        var result = _corpses.TryTakeGround(id, c.Body.GlobalPosition.ToNumerics(), c.Worn, c.Bag, _items, out var placed);
        if (result == ItemPickUp.NoRoom) { SendNotice(c, ItemNoticeKind.BagFull); return; }
        if (result is not (ItemPickUp.Ok or ItemPickUp.Crumbled))
        {
            GD.Print($"server: {c.Name} ground item pickup refused: {result}");
            return;
        }
        SendEquipmentIfChanged(c);
        if (result == ItemPickUp.Crumbled) SendNotice(c, ItemNoticeKind.Crumbled);
        GD.Print($"server: {c.Name} ground item pickup: {result} {placed}");
    }

    /// <summary>
    /// Where a drop lands: the terrain height under the dropper's feet, or under their vehicle (any seat, a helicopter in the air included). A
    /// vehicle on the ground (a car, the transport afloat) is where its feet are; a player on foot and a helicopter look down for the ground, and
    /// keep the height they started from when nothing is below.
    /// </summary>
    NVec3 DropPoint(ServerClient c)
    {
        var from = c.Car != null ? _carFeet(c.Car) : c.Body.GlobalPosition.ToNumerics();
        if (c.Car is { Flies: false }) return from;
        var down = c.Car is { Flies: true } ? DropRayAirMetres : DropRayFootMetres;
        // a drop lands on the ground: under a vehicle, never on a player's body or a vehicle's roof (ADR 0028, the Drop row)
        return _query().GroundY(MoverKind.Drop, new Vector3(from.X, from.Y, from.Z), DropRayLiftMetres, down) is { } y ? new NVec3(from.X, y, from.Z) : from;
    }
}
