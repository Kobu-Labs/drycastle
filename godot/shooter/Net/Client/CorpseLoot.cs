using Godot;
using Drycastle.Shooter.Corpses;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// What the player does at a corpse in reach (ticket #130): the pickup prompt, the hold and tap of the interact key, and the chooser. Holding the key takes the corpse's weapon into the
/// primary position (<see cref="CorpseRules.PrimaryPos"/>); a tap opens <see cref="CorpseChooser"/> to pick the primary or the secondary.
/// The server validates reach and slot; the hotbar it sends back (<see cref="EventKind.Hotbar"/>) is the one that counts.
/// A corpse that carries items (<see cref="EventKind.CorpseItems"/>, up to eight; issues #261, #269) adds a row of item cells to the chooser, one for each
/// of them: taking an item is a separate action from taking the weapon (a click or Enter on its cell, <see cref="ItemsClient.RequestTakeCorpseItem"/>), and the cell
/// leaves the list when the server sends the corpse's next list. Holding the key still takes the weapon only. The prompt names the item only when there is
/// exactly one, and nothing else for several.
/// </summary>
internal sealed class CorpseLoot
{
    /// <summary>Hold time after which the interact key takes the corpse into the primary position.</summary>
    const double HoldSeconds = 0.4;

    readonly ClientContext _ctx;
    readonly ItemsClient _items;
    readonly CorpsesClient _corpses;
    readonly InteractTargets _targets;
    readonly CorpseChooser _chooser;
    InteractKey _useKey; // the interact key's press and release, one reading per tick
    InteractHold _corpseHold; // the hold for the primary position: counts only while the corpse owns the key and offers a weapon to replace it

    public CorpseLoot(ClientContext ctx, ItemsClient items, CorpsesClient corpses, InteractTargets targets)
    {
        _ctx = ctx; _items = items; _corpses = corpses; _targets = targets;
        _chooser = new CorpseChooser();
        _chooser.Picked += pos =>
        {
            if (_chooser.IsOpen) SendPickUp(_chooser.Corpse, pos);
            _chooser.Close();
        };
        _chooser.ItemPicked += index => { if (_chooser.IsOpen) _items.RequestTakeCorpseItem(_chooser.Corpse, index); }; // the cell leaves with the server's next list
        items.CorpseItemsChanged += corpse => { if (_chooser.IsOpen && _chooser.Corpse == corpse) _chooser.SetItems(ChooserItems(corpse)); };
        corpses.Despawned += id => { if (_chooser.IsOpen && _chooser.Corpse == id) _chooser.Close(); };
        targets.ChooserOpen = () => _chooser.IsOpen;
        ctx.Root.AddChild(_chooser);
    }

    /// <summary>True while the corpse chooser is up; the hotbar keys and wheel belong to it then.</summary>
    public bool ChooserOpen => _chooser.IsOpen;

    /// <summary>The chooser the HUD screens ask whether it is up and whether it frees the mouse.</summary>
    public CorpseChooser Chooser => _chooser;

    /// <summary>The weapon a corpse carries, or null when it is unknown.</summary>
    WeaponDef? CorpseWeapon(ushort id) =>
        _ctx.Weapons != null && _ctx.WorldEntities.Entities.TryGetValue(id, out var e) && e.State.State < _ctx.Weapons.Weapons.Count
            ? _ctx.Weapons.Weapons[e.State.State] : null;

    /// <summary>The corpse prompt in reach (hidden while the chooser is up); null otherwise.</summary>
    public string? Prompt()
    {
        if (_targets.Corpse is not { } id || !_targets.CorpseShowsPrompt) return null;
        var key = InputBindings.Name("interact");
        if (IsItemOnlyCorpse(id)) // the Bigfoot's and the Gang boss's corpse have no weapon to take, only what they carry (#264, Thieves 10)
            return _items.CorpseItems(id).Count switch { 0 => null, 1 => Strings.F("hud.corpse.giant.item", key, _items.ItemName(_items.CorpseItem(id)!)), _ => Strings.F("hud.corpse.giant.items", key) };
        if (CorpseWeapon(id) is not { } w) return null;
        var hold = CorpseRules.CanReplace(w, CorpseRules.PrimaryPos);
        if (_items.CorpseItems(id).Count > 1) return Strings.F(hold ? "hud.corpse.hold.items" : "hud.corpse.tap.items", key, ClientText.WeaponName(w.Id)); // a list: its own chooser names them
        if (_items.CorpseItem(id) is { } item) return Strings.F(hold ? "hud.corpse.hold.item" : "hud.corpse.tap.item", key, ClientText.WeaponName(w.Id), _items.ItemName(item));
        return hold
            ? Strings.F("hud.corpse.hold", key, ClientText.WeaponName(w.Id))
            : Strings.F("hud.corpse.tap", key, ClientText.WeaponName(w.Id));
    }

