using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Characters;
using Drycastle.Shooter.Logs;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Hunts;

namespace Drycastle.Shooter.Hunts;

/// <summary>What the Hunter's Log screen needs from the match: the queries, the pictures and the names (docs/hunters-log.md).</summary>
public sealed record HuntLogEnv(HuntLogData Data, HuntArtResolver Art, Func<string, string> SpeciesName, Func<string, string> WeaponName, Func<IReadOnlyList<string>> WeaponIds, Func<bool> InSandbox, Func<IReadOnlyList<HuntReward>> Rewards, Func<HuntReward, Texture2D?> RewardIcon);

/// <summary>
/// The Hunter's Log (#336, J, rebindable action "hunters_log", also in the pause menu): the book of every wild animal a player hunted. A non-pausing overlay like the
/// Equipment screen, but a panel centred on about 60 % of the screen's width (on its own top layer, above the kill feed) so the world stays visible and live around it: the mouse is released (the shell
/// asks <see cref="Hud.HuntersLogOpen"/>), the movement keys still work (<see cref="Player.MoveWithFreeMouse"/>), firing does not, nothing pauses. It closes with its key,
/// Esc, death, the end of the match and a turret or missile view; Esc on another player's log first goes up to My Log (<see cref="HuntLogNav"/>).
/// Tabs, in order: My Log (the Life List of 10 index species and 3 Mystery creatures, one grid), Records (a card per species with its heaviest and its longest shot as
/// recorded hit pictures), All hunts (a sortable, filterable, paged table with the selected hunt's hit card beside it), Rewards (progress to the bronze, silver and gold collection tiers and what each gives, #473). Q / E, the bumpers and Page Up / Down change tab; the
/// arrows move focus. There is no hint text: every control is labelled by what it is. The server answers every query redacted (a Mystery creature the viewer has not caught is
/// a hidden id: a "?" and a blurred placeholder name); every query has a designed loading, empty and error (Retry) state, a late answer to an older request is dropped
/// (<see cref="HuntLoader{T}"/>), and nothing is rebuilt per frame (a pane is rebuilt when its answer arrives, the table's rows are pooled).
/// </summary>
public sealed partial class HuntersLogScreen : Control
{
    const int Tabs = 4;

    readonly HuntLogEnv _env;
    readonly HuntLogNav _nav = new();
    readonly LogFrame _frame;
    Control _sandboxNote = null!;
    bool _logStale, _recordsStale, _huntsStale, _rewardsStale;

    static LogSkin Skin => LogSkin.Lodge;
    HuntLogData Data => _env.Data;
    bool _open => _frame.IsOpen;
    public bool IsOpen => _open;
    Control[] _panes => _frame.Panes;
    Control _panel => _frame.Panel;

    public HuntersLogScreen(HuntLogEnv env)
    {
        _env = env;
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        _frame = new LogFrame(Skin, Strings.T("log.name"), Tabs);
        AddChild(_frame);
        BuildFrame();
        BuildLogPane();
        BuildRecordsPane();
        BuildHuntsPane();
        BuildRewardsPane();
        _panel.Resized += OnPanelResized;
        Data.Changed += OnDataChanged;
    }

    public override void _ExitTree() => Data.Changed -= OnDataChanged;

    // ---- the frame: what the shared frame's parts do here ----

    void BuildFrame()
    {
        _frame.Tabs[1].Text = Strings.T("log.tab.records");
        _frame.Tabs[2].Text = Strings.T("log.tab.hunts");
        _frame.Tabs[3].Text = Strings.T("log.tab.rewards");
        _frame.TabPressed += SelectTab;
        _frame.TabStepped += StepTab;
        _frame.BackPressed += BackPressed;
        _frame.CloseRequested += Close;
        _frame.Ticked += delta => { Data.Tick(delta); SyncSandbox(); };

        // sandbox writes nothing to the database, so no hunt is logged: a line under the tabs says so on every tab while it holds (text, not colour alone)
        _sandboxNote = new PanelContainer { ThemeTypeVariation = "LogNotice", Visible = false, MouseFilter = MouseFilterEnum.Ignore };
        _sandboxNote.AddChild(Text(Strings.T("log.sandbox"), "HudWarn"));
        _frame.Notices.AddChild(_sandboxNote);
    }

