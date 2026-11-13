using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>An Item on a corpse as the chooser draws it: the Item (with its pickups), its icon and the name its tooltip reads.</summary>
public readonly record struct ChooserItem(HeldItem Item, Texture2D? Icon, string Name);

/// <summary>
/// The chooser opened by a tap of the interact key at a corpse (ticket #130): pick which hotbar position the corpse's weapon replaces (keys 1 and 2), and,
/// when the corpse carries Items (issues #261, #269: up to seven, worn and bagged), take one of them. The Items are a row of the same cells as the
/// Equipment screen (<see cref="ItemCell"/>: icon, pickup pips, name tooltip). A click or Enter on a cell takes that Item (the server puts it in its
/// slot if empty, else the bag, else swaps it with what is worn) and the cell leaves the list when the server's next list for the corpse arrives
/// (<see cref="SetItems"/>); a take the server never answers is given up on after <see cref="ItemCell.PendingGiveUp"/> seconds. A corpse with no Items shows
/// no list, and then the chooser is keyboard only as before, with the mouse captured; with a list the mouse is released (the shell asks
/// <see cref="HasItems"/>). The arrows move between the cells, Esc cancels and is consumed here so it never reaches the pause menu. Sizes and colours
/// come from the shared Theme and <see cref="ShooterTheme"/>; the chooser scales with the screen like the other overlays.
/// </summary>
public sealed partial class CorpseChooser : CanvasLayer
{
    /// <summary>Raised with the 0-based hotbar position the player picked (0 primary, 1 secondary).</summary>
    public event Action<int>? Picked;

    /// <summary>Raised with the index (into the corpse's list) of the Item the player picked. The chooser stays open for the next one.</summary>
    public event Action<int>? ItemPicked;

    readonly Control _root = new();
    readonly PanelContainer _panel = new();
    readonly Label _title = new();
    readonly VBoxContainer _rows = new();
    readonly HBoxContainer _cells = new();
    readonly List<int> _valid = new();
    readonly List<ItemCell> _pool = new();
    int _shown;
    bool _pending, _dimmed;
    double _pendingSince;

    public bool IsOpen { get; private set; }
    /// <summary>The corpse the chooser is about (valid while open).</summary>
    public ushort Corpse { get; private set; }
    /// <summary>The list of Items is up: the player picks with the mouse or Enter, so the mouse is released.</summary>
    public bool HasItems => IsOpen && _shown > 0;

    public CorpseChooser()
    {
        Layer = 20;
        Visible = false;
        _root.Theme = ShooterTheme.Build();
        _root.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(_root);
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(center);
        _panel.AddThemeStyleboxOverride("panel", ShooterTheme.Box(ShooterTheme.Surface, ShooterTheme.Space * 2, ShooterTheme.Line));
        center.AddChild(_panel);
        var column = new VBoxContainer();
        _panel.AddChild(column);
        _title.ThemeTypeVariation = "HudHead";
        _title.MouseFilter = Control.MouseFilterEnum.Ignore;
        column.AddChild(_title);
        _rows.MouseFilter = Control.MouseFilterEnum.Ignore;
        column.AddChild(_rows);
        _cells.MouseFilter = Control.MouseFilterEnum.Ignore;
        column.AddChild(_cells);
        var hint = new Label { Text = Strings.F("corpse.cancel", "Esc"), ThemeTypeVariation = "HudSmall", MouseFilter = Control.MouseFilterEnum.Ignore };
        column.AddChild(hint);
        SetProcess(false);
    }

    public override void _Ready()
    {
        Relayout();
        GetViewport().SizeChanged += Relayout;
        GameSettings.Changed += Relayout;
    }

    public override void _ExitTree()
    {
        GameSettings.Changed -= Relayout;
        if (GetViewport() != null) GetViewport().SizeChanged -= Relayout;
    }

    /// <summary>The root is the screen divided by the scale (the screen's height over 1080 times the UI scale setting), so the chooser reads the same at any size.</summary>
    void Relayout()
    {
        var vp = GetViewport().GetVisibleRect().Size;
        var s = ShooterTheme.ScreenScale(vp);
        _root.Scale = new Vector2(s, s);
        _root.Position = Vector2.Zero;
        _root.Size = vp / s;
    }

