using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// Settings: Graphics / Audio / Controls / Gameplay / Accessibility, one config file. Every change previews live on the
/// shared settings; Apply saves, Revert restores the saved values, Back with unsaved changes asks. Fullscreen changes
/// ask to keep within 10 s and revert on their own. PageUp/PageDown switch tab; rebinding captures the next key or mouse
/// button (Esc cancels; a key another action holds swaps the two and says so).
/// </summary>
public partial class SettingsScreen : MenuScreen
{
    static readonly string[] Tabs = { "graphics", "audio", "controls", "gameplay", "access" };

    GameSettings _saved, _draft;
    int _tab;
    readonly Button[] _tabBtn = new Button[Tabs.Length];
    VBoxContainer _page = null!;
    ScrollContainer _scroll = null!;
    Button _apply = null!, _revert = null!, _back = null!;
    string? _capturing;
    readonly Dictionary<string, Button> _keyBtn = new();
    readonly bool _overGame;

    public SettingsScreen(bool overGame, int tab = 0) : base(overGame ? "HudScrim" : "MenuBackdrop")
    {
        _overGame = overGame; _tab = tab;
        _saved = GameSettings.Current.Clone();
        _draft = _saved.Clone();
        GameSettings.Apply(_draft, false);
    }

    protected override Control Build()
    {
        var root = Ui.VBox();
        root.CustomMinimumSize = new Vector2(1040, 0);
        root.AddChild(Ui.Lbl(Strings.T("menu.settings"), "HudDisplay"));
        var tabs = Ui.HBox();
        var grp = new ButtonGroup();
        for (var i = 0; i < Tabs.Length; i++)
        {
            var idx = i;
            var b = Ui.Btn(Strings.T("tab." + Tabs[i]), () => ShowTab(idx), "MenuTab", 160);
            b.ToggleMode = true; b.ButtonGroup = grp; b.ButtonPressed = i == _tab;
            _tabBtn[i] = b; tabs.AddChild(b);
        }
        root.AddChild(tabs);
        _scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 460), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        _page = Ui.VBox();
        _page.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _scroll.AddChild(_page);
        root.AddChild(_scroll);
        var foot = Ui.HBox();
        _back = Ui.Btn(Strings.T("dialog.back"), Back, null, 160);
        foot.AddChild(_back);
        foot.AddChild(Ui.Fill());
        foot.AddChild(Ui.Lbl(Strings.T("settings.hint"), "HudSmall"));
        foot.AddChild(Ui.Fill());
        _revert = Ui.Btn(Strings.T("settings.revert"), Revert, null, 160);
        _apply = Ui.Btn(Strings.T("settings.apply"), Apply, "MenuPrimary", 160);
        foot.AddChild(_revert); foot.AddChild(_apply);
        root.AddChild(foot);
        var card = new PanelContainer { ThemeTypeVariation = "MenuCard" };
        card.AddChild(root);
        ShowTab(_tab);
        return card;
    }

    protected override Control? FirstFocusTarget() => _tabBtn[_tab];

    bool Dirty => !_draft.SameAs(_saved);

    void Changed()
    {
        GameSettings.Apply(_draft);
        RefreshButtons();
    }

    void RefreshButtons() { _apply.Disabled = _revert.Disabled = !Dirty; }

    public void ShowTab(int i)
    {
        _tab = i;
        _capturing = null;
        for (var k = 0; k < _tabBtn.Length; k++) _tabBtn[k].SetPressedNoSignal(k == i);
        foreach (var c in _page.GetChildren()) { _page.RemoveChild(c); c.QueueFree(); }
        _keyBtn.Clear();
        switch (Tabs[i])
        {
            case "graphics": BuildGraphics(); break;
            case "audio": BuildAudio(); break;
            case "controls": BuildControls(); break;
            case "gameplay": BuildGameplay(); break;
            default: BuildAccess(); break;
        }
        _scroll.ScrollVertical = 0;
        RefreshButtons();
    }

    // ---- row builders ----

    HBoxContainer Row(string labelKey, Control c, string? helpKey = null)
    {
        var panel = Ui.HBox();
        var lab = Ui.Lbl(Strings.T(labelKey), null, 340);
        panel.AddChild(lab);
        c.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        panel.AddChild(c);
        _page.AddChild(panel);
        if (helpKey != null) _page.AddChild(Ui.Lbl(Strings.T(helpKey), "HudSmall", 0, true));
        return panel;
    }

    void Slider(string labelKey, float min, float max, float step, float value, Func<float, string> fmt, Action<float> set, string? helpKey = null)
    {
        var h = Ui.HBox();
        var s = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = value, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter, CustomMinimumSize = new Vector2(280, 32), FocusMode = FocusModeEnum.All, Scrollable = false };
        var val = Ui.Lbl(fmt(value), null, 96);
        val.HorizontalAlignment = HorizontalAlignment.Right;
        s.ValueChanged += v => { val.Text = fmt((float)v); set((float)v); Changed(); };
        h.AddChild(s); h.AddChild(val);
        Row(labelKey, h, helpKey);
    }

    void Toggle(string labelKey, bool value, Action<bool> set, string? helpKey = null)
    {
        // a toggle button (not a check glyph): the pressed state has a frame and the text says On / Off, so it is never colour only
        var c = Ui.Btn(Strings.T(value ? "state.on" : "state.off"), () => { }, "MenuTab", 120);
        c.ToggleMode = true; c.ButtonPressed = value; c.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        c.Toggled += on => { c.Text = Strings.T(on ? "state.on" : "state.off"); set(on); Changed(); };
        var wrap = Ui.HBox(); wrap.AddChild(c);
        Row(labelKey, wrap, helpKey);
    }

    void Segmented(string labelKey, string[] names, int value, Action<int> set)
    {
        var h = Ui.HBox();
        var grp = new ButtonGroup();
        for (var i = 0; i < names.Length; i++)
        {
            var idx = i;
            var b = Ui.Btn(names[i], () => { set(idx); Changed(); }, "MenuTab", 80);
            b.ToggleMode = true; b.ButtonGroup = grp; b.ButtonPressed = i == value;
            h.AddChild(b);
        }
        Row(labelKey, h);
    }

    // ---- pages ----

    void BuildGraphics()
    {
        Toggle("graphics.fullscreen", _draft.Fullscreen, on =>
        {
            _draft.Fullscreen = on;
            if (!Instant) AskKeepDisplay(!on);
        });
        Toggle("graphics.vsync", _draft.VSync, on => _draft.VSync = on);
        Segmented("graphics.msaa", GameSettings.MsaaNames, _draft.Msaa, i => _draft.Msaa = i);
        Slider("graphics.brightness", SettingsRanges.BrightnessMin * 100, SettingsRanges.BrightnessMax * 100, 5, _draft.Brightness * 100, v => Strings.F("fmt.percent", (int)v), v => _draft.Brightness = v / 100f);
    }

    /// <summary>Set by the screenshot mode: no timed dialogs.</summary>
    public static bool Instant { get; set; }

    void AskKeepDisplay(bool previousFullscreen)
    {
        GameSettings.Apply(_draft);
        Shell.Push(new DialogScreen(Strings.T("display.title"), "", new[] { new DialogScreen.Choice(Strings.T("display.keep"), () => { }, "MenuPrimary") },
            focus: 0, cancelText: "display.revert", onCancel: null, autoSeconds: 10,
            bodyFor: left => Strings.F("display.body", (int)Math.Ceiling(left)),
            autoRun: () => { _draft.Fullscreen = previousFullscreen; Changed(); ShowTab(_tab); }));
    }

    void BuildAudio()
    {
        for (var i = 0; i < GameSettings.Buses.Length; i++)
        {
            var bus = i;
            Slider("audio." + GameSettings.Buses[i].ToLowerInvariant(), 0, 1, 0.05f, _draft.Volume[i], v => Strings.F("fmt.percent", Mathf.RoundToInt(v * 100)), v => _draft.Volume[bus] = v);
        }
        _page.AddChild(Ui.Lbl(Strings.T("audio.help"), "HudSmall", 0, true));
    }

    void BuildGameplay()
    {
        Slider("gameplay.sensitivity", SettingsRanges.SensMin, SettingsRanges.SensMax, 0.05f, _draft.Sensitivity, v => Strings.F("fmt.times", v), v => _draft.Sensitivity = v);
        Toggle("gameplay.invert", _draft.InvertY, on => _draft.InvertY = on);
        Slider("gameplay.fov", SettingsRanges.FovMin, SettingsRanges.FovMax, 1, _draft.Fov, v => Strings.F("fmt.degrees", (int)v), v => _draft.Fov = v);
        Toggle("gameplay.crosshair", _draft.ShowCrosshair, on => _draft.ShowCrosshair = on);
    }

    void BuildAccess()
    {
        Slider("access.scale", HudLogic.UiScaleMin * 100, HudLogic.UiScaleMax * 100, 5, _draft.UiScale * 100, v => Strings.F("fmt.percent", (int)v), v => _draft.UiScale = v / 100f);
        Toggle("access.motion", _draft.ReduceMotion, on => _draft.ReduceMotion = on, "access.motion.help");
        Toggle("access.colour", _draft.ColorBlind, on => _draft.ColorBlind = on, "access.colour.help");
    }

    void BuildControls()
    {
        foreach (var a in _draft.Keys.Actions)
        {
            var action = a;
            var b = Ui.Btn("", () => BeginCapture(action), null, 240);
            _keyBtn[a] = b;
            Row(InputBindings.LabelKey(a), b);
        }
        RefreshKeys();
        var reset = Ui.Btn(Strings.T("controls.reset"), () =>
        {
            _draft.Keys.Reset(); Changed(); RefreshKeys();
            Shell.Toast(Strings.T("controls.reset.done"));
        }, null, 280);
        _page.AddChild(Ui.Gap(ShooterTheme.Space));
        _page.AddChild(reset);
        _page.AddChild(Ui.Lbl(Strings.T("controls.help"), "HudSmall", 0, true));
    }

    void RefreshKeys()
    {
        foreach (var (a, b) in _keyBtn)
            b.Text = _capturing == a ? Strings.T("controls.press") : InputBindings.Name(_draft.Keys.Get(a));
    }

    void BeginCapture(string action) { _capturing = action; RefreshKeys(); }

    public override bool HandleInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false } k)
        {
            if (_capturing != null)
            {
                if (k.PhysicalKeycode == Key.Escape || k.Keycode == Key.Escape) { EndCapture(); return true; }
                return Capture(e);
            }
            if (k.Keycode is Key.Pageup or Key.Pagedown)
            {
                var n = (_tab + (k.Keycode == Key.Pagedown ? 1 : Tabs.Length - 1)) % Tabs.Length;
                ShowTab(n); _tabBtn[n].GrabFocus();
                return true;
            }
        }
        else if (_capturing != null && e is InputEventMouseButton { Pressed: true }) return Capture(e);
        return false;
    }

    bool Capture(InputEvent e)
    {
        if (InputBindings.FromEvent(e) is not { } b) return true;
        var action = _capturing!;
        var swapped = _draft.Keys.Assign(action, b);
        EndCapture();
        Changed();
        if (swapped != null)
            Shell.Toast(Strings.F("controls.swapped", InputBindings.Name(b), Strings.T(InputBindings.LabelKey(swapped)), InputBindings.Name(_draft.Keys.Get(swapped))), ToastKind.Warn);
        return true;
    }

    void EndCapture() { _capturing = null; RefreshKeys(); }

    // ---- apply / revert / back ----

    void Apply()
    {
        _draft.Save();
        _saved = _draft.Clone();
        RefreshButtons();
        Shell.Toast(Strings.T("settings.saved"));
    }

    void Revert()
    {
        _draft = _saved.Clone();
        GameSettings.Apply(_draft);
        ShowTab(_tab);
        Shell.Toast(Strings.T("settings.reverted"));
    }

    public override void Back()
    {
        if (_capturing != null) { EndCapture(); return; }
        if (!Dirty) { Shell.Pop(); return; }
        Shell.Push(new DialogScreen(Strings.T("settings.unsaved.title"), Strings.T("settings.unsaved.body"), new[]
        {
            new DialogScreen.Choice(Strings.T("settings.apply"), () => { Apply(); Shell.Pop(); }, "MenuPrimary"),
            new DialogScreen.Choice(Strings.T("settings.discard"), () => { Revert(); Shell.Pop(); }, "MenuDanger"),
        }, focus: 0));
    }

    public override void _ExitTree()
    {
        // leaving by any route without Apply restores what is saved
        if (Dirty) GameSettings.Apply(_saved);
    }
}
