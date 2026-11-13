using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// An item as the screens read it: which one (<see cref="Index"/>, its items.json position, for an icon lookup), its definition and how many times
/// it has been taken (<see cref="Pickups"/>, the count that travels with the item).
/// </summary>
public readonly record struct HeldItem(int Index, ItemDef Def, int Pickups)
{
    /// <summary>Pickups the item has left before it crumbles, or null when it has no limit. 0 means the next take makes it crumble.</summary>
    public int? PickupsLeft => Def.PickupLimit is { } limit ? Math.Max(0, limit - Pickups) : null;
}

/// <summary>
/// Items on the client (issues #261, #267, #268): what the local player wears and carries in the bag (the server's <see cref="EventKind.Equipment"/>:
/// <see cref="WornItems"/>, <see cref="BagContents"/>, drawn by the Equipment screen; nothing on the HUD shows Items), the items each corpse carries
/// (<see cref="EventKind.CorpseItems"/>: <see cref="CorpseItems"/>) and the notices (an item crumbles, the bag is full). The server decides
/// everything; the client sends only wishes (the <c>Request...</c> methods), and what it shows is what the server last said. Screens read the state
/// through the public members below and redraw on <see cref="ItemsChanged"/> and <see cref="CorpseItemsChanged"/>. The items on the ground are
/// in the ground items partial.
/// </summary>
public partial class NetClient
{
    /// <summary>items.json: set by the host (an item is named on the wire by its position in this list).</summary>
    public ItemsFile ItemData = new();

    /// <summary>Cells in the bag.</summary>
    public const int BagCells = Bag.Cells;

    /// <summary>What the local player wears, one entry per <see cref="EquipSlot"/> (null for nothing). Replaced by each Equipment event.</summary>
    ItemStack?[] _wornItems = new ItemStack?[Equipment.SlotCount];

    /// <summary>What the local player carries unworn, one entry per cell (null for an empty one). Replaced by each Equipment event.</summary>
    ItemStack?[] _bagItems = new ItemStack?[Bag.Cells];

    /// <summary>The views the screens read, rebuilt once per Equipment event (not per frame, not per read).</summary>
    IReadOnlyList<HeldItem?> _wornView = Array.AsReadOnly(new HeldItem?[Equipment.SlotCount]);
    IReadOnlyList<HeldItem?> _bagView = Array.AsReadOnly(new HeldItem?[Bag.Cells]);

    /// <summary>The items each corpse carries, by corpse id. Only corpses that carry some are in it.</summary>
    readonly Dictionary<ushort, IReadOnlyList<HeldItem>> _corpseItems = new();

    /// <summary>
    /// What every player wears (items.json position per <see cref="EquipSlot"/>), by player id, for the characters to draw (#266): the others from
    /// <see cref="EventKind.WornItems"/>, the local player from <see cref="EventKind.Equipment"/>. Kept after the character is made, so a view made
    /// later is dressed at once; a player who leaves is forgotten.
    /// </summary>
    readonly Dictionary<ushort, int?[]> _wornBy = new();

    /// <summary>Raised after the server told what the local player wears or carries (every change of <see cref="WornItems"/> or <see cref="BagContents"/>, on the main thread).</summary>
    public event Action? ItemsChanged;

    /// <summary>Raised with the corpse id after the server told what a corpse carries now (including that it carries nothing, after the last item was taken).</summary>
    public event Action<ushort>? CorpseItemsChanged;

    // ---- what the screens read ----

    /// <summary>What the local player wears, one entry per <see cref="EquipSlot"/> in slot order (null for an empty slot). Stable between <see cref="ItemsChanged"/> events.</summary>
    public IReadOnlyList<HeldItem?> WornItems => _wornView;

    /// <summary>What the local player wears in <paramref name="slot"/>, or null.</summary>
    public HeldItem? Worn(EquipSlot slot) => (int)slot < _wornView.Count ? _wornView[(int)slot] : null;

    /// <summary>The bag, <see cref="BagCells"/> entries in cell order (null for an empty cell). Stable between <see cref="ItemsChanged"/> events.</summary>
    public IReadOnlyList<HeldItem?> BagContents => _bagView;

