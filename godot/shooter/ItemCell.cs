using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The one item cell (issues #268, #269): the icon of an Item and its pickup pips (used of the limit), no text; the Item's name is a tooltip on hover or
/// keyboard focus. The Equipment screen's slots and bag and the corpse chooser's list are made of it. Every state has its own box from the shared Theme
/// (type "ItemCell"): normal, hover, focus (the accent ring), pressed, disabled, drag source and valid drop target; a cell that cannot take the item being
/// dragged is dimmed, so no state is colour alone. It is a control of its own and not a Button: Enter (not Space, which is jump) and a click activate it,
/// Delete asks to drop it, the arrows ask the screen to move focus. A refused cell shakes (a flash of the danger border with reduce motion) and plays the
/// error sound (<see cref="Refuse"/>). The mouse hovering a cell also gives it focus, so Enter and Delete act on what the player points at.
/// </summary>
public sealed partial class ItemCell : Control
{
    const string Type = "ItemCell";

    /// <summary>
    /// A screen that sent a wish ignores further ones until the server answers, so a second click on the stale picture cannot undo the first. Its cells dim
    /// (<see cref="Disabled"/>) when the answer takes longer than <see cref="PendingDimAfter"/> seconds (no flicker on a fast link), and it gives up on an
    /// answer that never comes (a refusal sends no event) after <see cref="PendingGiveUp"/> seconds.
    /// </summary>
    public const double PendingDimAfter = 0.12, PendingGiveUp = 0.6;
    /// <summary>The gap between two pips; a limit too large for the cell is drawn with the pips closer together.</summary>
    const int PipGap = ShooterTheme.Space / 2;

    /// <summary>What the cell is on its screen (a worn slot, a bag cell, or a cell of the chooser's list).</summary>
    public CellRef Ref { get; set; }

    /// <summary>Click or Enter.</summary>
    public event Action<ItemCell>? Activated;
    /// <summary>Delete.</summary>
    public event Action<ItemCell>? DropPressed;
    /// <summary>An arrow key: the screen decides where focus goes.</summary>
    public event Action<ItemCell, NavDir>? Moved;
    /// <summary>The cell gained or lost the focus.</summary>
    public event Action<ItemCell, bool>? FocusChanged;
    /// <summary>A drag is released over this cell: is the dragged cell dropped on it valid?</summary>
    public Func<ItemCell, CellRef, bool>? AcceptDrop { get; set; }
    /// <summary>A drag is dropped on this cell (it was accepted).</summary>
    public Action<ItemCell, CellRef>? Dropped { get; set; }

    /// <summary>Whether a drag can start here (the Equipment screen's cells; not the chooser's).</summary>
    public bool Draggable { get; set; }
    /// <summary>The kind of ghost drawn while the cell is empty (the slot's type); null for a plain empty cell.</summary>
    public Glyph.Kind? Ghost { get; set; }

    public HeldItem? Item => _item;

    HeldItem? _item;
    Texture2D? _icon;
    string _name = "";
    Glyph? _ghost;
    bool _hover, _pressed, _disabled, _dragSource, _dragActive, _valid, _dragged, _focusFromHover, _error;
    float _shakeX;
    Tween? _shake;
    StyleBoxFlat? _errorBox;
    PanelContainer? _tip;
    Label? _tipText;

    public ItemCell()
    {
        CustomMinimumSize = new Vector2(ShooterTheme.Cell, ShooterTheme.Cell);
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
    }

    public override void _Ready()
    {
        if (Ghost is { } kind)
        {
            _ghost = new Glyph(kind, GetThemeColor("ghost", Type), ShooterTheme.CellIcon);
            AddChild(_ghost);
            PlaceGhost();
        }
    }

    // ---- what it shows ----

    /// <summary>Shows an Item (its icon and name come from the screen), or nothing with null: the cell then shows its ghost.</summary>
    public void SetItem(HeldItem? item, Texture2D? icon, string name)
    {
        _item = item;
        _icon = item == null ? null : icon;
        _name = item == null ? "" : name;
        if (_ghost != null) _ghost.Visible = item == null;
        if (_tipText != null) _tipText.Text = _name;
        UpdateTip();
        QueueRedraw();
    }

    /// <summary>A disabled cell takes no input and is drawn dimmed (the screen waits for the server's answer).</summary>
    public bool Disabled
    {
        get => _disabled;
        set
        {
            if (_disabled == value) return;
            _disabled = value;
            if (value) { _pressed = false; _hover = false; }
            UpdateTip();
            QueueRedraw();
        }
    }

    /// <summary>The cell was refused something: it shakes (a flash of the danger border with reduce motion) and the error sound plays.</summary>
    public void Refuse()
    {
        Sfx.Ui("ui.error");
        if (!IsInsideTree()) return;
        _shake?.Kill();
        _errorBox = new StyleBoxFlat { DrawCenter = false, BorderColor = ShooterTheme.Danger }; // once per refusal, not per frame (the colour follows the colour-blind setting)
        _errorBox.SetBorderWidthAll(ShooterTheme.BorderStrong);
        _errorBox.SetCornerRadiusAll(ShooterTheme.Radius);
        _error = true;
        QueueRedraw();
        if (GameSettings.Current.ReduceMotion)
        {
            GetTree().CreateTimer(ShooterTheme.ShakeSeconds).Timeout += EndError;
            return;
        }
        _shake = CreateTween();
        _shake.TweenMethod(Callable.From<float>(t =>
        {
            _shakeX = Mathf.Sin(t * ShooterTheme.ShakeSwings * Mathf.Tau) * ShooterTheme.ShakeReach * (1f - t);
            QueueRedraw();
        }), 0f, 1f, ShooterTheme.ShakeSeconds);
        _shake.TweenCallback(Callable.From(EndError));
    }

    void EndError()
    {
        if (!IsInstanceValid(this)) return;
        _error = false;
        _shakeX = 0;
        QueueRedraw();
    }

    // ---- drawing ----

    string StyleName =>
        _disabled ? "disabled"
        : _dragSource ? "drag"
        : _dragActive && _valid ? "target"
        : _pressed ? "pressed"
        : _hover ? "hover"
        : "normal";

    public override void _Draw()
    {
        var area = new Rect2(Vector2.Zero, Size);
        DrawSetTransform(new Vector2(_shakeX, 0));
        DrawStyleBox(GetThemeStylebox(StyleName, Type), area);
        var dim = _disabled ? ShooterTheme.DisabledAlpha : _dragSource || (_dragActive && !_valid) ? ShooterTheme.DragDimAlpha : 1f;
        var tint = new Color(1, 1, 1, dim);
        if (_item is { } held)
        {
            var side = GetThemeConstant("icon_size", Type);
            if (_icon != null) DrawTextureRect(_icon, new Rect2(new Vector2((Size.X - side) / 2f, ShooterTheme.Space / 2f), new Vector2(side, side)), false, tint);
            DrawPips(held, dim);
        }
        if (HasFocus()) DrawStyleBox(GetThemeStylebox("focus", Type), area);
        if (_error && _errorBox != null) DrawStyleBox(_errorBox, area);
        DrawSetTransform(Vector2.Zero);
    }

    /// <summary>The pickups used of the limit: a filled pip per pickup used, a hollow one per pickup left, the warn colour once the next take makes the Item crumble. No pips for an Item with no limit.</summary>
    void DrawPips(HeldItem held, float alpha)
    {
        if (held.Def.PickupLimit is not { } limit || limit < 1) return;
        var size = GetThemeConstant("pip_size", Type);
        var step = Mathf.Min(size + PipGap, (Size.X - 2 * ShooterTheme.Space) / limit);
        var used = Math.Clamp(held.Pickups, 0, limit);
        var colour = used >= limit ? GetThemeColor("pip_last", Type) : GetThemeColor("pip_used", Type);
        var hollow = GetThemeColor("pip_left", Type);
        colour.A *= alpha; hollow.A *= alpha;
        var y = Size.Y - ShooterTheme.Space;
        var x0 = (Size.X - (limit - 1) * step) / 2f;
        for (var i = 0; i < limit; i++)
        {
            var at = new Vector2(x0 + i * step, y);
            if (i < used) DrawCircle(at, size / 2f, colour);
            else DrawCircle(at, size / 2f - 1f, hollow, false, ShooterTheme.BorderStrong / 2f);
        }
    }

    void PlaceGhost()
    {
        if (_ghost == null) return;
        var side = GetThemeConstant("icon_size", Type);
        _ghost.Size = new Vector2(side, side);
        _ghost.Position = new Vector2((Size.X - side) / 2f, ShooterTheme.Space / 2f);
    }

    // ---- notifications: hover, focus, drag ----

    public override void _Notification(int what)
    {
        switch ((long)what)
        {
            case NotificationResized:
                PlaceGhost();
                QueueRedraw();
                break;
            case NotificationMouseEnter:
                if (_disabled) break;
                _hover = true;
                Sfx.Ui("ui.hover");
                if (!GetViewport().GuiIsDragging() && !HasFocus() && FocusMode != FocusModeEnum.None) { _focusFromHover = true; GrabFocus(); }
                UpdateTip();
                QueueRedraw();
                break;
            case NotificationMouseExit:
                _hover = false;
                _pressed = false;
                // the focus that only the mouse gave goes with it, so the head close-up and the tooltip do not stay behind
                if (_focusFromHover && HasFocus() && !GetViewport().GuiIsDragging()) ReleaseFocus();
                _focusFromHover = false;
                UpdateTip();
                QueueRedraw();
                break;
            case NotificationFocusEnter:
                FocusChanged?.Invoke(this, true);
                UpdateTip();
                QueueRedraw();
                break;
            case NotificationFocusExit:
                FocusChanged?.Invoke(this, false);
                UpdateTip();
                QueueRedraw();
                break;
            case NotificationVisibilityChanged:
                if (!IsVisibleInTree()) { _hover = _pressed = false; _focusFromHover = false; EndDrag(); UpdateTip(); }
                break;
            case NotificationDragBegin:
                BeginDrag();
                break;
            case NotificationDragEnd:
                EndDrag();
                break;
        }
    }

    /// <summary>Called by the screen when it moves focus with the keys, so a later mouse exit does not take the focus away.</summary>
    public void FocusByKeys()
    {
        _focusFromHover = false;
        GrabFocus();
    }

    // ---- input ----

    static readonly (string Action, NavDir Dir)[] Directions =
    {
        ("ui_left", NavDir.Left), ("ui_right", NavDir.Right), ("ui_up", NavDir.Up), ("ui_down", NavDir.Down),
    };

    public override void _GuiInput(InputEvent e)
    {
        if (_disabled) return;
        // the arrows and the pad's directions are the ui_* actions (the game binds none of them)
        foreach (var (action, dir) in Directions)
            if (e.IsActionPressed(action))
            {
                Moved?.Invoke(this, dir);
                AcceptEvent();
                return;
            }
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } mb:
                if (mb.Pressed)
                {
                    _pressed = true;
                    _dragged = false;
                    _focusFromHover = false;
                    GrabFocus();
                }
                else
                {
                    var click = _pressed && !_dragged && new Rect2(Vector2.Zero, Size).HasPoint(mb.Position);
                    _pressed = false;
                    if (click) Activated?.Invoke(this);
                }
                QueueRedraw();
                AcceptEvent();
                break;
            case InputEventKey { Pressed: true, Echo: false } k when k.Keycode is Key.Enter or Key.KpEnter:
                Activated?.Invoke(this);
                AcceptEvent();
                break;
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Delete }:
                DropPressed?.Invoke(this);
                AcceptEvent();
                break;
            case InputEventJoypadButton { Pressed: true } j when j.ButtonIndex is JoyButton.A:
                Activated?.Invoke(this);
                AcceptEvent();
                break;
            case InputEventJoypadButton { Pressed: true } j when j.ButtonIndex is JoyButton.X:
                DropPressed?.Invoke(this);
                AcceptEvent();
                break;
        }
    }

    // ---- drag and drop (Godot's own: a payload, a check, a drop) ----

    const string PayloadKind = "item_cell";

    public override Variant _GetDragData(Vector2 atPosition)
    {
        if (!Draggable || _item == null || _disabled || _icon == null) return default;
        var side = GetThemeConstant("icon_size", Type) * GetGlobalTransformWithCanvas().Scale.X; // the preview lives outside the scaled HUD root
        SetDragPreview(new TextureRect
        {
            Texture = _icon,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(side, side),
            Size = new Vector2(side, side),
            Modulate = new Color(1, 1, 1, ShooterTheme.DragPreviewAlpha),
            MouseFilter = MouseFilterEnum.Ignore,
        });
        _dragged = true;
        _dragSource = true;
        _pressed = false;
        UpdateTip();
        QueueRedraw();
        return new Godot.Collections.Dictionary { ["kind"] = PayloadKind, ["home"] = (int)Ref.Home, ["index"] = Ref.Index };
    }

    /// <summary>Reads the payload of a drag of an item cell.</summary>
    public static bool TryRead(Variant data, out CellRef cell)
    {
        cell = default;
        if (data.VariantType != Variant.Type.Dictionary) return false;
        var d = data.AsGodotDictionary();
        if (!d.ContainsKey("kind") || d["kind"].AsString() != PayloadKind) return false;
        cell = new CellRef((CellHome)d["home"].AsInt32(), d["index"].AsInt32());
        return true;
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data) =>
        !_disabled && TryRead(data, out var from) && AcceptDrop?.Invoke(this, from) == true;

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (TryRead(data, out var from)) Dropped?.Invoke(this, from);
    }

    void BeginDrag()
    {
        if (!TryRead(GetViewport().GuiGetDragData(), out var from)) return;
        _dragActive = true;
        _valid = !_dragSource && !_disabled && AcceptDrop?.Invoke(this, from) == true;
        UpdateTip();
        QueueRedraw();
    }

    void EndDrag()
    {
        if (!_dragActive && !_dragSource) return;
        _dragActive = _dragSource = _valid = false;
        QueueRedraw();
    }

    // ---- the name tooltip ----

    void UpdateTip()
    {
        var want = _item != null && !_disabled && !_dragActive && !_dragSource && (_hover || HasFocus()) && IsVisibleInTree();
        if (!want)
        {
            if (_tip != null) _tip.Visible = false;
            return;
        }
        if (_tip == null)
        {
            _tipText = new Label { Text = _name, ThemeTypeVariation = "HudSmall", MouseFilter = MouseFilterEnum.Ignore };
            _tip = new PanelContainer { ThemeTypeVariation = "ItemTip", MouseFilter = MouseFilterEnum.Ignore, Visible = false, ZIndex = 4 };
            _tip.AddChild(_tipText);
            AddChild(_tip);
        }
        _tipText!.Text = _name;
        _tip.Visible = true;
        _tip.ResetSize();
        PlaceTip();
    }

    /// <summary>Above the cell, centred; below it when there is no room above, and kept on the screen sideways.</summary>
    void PlaceTip()
    {
        if (_tip == null) return;
        var size = _tip.GetCombinedMinimumSize();
        _tip.Size = size;
        var xf = GetGlobalTransformWithCanvas();
        var scale = Mathf.Max(0.01f, xf.Scale.X);
        var screen = GetViewportRect().Size;
        var x = (Size.X - size.X) / 2f;
        var y = -size.Y - ShooterTheme.Space / 2f;
        if (xf.Origin.Y + y * scale < 0) y = Size.Y + ShooterTheme.Space / 2f;
        var left = xf.Origin.X + x * scale;
        if (left < 0) x -= left / scale;
        var right = xf.Origin.X + (x + size.X) * scale;
        if (right > screen.X) x -= (right - screen.X) / scale;
        _tip.Position = new Vector2(x, y);
    }

    public override void _ExitTree()
    {
        _shake?.Kill();
    }
}
