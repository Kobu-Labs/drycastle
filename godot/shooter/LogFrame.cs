using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The frame every collection log is built on (the Hunter's Log, the Mineral Log): a non-pausing panel centred on about 60 % of the screen's width and 85 % of its
/// height, opaque, dressed by a <see cref="LogSkin"/> (the board, the crest on its top edge, the header band with the title and the close button, the tabs as hung
/// signs, one scrolling pane per tab). It carries the skin's Theme, so the content a screen puts in the panes takes the skin too. It owns what the logs share: the
/// 150 ms fade (none with reduce motion), the keys that change tab (Q / E, the bumpers, Page Up / Down), the pad's B as Back, the arrow that starts from the tab
/// when nothing has the focus, and the loading / empty / error box. A log with one page has no tab row. What the tabs mean, what Back does and what the panes hold
/// is the screen's: it listens to <see cref="TabPressed"/>, <see cref="TabStepped"/>, <see cref="BackPressed"/>, <see cref="CloseRequested"/> and <see cref="Ticked"/>.
/// </summary>
public sealed partial class LogFrame : Control
{
    const int SlotW = ShooterTheme.Space * 18;

    public LogSkin Skin { get; }
    public Control Panel { get; }
    public Label Title { get; private set; } = null!;
    public Button Back { get; private set; } = null!;
    public Button CloseButton { get; private set; } = null!;
    public LogSign[] Tabs { get; }
    public Control[] Panes { get; }
    /// <summary>A row under the tabs for a line that holds on every tab (the Hunter's Log's sandbox notice).</summary>
    public VBoxContainer Notices { get; } = new() { MouseFilter = MouseFilterEnum.Ignore };

    public event Action<int>? TabPressed;
    /// <summary>Q / E, a bumper, Page Up / Down: one tab back (-1) or on (+1).</summary>
    public event Action<int>? TabStepped;
    public event Action? BackPressed;
    public event Action? CloseRequested;
    /// <summary>Once a frame while the log is wanted open.</summary>
    public event Action<float>? Ticked;

    PanelFade _fade;
    int _tab;

    public bool IsOpen => _fade.Wanted;

    public LogFrame(LogSkin skin, string title, int tabs = 1)
    {
        Skin = skin;
        Theme = skin.Theme;
        Visible = false;
        SetProcess(false);
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        Tabs = new LogSign[tabs > 1 ? tabs : 0];
        Panes = new Control[Math.Max(1, tabs)];
        Panel = new Control { MouseFilter = MouseFilterEnum.Stop };
        Build(title);
    }

    void Build(string title)
    {
        Panel.AnchorLeft = (1f - ShooterTheme.LogWidthShare) / 2; Panel.AnchorRight = 1f - Panel.AnchorLeft;
        Panel.AnchorTop = (1f - ShooterTheme.LogHeightShare) / 2; Panel.AnchorBottom = 1f - Panel.AnchorTop;
        Panel.OffsetLeft = 0; Panel.OffsetTop = 0; Panel.OffsetBottom = 0; Panel.OffsetRight = 0;
        AddChild(Panel);
        Panel.AddChild(new LogBoard(Skin));
        var crest = new LogCrestArt(Skin);
        crest.AnchorLeft = crest.AnchorRight = 0.5f; // its origin is the middle of the board's top edge
        Panel.AddChild(crest);

        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", LogSkin.Side);
        margin.AddThemeConstantOverride("margin_right", LogSkin.Side);
        margin.AddThemeConstantOverride("margin_bottom", ShooterTheme.Space * 2 + LogSkin.Frame);
        Panel.AddChild(margin);
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        margin.AddChild(col);

        // the header band: Back on the left, the title in the middle, the close button on the right (two slots of one width keep the title centred)
        var head = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(0, LogSkin.HeaderH) };
        var left = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(SlotW, 0), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        Back = Ui.Btn(Strings.T("log.back"), () => BackPressed?.Invoke());
        Back.Visible = false;
        left.AddChild(Back);
        Title = new Label
        {
            Text = title, ThemeTypeVariation = "LogTitle", Uppercase = true, SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore,
        };
        var right = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(SlotW, 0), Alignment = BoxContainer.AlignmentMode.End, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        var closeSize = ShooterTheme.LogCloseSize + ShooterTheme.Space * 3;
        CloseButton = new Button { ThemeTypeVariation = "LogClose", FocusMode = FocusModeEnum.All, TooltipText = Strings.T("log.close"), MouseDefaultCursorShape = CursorShape.PointingHand, CustomMinimumSize = new Vector2(closeSize, closeSize) };
        var cross = new Glyph(Glyph.Kind.Close, Skin.HeaderInk, ShooterTheme.LogCloseSize) { MouseFilter = MouseFilterEnum.Ignore };
        cross.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        cross.OffsetLeft = -ShooterTheme.LogCloseSize / 2f; cross.OffsetRight = ShooterTheme.LogCloseSize / 2f; cross.OffsetTop = -ShooterTheme.LogCloseSize / 2f; cross.OffsetBottom = ShooterTheme.LogCloseSize / 2f;
        CloseButton.AddChild(cross);
        CloseButton.Pressed += () => { Sfx.Ui("ui.back"); CloseRequested?.Invoke(); };
        right.AddChild(CloseButton);
        head.AddChild(left); head.AddChild(Title); head.AddChild(right);
        col.AddChild(head);

        if (Tabs.Length > 0)
        {
            var tabs = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            tabs.AddThemeConstantOverride("separation", ShooterTheme.Space + ShooterTheme.Space / 2);
            var group = new ButtonGroup();
            for (var i = 0; i < Tabs.Length; i++)
            {
                var index = i;
                var b = new LogSign(Skin) { ButtonGroup = group };
                b.Pressed += () => TabPressed?.Invoke(index);
                Tabs[i] = b;
                tabs.AddChild(b);
            }
            col.AddChild(tabs);
        }
        col.AddChild(Notices);

        // the panes share one space; the shown one is visible
        var stack = new MarginContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        col.AddChild(stack);
        for (var i = 0; i < Panes.Length; i++)
        {
            Panes[i] = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true, Visible = i == 0, MouseFilter = MouseFilterEnum.Pass };
            stack.AddChild(Panes[i]);
        }
    }

    /// <summary>The control a pane's content goes into: a vertical box that fills the pane's width, with room above for a pinned paper's tack and beside for the focus ring.</summary>
    public VBoxContainer NewHost(int pane)
    {
        var pad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        pad.AddThemeConstantOverride("margin_top", LogSkin.Tack + ShooterTheme.Space / 2);
        pad.AddThemeConstantOverride("margin_left", ShooterTheme.Space / 2);
        pad.AddThemeConstantOverride("margin_right", ShooterTheme.Space);
        pad.AddThemeConstantOverride("margin_bottom", ShooterTheme.Space);
        var host = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        host.AddThemeConstantOverride("separation", ShooterTheme.Space + ShooterTheme.Space / 2);
        pad.AddChild(host);
        Panes[pane].AddChild(pad);
        return host;
    }

    /// <summary>Marks the tab and shows its pane.</summary>
    public void ShowTab(int index)
    {
        _tab = Math.Clamp(index, 0, Panes.Length - 1);
        for (var i = 0; i < Panes.Length; i++)
        {
            if (i < Tabs.Length) Tabs[i].SetPressedNoSignal(i == _tab);
            Panes[i].Visible = i == _tab;
        }
        foreach (var t in Tabs) t.QueueRedraw(); // the tack moves with the current tab
    }

    /// <summary>The focus goes to the current tab, or to the close button of a log with one page.</summary>
    public void FocusTab()
    {
        if (!IsOpen) return;
        if (Tabs.Length > 0) Tabs[_tab].GrabFocus(); else CloseButton.GrabFocus();
    }

    /// <summary>A pane that was rebuilt freed the control that had the focus: the keys go to the tab again.</summary>
    public void EnsureFocus()
    {
        if (!IsOpen) return;
        var f = GetViewport().GuiGetFocusOwner();
        if (f == null || !IsAncestorOf(f) || !f.IsVisibleInTree()) FocusTab();
    }

    // ---- open and close ----

    public void Open()
    {
        if (IsOpen) return;
        _fade.Show(GameSettings.Current.ReduceMotion);
        Visible = true;
        MouseBehaviorRecursive = MouseBehaviorRecursiveEnum.Inherited;
        ApplyAlpha();
        SetProcess(true);
    }

    /// <summary>Starts hiding the log (a 150 ms fade, none with reduce motion).</summary>
    public void Close()
    {
        if (!IsOpen) return;
        var finished = _fade.Hide(GameSettings.Current.ReduceMotion);
        MouseBehaviorRecursive = MouseBehaviorRecursiveEnum.Disabled; // the fading log takes no clicks: the mouse is captured again already
        GetViewport().GuiReleaseFocus(); // nor keys
        if (finished) FinishClose();
    }

    void FinishClose()
    {
        ApplyAlpha();
        Visible = false;
        SetProcess(false);
    }

    void ApplyAlpha() => Modulate = new Color(1, 1, 1, _fade.Alpha);

    public override void _Process(double delta)
    {
        var (changed, finished) = _fade.Tick((float)delta, UiShell.FadeTime);
        if (changed) ApplyAlpha();
        if (finished) { FinishClose(); return; }
        if (IsOpen) Ticked?.Invoke((float)delta);
    }

    public override void _Input(InputEvent e)
    {
        if (!IsOpen) return;
        var step = Tabs.Length == 0 ? 0 : e switch
        {
            InputEventKey { Pressed: true, Echo: false } k when k.PhysicalKeycode is Key.E or Key.Pagedown => 1,
            InputEventKey { Pressed: true, Echo: false } k when k.PhysicalKeycode is Key.Q or Key.Pageup => -1,
            InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.RightShoulder } => 1,
            InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.LeftShoulder } => -1,
            _ => 0,
        };
        if (step != 0) { TabStepped?.Invoke(step); GetViewport().SetInputAsHandled(); return; }
        // the pad's B goes up one level like Esc (Esc itself is the shell's: it asks the HUD to go back before it opens Pause)
        if (e is InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.B }) { BackPressed?.Invoke(); GetViewport().SetInputAsHandled(); return; }
        // an arrow with nothing focused (the player clicked on the panel) starts from the tab instead of doing nothing
        if (GetViewport().GuiGetFocusOwner() == null && (e.IsActionPressed("ui_left") || e.IsActionPressed("ui_right") || e.IsActionPressed("ui_up") || e.IsActionPressed("ui_down")))
        {
            FocusTab();
            GetViewport().SetInputAsHandled();
        }
    }

    // ---- builders the logs share ----

    public static Label Text(string text, string? variation = null, HorizontalAlignment align = HorizontalAlignment.Left, bool wrap = false)
    {
        var l = new Label { Text = text, HorizontalAlignment = align, MouseFilter = MouseFilterEnum.Ignore };
        if (variation != null) l.ThemeTypeVariation = variation;
        if (wrap) l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        return l;
    }

    /// <summary>A section of a collection: its title, a carved line, and how much of it is collected.</summary>
    public Control Section(string title, string count)
    {
        var h = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        h.AddThemeConstantOverride("separation", ShooterTheme.Space + ShooterTheme.Space / 2);
        var t = Text(title, "LogSection");
        t.Uppercase = true;
        h.AddChild(t);
        h.AddChild(new LogRule(Skin));
        if (count != "") h.AddChild(Text(count, "HudSmall"));
        return h;
    }

    /// <summary>
    /// A loading, empty, error or missing state in the middle of its pane: a spinner (loading), the title, what happened (<paramref name="body"/> empty: just the title), and a
    /// button (Retry, or the action that leaves the state). <paramref name="action"/> null: no button.
    /// </summary>
    public static Control StateBox(string title, string body, bool spinner = false, string? actionText = null, Action? action = null)
    {
        var center = new CenterContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        var v = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(ShooterTheme.Space * 40, 0) };
        v.AddThemeConstantOverride("separation", ShooterTheme.Space);
        center.AddChild(v);
        if (spinner)
        {
            var s = new Spinner(ShooterTheme.Space * 6) { SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
            v.AddChild(s);
        }
        v.AddChild(Text(title, "HudHead", HorizontalAlignment.Center));
        if (body != "") v.AddChild(Text(body, "HudDim", HorizontalAlignment.Center, wrap: true));
        if (actionText != null && action != null)
        {
            var b = Ui.Btn(actionText, action, "MenuPrimary", ShooterTheme.Space * 16);
            b.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
            v.AddChild(b);
        }
        return center;
    }
}
