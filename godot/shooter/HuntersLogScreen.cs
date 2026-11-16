using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>What the Hunter's Log screen needs from the match: the queries, the pictures and the names (docs/hunters-log.md).</summary>
public sealed record HuntLogEnv(HuntLogData Data, HuntArtResolver Art, Func<string, string> SpeciesName, Func<string, string> WeaponName, Func<IReadOnlyList<string>> WeaponIds);

/// <summary>
/// The Hunter's Log (#336, J, rebindable action "hunters_log", also in the pause menu): the book of every wild animal a player hunted. A non-pausing overlay like the
/// Equipment screen, but a panel centred on about 60 % of the screen's width (on its own top layer, above the kill feed) so the world stays visible and live around it: the mouse is released (the shell
/// asks <see cref="Hud.HuntersLogOpen"/>), the movement keys still work (<see cref="Player.MoveWithFreeMouse"/>), firing does not, nothing pauses. It closes with its key,
/// Esc, death, the end of the match and a turret or missile view; Esc on another player's log first goes up to My Log (<see cref="HuntLogNav"/>).
/// Tabs, in order: My Log (the Life List of 10 index species and 3 Mystery creatures, one grid), Records (a card per species with its heaviest and its longest shot as
/// recorded hit pictures), All hunts (a sortable, filterable, paged table with the selected hunt's hit card beside it). Q / E, the bumpers and Page Up / Down change tab; the
/// arrows move focus. There is no hint text: every control is labelled by what it is. The server answers every query redacted (a Mystery creature the viewer has not caught is
/// a hidden id: a "?" and a blurred placeholder name); every query has a designed loading, empty and error (Retry) state, a late answer to an older request is dropped
/// (<see cref="HuntLoader{T}"/>), and nothing is rebuilt per frame (a pane is rebuilt when its answer arrives, the table's rows are pooled).
/// </summary>
public sealed partial class HuntersLogScreen : Control
{
    const int Tabs = 3;

    readonly HuntLogEnv _env;
    readonly HuntLogNav _nav = new();
    readonly PanelContainer _panel = new() { ThemeTypeVariation = "LogPanel", MouseFilter = MouseFilterEnum.Stop };
    readonly Button[] _tabs = new Button[Tabs];
    readonly Control[] _panes = new Control[Tabs];
    Label _title = null!;
    Button _back = null!, _close = null!;
    PanelFade _fade;
    bool _logStale, _recordsStale, _huntsStale;

    HuntLogData Data => _env.Data;
    bool _open => _fade.Wanted;
    public bool IsOpen => _open;

    public HuntersLogScreen(HuntLogEnv env)
    {
        _env = env;
        Visible = false;
        SetProcess(false);
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        BuildFrame();
        BuildLogPane();
        BuildRecordsPane();
        BuildHuntsPane();
        _panel.Resized += OnPanelResized;
        Data.Changed += OnDataChanged;
    }

    public override void _ExitTree() => Data.Changed -= OnDataChanged;

    // ---- the frame: header, tabs, the three panes ----

