using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The Equipment screen (issue #268, F, rebindable action "inventory"): the player's figure in their Camouflage with the worn Items drawn, a cell for
/// each equipment slot at its body part (headgear above the head) and the six cells of the Bag. No title, labels, buttons or explanation text: an
/// Item is its icon, its pickup pips and a name tooltip (<see cref="ItemCell"/>), and the controls are implicit:
/// click or Enter on a worn Item takes it off into the bag (a full bag shakes the cell and plays the error sound), on a bagged Item puts it on (what was
/// worn goes into its cell); drag between the bag, a slot and the avatar (a valid target highlights); drag out of the panel or press Delete to drop an
/// Item on the ground (no confirmation: picking it up again costs a pickup); the arrows move focus. Over the head slot (hovered or focused) the figure
/// shows the head close up. One glyph-only hint row follows the active device.
/// It is a non-pausing overlay in the HUD, like the map: the mouse is released (UiShell asks <see cref="Hud.EquipmentOpen"/>), the movement keys still
/// work (<see cref="Player.MoveWithFreeMouse"/>), firing does not, and nothing pauses. It closes with F, Esc, death, the end of the match and a turret
/// or missile view. It never changes what it shows by itself: every move is a wish to the server, and the cells redraw on the next Equipment event.
/// The rules (which move a click, a key or a drop makes, where focus goes) are <see cref="EquipmentWishes"/> and <see cref="EquipmentNav"/>.
/// </summary>
public sealed partial class EquipmentScreen : Control
{
    /// <summary>Seconds after a wish before the cells dim to say they wait for the server, and after which a wish the server never answered (refused) is given up on.</summary>
    const double PendingDimAfter = 0.12, PendingGiveUp = 0.6;

    readonly NetClient _client;
    readonly CamoPreview _avatar;
    readonly PanelContainer _panel;
    readonly ItemCell[] _slots = new ItemCell[Equipment.SlotCount];
    readonly ItemCell[] _bag = new ItemCell[Bag.Cells];
    readonly Label _openKey = new(), _activateKey = new(), _dropKey = new();
    InputDevice _device = InputDevice.KeyboardMouse;
    CamoSpec? _shownLook;
    bool _open, _framingQueued;
    float _alpha;
    bool _pending, _dimmed;
    double _pendingSince;

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

        // the figure with the slots at their body part: headgear above the head
        var left = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddChild(left);
        _avatar = new CamoPreview(new Vector2(ShooterTheme.AvatarWidth, ShooterTheme.AvatarHeight)) { Focusable = false };
        _avatar.SetDropTarget(
            data => ItemCell.TryRead(data, out var from) && EquipmentWishes.CanDrop(from, DropTarget.Avatar, SlotOf),
            data => { if (ItemCell.TryRead(data, out var from)) Perform(EquipmentWishes.Drag(from, DropTarget.Avatar, SlotOf), null); });
        var headRow = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        for (var s = 0; s < _slots.Length; s++) _slots[s] = MakeCell(CellRef.Slot((EquipSlot)s), GhostFor((EquipSlot)s));
        headRow.AddChild(_slots[(int)EquipSlot.Headgear]); // a slot added to EquipSlot gets its place at its body part here
        left.AddChild(headRow);
        left.AddChild(_avatar.Root);
        left.AddChild(BuildHints());

        // the bag: three columns of two
        var grid = new GridContainer { Columns = EquipmentNav.BagColumns, MouseFilter = MouseFilterEnum.Ignore, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        row.AddChild(grid);
        for (var c = 0; c < _bag.Length; c++)
        {
            _bag[c] = MakeCell(CellRef.BagCell(c), null);
            grid.AddChild(_bag[c]);
        }
        _client.ItemsChanged += OnItemsChanged;
    }

    ItemCell MakeCell(CellRef at, Glyph.Kind? ghost)
    {
        var cell = new ItemCell { Ref = at, Draggable = true, Ghost = ghost };
        cell.Activated += c => Perform(EquipmentWishes.Activate(c.Ref, SlotOf, _client.BagIsFull), c);
        cell.DropPressed += c => Perform(EquipmentWishes.Drop(c.Ref, SlotOf), c);
        cell.Moved += OnMoved;
        cell.FocusChanged += (_, _) => QueueFraming();
        cell.AcceptDrop = (target, from) => EquipmentWishes.CanDrop(from, DropTarget.Onto(target.Ref), SlotOf);
        cell.Dropped = (target, from) => Perform(EquipmentWishes.Drag(from, DropTarget.Onto(target.Ref), SlotOf), target);
        return cell;
    }

    /// <summary>The ghost an empty slot shows: the kind of Item it takes.</summary>
    static Glyph.Kind? GhostFor(EquipSlot slot) => slot == EquipSlot.Headgear ? Glyph.Kind.Headgear : null;

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

    EquipSlot? SlotOf(CellRef at) => (at.Home == CellHome.Worn ? _client.Worn((EquipSlot)at.Index) : _client.InBag(at.Index))?.Def.Slot;

    ItemCell CellAt(CellRef at) => at.Home == CellHome.Worn ? _slots[at.Index] : _bag[at.Index];

    void Refresh()
    {
        for (var s = 0; s < _slots.Length; s++)
        {
            var held = _client.Worn((EquipSlot)s);
            Fill(_slots[s], held);
            _avatar.ShowWorn((EquipSlot)s, held?.Def);
        }
        for (var c = 0; c < _bag.Length; c++) Fill(_bag[c], _client.InBag(c));
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
        _open = true;
        Visible = true;
        MouseFilter = MouseFilterEnum.Stop;
        MouseBehaviorRecursive = MouseBehaviorRecursiveEnum.Inherited;
        if (_client.OwnLook is { } look && !look.Equals(_shownLook)) { _avatar.Show(look, _client.CamoData); _shownLook = look; }
        _avatar.ResetView();
        _avatar.Frame(CamoPreview.Framing.Body);
        SetHintNames();
        Refresh();
        SetAlpha(GameSettings.Current.ReduceMotion ? 1f : _alpha);
        SetProcess(true);
        Callable.From(FocusFirst).CallDeferred();
    }

    /// <summary>Starts hiding the screen (a 150 ms fade, none with reduce motion).</summary>
    public void Close()
    {
        if (!_open) return;
        _open = false;
        MouseFilter = MouseFilterEnum.Ignore;
        MouseBehaviorRecursive = MouseBehaviorRecursiveEnum.Disabled; // the fading screen takes no clicks: the mouse is captured again already
        GetViewport().GuiReleaseFocus(); // nor keys
        EndPending();
        if (GameSettings.Current.ReduceMotion) FinishClose();
    }

    void FinishClose()
    {
        SetAlpha(0f);
        Visible = false;
        SetProcess(false);
    }

    void SetAlpha(float a)
    {
        _alpha = a;
        Modulate = new Color(1, 1, 1, a);
    }

    void FocusFirst()
    {
        if (!_open) return;
        CellAt(EquipmentNav.First(c => SlotOf(c) != null)).FocusByKeys();
    }

    public override void _Process(double delta)
    {
        var target = _open ? 1f : 0f;
        if (!Mathf.IsEqualApprox(_alpha, target)) SetAlpha(Mathf.MoveToward(_alpha, target, (float)delta / UiShell.FadeTime));
        if (!_open && _alpha <= 0f) { FinishClose(); return; }
        if (_pending) UpdatePending();
    }

    // ---- the player's wishes ----

    /// <summary>
    /// Sends a wish to the server. While one waits for the answer (the next Equipment event) the others are ignored: a second click on the stale picture
    /// would undo the first (a swap twice is no swap). A wish the server never answers (a refusal) is given up on after <see cref="PendingGiveUp"/> seconds.
    /// </summary>
    void Perform(ItemWish wish, ItemCell? at)
    {
        if (_pending || !_open) return;
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
        _pending = true;
        _pendingSince = Time.GetTicksMsec() / 1000.0;
    }

    void UpdatePending()
    {
        var waited = Time.GetTicksMsec() / 1000.0 - _pendingSince;
        if (waited >= PendingGiveUp) { EndPending(); return; }
        if (waited >= PendingDimAfter && !_dimmed) SetDimmed(true);
    }

    void EndPending()
    {
        _pending = false;
        if (_dimmed) SetDimmed(false);
    }

    void SetDimmed(bool on)
    {
        _dimmed = on;
        foreach (var c in _slots) c.Disabled = on;
        foreach (var c in _bag) c.Disabled = on;
    }

    // ---- focus and framing ----

    void OnMoved(ItemCell from, NavDir dir)
    {
        if (EquipmentNav.Move(from.Ref, dir) is not { } to) return;
        Sfx.Ui("ui.hover");
        CellAt(to).FocusByKeys();
    }

    /// <summary>The figure shows the head close up while the headgear slot is hovered or focused (a hovered cell has the focus), and the whole body otherwise.</summary>
    void QueueFraming()
    {
        if (_framingQueued) return;
        _framingQueued = true;
        Callable.From(() =>
        {
            _framingQueued = false;
            if (_open) _avatar.Frame(_slots[(int)EquipSlot.Headgear].HasFocus() ? CamoPreview.Framing.Head : CamoPreview.Framing.Body);
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

    public override void _ExitTree() => _client.ItemsChanged -= OnItemsChanged;
}
