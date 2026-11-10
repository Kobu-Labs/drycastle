using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

public static class App
{
    public static void Quit(Node n)
    {
        GameSettings.SaveWindowSize();
        n.GetTree().Quit();
    }
}

/// <summary>Modal dialog: a title, a body, action buttons and a Cancel that is always the last button and what Esc does.</summary>
public partial class DialogScreen : MenuScreen
{
    public record Choice(string Text, Action Run, string? Variation = null);

    readonly string _title, _body;
    readonly Choice[] _choices;
    readonly int _focus;
    readonly string? _cancelText;
    readonly double _auto; readonly Func<double, string>? _bodyFor; readonly Action? _autoRun;
    double _left;
    Label? _bodyLabel;
    int _shown = -1;

    public override bool Overlay => true;

    /// <param name="focus">Index into the choices that gets first focus (cancel is index <c>choices.Length</c>); destructive dialogs focus Cancel.</param>
    public DialogScreen(string title, string body, Choice[] choices, int focus = -1, string? cancelText = "dialog.cancel", Action? onCancel = null,
        double autoSeconds = 0, Func<double, string>? bodyFor = null, Action? autoRun = null) : base("HudScrim")
    {
        _title = title; _body = body; _choices = choices; _cancelText = cancelText; _onCancel = onCancel;
        _focus = focus < 0 ? choices.Length : focus;
        _auto = autoSeconds; _left = autoSeconds; _bodyFor = bodyFor; _autoRun = autoRun;
    }

    readonly Action? _onCancel;
    readonly List<Button> _buttons = new();

    protected override Control Build()
    {
        var card = new PanelContainer { ThemeTypeVariation = "MenuCard", CustomMinimumSize = new Vector2(640, 0) };
        var v = Ui.VBox();
        v.AddChild(Ui.Lbl(_title, "HudBig"));
        _bodyLabel = Ui.Lbl(_bodyFor != null ? _bodyFor(_left) : _body, "HudDim", 580, true);
        v.AddChild(_bodyLabel);
        v.AddChild(Ui.Gap(ShooterTheme.Space));
        var row = Ui.HBox();
        row.AddChild(Ui.Fill());
        foreach (var c in _choices)
        {
            var b = Ui.Btn(c.Text, () => { Shell.Pop(); c.Run(); }, c.Variation, 160);
            _buttons.Add(b); row.AddChild(b);
        }
        if (_cancelText != null)
        {
            var cb = Ui.Btn(Strings.T(_cancelText), Back, null, 160);
            _buttons.Add(cb); row.AddChild(cb);
        }
        v.AddChild(row);
        card.AddChild(v);
        return card;
    }

    protected override Control? FirstFocusTarget() => _buttons.Count == 0 ? null : _buttons[Math.Min(_focus, _buttons.Count - 1)];

    public override void Back() { Shell.Pop(); if (_auto > 0) _autoRun?.Invoke(); else _onCancel?.Invoke(); }

    public override void _Process(double delta)
    {
        if (_auto <= 0 || _bodyFor == null) return;
        _left -= delta;
        if (_left <= 0) { Back(); return; }
        var sec = (int)Math.Ceiling(_left);
        if (sec != _shown) { _shown = sec; _bodyLabel!.Text = _bodyFor(_left); }
    }
}

public partial class MainMenu : MenuScreen
{
    readonly Action _play, _settings;
    Button _first = null!;
    public MainMenu(Action play, Action settings) { _play = play; _settings = settings; }

    protected override Control Build()
    {
        var v = Ui.VBox();
        v.CustomMinimumSize = new Vector2(440, 0);
        v.AddChild(Ui.Lbl(Strings.T("app.title"), "HudDisplay"));
        v.AddChild(Ui.Lbl(Strings.T("app.subtitle"), "HudDim"));
        v.AddChild(Ui.Gap(ShooterTheme.Space * 4));
        _first = Ui.Btn(Strings.T("menu.play"), _play, "MenuPrimary");
        v.AddChild(_first);
        v.AddChild(Ui.Btn(Strings.T("menu.settings"), _settings));
        v.AddChild(Ui.Btn(Strings.T("menu.quit"), Back));
        v.AddChild(Ui.Gap(ShooterTheme.Space * 3));
        v.AddChild(Ui.Lbl(Strings.T("menu.hint"), "HudSmall"));
        return v;
    }