    /// <summary>The control a pane's content goes into: a vertical box that fills the pane's width.</summary>
    VBoxContainer NewHost(Control pane) => _frame.NewHost(Array.IndexOf(_panes, pane));

    // ---- open and close ----

    public void Open()
    {
        if (_open) return;
        _frame.Open();
        _nav.Reset();
        _logStale = _recordsStale = _huntsStale = _rewardsStale = true; // every pane asks again when it shows: the book changes while the screen is closed
        ShowPage();
        Callable.From(FocusTab).CallDeferred();
    }

    /// <summary>Starts hiding the screen (a 150 ms fade, none with reduce motion).</summary>
    public void Close() => _frame.Close();

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

    /// <summary>Shows the sandbox line while the player is in sandbox (a flag read per frame while open; the line only changes when it flips).</summary>
    void SyncSandbox()
    {
        var on = _env.InSandbox();
        if (_sandboxNote.Visible != on) _sandboxNote.Visible = on;
    }

    // ---- tabs ----

    void SelectTab(int index)
    {
        if (index == _nav.TabIndex && _panes[index].Visible) { _frame.ShowTab(index); return; }
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

    void FocusTab() => _frame.FocusTab();

    /// <summary>
    /// Shows the page the navigation names: tab states, the first tab's name, the pane; asks for its data when it is stale (an answer redraws the pane, so a request ends here)
    /// and draws what it holds when it is not.
    /// </summary>
    void ShowPage()
    {
        _frame.ShowTab(_nav.TabIndex);
        SyncSandbox();
        _frame.Tabs[0].Text = TitleText();
        _frame.Back.Visible = _nav.Page == HuntLogPage.PlayerLog;
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
            case HuntLogPage.Rewards:
                // always the asker's own log (another player's parked on the first tab is asked for again when that tab shows)
                if (_rewardsStale || Data.LogSubject != 0) { _rewardsStale = false; Data.RequestLog(0); return; }
                RebuildRewards();
                break;
            default:
                // the table needs the records too (the "Mystery hunts" option and the record seals)
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

    void EnsureFocus() => _frame.EnsureFocus();

    // ---- small builders the panes share ----

    static Label Text(string text, string? variation = null, HorizontalAlignment align = HorizontalAlignment.Left, bool wrap = false) => LogFrame.Text(text, variation, align, wrap);

    static void Clear(Node host)
    {
        foreach (var c in host.GetChildren()) { host.RemoveChild(c); c.QueueFree(); }
    }

    static Control StateBox(string title, string body, bool spinner = false, string? actionText = null, Action? action = null) => LogFrame.StateBox(title, body, spinner, actionText, action);

    /// <summary>A recorded hit's picture in the inks of the paper it sits on.</summary>
    static AnimalPicture PaperPicture(Vector2 size) => new(size) { FillInk = Skin.InkDim, OutlineInk = Skin.InkDim, HitInk = Skin.InkDim };

    /// <summary>The darker box a picture on paper sits in.</summary>
    static PanelContainer Figure(Control picture)
    {
        var box = new PanelContainer { ThemeTypeVariation = "LogFigure", MouseFilter = MouseFilterEnum.Ignore };
        box.AddChild(picture);
        return box;
    }

    /// <summary>
    /// The Mystery creature's picture: a "?" and nothing else (no shape, the name stays unsent). The full ink when the log's owner caught it, the dim one when nobody did
    /// (<paramref name="onTile"/>: on a covered plaque, else on paper).
    /// </summary>
    static Control HiddenFigure(Vector2 size, bool caught, bool onTile = false)
    {
        var holder = new Control { CustomMinimumSize = size, MouseFilter = MouseFilterEnum.Ignore };
        var mark = Text(Strings.T("log.hidden.mark"), onTile ? (caught ? "TileMark" : "TileMarkDim") : caught ? "InkDisplay" : "InkDimDisplay", HorizontalAlignment.Center);
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