    void BuildFrame()
    {
        _panel.AnchorLeft = (1f - ShooterTheme.LogWidthShare) / 2; _panel.AnchorRight = 1f - _panel.AnchorLeft;
        _panel.AnchorTop = (1f - ShooterTheme.LogHeightShare) / 2; _panel.AnchorBottom = 1f - _panel.AnchorTop;
        _panel.OffsetLeft = 0; _panel.OffsetTop = 0; _panel.OffsetBottom = 0; _panel.OffsetRight = 0;
        AddChild(_panel);

        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _panel.AddChild(col);

        var head = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _back = Ui.Btn(Strings.T("log.back"), BackPressed);
        _back.Visible = false;
        _title = new Label { ThemeTypeVariation = "HudBig", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true, VerticalAlignment = VerticalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
        _close = new Button { ThemeTypeVariation = "LogClose", FocusMode = FocusModeEnum.All, TooltipText = Strings.T("log.close"), MouseDefaultCursorShape = CursorShape.PointingHand, CustomMinimumSize = new Vector2(ShooterTheme.LogCloseSize + ShooterTheme.Space * 2, ShooterTheme.LogCloseSize + ShooterTheme.Space * 2) };
        var cross = new Glyph(Glyph.Kind.Close, ShooterTheme.Text, ShooterTheme.LogCloseSize) { MouseFilter = MouseFilterEnum.Ignore };
        cross.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        cross.OffsetLeft = -ShooterTheme.LogCloseSize / 2f; cross.OffsetRight = ShooterTheme.LogCloseSize / 2f; cross.OffsetTop = -ShooterTheme.LogCloseSize / 2f; cross.OffsetBottom = ShooterTheme.LogCloseSize / 2f;
        _close.AddChild(cross);
        _close.Pressed += () => { Sfx.Ui("ui.back"); Close(); };
        head.AddChild(_back); head.AddChild(_title); head.AddChild(_close);
        col.AddChild(head);

        var tabs = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        var group = new ButtonGroup();
        for (var i = 0; i < Tabs; i++)
        {
            var index = i;
            var b = new Button { ThemeTypeVariation = "MenuTab", ToggleMode = true, ButtonGroup = group, FocusMode = FocusModeEnum.All, MouseDefaultCursorShape = CursorShape.PointingHand };
            b.Pressed += () => SelectTab(index);
            _tabs[i] = b;
            tabs.AddChild(b);
        }
        _tabs[1].Text = Strings.T("log.tab.records");
        _tabs[2].Text = Strings.T("log.tab.hunts");
        col.AddChild(tabs);

        // the panes share one space; the shown one is visible
        var stack = new MarginContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        stack.AddThemeConstantOverride("margin_top", ShooterTheme.Space);
        col.AddChild(stack);
        for (var i = 0; i < Tabs; i++)
        {
            _panes[i] = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true, Visible = i == 0, MouseFilter = MouseFilterEnum.Pass };
            stack.AddChild(_panes[i]);
        }
    }

    /// <summary>The control a pane's content goes into: a vertical box that fills the pane's width.</summary>
    static VBoxContainer NewHost(Control pane)
    {
        var host = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        pane.AddChild(host);
        return host;
    }

    // ---- open and close ----

    public void Open()
    {
        if (_open) return;
        _fade.Show(GameSettings.Current.ReduceMotion);
        Visible = true;
        MouseFilter = MouseFilterEnum.Ignore;
        MouseBehaviorRecursive = MouseBehaviorRecursiveEnum.Inherited;
        _nav.Reset();
        _logStale = _recordsStale = _huntsStale = true; // every pane asks again when it shows: the book changes while the screen is closed
        ApplyAlpha();
        SetProcess(true);
        ShowPage();
        Callable.From(FocusTab).CallDeferred();
    }

