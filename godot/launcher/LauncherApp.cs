using System.Diagnostics;
using Godot;
using Suchohrad.LauncherCore;

namespace Suchohrad.Launcher;

/// <summary>
/// The launcher screen: checks the release feed, downloads/updates the game and the map (LauncherCore), then starts the game.
/// States: Checking, Ready, UpdateAvailable, Working (progress, Esc cancels), Failed (message + Try again; the installed version stays playable).
/// Esc: closes Settings, cancels a running download, otherwise nothing (the launcher never quits without the Quit button).
/// </summary>
public partial class LauncherApp : Control
{
    enum State { Checking, Ready, UpdateAvailable, Working, Failed }

    static readonly int[] Scales = { 75, 100, 125, 150 };

    State _state = State.Checking;
    Installer _installer = null!;
    ReleaseFeed _feed = null!;
    LauncherSettings _settings = null!;
    Manifest? _latest;
    CancellationTokenSource? _cts;

    VBoxContainer _main = null!, _settingsBox = null!;
    Label _version = null!, _status = null!, _error = null!, _detail = null!, _hint = null!;
    ProgressBar _bar = null!;
    Button _primary = null!, _settingsBtn = null!, _quit = null!, _retry = null!;
    OptionButton _channel = null!, _scale = null!;
    CheckBox _auto = null!;
    bool _loadingSettings;

    public override void _Ready()
    {
        Theme = LauncherTheme.Build();
        var root = Installer.DefaultRoot();
        _installer = new Installer(ReleaseFeed.CreateClient(TimeSpan.FromMinutes(30)), root);
        _feed = new ReleaseFeed(ReleaseFeed.CreateClient());
        _settings = LauncherSettings.Load(root);
        BuildUi();
        ApplyScale();
        try { _installer.Recover(); } catch (Exception e) { GD.PrintErr("recover: " + e.Message); }
        _ = CheckAsync();
    }

    // ---- UI construction (theme variations only; no per-control overrides) ----

    void BuildUi()
    {
        var bg = new ColorRect { Color = LauncherTheme.Backdrop, MouseFilter = MouseFilterEnum.Ignore };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(LauncherTheme.Space * 64, 0) };
        center.AddChild(panel);
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", LauncherTheme.Space * 2);
        panel.AddChild(col);

        col.AddChild(new Label { Text = S.T("title"), ThemeTypeVariation = "Display", HorizontalAlignment = HorizontalAlignment.Center });
        _version = new Label { ThemeTypeVariation = "Dim", HorizontalAlignment = HorizontalAlignment.Center };
        col.AddChild(_version);

