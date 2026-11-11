using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

public enum ToastKind { Info, Warn, Error }

/// <summary>
/// The menu shell (CanvasLayer above the HUD): one screen stack, one toast, scale from viewport height and the UI scale
/// setting, 150 ms skippable fade between screens, and the single owner of Esc (one level up; in a match with no screen
/// open it opens Pause) and of the mouse mode (explicitly Captured only while a match runs with no screen open).
/// </summary>
public partial class UiShell : CanvasLayer
{
    public const float FadeTime = 0.15f, RefHeight = 1080f, MinBaseScale = 0.8f;

    public static UiShell? Instance { get; private set; }
    /// <summary>True while a match is running (Esc then opens Pause and the mouse is captured with no screen open).</summary>
    public bool GameActive { get; set; }
    /// <summary>Builds the pause screen (set by the owner of the match).</summary>
    public Func<MenuScreen>? PauseFactory { get; set; }
    /// <summary>Screenshot mode switches the fade and toast timer off.</summary>
    public bool Instant { get; set; }

    Control _root = null!, _stackHost = null!;
    readonly List<MenuScreen> _stack = new();
    PanelContainer _toast = null!;
    Label _toastText = null!;
    TextureRect _toastIcon = null!;
    int _toastId;
    Tween? _fade;
    MenuScreen? _fading;
    Vector2 _lastSize;

    public MenuScreen? Top => _stack.Count > 0 ? _stack[^1] : null;
    public int Depth => _stack.Count;

