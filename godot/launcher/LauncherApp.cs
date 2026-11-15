using System.Diagnostics;
using Godot;
using Drycastle.LauncherCore;

namespace Drycastle.Launcher;

/// <summary>
/// The launcher screen: checks the release feed, downloads/updates the game, the map and (with the pack password from Settings) the private
/// asset pack (LauncherCore), then starts the game. The pack is optional: without it, or when it fails, the game still installs and plays.
/// States (<see cref="LauncherFlow"/>): Checking, Ready, UpdateAvailable, Working (progress, Esc cancels the download), Failed (message + Try again; the installed version stays playable).
/// Once the downloaded files are being swapped in (Installing) nothing can cancel it: the button says Installing and is disabled.
/// Esc: closes Settings, cancels a running download, otherwise nothing (the launcher never quits without the Quit button).
/// </summary>
public partial class LauncherApp : Control
{
    static readonly int[] Scales = { 75, 100, 125, 150 };

    readonly LauncherFlow _flow = new();
    Installer _installer = null!;
    ReleaseFeed _feed = null!;
    LauncherSettings _settings = null!;
    Manifest? _latest;
    CancellationTokenSource? _cts, _checkCts;

    VBoxContainer _main = null!, _settingsBox = null!;
    Label _version = null!, _status = null!, _error = null!, _detail = null!, _hint = null!, _packNote = null!;
    LineEdit _pack = null!;
    CheckBox _packShow = null!;
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

        _packNote = new Label { ThemeTypeVariation = "Small", HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false };
        _main.AddChild(_packNote);

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

