using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The chat window, bottom left above the health block. Hidden until there is something to show: a message puts the log up
/// for <see cref="ShowSeconds"/> (it fades out, or just goes with Reduce motion), and opening the box (T, or "/" for a command)
/// shows the log with the input under it. While the box is open the mouse is free (the match reads no keys then), Enter sends,
/// Esc or a click away closes, Up and Down walk through what was sent, and Tab completes a command, its arguments (vehicles,
/// times) and cycles through the candidates, which are listed above the input with the usage of the command. A command that
/// cannot be valid is shown with the reason and is not sent. The rules (grammar, completions, history) are in ShooterCore
/// (<see cref="ChatCommands"/>, <see cref="ChatHistory"/>, <see cref="ChatCompleter"/>); this is only the view. Built once;
/// the rows are reused and only rewritten when the text changes, never per frame.
/// </summary>
public sealed partial class ChatBox : Control
{
    public const double ShowSeconds = 10, FadeSeconds = 0.5;
    const int LogRows = 7, MaxLines = 100, HintRows = 6, Width = 600, Height = 640;

    readonly Func<ChatContext> _context;
    readonly Action<string> _submit;
    readonly ChatHistory _history = new();
    readonly ChatCompleter _completer = new();
    PanelContainer _logPanel = null!, _hintPanel = null!, _inputPanel = null!;
    RichTextLabel _log = null!;
    LineEdit _input = null!;
    readonly Button[] _rows = new Button[HintRows];
    Label _usage = null!, _argHelp = null!, _error = null!, _keys = null!;
    ChatHint _hint = ChatHint.None;
    int _lines;
    bool _open;
    double _shownUntil;

    public ChatBox(Func<ChatContext> context, Action<string> submit)
    {
        _context = context;
        _submit = submit;
    }