    /// <summary>Starts hiding the screen (a 150 ms fade, none with reduce motion).</summary>
    public void Close()
    {
        if (!_open) return;
        var finished = _fade.Hide(GameSettings.Current.ReduceMotion);
        MouseBehaviorRecursive = MouseBehaviorRecursiveEnum.Disabled; // the fading screen takes no clicks: the mouse is captured again already
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

    /// <summary>Esc: another player's log goes up to My Log; anything else closes. True when it was open (the Esc is used).</summary>
    public bool HandleBack()
    {
        if (!_open) return false;
        BackPressed();
        return true;
    }

    void BackPressed()
    {
        Sfx.Ui("ui.back");
        if (_nav.Back() == HuntLogBack.Close) { Close(); return; }
        ShowPage();
        Callable.From(FocusTab).CallDeferred();
    }

    public override void _Process(double delta)
    {
        var (changed, finished) = _fade.Tick((float)delta, UiShell.FadeTime);
        if (changed) ApplyAlpha();
        if (finished) { FinishClose(); return; }
        if (_open) Data.Tick((float)delta);
    }

    // ---- tabs ----

    void SelectTab(int index)
    {
        if (index == _nav.TabIndex && _tabs[index].ButtonPressed && _panes[index].Visible) return;
        Sfx.Ui("ui.click");
        _nav.ShowTab(index);
        ShowPage();
    }

    void StepTab(int by)
    {
        var next = (_nav.TabIndex + by + Tabs) % Tabs;
        Sfx.Ui("ui.hover");
        _nav.ShowTab(next);
        ShowPage();
        FocusTab();
    }

    void FocusTab() { if (_open) _tabs[_nav.TabIndex].GrabFocus(); }

    /// <summary>
    /// Shows the page the navigation names: tab states, title, the pane; asks for its data when it is stale (an answer redraws the pane, so a request ends here)
    /// and draws what it holds when it is not.
    /// </summary>
    void ShowPage()
    {
        for (var i = 0; i < Tabs; i++)
        {
            _tabs[i].SetPressedNoSignal(i == _nav.TabIndex);
            _panes[i].Visible = i == _nav.TabIndex;
        }
        var title = TitleText();
        _tabs[0].Text = title;
        _title.Text = title;
        _back.Visible = _nav.Page == HuntLogPage.PlayerLog;
        switch (_nav.Page)
        {
            case HuntLogPage.MyLog or HuntLogPage.PlayerLog:
                if (_logStale || Data.LogSubject != _nav.Subject) { _logStale = false; Data.RequestLog(_nav.Subject); return; }
                RebuildLog();
                break;
            case HuntLogPage.Records:
                if (_recordsStale) { _recordsStale = false; Data.RequestRecords(); return; }
                RebuildRecords();
                break;
            default:
                // the table needs the records too (the "Mystery hunts" option and the record pills)
                if (_recordsStale) { _recordsStale = false; Data.RequestRecords(); }
                if (_huntsStale) { _huntsStale = false; _selected = -1; Data.RequestHunts(drop: false); return; }
                RefreshHunts();
                break;
        }
    }

    string TitleText() => _nav.ViewingOther
        ? Strings.F("log.title.other", Data.Log.Data is { } l && l.PlayerId == _nav.Subject && l.Name != "" ? l.Name : _nav.SubjectName)
        : Strings.T("log.title.my");

    /// <summary>An answer came: redraw the pane that shows.</summary>
    void OnDataChanged()
    {
        if (!_open) return;
        ShowPage();
        Callable.From(EnsureFocus).CallDeferred();
    }

    /// <summary>A pane that was rebuilt freed the control that had the focus: the keys go to the tab again.</summary>
    void EnsureFocus()
    {
        if (!_open) return;
        var f = GetViewport().GuiGetFocusOwner();
        if (f == null || !IsAncestorOf(f) || !f.IsVisibleInTree()) FocusTab();
    }

    // ---- input ----

    public override void _Input(InputEvent e)
    {
        if (!_open) return;
        var step = e switch
        {
            InputEventKey { Pressed: true, Echo: false } k when k.PhysicalKeycode is Key.E or Key.Pagedown => 1,
            InputEventKey { Pressed: true, Echo: false } k when k.PhysicalKeycode is Key.Q or Key.Pageup => -1,
            InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.RightShoulder } => 1,
            InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.LeftShoulder } => -1,
            _ => 0,
        };
        if (step != 0) { StepTab(step); GetViewport().SetInputAsHandled(); return; }
        // the pad's B goes up one level like Esc (Esc itself is the shell's: it asks the HUD to go back before it opens Pause)
        if (e is InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.B }) { BackPressed(); GetViewport().SetInputAsHandled(); return; }
        // an arrow with nothing focused (the player clicked on the panel) starts from the tab instead of doing nothing
        if (GetViewport().GuiGetFocusOwner() == null && (e.IsActionPressed("ui_left") || e.IsActionPressed("ui_right") || e.IsActionPressed("ui_up") || e.IsActionPressed("ui_down")))
        {
            FocusTab();
            GetViewport().SetInputAsHandled();
        }
    }

    // ---- small builders the panes share ----

    static Label Text(string text, string? variation = null, HorizontalAlignment align = HorizontalAlignment.Left, bool wrap = false)
    {
        var l = new Label { Text = text, HorizontalAlignment = align, MouseFilter = MouseFilterEnum.Ignore };
        if (variation != null) l.ThemeTypeVariation = variation;
        if (wrap) l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        return l;
    }

    static void Clear(Node host)
    {
        foreach (var c in host.GetChildren()) { host.RemoveChild(c); c.QueueFree(); }
    }

    /// <summary>
    /// A loading, empty, error or missing state in the middle of its pane: a spinner (loading), the title, what happened (<paramref name="body"/> empty: just the title), and a
    /// button (Retry, or the action that leaves the state). <paramref name="action"/> null: no button.
    /// </summary>
    Control StateBox(string title, string body, bool spinner = false, string? actionText = null, Action? action = null)
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

    /// <summary>The Mystery creature's picture: a "?" and nothing else (no shape, the name stays unsent). The accent when the log's owner caught it, the dim ink when nobody did.</summary>
    static Control HiddenFigure(Vector2 size, bool caught)
    {
        var holder = new Control { CustomMinimumSize = size, MouseFilter = MouseFilterEnum.Ignore };
        var mark = Text(Strings.T("log.hidden.mark"), size.Y < ShooterTheme.Space * 12 ? "HudBig" : "HudDisplay", HorizontalAlignment.Center);
        mark.AddThemeColorOverride("font_color", caught ? ShooterTheme.Accent : ShooterTheme.Dim);
        mark.VerticalAlignment = VerticalAlignment.Center;
        mark.SetAnchorsPreset(LayoutPreset.FullRect);
        holder.AddChild(mark);
        return holder;
    }

    string AgoText(long unix)
    {
        var (unit, n) = HuntFormat.Ago(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), unix);
        return unit switch
        {
            AgoUnit.Now => Strings.T("log.ago.now"),
            AgoUnit.Minutes => Strings.F("log.ago.min", n),
            AgoUnit.Hours => Strings.F("log.ago.h", n),
            AgoUnit.Days => Strings.F("log.ago.d", n),
            _ => HuntFormat.Date(unix),
        };
    }

    string Kg(float? kg) => kg is { } v ? Strings.F("log.kg", HuntFormat.Kg(v)) : Strings.T("log.none");
    string Metres(float? m) => m is { } v ? Strings.F("log.m", HuntFormat.Number(v)) : Strings.T("log.none");

    /// <summary>The species name of a hunt as the table and cards word it: the exact animal where it differs from the index species (a doe), else the index species.</summary>
    string HuntSpeciesName(HuntView h) => !h.Hidden && h.Exact != "" && h.Exact != h.Species && !Mysteries.Is(h.Species) ? _env.SpeciesName(h.Exact) : _env.SpeciesName(h.Species);

    /// <summary>Jumps to All hunts of one species, heaviest first.</summary>
    void OpenSpeciesHunts(string species)
    {
        Sfx.Ui("ui.click");
        Data.Table.JumpToSpecies(HuntFormat.FilterKey(species));
        _huntsStale = true;
        _selected = -1;
        _nav.ShowTab(2);
        ShowPage();
        Callable.From(FocusFirstRow).CallDeferred();
    }

    /// <summary>Jumps to All hunts of one player, newest first.</summary>
    void OpenPlayerHunts(long player)
    {
        Sfx.Ui("ui.click");
        Data.Table.JumpToPlayer(player);
        _huntsStale = true;
        _selected = -1;
        _nav.ShowTab(2);
        ShowPage();
        Callable.From(FocusFirstRow).CallDeferred();
    }

    /// <summary>A name was picked: that player's log opens on the first tab (the viewer's own id is My Log).</summary>
    void OpenPlayerLog(long id, string name)
    {
        Sfx.Ui("ui.click");
        _nav.OpenPlayer(Data.ViewerKnown && id == Data.ViewerId ? 0 : id, name);
        _logStale = true;
        ShowPage();
        Callable.From(FocusTab).CallDeferred();
    }
}