        _main = new VBoxContainer();
        _main.AddThemeConstantOverride("separation", LauncherTheme.Space * 2);
        col.AddChild(_main);
        _status = new Label { ThemeTypeVariation = "Head", HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _main.AddChild(_status);
        _bar = new ProgressBar { MaxValue = 1000, ShowPercentage = false, CustomMinimumSize = new Vector2(0, LauncherTheme.Space * 3), Visible = false };
        _main.AddChild(_bar);
        _detail = new Label { ThemeTypeVariation = "Small", HorizontalAlignment = HorizontalAlignment.Center, Visible = false };
        _main.AddChild(_detail);
        _error = new Label { ThemeTypeVariation = "Error", HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false };
        _main.AddChild(_error);

        _primary = new Button { ThemeTypeVariation = "Primary", CustomMinimumSize = new Vector2(0, LauncherTheme.ButtonHeight * 1.25f), FocusMode = FocusModeEnum.All };
        _primary.Pressed += OnPrimary;
        _main.AddChild(_primary);
        _retry = new Button { Text = S.T("retry"), CustomMinimumSize = new Vector2(0, LauncherTheme.ButtonHeight), Visible = false };
        _retry.Pressed += () => _ = CheckAsync();
        _main.AddChild(_retry);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", LauncherTheme.Space * 2);
        _main.AddChild(row);
        _settingsBtn = new Button { Text = S.T("settings"), SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, LauncherTheme.ButtonHeight) };
        _settingsBtn.Pressed += () => ShowSettings(true);
        _quit = new Button { Text = S.T("quit"), SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, LauncherTheme.ButtonHeight) };
        _quit.Pressed += () => GetTree().Quit();
        row.AddChild(_settingsBtn);
        row.AddChild(_quit);

        _settingsBox = BuildSettings();
        _settingsBox.Visible = false;
        col.AddChild(_settingsBox);

        _hint = new Label { ThemeTypeVariation = "Small", HorizontalAlignment = HorizontalAlignment.Center };
        col.AddChild(_hint);
    }

    VBoxContainer BuildSettings()
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", LauncherTheme.Space * 2);
        box.AddChild(new Label { Text = S.T("settings"), ThemeTypeVariation = "Head", HorizontalAlignment = HorizontalAlignment.Center });

        box.AddChild(new Label { Text = S.T("set_channel"), ThemeTypeVariation = "Dim" });
        _channel = new OptionButton { CustomMinimumSize = new Vector2(0, LauncherTheme.ButtonHeight), FitToLongestItem = true };
        _channel.AddItem(S.T("channel_stable"));
        _channel.AddItem(S.T("channel_dev"));
        _channel.ItemSelected += _ => OnSettingsChanged();
        box.AddChild(_channel);

        _auto = new CheckBox { Text = S.T("set_auto"), CustomMinimumSize = new Vector2(0, LauncherTheme.ButtonHeight) };
        _auto.Toggled += _ => OnSettingsChanged();
        box.AddChild(_auto);

        box.AddChild(new Label { Text = S.T("set_scale"), ThemeTypeVariation = "Dim" });
        _scale = new OptionButton { CustomMinimumSize = new Vector2(0, LauncherTheme.ButtonHeight) };
        foreach (var s in Scales) _scale.AddItem($"{s} %");
        _scale.ItemSelected += _ => OnSettingsChanged();
        box.AddChild(_scale);

        _dirtyNote = new Label { ThemeTypeVariation = "Small", HorizontalAlignment = HorizontalAlignment.Center, Visible = false, Text = S.T("unsaved") };
        box.AddChild(_dirtyNote);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", LauncherTheme.Space * 2);
        _apply = new Button { Text = S.T("apply"), SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, LauncherTheme.ButtonHeight), Disabled = true };
        _apply.Pressed += ApplySettings;
        _revert = new Button { Text = S.T("revert"), SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, LauncherTheme.ButtonHeight), Disabled = true };
        _revert.Pressed += RevertSettings;
        row.AddChild(_apply);
        row.AddChild(_revert);
        box.AddChild(row);
        var back = new Button { Text = S.T("back"), CustomMinimumSize = new Vector2(0, LauncherTheme.ButtonHeight) };
        back.Pressed += CloseSettings;
        box.AddChild(back);
        return box;
    }

    // ---- settings ----

    void ShowSettings(bool on)
    {
        if (on)
        {
            _discardArmed = false;
            _loadingSettings = true;
            _channel.Selected = _settings.Channel == Channels.Dev ? 1 : 0;
            _auto.ButtonPressed = _settings.AutoUpdate;
            _scale.Selected = Math.Max(0, Array.IndexOf(Scales, _settings.UiScale));
            _loadingSettings = false;
            RefreshDirty();
        }
        _main.Visible = !on;
        _settingsBox.Visible = on;
        _hint.Text = S.T(on ? "hint_settings" : "hint_main");
        if (on) _channel.GrabFocus(); else _settingsBtn.GrabFocus();
    }

    Button _apply = null!, _revert = null!;
    Label _dirtyNote = null!;
    bool _discardArmed;
    string _checkedChannel = Channels.Stable;

    string DraftChannel => _channel.Selected == 1 ? Channels.Dev : Channels.Stable;
    int DraftScale => Scales[Math.Max(0, _scale.Selected)];
    bool Dirty => DraftChannel != _settings.Channel || _auto.ButtonPressed != _settings.AutoUpdate || DraftScale != _settings.UiScale;

    /// <summary>Controls edit a draft; the UI scale previews live. Apply saves, Revert restores the saved values.</summary>
    void OnSettingsChanged()
    {
        if (_loadingSettings) return;
        _discardArmed = false;
        GetWindow().ContentScaleFactor = DraftScale / 100f;
        RefreshDirty();
    }

    void RefreshDirty()
    {
        var d = Dirty;
        _apply.Disabled = _revert.Disabled = !d;
        _dirtyNote.Visible = d;
        _dirtyNote.Text = S.T(_discardArmed ? "unsaved_discard" : "unsaved");
    }

    void ApplySettings()
    {
        _settings.Channel = DraftChannel;
        _settings.AutoUpdate = _auto.ButtonPressed;
        _settings.UiScale = DraftScale;
        _settings.Save(_installer.Root);
        _discardArmed = false;
        ApplyScale();
        RefreshDirty();
        _channel.GrabFocus();
    }

    void RevertSettings()
    {
        _loadingSettings = true;
        _channel.Selected = _settings.Channel == Channels.Dev ? 1 : 0;
        _auto.ButtonPressed = _settings.AutoUpdate;
        _scale.Selected = Math.Max(0, Array.IndexOf(Scales, _settings.UiScale));
        _loadingSettings = false;
        _discardArmed = false;
        ApplyScale();
        RefreshDirty();
        _channel.GrabFocus();
    }

    /// <summary>Back or Esc. With unsaved changes the first press warns, the second discards them (destructive actions confirm).</summary>
    void CloseSettings()
    {
        if (Dirty && !_discardArmed) { _discardArmed = true; RefreshDirty(); return; }
        if (Dirty) RevertSettings();
        ShowSettings(false);
        if (_settings.Channel != _checkedChannel) _ = CheckAsync();
    }

    void ApplyScale() => GetWindow().ContentScaleFactor = _settings.UiScale / 100f;

    // ---- flow ----

    async Task CheckAsync()
    {
        _cts?.Cancel();
        _checkedChannel = _settings.Channel;
        SetState(State.Checking);
        var have = _installer.Installed();
        try
        {
            _latest = await _feed.LatestAsync(_settings.Channel, CancellationToken.None);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or FormatException)
        {
            GD.PrintErr("update check: " + e.Message);
            _latest = null;
            Fail(S.T(have != null ? "offline_installed" : "offline_fresh"), null);
            return;
        }
        if (_latest is null) { Fail(S.T("no_release", _settings.Channel), null); return; }
        var (game, map, _) = Installer.Plan(have, _latest, Directory.Exists(Path.Combine(_installer.GameDir, "map")));
        if (!game && !map) { SetState(State.Ready); return; }
        if (_settings.AutoUpdate || have is null) { _ = InstallAsync(); return; }
        SetState(State.UpdateAvailable);
    }

    async Task InstallAsync()
    {
        if (_latest is null) return;
        var m = _latest;
        _cts = new CancellationTokenSource();
        SetState(State.Working);
        var have = _installer.Installed();
        var plan = Installer.Plan(have, m, Directory.Exists(Path.Combine(_installer.GameDir, "map")));
        long total = Math.Max(1, plan.Bytes);
        long before = 0;
        var progress = new Progress<InstallProgress>(p =>
        {
            if (p.Phase == InstallPhase.Installing) { _status.Text = S.T("installing"); _detail.Text = ""; _bar.Value = 1000; return; }
            long baseBytes = p.Phase == InstallPhase.DownloadingMap && plan.Game ? m.Game.Size : 0;
            before = baseBytes + p.Done;
            _bar.Value = 1000.0 * Math.Min(before, total) / total;
            _status.Text = S.T(p.Phase == InstallPhase.DownloadingGame ? "dl_game" : "dl_map", Mb(p.Done), Mb(p.Total));
            _detail.Text = S.T("protocol", m.Protocol) + "   |   " + m.Version;
        });
        try
        {
            await _installer.InstallAsync(m, progress, _cts.Token);
            SetState(State.Ready);
        }
        catch (OperationCanceledException)
        {
            Fail(S.T("cancelled"), null);
        }
        catch (Exception e)
        {
            GD.PrintErr("install: " + e);
            Fail(S.T("failed", e.Message), S.T("failed_hint"));
        }
    }

    static string Mb(long b) => $"{b / 1048576.0:0.0} MB";

    void OnPrimary()
    {
        switch (_state)
        {
            case State.Ready: Play(); break;
            case State.UpdateAvailable: _ = InstallAsync(); break;
            case State.Working: _cts?.Cancel(); break;
            case State.Failed:
                if (_installer.Installed() != null) Play(); else _ = CheckAsync();
                break;
        }
    }

    void Play()
    {
        var have = _installer.Installed();
        if (have is null) return;
        try
        {
            var exe = _installer.ExePath(have);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = _installer.GameDir, UseShellExecute = false });
            GetTree().Quit();
        }
        catch (Exception e)
        {
            GD.PrintErr("launch: " + e);
            Fail(S.T("launch_failed", e.Message), null);
        }
    }

    // ---- state -> widgets ----

    void Fail(string message, string? hint)
    {
        SetState(State.Failed);
        _status.Text = message;
        _error.Visible = hint != null;
        _error.Text = hint ?? "";
    }

    void SetState(State s)
    {
        _state = s;
        var have = _installer.Installed();
        _version.Text = have != null ? S.T("installed", have.Version) : S.T("not_installed");
        _bar.Visible = _detail.Visible = s == State.Working;
        _error.Visible = false;
        _retry.Visible = s == State.Failed;
        _settingsBtn.Disabled = _quit.Disabled = s == State.Working;
        _primary.Disabled = s == State.Checking || (s == State.Failed && have is null);
        switch (s)
        {
            case State.Checking: _status.Text = S.T("checking"); _primary.Text = S.T("play"); break;
            case State.Ready: _status.Text = S.T("uptodate"); _primary.Text = S.T("play"); break;
            case State.UpdateAvailable:
                _status.Text = S.T("update_available", _latest?.Version ?? "");
                _primary.Text = S.T("update"); break;
            case State.Working: _primary.Text = S.T("cancel"); _bar.Value = 0; _status.Text = ""; break;
            case State.Failed: _primary.Text = S.T("play"); break;
        }
        _hint.Text = S.T(s == State.Working ? "hint_busy" : "hint_main");
        if (!_settingsBox.Visible)
        {
            // first focus: the primary action, or Try again when there is nothing to play
            if (s == State.Failed && have is null) _retry.GrabFocus();
            else if (!_primary.Disabled) _primary.GrabFocus();
            else _settingsBtn.GrabFocus();
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!e.IsActionPressed("ui_cancel")) return;
        if (_settingsBox.Visible)
        {
            CloseSettings();
        }
        else if (_state == State.Working) _cts?.Cancel();
        else return;
        GetViewport().SetInputAsHandled();
    }
}
