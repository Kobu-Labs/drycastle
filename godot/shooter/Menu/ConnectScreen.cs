using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// Play: direct connect (address, port, name) with validation, a persisted recent servers list and three states:
/// form, connecting (a reachability probe over ENet with a visible timeout, Esc/Cancel aborts) and error (what happened and what to do).
/// </summary>
public partial class ConnectScreen : MenuScreen
{
    const double ProbeTimeout = 6.0;

    enum State { Form, Connecting, Error }

    readonly Action<string, int, string> _go;
    readonly string? _initialError;
    LineEdit _addr = null!, _port = null!, _name = null!;
    Label _addrErr = null!, _portErr = null!, _nameErr = null!;
    Button _connect = null!, _retry = null!;
    VBoxContainer _form = null!, _connecting = null!, _error = null!, _recentBox = null!;
    Label _connectingText = null!, _errorText = null!, _errorHelp = null!;
    ProgressBar _wait = null!;
    State _state = State.Form;
    RecentServers _recent = RecentServers.Parse(GameSettings.LoadConnect("recent", ""));
    string _target = "";
    ConnectCheck _check;

    ENetConnection? _conn;
    ENetPacketPeer? _peer;
    double _elapsed;
    bool _touched;

    public ConnectScreen(Action<string, int, string> go, string? initialError = null) { _go = go; _initialError = initialError; }

    protected override Control Build()
    {
        var root = Ui.VBox();
        root.CustomMinimumSize = new Vector2(760, 0);
        root.AddChild(Ui.Lbl(Strings.T("connect.title"), "HudDisplay"));
        root.AddChild(Ui.Gap(ShooterTheme.Space));

        _form = Ui.VBox();
        _addr = Field(_form, "connect.address", GameSettings.LoadConnect("address", ""), "connect.address.hint", out _addrErr);
        _port = Field(_form, "connect.port", GameSettings.LoadConnect("port", ConnectForm.DefaultPort.ToString()), null, out _portErr);
        _name = Field(_form, "connect.name", GameSettings.LoadConnect("name", "player"), null, out _nameErr);
        _addr.MaxLength = ConnectForm.MaxHost + 6; _port.MaxLength = 5; _name.MaxLength = ConnectForm.MaxName + 8;
        foreach (var e in new[] { _addr, _port, _name })
        {
            e.TextChanged += _ => { _touched = true; Validate(); };
            e.TextSubmitted += _ => TryConnect();
        }
        _form.AddChild(Ui.Gap(ShooterTheme.Space));
        _form.AddChild(Ui.Lbl(Strings.T("connect.recent"), "HudHead"));
        _recentBox = Ui.VBox();
        _form.AddChild(_recentBox);
        _form.AddChild(Ui.Gap(ShooterTheme.Space));
        var row = Ui.HBox();
        row.AddChild(Ui.Btn(Strings.T("dialog.back"), Back, null, 160));
        row.AddChild(Ui.Fill());
        _connect = Ui.Btn(Strings.T("connect.go"), TryConnect, "MenuPrimary", 200);
        row.AddChild(_connect);
        _form.AddChild(row);
        root.AddChild(_form);

        _connecting = Ui.VBox();
        _connectingText = Ui.Lbl("", "HudHead", 0, true);
        _connecting.AddChild(_connectingText);
        _wait = new ProgressBar { MinValue = 0, MaxValue = ProbeTimeout, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 24), MouseFilter = MouseFilterEnum.Ignore };
        _connecting.AddChild(_wait);
        _connecting.AddChild(Ui.Gap(ShooterTheme.Space));
        _connecting.AddChild(Ui.Btn(Strings.T("dialog.cancel"), Back, null, 200));
        root.AddChild(_connecting);

        _error = Ui.VBox();
        _errorText = Ui.Lbl("", "MenuError", 0, true);
        _errorHelp = Ui.Lbl("", "HudDim", 0, true);
        _error.AddChild(_errorText); _error.AddChild(_errorHelp);
        _error.AddChild(Ui.Gap(ShooterTheme.Space));
        var er = Ui.HBox();
        er.AddChild(Ui.Btn(Strings.T("dialog.back"), () => SetState(State.Form), null, 160));
        er.AddChild(Ui.Fill());
        _retry = Ui.Btn(Strings.T("connect.retry"), TryConnect, "MenuPrimary", 200);
        er.AddChild(_retry);
        _error.AddChild(er);
        root.AddChild(_error);