    /// <summary>The item in bag cell <paramref name="cell"/>, or null (empty, or no such cell).</summary>
    public HeldItem? InBag(int cell) => cell >= 0 && cell < _bagView.Count ? _bagView[cell] : null;

    /// <summary>No empty bag cell is left: unequipping, and taking a ground item into the bag, would be refused.</summary>
    public bool BagIsFull => _bagItems.All(c => c != null);

    /// <summary>The items a corpse carries, in the order the server keeps them (the index <see cref="RequestTakeCorpseItem"/> takes). Empty for a corpse that carries none or one that is gone.</summary>
    public IReadOnlyList<HeldItem> CorpseItems(ushort corpse) => _corpseItems.TryGetValue(corpse, out var list) ? list : Array.Empty<HeldItem>();

    /// <summary>The corpse within reach from the last render (null when none, or the player cannot take from one): the one the corpse chooser is about.</summary>
    public ushort? CorpseInReach => _near;

    /// <summary>The item lying on the ground within reach from the last render (a world entity id for <see cref="RequestTakeGroundItem"/>), or null.</summary>
    public ushort? GroundItemInReach => _groundNear;

    IconRegistry? _itemIcons;

    /// <summary>The icon of an item (<c>ui/icons/items/&lt;id&gt;.png</c>, else the generic item icon), for the cells of the Equipment screen and the corpse chooser.</summary>
    public Texture2D? ItemIcon(ItemDef def)
    {
        _itemIcons ??= new IconRegistry(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), ItemData.Items.Select(i => i.Id));
        return ResourceLoader.Load<Texture2D>(_itemIcons.Resolve(IconKind.Item, def.Id, p => ResourceLoader.Exists(p)));
    }

    /// <summary>The name a player reads for an item: the translation (<c>item.id</c>), else the name in items.json.</summary>
    public string ItemName(ItemDef def)
    {
        var k = "item." + def.Id;
        var t = Strings.T(k);
        return t == k ? def.Name : t;
    }

    // ---- what the screens ask for (wishes: the server decides, and tells the result with the next events) ----

    /// <summary>Put the item in bag cell <paramref name="cell"/> on. What was worn in its slot goes into that cell. Free.</summary>
    public void RequestEquip(int cell) => SendItem(EventKind.EquipItem, ItemEvents.EncodeEquip(cell));

    /// <summary>Take off what is worn in <paramref name="slot"/> into the bag. The server refuses it with a "bag is full" notice when no cell is free. Free.</summary>
    public void RequestUnequip(EquipSlot slot) => SendItem(EventKind.UnequipItem, ItemEvents.EncodeUnequip(slot));

    /// <summary>Drop what is worn in <paramref name="slot"/> on the ground (at the feet, or under the vehicle the player sits in).</summary>
    public void RequestDropWorn(EquipSlot slot) => SendItem(EventKind.DropItem, ItemEvents.EncodeDrop(ItemHome.Worn, (int)slot));

    /// <summary>Drop what is in bag cell <paramref name="cell"/> on the ground (at the feet, or under the vehicle the player sits in).</summary>
    public void RequestDropBagged(int cell) => SendItem(EventKind.DropItem, ItemEvents.EncodeDrop(ItemHome.Bag, cell));

    /// <summary>Take the item lying on the ground (an id from <see cref="GroundItemInReach"/>). Counts one pickup.</summary>
    public void RequestTakeGroundItem(ushort id) => SendItem(EventKind.TakeGroundItem, ItemEvents.EncodeTakeGround(id));

    /// <summary>Take the item at <paramref name="index"/> of <see cref="CorpseItems"/> from a corpse in reach: into its empty slot, else the bag, else swapped with what is worn. Counts one pickup.</summary>
    public void RequestTakeCorpseItem(ushort corpse, int index) =>
        SendItem(EventKind.PickUpCorpseItem, ItemEvents.EncodePickUp(corpse, index));

    void SendItem(EventKind kind, byte[] payload)
    {
        if (_peer == null || Bot) return;
        Tx(_peer, Wire.ChanReliable, Wire.Encode(new EventMsg((byte)kind, payload)), (int)ENetPacketPeer.FlagReliable);
    }

    // ---- the server's events ----

    void OnItemEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.Equipment when ItemEvents.TryDecodeEquipment(payload, out var worn, out var bag):
                _wornItems = worn;
                _bagItems = bag;
                _wornView = View(worn);
                _bagView = View(bag);
                SetWornOf(_myId, worn.Select(w => (int?)w?.Item).ToArray()); // the seat view of the local player
                ItemsChanged?.Invoke();
                break;
            case EventKind.WornItems when ItemEvents.TryDecodeWorn(payload, out var player, out var others):
                SetWornOf(player, others);
                break;
            case EventKind.CorpseItems when ItemEvents.TryDecodeCorpseItems(payload, out var corpse, out var items):
                var held = items.Select(Held).Where(h => h != null).Select(h => h!.Value).ToList();
                if (held.Count > 0) _corpseItems[corpse] = held; else _corpseItems.Remove(corpse);
                DressCorpse(corpse);
                CorpseItemsChanged?.Invoke(corpse);
                break;
            case EventKind.ItemNotice when ItemEvents.TryDecodeNotice(payload, out var notice):
                if (notice == ItemNoticeKind.Crumbled) UiShell.Instance?.Toast(Strings.T("toast.item.crumbled"), ToastKind.Warn);
                else if (notice == ItemNoticeKind.BagFull) UiShell.Instance?.Toast(Strings.T("toast.item.bagfull"), ToastKind.Warn);
                break;
        }
    }

    /// <summary>An item the data knows, as the screens read it; null for a position items.json does not have (a server with more items than this client).</summary>
    HeldItem? Held(ItemStack stack) => ItemData.At(stack.Item) is { } def ? new HeldItem(stack.Item, def, stack.Pickups) : null;

    IReadOnlyList<HeldItem?> View(ItemStack?[] cells) => Array.AsReadOnly(cells.Select(c => c is { } s ? Held(s) : null).ToArray());

    /// <summary>The definition of what the local player wears in a slot, or null.</summary>
    ItemDef? WornIn(EquipSlot slot) => Worn(slot)?.Def;

    /// <summary>Records what a player wears and dresses their character (a remote player's, or the local player's in a vehicle seat) when it exists.</summary>
    void SetWornOf(ushort player, int?[] slots)
    {
        _wornBy[player] = slots;
        if (_remotes.TryGetValue(player, out var r) && r.View != null) Dress(r.View, slots);
        if (player == _myId && _selfView != null) Dress(_selfView, slots);
    }

    /// <summary>Forgets a player who left.</summary>
    void ForgetWorn(ushort player) => _wornBy.Remove(player);

    /// <summary>The items.json positions of a player's equipment on a character: every slot is set, so an empty one takes off what was worn.</summary>
    void Dress(CharacterView view, int?[]? slots)
    {
        for (var s = 0; s < Equipment.SlotCount; s++)
            view.SetWorn((EquipSlot)s, slots != null && s < slots.Length && slots[s] is { } i ? ItemData.At(i) : null);
    }

    /// <summary>Dresses a new character of a player with what they wear.</summary>
    void DressPlayer(CharacterView view, ushort player) => Dress(view, _wornBy.GetValueOrDefault(player));

    /// <summary>Shows the item a corpse carries on the dead body (and nothing once it is taken or has crumbled). The view may not exist yet: it is dressed when it spawns.</summary>
    void DressCorpse(ushort corpse)
    {
        if (!_corpses.TryGetValue(corpse, out var v) || v.View == null) return; // the giant's corpse draws no worn item
        var items = CorpseItems(corpse);
        for (var s = 0; s < Equipment.SlotCount; s++)
        {
            ItemDef? shown = null; // the first item of the slot in the corpse's list (the worn ones come first)
            foreach (var h in items) if ((int)h.Def.Slot == s) { shown = h.Def; break; }
            v.View.SetWorn((EquipSlot)s, shown);
        }
    }

    /// <summary>The first item a corpse carries, or null: what the corpse prompt and the chooser's single item row name.</summary>
    ItemDef? CorpseItem(ushort corpse) => CorpseItems(corpse) is { Count: > 0 } list ? list[0].Def : null;
}
