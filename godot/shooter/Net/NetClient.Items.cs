using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Items and equipment on the client (issue #261): what the local player wears (the server's <see cref="EventKind.Equipment"/>, drawn as the
/// HUD's headgear chip), which corpses carry an item (<see cref="EventKind.CorpseItem"/>, for the pickup prompt and the chooser's item row)
/// and the notice when an item crumbles. The server decides everything; the client sends only the wish to take an item
/// (<see cref="EventKind.PickUpCorpseItem"/>, see <see cref="NetClient"/>'s corpse partial).
/// </summary>
public partial class NetClient
{
    /// <summary>items.json: set by the host (an item is named on the wire by its position in this list).</summary>
    public ItemsFile ItemData = new();

    /// <summary>What the local player wears, one entry per <see cref="EquipSlot"/> (items.json position, null for nothing). Replaced by each Equipment event.</summary>
    int?[] _wornItems = new int?[Equipment.SlotCount];

    /// <summary>The name of what the local player wears in the headgear slot, for the HUD chip (worded once per Equipment event, not per frame); null for nothing.</summary>
    string? _headgearName;

    /// <summary>The item each corpse carries (items.json position), by corpse id. Only corpses that carry one are in it.</summary>
    readonly Dictionary<ushort, int> _corpseItems = new();

    /// <summary>
    /// What every player wears (items.json position per <see cref="EquipSlot"/>), by player id, for the characters to draw (#266): the others from
    /// <see cref="EventKind.WornItems"/>, the local player from <see cref="EventKind.Equipment"/>. Kept after the character is made, so a view made
    /// later is dressed at once; a player who leaves is forgotten.
    /// </summary>
    readonly Dictionary<ushort, int?[]> _wornBy = new();

    void OnItemEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.Equipment when ItemEvents.TryDecodeEquipment(payload, out var slots):
                _wornItems = slots;
                _headgearName = WornHeadgear is { } worn ? ItemName(worn) : null;
                SetWornOf(_myId, slots); // the seat view of the local player
                break;
            case EventKind.WornItems when ItemEvents.TryDecodeWorn(payload, out var player, out var others):
                SetWornOf(player, others);
                break;
            case EventKind.CorpseItem when ItemEvents.TryDecodeCorpseItem(payload, out var corpse, out var item):
                if (item is { } i) _corpseItems[corpse] = i; else _corpseItems.Remove(corpse);
                DressCorpse(corpse);
                break;
            case EventKind.ItemNotice when ItemEvents.TryDecodeNotice(payload, out var notice):
                if (notice == ItemNoticeKind.Crumbled) UiShell.Instance?.Toast(Strings.T("toast.item.crumbled"), ToastKind.Warn);
                break;
        }
    }

    /// <summary>The name a player reads for an item: the translation (<c>item.id</c>), else the name in items.json.</summary>
    string ItemName(ItemDef def)
    {
        var k = "item." + def.Id;
        var t = Strings.T(k);
        return t == k ? def.Name : t;
    }

    /// <summary>The item a corpse carries, or null.</summary>
    ItemDef? CorpseItem(ushort corpse) => _corpseItems.TryGetValue(corpse, out var i) ? ItemData.At(i) : null;

    /// <summary>What the local player wears in a slot, or null.</summary>
    ItemDef? WornIn(EquipSlot slot) => (int)slot < _wornItems.Length && _wornItems[(int)slot] is { } i ? ItemData.At(i) : null;

    ItemDef? WornHeadgear => WornIn(EquipSlot.Headgear);

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
        if (!_corpses.TryGetValue(corpse, out var v)) return;
        var item = CorpseItem(corpse);
        for (var s = 0; s < Equipment.SlotCount; s++) v.View.SetWorn((EquipSlot)s, item is { } d && (int)d.Slot == s ? d : null);
    }

    void SendPickUpItem(ushort corpse) =>
        Tx(_peer, Wire.ChanReliable, Wire.Encode(new EventMsg((byte)EventKind.PickUpCorpseItem, ItemEvents.EncodePickUp(corpse))), (int)ENetPacketPeer.FlagReliable);
}
