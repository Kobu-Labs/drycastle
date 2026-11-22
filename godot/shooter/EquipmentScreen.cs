using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The Equipment screen (issue #268, F, rebindable action "inventory"): the player's figure in their Camouflage with the worn Items drawn, a cell for
/// each equipment slot (a column at the figure's head: headgear, then the neck) and the six cells of the Bag. No title, labels, buttons or explanation text: an
/// Item is its icon, its pickup pips and a name tooltip (<see cref="ItemCell"/>), and the controls are implicit:
/// click or Enter on a worn Item takes it off into the bag (a full bag shakes the cell and plays the error sound), on a bagged Item puts it on (what was
/// worn goes into its cell); drag between the bag, a slot and the avatar (a valid target highlights); drag out of the panel or press Delete to drop an
/// Item on the ground (no confirmation: picking it up again costs a pickup); the arrows move focus. Over the headgear or neck slot (hovered or focused) the figure
/// shows the head close up. One glyph-only hint row follows the active device.
/// Under the bag, after a line, is the Companion picker (#488): a cell for each Companion the player can have out (its species' silhouette, its name as a tooltip),
/// the one that is out filled and ticked ("with you" in its tooltip). Click or Enter on another puts that one out: the old one goes in a puff and the new one appears
/// beside the player, and the choice is kept with the Profile. A player who owns none sees one empty cell with a paw ghost. Companion cells do not drag or drop.
/// It is a non-pausing overlay in the HUD, like the map: the mouse is released (UiShell asks <see cref="Hud.EquipmentOpen"/>), the movement keys still
/// work (<see cref="Player.MoveWithFreeMouse"/>), firing does not, and nothing pauses. It closes with F, Esc, death, the end of the match and a turret
/// or missile view. It never changes what it shows by itself: every move is a wish to the server, and the cells redraw on the next Equipment event.
/// The rules (which move a click, a key or a drop makes, where focus goes) are <see cref="EquipmentWishes"/> and <see cref="EquipmentNav"/>.
/// </summary>
public sealed partial class EquipmentScreen : Control
{
    readonly NetClient _client;
    readonly CamoPreview _avatar;
    readonly PanelContainer _panel;
    readonly ItemCell[] _slots = new ItemCell[Equipment.SlotCount];
    readonly ItemCell[] _bag = new ItemCell[Bag.Cells];
    readonly List<ItemCell> _companions = new(); // the picker: one cell per Companion the player can pick, or one empty cell
    readonly GridContainer _companionGrid;
    readonly Label _openKey = new(), _activateKey = new(), _dropKey = new();
    InputDevice _device = InputDevice.KeyboardMouse;
    CamoSpec? _shownLook;
    bool _framingQueued, _quietFraming;
    PanelFade _fade; // open/close and the alpha: the same fade as the tactical map's
    PendingAnswer _wait; // a wish waiting for the server's answer
    bool _dimmed;

    bool _open => _fade.Wanted;
    public bool IsOpen => _open;

    public EquipmentScreen(NetClient client)
    {
        _client = client;
        Visible = false;
        SetProcess(false);
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);

        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);
        // Pass, not Stop: a drop that no cell takes bubbles up to this screen, which tells inside the panel (nothing) from outside it (the ground)
        _panel = new PanelContainer { ThemeTypeVariation = "MenuCard", MouseFilter = MouseFilterEnum.Pass };
        center.AddChild(_panel);

        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _panel.AddChild(row);

        // the figure with the slots in a column at its head, in body order from the top: headgear, then the neck
        var left = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddChild(left);
        _avatar = new CamoPreview(new Vector2(ShooterTheme.AvatarWidth, ShooterTheme.AvatarHeight)) { Focusable = false };
        _avatar.SetDropTarget(
            data => ItemCell.TryRead(data, out var from) && EquipmentWishes.CanDrop(from, DropTarget.Avatar, SlotOf, Plain),
            data => { if (ItemCell.TryRead(data, out var from)) Perform(EquipmentWishes.Drag(from, DropTarget.Avatar, SlotOf, Plain), null); });
        var figure = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        var slotColumn = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsVertical = SizeFlags.ShrinkBegin }; // the arrows walk it top to bottom in slot order (EquipmentNav)
        for (var s = 0; s < _slots.Length; s++)
        {
            _slots[s] = MakeCell(CellRef.Slot((EquipSlot)s), GhostFor((EquipSlot)s));
            slotColumn.AddChild(_slots[s]);
        }
        figure.AddChild(_avatar.Root);
        figure.AddChild(slotColumn);
        left.AddChild(figure);
        left.AddChild(BuildHints());

        // the bag: three columns of two; under it, after a line, the Companion picker in the same columns
        var right = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        row.AddChild(right);
        var grid = new GridContainer { Columns = EquipmentNav.BagColumns, MouseFilter = MouseFilterEnum.Ignore };
        right.AddChild(grid);
        for (var c = 0; c < _bag.Length; c++)
        {
            _bag[c] = MakeCell(CellRef.BagCell(c), null);
            grid.AddChild(_bag[c]);
        }
        right.AddChild(new HSeparator { MouseFilter = MouseFilterEnum.Ignore });
        _companionGrid = new GridContainer { Columns = EquipmentNav.BagColumns, MouseFilter = MouseFilterEnum.Ignore };
        right.AddChild(_companionGrid);
        RefreshCompanions();
        _client.ItemsChanged += OnItemsChanged;
        _client.CompanionsChanged += OnCompanionsChanged;
    }

    ItemCell MakeCell(CellRef at, Glyph.Kind? ghost)
    {
        var cell = new ItemCell { Ref = at, Draggable = true, Ghost = ghost };
        cell.Activated += c => Perform(EquipmentWishes.Activate(c.Ref, SlotOf, _client.BagIsFull, Plain), c);
        cell.DropPressed += c => Perform(EquipmentWishes.Drop(c.Ref, SlotOf), c);
        cell.Moved += OnMoved;
        cell.FocusChanged += (_, _) => QueueFraming();
        cell.AcceptDrop = (target, from) => EquipmentWishes.CanDrop(from, DropTarget.Onto(target.Ref), SlotOf, Plain);
        cell.Dropped = (target, from) => Perform(EquipmentWishes.Drag(from, DropTarget.Onto(target.Ref), SlotOf, Plain), target);
        return cell;
    }

    /// <summary>The ghost an empty slot shows: the kind of Item it takes.</summary>
    static Glyph.Kind? GhostFor(EquipSlot slot) => slot switch { EquipSlot.Headgear => Glyph.Kind.Headgear, EquipSlot.Neck => Glyph.Kind.Neck, _ => null };

    /// <summary>The hint row: a key cap and a pictogram for each of F (close), Enter (swap) and Delete (drop). No words.</summary>
    Control BuildHints()
    {
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddChild(Hint(_openKey, Glyph.Kind.Close));
        row.AddChild(Hint(_activateKey, Glyph.Kind.Swap));
        row.AddChild(Hint(_dropKey, Glyph.Kind.Drop));
        return row;
    }

    static Control Hint(Label key, Glyph.Kind pictogram)
    {
        var pair = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        var cap = new PanelContainer { ThemeTypeVariation = "KeyCap", MouseFilter = MouseFilterEnum.Ignore };
        key.ThemeTypeVariation = "HudSmall";
        key.MouseFilter = MouseFilterEnum.Ignore;
        cap.AddChild(key);
        pair.AddChild(cap);
        pair.AddChild(new Glyph(pictogram, ShooterTheme.Dim, ShooterTheme.Space * 3) { SizeFlagsVertical = SizeFlags.ShrinkCenter });
        return pair;
    }

    void SetHintNames()
    {
        var (open, activate, drop) = EquipmentHints.Names(_device, InputBindings.Name("inventory"));
        _openKey.Text = open;
        _activateKey.Text = activate;
        _dropKey.Text = drop;
    }

    // ---- what it shows ----

    /// <summary>A cell that holds a bag-only item (no slot): it cannot be put on.</summary>
    bool Plain(CellRef at) => (at.Home == CellHome.Worn ? _client.Worn((EquipSlot)at.Index) : _client.InBag(at.Index))?.Def.BagOnly == true;

    EquipSlot? SlotOf(CellRef at) => (at.Home == CellHome.Worn ? _client.Worn((EquipSlot)at.Index) : _client.InBag(at.Index))?.Def.Slot;

    ItemCell CellAt(CellRef at) => at.Home switch { CellHome.Worn => _slots[at.Index], CellHome.Companion => _companions[at.Index], _ => _bag[at.Index] };

    void Refresh()
    {
        for (var s = 0; s < _slots.Length; s++)
        {
            var held = _client.Worn((EquipSlot)s);
            Fill(_slots[s], held);
            _avatar.ShowWorn((EquipSlot)s, held?.Def);
        }
        for (var c = 0; c < _bag.Length; c++) Fill(_bag[c], _client.InBag(c));
        RefreshCompanions();
    }

    /// <summary>
    /// The picker's cells: one per Companion the player can pick, in the order of companions.json, the one that is out marked. With none to pick it is one empty cell
    /// (a paw ghost, "No Companion yet"), so the row never vanishes and the arrows always have somewhere to go.
    /// </summary>
    void RefreshCompanions()
    {
        var owned = _client.CompanionsOwned;
        var want = Math.Max(1, owned.Count);
        while (_companions.Count > want)
        {
            var last = _companions[^1];
            _companions.RemoveAt(_companions.Count - 1);
            _companionGrid.RemoveChild(last); // now, not at the frame's end: the grid lays out without it
            last.QueueFree();
        }
        while (_companions.Count < want)
        {
            var cell = new ItemCell { Ref = CellRef.Companion(_companions.Count), Ghost = Glyph.Kind.Paw, Disabled = _dimmed };
            cell.Activated += PickCompanion;
            cell.Moved += OnMoved;
            _companions.Add(cell);
            _companionGrid.AddChild(cell);
        }
        for (var i = 0; i < _companions.Count; i++)
        {
            if (i >= owned.Count) { _companions[i].SetPicture(null, Strings.T("companion.none"), false); continue; }
            var def = owned[i];
            var isOut = def == _client.CompanionOut;
            var name = _client.CompanionName(def);
            _companions[i].SetPicture(_client.CompanionIcon(def), isOut ? Strings.F("companion.out", name) : name, isOut);
        }
    }

    void OnCompanionsChanged()
    {
        EndPending();
        RefreshCompanions();
    }

    void Fill(ItemCell cell, HeldItem? held) =>
        cell.SetItem(held, held is { } h ? _client.ItemIcon(h.Def) : null, held is { } n ? _client.ItemName(n.Def) : "");

    void OnItemsChanged()
    {
        EndPending();
        if (_open) Refresh();
    }

    // ---- open and close ----

    public void Open()
    {
        if (_open) return;
        _fade.Show(GameSettings.Current.ReduceMotion);
        Visible = true;
        MouseFilter = MouseFilterEnum.Stop;
        MouseBehaviorRecursive = MouseBehaviorRecursiveEnum.Inherited;
        if (_client.OwnLook is { } look && !look.Equals(_shownLook)) { _avatar.Show(look, _client.CamoData); _shownLook = look; }
        _avatar.ResetView();
        _avatar.Frame(CamoPreview.Framing.Body);
        SetHintNames();
        Refresh();
        ApplyAlpha();
        SetProcess(true);
        Callable.From(FocusFirst).CallDeferred();
    }

    /// <summary>Starts hiding the screen (a 150 ms fade, none with reduce motion).</summary>
    public void Close()
    {
        if (!_open) return;
        var finished = _fade.Hide(GameSettings.Current.ReduceMotion);
        MouseFilter = MouseFilterEnum.Ignore;
        MouseBehaviorRecursive = MouseBehaviorRecursiveEnum.Disabled; // the fading screen takes no clicks: the mouse is captured again already
        GetViewport().GuiReleaseFocus(); // nor keys
        EndPending();
        if (finished) FinishClose();
    }

    /// <summary>The fade is over (its alpha is zero): hide the screen and stop processing.</summary>
    void FinishClose()
    {
        ApplyAlpha();
        Visible = false;
        SetProcess(false);
    }

    void ApplyAlpha() => Modulate = new Color(1, 1, 1, _fade.Alpha);

    /// <summary>
    /// Gives the first cell the focus for the keys. The figure keeps the whole-body framing it opens with: the head close-up is for a player who points at
    /// or moves to the head slot, not for the focus the screen starts on.
    /// </summary>
    void FocusFirst()
    {
        if (!_open) return;
        _quietFraming = true;
        CellAt(EquipmentNav.First(c => SlotOf(c) != null)).FocusByKeys();
        Callable.From(() => _quietFraming = false).CallDeferred(); // after the framing check the focus queued
    }

    public override void _Process(double delta)
    {
        _avatar.SetHigh(_client.OwnHigh ? _client.Plantation : null); // the figure shows the high (cigarette, red eyes) as the others see it
        var (changed, finished) = _fade.Tick((float)delta, UiShell.FadeTime);
        if (changed) ApplyAlpha();
        if (finished) { FinishClose(); return; }
        if (_wait.Active) UpdatePending();
    }

    // ---- the player's wishes ----

    /// <summary>
    /// Sends a wish to the server. While one waits for the answer (the next Equipment event) the others are ignored: a second click on the stale picture
    /// would undo the first (a swap twice is no swap). A wish the server never answers (a refusal) is given up on after <see cref="ItemCell.PendingGiveUp"/> seconds.
    /// </summary>
    void Perform(ItemWish wish, ItemCell? at)
    {
        if (_wait.Active || !_open) return;
        switch (wish.Kind)
        {
            case WishKind.Equip: _client.RequestEquip(wish.Index); break;
            case WishKind.Unequip: _client.RequestUnequip((EquipSlot)wish.Index); break;
            case WishKind.DropWorn: _client.RequestDropWorn((EquipSlot)wish.Index); break;
            case WishKind.DropBagged: _client.RequestDropBagged(wish.Index); break;
            case WishKind.BagFull:
                // the server would refuse it with this very toast; saying it here lets the cell answer at once
                at?.Refuse();
                UiShell.Instance?.Toast(Strings.T("toast.item.bagfull"), ToastKind.Warn);
                return;
            default: return;
        }
        Sfx.Ui("ui.click");
        _wait.Begin(Time.GetTicksMsec() / 1000.0);
    }

    /// <summary>Click or Enter on a Companion: a wish to have it out. The one that is out already, and the empty cell, do nothing. Waits for the answer like an item wish.</summary>
    void PickCompanion(ItemCell cell)
    {
        if (_wait.Active || !_open) return;
        var owned = _client.CompanionsOwned;
        if (cell.Ref.Index >= owned.Count || owned[cell.Ref.Index] == _client.CompanionOut) return;
        _client.RequestCompanion(owned[cell.Ref.Index]);
        Sfx.Ui("ui.click");
        _wait.Begin(Time.GetTicksMsec() / 1000.0);
    }

    void UpdatePending()
    {
        var now = Time.GetTicksMsec() / 1000.0;
        if (_wait.GiveUp(now, ItemCell.PendingGiveUp)) { EndPending(); return; }
        if (_wait.Dimmed(now, ItemCell.PendingDimAfter) && !_dimmed) SetDimmed(true);
    }

    void EndPending()
    {
        _wait.End();
        if (_dimmed) SetDimmed(false);
    }

    void SetDimmed(bool on)
    {
        _dimmed = on;
        foreach (var c in _slots) c.Disabled = on;
        foreach (var c in _bag) c.Disabled = on;
        foreach (var c in _companions) c.Disabled = on;
    }

    // ---- focus and framing ----

    void OnMoved(ItemCell from, NavDir dir)
    {
        if (EquipmentNav.Move(from.Ref, dir, companions: _companions.Count) is not { } to) return;
        Sfx.Ui("ui.hover");
        CellAt(to).FocusByKeys();
    }

    /// <summary>The figure shows the head close up while the headgear or neck slot is hovered or focused (a hovered cell has the focus), and the whole body otherwise.</summary>
    void QueueFraming()
    {
        if (_framingQueued) return;
        _framingQueued = true;
        Callable.From(() =>
        {
            _framingQueued = false;
            if (_open && !_quietFraming) _avatar.Frame(_slots[(int)EquipSlot.Headgear].HasFocus() || _slots[(int)EquipSlot.Neck].HasFocus() ? CamoPreview.Framing.Head : CamoPreview.Framing.Body);
        }).CallDeferred();
    }

    public override void _Input(InputEvent e)
    {
        if (!_open) return;
        // the hint row follows the device used last
        var device = e switch
        {
            InputEventJoypadButton => InputDevice.Gamepad,
            InputEventJoypadMotion { AxisValue: > 0.5f or < -0.5f } => InputDevice.Gamepad,
            InputEventKey or InputEventMouseButton or InputEventMouseMotion => InputDevice.KeyboardMouse,
            _ => _device,
        };
        if (device != _device) { _device = device; SetHintNames(); }
        // an arrow with nothing focused (the player clicked on the panel) starts from the first cell instead of doing nothing
        if (GetViewport().GuiGetFocusOwner() == null && (e.IsActionPressed("ui_left") || e.IsActionPressed("ui_right") || e.IsActionPressed("ui_up") || e.IsActionPressed("ui_down")))
        {
            FocusFirst();
            GetViewport().SetInputAsHandled();
        }
    }

    // ---- dropping outside the panel ----

    Rect2 PanelRect => new(_panel.Position, _panel.Size);

    /// <summary>The ground: anywhere on the screen outside the panel (the panel's own empty space cancels the drag).</summary>
    public override bool _CanDropData(Vector2 atPosition, Variant data) =>
        _open && ItemCell.TryRead(data, out var from) && !PanelRect.HasPoint(atPosition) && EquipmentWishes.CanDrop(from, DropTarget.Ground, SlotOf);

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (ItemCell.TryRead(data, out var from)) Perform(EquipmentWishes.Drag(from, DropTarget.Ground, SlotOf), null);
    }

    public override void _ExitTree()
    {
        _client.ItemsChanged -= OnItemsChanged;
        _client.CompanionsChanged -= OnCompanionsChanged;
    }
}