        box.AddChild(new Label { Text = S.T("set_pack"), ThemeTypeVariation = "Dim" });
        _pack = new LineEdit { CustomMinimumSize = new Vector2(0, LauncherTheme.ButtonHeight), Secret = true, PlaceholderText = S.T("set_pack_placeholder"), SelectAllOnFocus = true };
        _pack.TextChanged += _ => OnSettingsChanged();
        box.AddChild(_pack);
        _packShow = new CheckBox { Text = S.T("set_pack_show"), CustomMinimumSize = new Vector2(0, LauncherTheme.ButtonHeight) };
        _packShow.Toggled += on => _pack.Secret = !on;
        box.AddChild(_packShow);

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
            _pack.Text = _settings.PackPassword;
            _packShow.ButtonPressed = false;
            _loadingSettings = false;
            RefreshDirty();
        }
        _main.Visible = !on;
        _settingsBox.Visible = on;
        _hint.Text = S.T(_flow.HintKey(on));
        if (on) _channel.GrabFocus(); else _settingsBtn.GrabFocus();
    }

    Button _apply = null!, _revert = null!;
    Label _dirtyNote = null!;
    bool _discardArmed;
    string _checkedChannel = Channels.Stable, _checkedPack = "";

    string DraftChannel => _channel.Selected == 1 ? Channels.Dev : Channels.Stable;
    int DraftScale => Scales[Math.Max(0, _scale.Selected)];
    string DraftPack => _pack.Text.Trim();
    bool Dirty => DraftChannel != _settings.Channel || _auto.ButtonPressed != _settings.AutoUpdate || DraftScale != _settings.UiScale || DraftPack != _settings.PackPassword;

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
        _settings.PackPassword = DraftPack;
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
        _pack.Text = _settings.PackPassword;
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
        if (SettingsNeedCheck) _ = CheckAsync(); // another channel, or a new pack password to try
    }

    bool SettingsNeedCheck => _settings.Channel != _checkedChannel || _settings.PackPassword != _checkedPack;

    void ApplyScale() => GetWindow().ContentScaleFactor = _settings.UiScale / 100f;

    // ---- flow ----

    /// <summary>
    /// Asks the release feed and decides: up to date, update available, or install now. Every await is followed by a stale check: a newer check
    /// (settings closed with another channel) or an install owns the state by then, and this older answer must not touch it.
    /// </summary>
    async Task CheckAsync()
    {
        if (!_flow.TryBeginCheck(out var ticket)) return; // an install is running: it owns the install folder
        _checkCts?.Cancel();
        _checkCts = new CancellationTokenSource();
        var ct = _checkCts.Token;
        _checkedChannel = _settings.Channel;
        _checkedPack = _settings.PackPassword;
        Render();
        var have = _installer.Installed();
        Manifest? latest;
        try
        {
            latest = await _feed.LatestAsync(_settings.Channel, ct);
        }
        catch (OperationCanceledException) when (!_flow.IsCurrent(ticket)) { return; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or FormatException)
        {
            if (!_flow.IsCurrent(ticket)) return;
            GD.PrintErr("update check: " + e.Message);
            _latest = null;
            Fail(S.T(have != null ? "offline_installed" : "offline_fresh"), null);
            return;
        }
        catch (Exception e)
        {
            // anything else the feed throws must still leave Checking, or the launcher spins forever
            if (!_flow.IsCurrent(ticket)) return;
            GD.PrintErr("update check: " + e);
            _latest = null;
            Fail(S.T(have != null ? "offline_installed" : "offline_fresh"), null);
            return;
        }
        if (!_flow.IsCurrent(ticket)) return;
        _latest = latest;
        if (_latest is null) { Fail(S.T("no_release", _settings.Channel), null); return; }
        var (game, map, _) = Installer.Plan(have, _latest, Directory.Exists(Path.Combine(_installer.GameDir, "map")));
        var pack = Installer.PlanPack(have, _latest, _settings.PackPassword, Directory.Exists(_installer.PackDir));
        if (!game && !map && !pack) { _flow.Finish(LauncherState.Ready); Render(); return; }
        if (_settings.AutoUpdate || have is null) { _ = InstallAsync(); return; }
        _flow.Finish(LauncherState.UpdateAvailable);
        Render();
    }

    async Task InstallAsync()
    {
        if (_latest is null || !_flow.TryBeginInstall()) return; // one installer at a time
        var m = _latest;
        _cts = new CancellationTokenSource();
        Render();
        var have = _installer.Installed();
        var plan = Installer.Plan(have, m, Directory.Exists(Path.Combine(_installer.GameDir, "map")));
        var password = _settings.PackPassword;
        var planPack = Installer.PlanPack(have, m, password, Directory.Exists(_installer.PackDir));
        long total = Math.Max(1, plan.Bytes + (planPack ? m.Pack!.File.Size : 0));
        long before = 0;
        var progress = new Progress<InstallProgress>(p =>
        {
            if (_flow.State != LauncherState.Working) return; // a late report after the install ended
            if (p.Phase == InstallPhase.Installing)
            {
                _flow.MarkInstalling(); // the swap cannot be cancelled: relabel and disable the button
                Render();
                _status.Text = S.T("installing"); _detail.Text = ""; _bar.Value = 1000;
                return;
            }
            // downloads run in the order game, map, pack: the bar counts the ones already done
            long baseBytes = (p.Phase != InstallPhase.DownloadingGame && plan.Game ? m.Game.Size : 0)
                + (p.Phase == InstallPhase.DownloadingPack && plan.Map ? m.Map.File.Size : 0);
            before = baseBytes + p.Done;
            _bar.Value = 1000.0 * Math.Min(before, total) / total;
            _status.Text = S.T(p.Phase == InstallPhase.DownloadingGame ? "dl_game" : p.Phase == InstallPhase.DownloadingMap ? "dl_map" : "dl_pack", Mb(p.Done), Mb(p.Total));
            _detail.Text = S.T("protocol", m.Protocol) + "   |   " + m.Version;
        });
        try
        {
            await _installer.InstallAsync(m, progress, _cts.Token, password);
            _flow.Finish(LauncherState.Ready);
            Render();
            if (SettingsNeedCheck) _ = CheckAsync(); // the channel or the pack password was changed while the install ran (CloseSettings could not check then)
        }
        catch (PackException e)
        {
            // the game and the map are installed; only the private assets are missing
            GD.PrintErr("asset pack: " + e);
            Fail(e.WrongPassword ? S.T("pack_wrong") : S.T("pack_failed", e.Message), S.T("pack_hint"));
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
        switch (_flow.State)
        {
            case LauncherState.Ready: Play(); break;
            case LauncherState.UpdateAvailable: _ = InstallAsync(); break;
            case LauncherState.Working: if (_flow.CanCancel) _cts?.Cancel(); break;
            case LauncherState.Failed:
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
        _flow.Finish(LauncherState.Failed);
        Render();
        _status.Text = message;
        _error.Visible = hint != null;
        _error.Text = hint ?? "";
    }

    InstalledState? _shownHave;

    /// <summary>Draws the flow's current state into the widgets.</summary>
    void Render()
    {
        var s = _flow.State;
        // not while the swap runs: the state file is being replaced and would read as "not installed"
        var have = _flow.Installing ? _shownHave : (_shownHave = _installer.Installed());
        _version.Text = have != null ? S.T("installed", have.Version) : S.T("not_installed");
        _bar.Visible = _detail.Visible = s == LauncherState.Working;
        _error.Visible = false;
        _retry.Visible = s == LauncherState.Failed;
        _settingsBtn.Disabled = _quit.Disabled = s == LauncherState.Working;
        _primary.Disabled = s == LauncherState.Checking || (s == LauncherState.Failed && have is null) || _flow.Installing;
        switch (s)
        {
            case LauncherState.Checking: _status.Text = S.T("checking"); _primary.Text = S.T("play"); break;
            case LauncherState.Ready: _status.Text = S.T("uptodate"); _primary.Text = S.T("play"); break;
            case LauncherState.UpdateAvailable:
                _status.Text = S.T("update_available", _latest?.Version ?? "");
                _primary.Text = S.T("update"); break;
            case LauncherState.Working: _primary.Text = S.T(_flow.Installing ? "installing" : "cancel"); _bar.Value = 0; _status.Text = ""; break;
            case LauncherState.Failed: _primary.Text = S.T("play"); break;
        }
        // the private asset pack: say when it is there, and what to do when it is locked (nothing is shown for a release without one, or mid-install)
        var packKey = _latest?.Pack is null || s is LauncherState.Working or LauncherState.Checking ? null
            : _installer.PackInstalled(_latest) ? "pack_installed"
            : string.IsNullOrWhiteSpace(_settings.PackPassword) ? "pack_locked" : null;
        _packNote.Visible = packKey != null;
        _packNote.Text = packKey != null ? S.T(packKey) : "";
        _hint.Text = S.T(_flow.HintKey(_settingsBox.Visible)); // the settings hint stays while Settings is open
        if (!_settingsBox.Visible)
        {
            // first focus: the primary action, or Try again when there is nothing to play
            if (s == LauncherState.Failed && have is null) _retry.GrabFocus();
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
        else if (_flow.CanCancel) _cts?.Cancel();
        else return;
        GetViewport().SetInputAsHandled();
    }
}