    public override void _Ready()
    {
        Instance = this;
        Strings.Register();
        Layer = 20;
        _root = new Control { Theme = ShooterTheme.Build(), MouseFilter = Control.MouseFilterEnum.Ignore };
        AddChild(_root);
        _stackHost = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _stackHost.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_stackHost);
        _toast = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore, ThemeTypeVariation = "MenuToast" };
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", ShooterTheme.Space * 2);
        _toastIcon = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(ShooterTheme.Space * 6, ShooterTheme.Space * 6),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        _toastText = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddChild(_toastIcon);
        row.AddChild(_toastText);
        _toast.AddChild(row);
        _root.AddChild(_toast);
        Relayout();
        GetViewport().SizeChanged += Relayout;
        GameSettings.Changed += Relayout;
        GetTree().NodeAdded += WireSounds;
        TreeExiting += () => { GameSettings.Changed -= Relayout; GetTree().NodeAdded -= WireSounds; if (Instance == this) Instance = null; };
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) GameSettings.SaveWindowSize();
    }

    /// <summary>UI sounds for every menu control as it is added: hover (mouse, or keyboard focus move), press (accept for primary buttons, click otherwise). Back and error come from Pop and Toast.</summary>
    void WireSounds(Node n)
    {
        if (n is BaseButton b && IsAncestorOf(b))
        {
            b.MouseEntered += () => { if (!b.Disabled) Sfx.Ui("ui.hover"); };
            b.FocusEntered += () => { if (!b.Disabled && KeyNavigated()) Sfx.Ui("ui.hover"); };
            b.Pressed += () => Sfx.Ui(b.ThemeTypeVariation == "MenuPrimary" ? "ui.accept" : "ui.click");
        }
        else if (n is Slider sl && IsAncestorOf(sl)) sl.MouseEntered += () => Sfx.Ui("ui.hover");
    }

    static bool KeyNavigated() =>
        Input.IsActionJustPressed("ui_up") || Input.IsActionJustPressed("ui_down") || Input.IsActionJustPressed("ui_left") || Input.IsActionJustPressed("ui_right")
        || Input.IsActionJustPressed("ui_focus_next") || Input.IsActionJustPressed("ui_focus_prev");

    void Relayout()
    {
        var vp = GetViewport().GetVisibleRect().Size;
        var s = Math.Max(MinBaseScale, vp.Y / RefHeight) * HudLogic.ClampUiScale(GameSettings.Current.UiScale);
        _root.Scale = new Vector2(s, s);
        _root.Size = vp / s;
        _lastSize = vp;
        PlaceToast();
    }

    // ---- stack ----

    public void Push(MenuScreen s)
    {
        Top?.Park(s.Overlay);
        _stack.Add(s);
        _stackHost.AddChild(s);
        Present(s);
    }

    public void Pop()
    {
        if (_stack.Count == 0) return;
        Sfx.Ui("ui.back");
        var s = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        s.QueueFree();
        if (_fading == s) { _fade?.Kill(); _fading = null; }
        if (Top is { } t) { t.Unpark(); Present(t, false); }
        else UpdateMouse();
        UpdateMouse();
    }

    /// <summary>Drops every screen (no transition) and shows <paramref name="s"/> (or nothing).</summary>
    public void Reset(MenuScreen? s = null)
    {
        _fade?.Kill(); _fading = null;
        foreach (var o in _stack) o.QueueFree();
        _stack.Clear();
        if (s != null) { _stack.Add(s); _stackHost.AddChild(s); Present(s); }
        UpdateMouse();
    }

    void Present(MenuScreen s, bool fade = true)
    {
        UpdateMouse();
        s.Modulate = Colors.White;
        if (fade && !Instant && !GameSettings.Current.ReduceMotion)
        {
            _fade?.Kill();
            s.Modulate = new Color(1, 1, 1, 0);
            _fading = s;
            _fade = CreateTween();
            _fade.TweenProperty(s, "modulate:a", 1f, FadeTime);
            _fade.TweenCallback(Callable.From(() => _fading = null));
        }
        s.FocusFirst();
    }

    /// <summary>Mouse mode is explicit: captured only while a match runs and no screen is open; every screen releases it.</summary>
    void UpdateMouse()
    {
        if (DisplayServer.GetName() == "headless") return;
        var free = WantsFreeMouse?.Invoke() ?? false;
        var want = GameActive && _stack.Count == 0 && !free ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
        if (Input.MouseMode != want) Input.MouseMode = want;
    }

    /// <summary>True while the match asks for a free mouse with no screen open (the respawn screen, so its buttons can be clicked).</summary>
    public Func<bool>? WantsFreeMouse { get; set; }

    public override void _Process(double delta) => UpdateMouse();

    /// <summary>Called by the match owner when the match starts or ends.</summary>
    public void SetGameActive(bool on) { GameActive = on; UpdateMouse(); }

    // ---- input: one owner for Esc ----

    public override void _Input(InputEvent e)
    {
        // any press skips a running transition (the event still goes on)
        if (_fading != null && (e is InputEventKey or InputEventMouseButton) && e.IsPressed())
        {
            _fade?.Kill();
            _fading.Modulate = Colors.White;
            _fading = null;
        }
        if (Top is { } t && t.HandleInput(e)) { GetViewport().SetInputAsHandled(); return; }
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            if (Top is { } top) { top.Back(); GetViewport().SetInputAsHandled(); }
            else if (GameActive && PauseFactory != null) { Push(PauseFactory()); GetViewport().SetInputAsHandled(); }
        }
        // re-grab focus after a mouse click on empty space so the keyboard keeps working
        else if (e is InputEventKey { Pressed: true } && Top != null && GetViewport().GuiGetFocusOwner() == null) Top.FocusFirst();
    }

    // ---- toast: the one notification pattern ----

    /// <summary>Shows the one toast. <paramref name="icon"/> (optional) sits before the text; the text always says what happened.</summary>
    public void Toast(string text, ToastKind kind = ToastKind.Info, Texture2D? icon = null)
    {
        var prefix = Strings.T(kind switch { ToastKind.Warn => "toast.warn", ToastKind.Error => "toast.error", _ => "toast.info" });
        _toast.ThemeTypeVariation = kind switch { ToastKind.Warn => "MenuToastWarn", ToastKind.Error => "MenuToastError", _ => "MenuToast" };
        _toastText.Text = $"{prefix}  {text}";
        _toastIcon.Texture = icon;
        _toastIcon.Visible = icon != null;
        if (kind == ToastKind.Error) Sfx.Ui("ui.error");
        _toast.Visible = true;
        _toast.Modulate = Colors.White;
        PlaceToast();
        var id = ++_toastId;
        if (Instant) return;
        GetTree().CreateTimer(kind == ToastKind.Error ? 6.0 : 3.5).Timeout += () =>
        {
            if (id != _toastId || !IsInstanceValid(_toast)) return;
            if (GameSettings.Current.ReduceMotion) { _toast.Visible = false; return; }
            var tw = CreateTween();
            tw.TweenProperty(_toast, "modulate:a", 0f, FadeTime);
            tw.TweenCallback(Callable.From(() => { if (id == _toastId) _toast.Visible = false; }));
        };
    }

    void PlaceToast()
    {
        if (_toast == null) return;
        _toast.ResetSize();
        var sz = _toast.GetCombinedMinimumSize();
        _toast.Size = sz;
        _toast.Position = new Vector2((_root.Size.X - sz.X) / 2, _root.Size.Y - sz.Y - ShooterTheme.Margin * 2);
    }
}