        var card = new PanelContainer { ThemeTypeVariation = "MenuCard" };
        card.AddChild(root);
        RebuildRecent();
        Validate();
        SetState(State.Form);
        if (_initialError != null) ShowError(_initialError, "connect.err.help.generic");
        return card;
    }


    LineEdit Field(VBoxContainer into, string labelKey, string text, string? placeholderKey, out Label err)
    {
        var col = Ui.VBox();
        col.AddChild(Ui.Lbl(Strings.T(labelKey), "HudDim"));
        var e = new LineEdit { Text = text, PlaceholderText = placeholderKey != null ? Strings.T(placeholderKey) : "", CustomMinimumSize = new Vector2(0, 48), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        col.AddChild(e);
        err = Ui.Lbl("", "MenuError");
        err.Visible = false; // reserved by Validate only when there is something to say
        col.AddChild(err);
        into.AddChild(col);
        return e;
    }

    void Validate()
    {
        _check = ConnectForm.Validate(_addr.Text, _port.Text, _name.Text);
        // errors show once the user has typed; an untouched empty form stays calm and only disables Connect
        Show(_addrErr, _touched || _addr.Text.Length > 0 ? _check.AddressError : null);
        Show(_portErr, _check.PortError);
        Show(_nameErr, _check.NameError);
        _connect.Disabled = !_check.Ok;
    }

    static void Show(Label l, string? key)
    {
        l.Visible = key != null;
        if (key != null) l.Text = Strings.T("connect.err.mark") + " " + Strings.T(key);
    }

    void RebuildRecent()
    {
        foreach (var c in _recentBox.GetChildren()) c.QueueFree();
        if (_recent.Items.Count == 0)
        {
            _recentBox.AddChild(Ui.Lbl(Strings.T("connect.recent.empty"), "HudDim", 0, true));
            return;
        }
        foreach (var item in _recent.Items)
        {
            var e = item;
            _recentBox.AddChild(Ui.Btn(e, () => Fill(e)));
        }
        _recentBox.AddChild(Ui.Btn(Strings.T("connect.recent.clear"), () => { _recent.Clear(); Save(); RebuildRecent(); }));
    }

    void Fill(string entry)
    {
        var (h, p) = ConnectForm.Split(entry);
        _addr.Text = h; _port.Text = p ?? ConnectForm.DefaultPort.ToString();
        _touched = true; Validate();
        _connect.GrabFocus();
    }

    void Save()
    {
        GameSettings.SaveConnect("address", _check.Host); GameSettings.SaveConnect("port", _check.Port.ToString());
        GameSettings.SaveConnect("name", _name.Text.Trim()); GameSettings.SaveConnect("recent", _recent.Serialize());
    }

    protected override Control? FirstFocusTarget() => _state switch
    {
        State.Form => _addr.Text.Length == 0 || !_check.Ok ? _addr : _connect,
        State.Error => _retry,
        _ => null,
    };

    void SetState(State s)
    {
        _state = s;
        _form.Visible = s == State.Form; _connecting.Visible = s == State.Connecting; _error.Visible = s == State.Error;
        Card?.ResetSize();
        if (IsInsideTree()) FocusFirst();
    }

    void TryConnect()
    {
        Validate();
        if (!_check.Ok || _state == State.Connecting) { if (_state == State.Form) _addr.GrabFocus(); return; }
        _target = RecentServers.Format(_check.Host, _check.Port);
        _connectingText.Text = Strings.F("connect.connecting", _target);
        _elapsed = 0; _wait.Value = 0;
        SetState(State.Connecting);
        Probe();
    }

    void Probe()
    {
        StopProbe();
        _conn = new ENetConnection();
        if (_conn.CreateHost(1, Wire.Channels, 0, 0) != Error.Ok) { ShowError(Strings.T("connect.err.local"), "connect.err.help.local"); return; }
        _peer = _conn.ConnectToHost(_check.Host, _check.Port, Wire.Channels, 0);
        if (_peer == null) ShowError(Strings.F("connect.err.resolve", _check.Host), "connect.err.help.resolve");
    }

    void StopProbe()
    {
        if (_peer != null && _conn != null) { _peer.PeerDisconnect(); _conn.Flush(); }
        _peer = null;
        _conn?.Destroy(); _conn = null;
    }

    public override void _Process(double delta)
    {
        if (_state != State.Connecting || _conn == null) return;
        _elapsed += delta; _wait.Value = Math.Min(_elapsed, ProbeTimeout);
        for (var guard = 0; guard < 64; guard++)
        {
            var r = _conn.Service(0);
            var ev = (ENetConnection.EventType)(long)r[0];
            if (ev is ENetConnection.EventType.None or ENetConnection.EventType.Error) break;
            if (ev == ENetConnection.EventType.Connect) { Succeed(); return; }
            if (ev == ENetConnection.EventType.Disconnect) { ShowError(Strings.F("connect.err.refused", _target), "connect.err.help.refused"); return; }
        }
        if (_elapsed >= ProbeTimeout) ShowError(Strings.F("connect.err.timeout", _target, (int)ProbeTimeout), "connect.err.help.timeout");
    }

    void Succeed()
    {
        StopProbe();
        _recent.Add(_target);
        Save();
        _go(_check.Host, _check.Port, _name.Text.Trim());
    }

    public void ShowError(string what, string helpKey)
    {
        StopProbe();
        _errorText.Text = Strings.T("connect.err.mark") + " " + what;
        _errorHelp.Text = Strings.T(helpKey);
        SetState(State.Error);
    }

    /// <summary>Esc: cancel a connection attempt, leave the error state for the form, or leave the screen.</summary>
    public override void Back()
    {
        switch (_state)
        {
            case State.Connecting: StopProbe(); SetState(State.Form); break;
            case State.Error: SetState(State.Form); break;
            default: Shell.Pop(); break;
        }
    }

    public override void _ExitTree() => StopProbe();
}