    protected override Control? FirstFocusTarget() => _first;

    /// <summary>The root of the flow: Esc asks to quit and never quits unasked.</summary>
    public override void Back() => Shell.Push(new DialogScreen(Strings.T("quit.title"), Strings.T("quit.menu.body"),
        new[] { new DialogScreen.Choice(Strings.T("quit.confirm"), () => App.Quit(this), "MenuDanger") }));
}

public partial class PauseScreen : MenuScreen
{
    readonly Action _resume, _disconnect;
    Button _first = null!;
    public PauseScreen(Action resume, Action disconnect) : base("HudScrim") { _resume = resume; _disconnect = disconnect; }

    protected override Control Build()
    {
        var v = Ui.VBox();
        v.CustomMinimumSize = new Vector2(440, 0);
        v.AddChild(Ui.Lbl(Strings.T("pause.title"), "HudDisplay"));
        v.AddChild(Ui.Gap(ShooterTheme.Space * 3));
        _first = Ui.Btn(Strings.T("pause.resume"), Back, "MenuPrimary");
        v.AddChild(_first);
        v.AddChild(Ui.Btn(Strings.T("menu.settings"), () => Shell.Push(new SettingsScreen(true))));
        v.AddChild(Ui.Btn(Strings.T("pause.disconnect"), () => Shell.Push(new DialogScreen(Strings.T("disconnect.title"), Strings.T("disconnect.body"),
            new[] { new DialogScreen.Choice(Strings.T("pause.disconnect"), _disconnect, "MenuDanger") }))));
        v.AddChild(Ui.Btn(Strings.T("menu.quit"), () => Shell.Push(new DialogScreen(Strings.T("quit.title"), Strings.T("quit.game.body"),
            new[] { new DialogScreen.Choice(Strings.T("quit.confirm"), () => App.Quit(this), "MenuDanger") }))));
        v.AddChild(Ui.Gap(ShooterTheme.Space * 2));
        v.AddChild(Ui.Lbl(Strings.T("pause.hint"), "HudSmall"));
        return v;
    }

    protected override Control? FirstFocusTarget() => _first;
    public override void Back() { Shell.Pop(); _resume(); }
}

public partial class LoadingScreen : MenuScreen
{
    readonly string _target;
    Label _stage = null!, _pct = null!;
    ProgressBar _bar = null!;
    public LoadingScreen(string target) { _target = target; }

    protected override Control Build()
    {
        var v = Ui.VBox();
        v.CustomMinimumSize = new Vector2(640, 0);
        v.AddChild(Ui.Lbl(Strings.T("loading.title"), "HudDisplay"));
        v.AddChild(Ui.Lbl(Strings.F("loading.target", _target), "HudDim"));
        v.AddChild(Ui.Gap(ShooterTheme.Space * 3));
        _bar = new ProgressBar { MinValue = 0, MaxValue = 1, Step = 0.001, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 24), MouseFilter = MouseFilterEnum.Ignore };
        v.AddChild(_bar);
        var row = Ui.HBox();
        _stage = Ui.Lbl("", null);
        _stage.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _pct = Ui.Lbl("0%", "HudDim", 64);
        _pct.HorizontalAlignment = HorizontalAlignment.Right;
        row.AddChild(_stage); row.AddChild(_pct);
        v.AddChild(row);
        return v;
    }

    protected override Control? FirstFocusTarget() => null;
    /// <summary>Loading cannot be cancelled while a step blocks the main thread, so Esc is deliberately inert here.</summary>
    public override void Back() { }

    public void Set(string stageKey, float fraction)
    {
        if (_bar == null) return;
        _stage.Text = Strings.T(stageKey);
        _bar.Value = fraction;
        _pct.Text = Strings.F("loading.pct", Mathf.RoundToInt(fraction * 100));
    }
}