/// <summary>A full-screen menu page: own backdrop variation, first-focus target, Back handler and an optional input grab.</summary>
public abstract partial class MenuScreen : Control
{
    protected UiShell Shell => UiShell.Instance!;
    readonly string _backdrop;
    protected Control Card = null!;
    Control? _lastFocus;

    protected MenuScreen(string backdrop = "MenuBackdrop") { _backdrop = backdrop; }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var bg = new PanelContainer { ThemeTypeVariation = _backdrop, MouseFilter = MouseFilterEnum.Stop };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);
        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);
        Card = Build();
        center.AddChild(Card);
    }

    protected abstract Control Build();
    protected abstract Control? FirstFocusTarget();
    /// <summary>Esc / Back: one level up. Default pops the screen.</summary>
    public virtual void Back() => Shell.Pop();
    /// <summary>Gets every input before Esc handling; return true to consume it (rebinding capture).</summary>
    public virtual bool HandleInput(InputEvent e) => false;

    /// <summary>Overlay screens (dialogs) sit on top of the screen below, which stays visible but inert.</summary>
    public virtual bool Overlay => false;
    public void Park(bool overlay) { _lastFocus = GetViewport()?.GuiGetFocusOwner(); if (overlay) ProcessMode = ProcessModeEnum.Disabled; else Visible = false; }
    public void Unpark() { Visible = true; ProcessMode = ProcessModeEnum.Inherit; }

    public void FocusFirst()
    {
        var target = _lastFocus != null && IsInstanceValid(_lastFocus) && _lastFocus.IsVisibleInTree() ? _lastFocus : FirstFocusTarget();
        _lastFocus = null;
        target?.CallDeferred(Control.MethodName.GrabFocus);
    }
}

/// <summary>Small control factory so screens hold no styling: everything comes from the Theme.</summary>
public static class Ui
{
    public static Label Lbl(string text, string? variation = null, float minW = 0, bool wrap = false)
    {
        var l = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(minW, 0) };
        if (variation != null) l.ThemeTypeVariation = variation;
        if (wrap) { l.AutowrapMode = TextServer.AutowrapMode.WordSmart; }
        return l;
    }

    public static Button Btn(string text, Action onPress, string? variation = null, float minW = 0)
    {
        var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(minW, 3 * ShooterTheme.Space * 2), MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        if (variation != null) b.ThemeTypeVariation = variation;
        b.Pressed += onPress;
        return b;
    }

    public static VBoxContainer VBox() => new();
    public static HBoxContainer HBox() => new();
    public static Control Gap(float h) => new Control { CustomMinimumSize = new Vector2(0, h) };
    public static Control Fill() => new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
}