    /// <summary>
    /// Hold and tap of the interact key at a corpse. Held for <see cref="HoldSeconds"/> takes it into the primary
    /// position; released sooner opens the chooser. Runs on the input tick, with the same Use key as the vehicles.
    /// </summary>
    public void Tick(double dt)
    {
        var use = _ctx.InteractHeld;
        if (ChooserOpen && (_ctx.Dead || _ctx.Driving || _targets.Corpse != _chooser.Corpse)) _chooser.Close();
        var (_, released) = _useKey.Step(use);
        var near = _targets.Owner == InteractOwner.Corpse ? _targets.Corpse : null; // a care package in reach owns the key (ticket #143), and so does a ground item nearer than the corpse (#267)
        var holdWeapon = !ChooserOpen && near is { } hid && CorpseWeapon(hid) is { } hw && CorpseRules.CanReplace(hw, CorpseRules.PrimaryPos);
        var wasSent = _corpseHold.Sent;
        if (_corpseHold.Tick(use, holdWeapon, dt, HoldSeconds)) SendPickUp(near!.Value, CorpseRules.PrimaryPos);
        if (released && !wasSent && !ChooserOpen && near is { } tap)
        {
            if (IsItemOnlyCorpse(tap)) OpenChooser(tap, null);
            else if (CorpseWeapon(tap) is { } tw) OpenChooser(tap, tw);
        }
    }

    /// <summary>The corpse's Items as the chooser's cells, in the server's order.</summary>
    List<ChooserItem> ChooserItems(ushort corpse) =>
        _items.CorpseItems(corpse).Select(h => new ChooserItem(h, _items.ItemIcon(h.Def), _items.ItemName(h.Def))).ToList();

    /// <summary>A corpse that offers no weapon, only its Items: the giant's, and the Gang boss's with the Gold chain (owner <see cref="ThievesReward.ChainCorpseOwner"/>, drawn as a body like any other).</summary>
    bool IsItemOnlyCorpse(ushort corpse) =>
        _corpses.IsGiantCorpse(corpse) || _corpses.IsBaleCorpse(corpse) || _ctx.WorldEntities.Entities.TryGetValue(corpse, out var e) && e.State.Owner == ThievesReward.ChainCorpseOwner;

    /// <summary>Opens the chooser at a corpse. <paramref name="weapon"/> is null for a corpse that offers no weapon (<see cref="IsItemOnlyCorpse"/>): the Items are all it offers.</summary>
    void OpenChooser(ushort corpse, WeaponDef? weapon)
    {
        var options = new List<(int Pos, string Text)>();
        foreach (var pos in new[] { CorpseRules.PrimaryPos, CorpseRules.SecondaryPos })
        {
            if (weapon == null || !CorpseRules.CanReplace(weapon, pos) || pos >= _ctx.Hotbar.Slots.Length) continue;
            var current = ClientText.WeaponName(_ctx.Weapons!.Weapons[_ctx.Hotbar.Slots[pos]].Id);
            options.Add((pos, Strings.F(pos == CorpseRules.PrimaryPos ? "corpse.primary" : "corpse.secondary", current)));
        }
        var items = ChooserItems(corpse);
        if (options.Count == 0 && items.Count == 0) return;
        var title = options.Count == 0 // nothing of the weapon can be taken: the Items are the offer
            ? Strings.T("corpse.title.items") // each Item's name stands in its row
            : items.Count == 0 ? Strings.F("corpse.title", ClientText.WeaponName(weapon!.Id))
            : Strings.F("corpse.title.with_items", ClientText.WeaponName(weapon!.Id));
        _chooser.Open(corpse, title, options, items);
    }

    void SendPickUp(ushort corpse, int pos) => _ctx.Send(EventKind.PickUpCorpse, CorpseCodec.EncodePickUp(corpse, (byte)pos));
}