    /// <summary>
    /// Shows the chooser. <paramref name="options"/> holds (position, text) pairs, the position being the number key minus one; only those can be picked.
    /// <paramref name="items"/> are the Items of the corpse, in the server's order (the index a take names); none shows no list.
    /// </summary>
    public void Open(ushort corpse, string title, IReadOnlyList<(int Pos, string Text)> options, IReadOnlyList<ChooserItem> items)
    {
        Corpse = corpse;
        _valid.Clear();
        foreach (var child in _rows.GetChildren()) child.QueueFree();
        foreach (var (pos, text) in options)
        {
            _valid.Add(pos);
            _rows.AddChild(new Label { Text = Strings.F("corpse.option", pos + 1, text), MouseFilter = Control.MouseFilterEnum.Ignore });
        }
        _title.Text = title;
        _pending = false;
        FillCells(items);
        IsOpen = true;
        Visible = true;
        SetProcess(true);
        if (_shown > 0) Callable.From(() => { if (IsOpen && _shown > 0) _pool[0].FocusByKeys(); }).CallDeferred();
    }

    /// <summary>
    /// The server's list of the corpse's Items changed (one was taken, crumbled or swapped): shows it, the answer a take waited for. Focus stays at the
    /// same place in the list, so the next Item is under it. With nothing left to take (no weapon option either) the chooser closes.
    /// </summary>
    public void SetItems(IReadOnlyList<ChooserItem> items)
    {
        if (!IsOpen) return;
        EndPending();
        var focused = _pool.FindIndex(c => c.HasFocus());
        FillCells(items);
        if (_shown == 0 && _valid.Count == 0) { Close(); return; }
        if (focused >= 0 && _shown > 0) _pool[Math.Min(focused, _shown - 1)].FocusByKeys();
    }

    void FillCells(IReadOnlyList<ChooserItem> items)
    {
        while (_pool.Count < items.Count)
        {
            var index = _pool.Count;
            var cell = new ItemCell { Ref = CellRef.BagCell(index) }; // a list: only the index of the cell means something
            cell.Activated += c => Pick(c.Ref.Index);
            cell.Moved += OnMoved;
            _pool.Add(cell);
            _cells.AddChild(cell);
        }
        for (var i = 0; i < _pool.Count; i++)
        {
            var on = i < items.Count;
            _pool[i].Visible = on;
            if (on) _pool[i].SetItem(items[i].Item, items[i].Icon, items[i].Name);
            else _pool[i].SetItem(null, null, "");
        }
        _shown = items.Count;
    }

    void Pick(int index)
    {
        if (!IsOpen || _pending || index < 0 || index >= _shown) return;
        Sfx.Ui("ui.click");
        _pending = true;
        _pendingSince = Time.GetTicksMsec() / 1000.0;
        ItemPicked?.Invoke(index);
    }

    void OnMoved(ItemCell from, NavDir dir)
    {
        if (EquipmentNav.MoveInList(from.Ref.Index, dir, _shown) is not { } to) return;
        Sfx.Ui("ui.hover");
        _pool[to].FocusByKeys();
    }

    public override void _Process(double delta)
    {
        if (!_pending) return;
        var waited = Time.GetTicksMsec() / 1000.0 - _pendingSince;
        if (waited >= ItemCell.PendingGiveUp) EndPending();
        else if (waited >= ItemCell.PendingDimAfter && !_dimmed) SetDimmed(true);
    }

    void EndPending()
    {
        _pending = false;
        if (_dimmed) SetDimmed(false);
    }

    void SetDimmed(bool on)
    {
        _dimmed = on;
        foreach (var c in _pool) c.Disabled = on;
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        Visible = false;
        SetProcess(false);
        _valid.Clear();
        EndPending();
        _shown = 0;
        GetViewport().GuiReleaseFocus();
    }

    public override void _Input(InputEvent e)
    {
        if (!IsOpen || e is not InputEventKey { Pressed: true, Echo: false } k) return;
        int pos;
        switch (k.Keycode)
        {
            case Key.Key1: pos = 0; break;
            case Key.Key2: pos = 1; break;
            case Key.Escape: Close(); Sfx.Ui("ui.back"); GetViewport().SetInputAsHandled(); return;
            default: return;
        }
        GetViewport().SetInputAsHandled();
        if (!_valid.Contains(pos)) return;
        Picked?.Invoke(pos);
    }
}