    public bool IsOpen => _open;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        // a fixed box that grows no further than this; the stack inside sits at its bottom, so what is hidden leaves no gap
        AnchorLeft = AnchorRight = 0; AnchorTop = AnchorBottom = 1;
        OffsetLeft = ShooterTheme.Margin; OffsetRight = ShooterTheme.Margin + Width;
        OffsetBottom = -ShooterTheme.Margin - 112 - ShooterTheme.Space - 64 - ShooterTheme.Space * 2; // above the health block and the air gauge
        OffsetTop = OffsetBottom - Height;
        var stack = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.End, MouseFilter = MouseFilterEnum.Ignore };
        stack.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        stack.AddThemeConstantOverride("separation", ShooterTheme.Space / 2);
        AddChild(stack);

        _hintPanel = NewPanel("HudPanel");
        var hint = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        hint.AddThemeConstantOverride("separation", 2);
        _hintPanel.AddChild(hint);
        for (var i = 0; i < HintRows; i++)
        {
            var b = new Button { FocusMode = FocusModeEnum.None, Alignment = HorizontalAlignment.Left, ClipText = true, Visible = false, ThemeTypeVariation = "ChatSuggest" };
            var idx = i;
            b.Pressed += () => TakeSuggestion(idx);
            hint.AddChild(b);
            _rows[i] = b;
        }
        _usage = NewLabel("HudAccent"); _argHelp = NewLabel("HudSmall"); _error = NewLabel("MenuError");
        hint.AddChild(_usage); hint.AddChild(_argHelp); hint.AddChild(_error);
        stack.AddChild(_hintPanel);

        _logPanel = NewPanel("HudPanel");
        _log = new RichTextLabel
        {
            ThemeTypeVariation = "HudChatLog", BbcodeEnabled = false, ScrollFollowing = true, FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0, LogRows * 28), MouseFilter = MouseFilterEnum.Ignore, SelectionEnabled = false, AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _logPanel.AddChild(_log);
        stack.AddChild(_logPanel);

        _inputPanel = NewPanel("HudPanel");
        var inRow = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _input = new LineEdit { PlaceholderText = Strings.T("chat.placeholder"), MaxLength = ChatEvents.MaxChars, CaretBlink = true, ContextMenuEnabled = false, KeepEditingOnTextSubmit = true };
        _input.TextChanged += OnTyped;
        _input.TextSubmitted += OnSubmitted;
        _input.GuiInput += OnInputKey;
        _input.FocusExited += () => Callable.From(OnFocusLost).CallDeferred(); // a click away closes: the keys must never go on to the match behind an open box
        _keys = NewLabel("HudSmall");
        inRow.AddChild(_input); inRow.AddChild(_keys);
        _inputPanel.AddChild(inRow);
        stack.AddChild(_inputPanel);

        _hintPanel.Visible = _inputPanel.Visible = _logPanel.Visible = false;
        _keys.Text = Strings.T("chat.keys");
        SetProcess(false);
    }

    static PanelContainer NewPanel(string variation) => new() { ThemeTypeVariation = variation, MouseFilter = MouseFilterEnum.Ignore };

    static Label NewLabel(string variation) => new() { ThemeTypeVariation = variation, MouseFilter = MouseFilterEnum.Ignore, AutowrapMode = TextServer.AutowrapMode.WordSmart };

    // ---- the log ----

    /// <summary>Adds a line and shows the log for a while. A player's line is "Name: text"; the server's lines carry a mark so they never rely on colour alone.</summary>
    public void AddLine(ChatLine l)
    {
        var (mark, colour) = l.Kind switch
        {
            ChatLineKind.System => ("* ", ShooterTheme.Warn),
            ChatLineKind.Error => ("! ", ShooterTheme.Danger),
            ChatLineKind.Info => ("> ", ShooterTheme.Dim),
            _ => ("", ShooterTheme.Text),
        };
        if (l.Kind == ChatLineKind.Player)
        {
            _log.PushColor(ShooterTheme.Accent); _log.AddText(l.Sender + ": "); _log.Pop();
            _log.AddText(l.Text);
        }
        else
        {
            _log.PushColor(colour); _log.AddText(mark + l.Text); _log.Pop();
        }
        _log.Newline();
        if (++_lines > MaxLines) { _log.RemoveParagraph(0); _lines--; }
        Reveal(Now() + ShowSeconds);
    }

    static double Now() => Time.GetTicksMsec() / 1000.0;

    void Reveal(double until)
    {
        _shownUntil = Math.Max(_shownUntil, until);
        _logPanel.Visible = true;
        _logPanel.Modulate = Colors.White;
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        if (_open) return;
        var left = _shownUntil - Now();
        if (left <= 0) { _logPanel.Visible = false; SetProcess(false); return; }
        var a = GameSettings.Current.ReduceMotion ? 1f : (float)Math.Clamp(left / FadeSeconds, 0, 1);
        if (!Mathf.IsEqualApprox(_logPanel.Modulate.A, a)) _logPanel.Modulate = new Color(1, 1, 1, a);
    }

    // ---- open and close ----

    /// <summary>Opens the input (with "/" already typed for a command). The log shows with it, unless nothing was ever said.</summary>
    public void Open(string prefill = "")
    {
        if (_open) return;
        _open = true;
        _history.Reset(); _completer.Reset();
        _inputPanel.Visible = true;
        _logPanel.Visible = _lines > 0;
        _logPanel.Modulate = Colors.White;
        _input.Text = prefill;
        _input.CaretColumn = prefill.Length;
        _log.MouseFilter = MouseFilterEnum.Pass; // the wheel scrolls back through the log while the box is open
        _input.GrabFocus();
        Refresh();
    }

    /// <summary>Closes the box, keeps what was said on screen for a few seconds more. True when it was open.</summary>
    public bool Close()
    {
        if (!_open) return false;
        _open = false;
        _input.ReleaseFocus();
        _input.Text = "";
        _inputPanel.Visible = _hintPanel.Visible = false;
        _log.MouseFilter = MouseFilterEnum.Ignore;
        _log.ScrollToLine(Math.Max(0, _log.GetLineCount() - 1));
        if (_lines > 0) Reveal(Now() + ShowSeconds / 2); else _logPanel.Visible = false;
        return true;
    }

    /// <summary>The input lost focus: closes, unless the click was on a completion row (it takes the completion and gives the focus back).</summary>
    void OnFocusLost()
    {
        if (!_open || _input.HasFocus()) return;
        if (_hintPanel.Visible && _hintPanel.GetGlobalRect().HasPoint(GetGlobalMousePosition())) { _input.GrabFocus(); return; }
        Close();
    }

    // ---- typing ----

    void OnTyped(string _)
    {
        _history.Reset(); _completer.Reset(); // an edit leaves the history and starts the completion over
        Refresh();
    }

    void OnSubmitted(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0) { Close(); return; }
        if (text[0] == '/' && ChatCommands.Parse(text, _context()) is { Ok: false } bad)
        {
            Show(ChatHint.None with { Error = bad.Error }); // not sent: the reason stays under the input, the text stays to be fixed
            return;
        }
        _history.Add(text);
        _submit(text);
        Close();
    }

    void OnInputKey(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } k) return;
        switch (k.Keycode)
        {
            case Key.Up: Set(_history.Older(_input.Text)); break;
            case Key.Down: Set(_history.Newer()); break;
            case Key.Tab: Set(_completer.Tab(_input.Text, _context(), k.ShiftPressed), keepHistory: true); break;
            case Key.Escape: Close(); break;
            default: return;
        }
        _input.AcceptEvent();
    }

    /// <summary>Puts text in the box from code (history, completion). The edit signal does not fire for it, so the hint is redrawn here.</summary>
    void Set(string? text, bool keepHistory = false)
    {
        if (text == null) return;
        _input.Text = text;
        _input.CaretColumn = text.Length;
        if (!keepHistory) _completer.Reset();
        Refresh();
    }

    void TakeSuggestion(int row)
    {
        var i = _suggestionStart + row;
        if (i >= _hint.Suggestions.Count) return;
        _completer.Reset();
        Set(ChatCommands.Complete(_input.Text, _hint, _hint.Suggestions[i]));
        _input.GrabFocus();
    }

    // ---- the hint above the input ----

    int _suggestionStart;

    void Refresh() => Show(ChatCommands.Hint(_input.Text, _context()));

    void Show(ChatHint hint)
    {
        _hint = hint;
        var next = _completer.Pending(_input.Text);
        _suggestionStart = Math.Clamp(next - 2, 0, Math.Max(0, hint.Suggestions.Count - HintRows)); // the one Tab takes stays in view
        for (var i = 0; i < HintRows; i++)
        {
            var at = _suggestionStart + i;
            var b = _rows[i];
            b.Visible = at < hint.Suggestions.Count;
            if (!b.Visible) continue;
            var s = hint.Suggestions[at];
            b.Text = s.Detail == "" ? s.Label : $"{s.Label}    {s.Detail}";
            b.ThemeTypeVariation = at == next ? "ChatSuggestNext" : "ChatSuggest";
        }
        SetText(_usage, hint.Usage == "" ? "" : hint.Usage);
        SetText(_argHelp, hint.ArgHelp != "" ? hint.ArgHelp : hint.Summary);
        SetText(_error, hint.Error == null ? "" : "! " + hint.Error);
        _hintPanel.Visible = _open && !hint.IsEmpty;
    }

    static void SetText(Label l, string text)
    {
        if (l.Text != text) l.Text = text;
        l.Visible = text != "";
    }
}
