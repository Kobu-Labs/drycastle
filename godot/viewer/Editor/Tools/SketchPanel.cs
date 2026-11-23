using Godot;
using System.Collections.Generic;
using System.Linq;
using SketchState = Drycastle.ShooterCore.Editor.EditorSketchState;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// The Sketch tool's sidebar page: the sketch picker with New / Rename / Delete (and the name field they open), the point and line modes, what the state is
/// and the buttons that fit it, and the list of marks. It shows what <see cref="SketchTool"/> holds and calls its actions; nothing here changes the sketch itself.
/// </summary>
public sealed class SketchPanel
{
    readonly IEditorHost _host;
    readonly SketchTool _tool;

    public SketchPanel(IEditorHost host, SketchTool tool) => (_host, _tool) = (host, tool);

    Button _btnPoint = null!, _btnLine = null!, _finish = null!, _closeBtn = null!, _back = null!, _cancel = null!, _delete = null!, _clear = null!;
    Label _noOrigin = null!, _stateLabel = null!, _note = null!, _listHead = null!, _empty = null!, _nameNote = null!;
    VBoxContainer _list = null!;
    string _listSig = "", _pickSig = "";
    OptionButton _pick = null!;
    Button _newSketch = null!, _renameSketch = null!, _deleteSketch = null!;
    HBoxContainer _nameRow = null!;
    LineEdit _name = null!;
    enum Naming { None, New, Rename }
    Naming _naming;

    /// <summary>The name field is open (New or Rename): Esc closes it first.</summary>
    public bool IsNaming => _naming != Naming.None;

    public void Build(VBoxContainer page)
    {
        page.AddChild(new Label { Text = "Sketch", ThemeTypeVariation = "Header" });
        page.AddChild(Ui.Note("Temporary points and lines on the ground that a prompt can name: \"plant trees inside L1\", \"a lamp at A\", \"everything in sketch ROAD\". They are not part of the map and are never saved to it; Claude can draw into them too."));

        BuildSketchPicker(page);
        page.AddChild(new HSeparator());
        _noOrigin = Ui.Note("This map has no origin_epsg5514, so nothing can be marked. Export it again (map 0.4.0+).", "Danger");
        page.AddChild(_noOrigin);

        var modes = new HBoxContainer();
        Button Mode(string text, string tip, SketchKind kind)
        {
            var b = new Button { Text = text, ToggleMode = true, FocusMode = Control.FocusModeEnum.All, TooltipText = tip, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            b.Pressed += () => { _tool.SetMode(kind); b.ReleaseFocus(); };
            modes.AddChild(b);
            return b;
        }
        _btnPoint = Mode("Point  (P)", "Each click on the ground marks a point: A, B, C, ...", SketchKind.Point);
        _btnLine = Mode("Line  (L)", "Clicks place the corners of a line: L1, L2, ... Enter finishes it; a click on its first corner closes it, to mark the ground inside", SketchKind.Line);
        page.AddChild(modes);

        _stateLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, ThemeTypeVariation = "Header" };
        _note = Ui.Note("");
        page.AddChild(_stateLabel);
        page.AddChild(_note);

        _finish = Ui.FocusBtn("Finish the line  (Enter)", () => _tool.FinishLine(), "PrimaryButton");
        _closeBtn = Ui.FocusBtn("Close the line  (Shift+Enter)", () => _tool.FinishLine(true));
        _closeBtn.TooltipText = "The last corner is joined to the first; a click on the first corner does the same";
        _back = Ui.FocusBtn("Remove the last corner  (Backspace)", () => _tool.RemoveLastCorner());
        _cancel = Ui.FocusBtn("Cancel the line  (Esc)", _tool.CancelDraft);
        _delete = Ui.FocusBtn("Delete  (Del)", () => { if (_tool.Selected != null) _tool.DeleteItem(_tool.Selected); }, "DangerButton");
        foreach (var b in new[] { _finish, _closeBtn, _back, _cancel, _delete }) page.AddChild(b);

        page.AddChild(new HSeparator());
        _listHead = new Label { Text = "Marks", ThemeTypeVariation = "Header" };
        page.AddChild(_listHead);
        _empty = Ui.Note("Nothing marked yet. Click the ground, or press C to mark the spot under the cursor.");
        page.AddChild(_empty);
        _list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        page.AddChild(_list);
        _clear = Ui.FocusBtn("Clear the sketch", () => _tool.RequestClear(), "DangerButton");
        _clear.TooltipText = "Remove the marks of the active sketch (asks first; Ctrl+Z brings them back)";
        page.AddChild(_clear);
        page.AddChild(Ui.Note("Marks stay visible in every tool. Esc steps back: cancel the line, deselect, leave the tool."));
    }

    /// <summary>The sketch picker, its New / Rename / Delete buttons and the name field they open.</summary>
    void BuildSketchPicker(VBoxContainer page)
    {
        var pickRow = new HBoxContainer();
        pickRow.AddChild(new Label { Text = "Sketch" });
        _pick = new OptionButton { FocusMode = Control.FocusModeEnum.All, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "The sketch new marks go into. Marks of every sketch stay visible." };
        _pick.ItemSelected += i => { if (i >= 0 && i < _tool.Doc.Names.Count) _tool.SetActiveSketch(_tool.Doc.Names[(int)i]); _pick.ReleaseFocus(); };
        pickRow.AddChild(_pick);
        page.AddChild(pickRow);
        var sketchBtns = new HBoxContainer();
        _newSketch = Ui.FocusBtn("New", () => StartNaming(Naming.New));
        _newSketch.TooltipText = "Make a new, empty sketch and draw into it";
        _renameSketch = Ui.FocusBtn("Rename", () => StartNaming(Naming.Rename));
        _renameSketch.TooltipText = "Rename the active sketch (prompts name sketches, so keep it short: ROAD, FENCE_PLAN)";
        _deleteSketch = Ui.FocusBtn("Delete", () => _tool.RequestDeleteSketch(_tool.ActiveSketch), "DangerButton");
        _deleteSketch.TooltipText = "Delete the active sketch with all its marks (asks first; Ctrl+Z brings it back)";
        foreach (var b in new[] { _newSketch, _renameSketch, _deleteSketch }) { b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; sketchBtns.AddChild(b); }
        page.AddChild(sketchBtns);
        _nameRow = new HBoxContainer { Visible = false };
        _name = new LineEdit { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, PlaceholderText = "name, e.g. ROAD", MaxLength = 24, ClearButtonEnabled = true, TooltipText = "1 to 24 letters, digits, _ or -. Enter applies, Esc cancels" };
        _name.TextChanged += t => RefreshNaming(t);
        _name.TextSubmitted += t => ApplyNaming(t);
        _name.GuiInput += e => { if (e is InputEventKey { Pressed: true, Keycode: Key.Escape }) { CancelNaming(); _name.AcceptEvent(); } };
        _nameRow.AddChild(_name);
        var nameOk = Ui.FocusBtn("OK", () => ApplyNaming(_name.Text), "PrimaryButton");
        var nameCancel = Ui.FocusBtn("Cancel", CancelNaming);
        _nameRow.AddChild(nameOk); _nameRow.AddChild(nameCancel);
        page.AddChild(_nameRow);
        _nameNote = Ui.Note("", "Dim");
        _nameNote.Visible = false;
        page.AddChild(_nameNote);
    }

    void StartNaming(Naming kind)
    {
        if (_tool.Draft.Count > 0) { _host.Say("finish the line (Enter) or cancel it (Esc) first", true); return; }
        if (kind == Naming.Rename && !_tool.Doc.Names.Contains(_tool.ActiveSketch)) { _host.Say("this sketch has no marks yet; mark something first", true); return; }
        _naming = kind;
        _name.Text = kind == Naming.Rename ? _tool.ActiveSketch : "";
        _nameRow.Visible = _nameNote.Visible = true;
        RefreshNaming(_name.Text);
        _name.GrabFocus();
        _name.SelectAll();
        _tool.Changed();
    }

    void RefreshNaming(string text)
    {
        var t = text.Trim();
        var why = t == "" ? null : _tool.NameProblem(t, _naming == Naming.Rename ? _tool.ActiveSketch : null);
        _nameNote.Text = why ?? (_naming == Naming.Rename ? $"Enter renames {_tool.ActiveSketch}. Esc cancels." : "Enter makes the sketch and draws into it. Esc cancels.");
        _nameNote.ThemeTypeVariation = why == null ? "Dim" : "Warn";
    }

    void ApplyNaming(string text)
    {
        var kind = _naming;
        if (kind == Naming.None) return;
        var ok = kind == Naming.New ? _tool.NewSketch(text) : _tool.RenameSketch(_tool.ActiveSketch, text);
        if (ok) CancelNaming(); else RefreshNaming(text);
    }

    public void CancelNaming()
    {
        if (_naming == Naming.None) return;
        _naming = Naming.None;
        _name.ReleaseFocus();
        _nameRow.Visible = _nameNote.Visible = false;
        _tool.Changed();
    }

    static string RowText(SketchLine l)
        => $"{l.Id}   {(l.Closed ? "closed line" : "line")}   {SketchTool.Plural(l.Points.Count, "corner")}   " + (l.Closed ? $"{l.AreaM2:F0} m2" : $"{l.LengthM:F0} m");

    /// <summary>What the panel says about the state: a headline, a note under it and the note's look.</summary>
    (string state, string note, string variation) StateText()
    {
        var n = _tool.Draft.Count;
        var selPoint = _tool.Selected == null ? null : _tool.Doc.Points.FirstOrDefault(p => p.Id == _tool.Selected);
        var selLine = _tool.Selected == null ? null : _tool.Doc.Lines.FirstOrDefault(l => l.Id == _tool.Selected);
        string state, note, variation = "Dim";
        if (_tool.Phase == SketchState.Drawing)
        {
            state = $"Drawing a line: {SketchTool.Plural(n, "corner")}, {new SketchLine("", false, _tool.Draft).LengthM:F1} m";
            var why = n >= 3 ? _tool.CloseProblem() : null;
            if (n == 1) note = "Enter now marks a single point. Click to add corners.";
            else if (n == 2) note = "Enter finishes the line. A closed line needs 3 corners.";
            else if (why == null) note = "Enter finishes the line. Shift+Enter, or a click on the first corner, closes it to mark the ground inside.";
            else { note = $"Cannot be closed: {why}. Enter still finishes it as an open line."; variation = "Warn"; }
        }
        else if (selPoint != null) (state, note) = ($"{selPoint.Id}: point at x {selPoint.X:F1}, y {selPoint.Y:F1}", $"Name it in a prompt: \"at {selPoint.Id}\". Del removes it, Ctrl+Z brings it back.");
        else if (selLine != null)
            (state, note) = ($"{selLine.Id}: {(selLine.Closed ? "closed line" : "line")}, {SketchTool.Plural(selLine.Points.Count, "corner")}, {selLine.LengthM:F1} m{(selLine.Closed ? $" around, {selLine.AreaM2:F0} m2" : "")}",
                $"Name it in a prompt: \"{(selLine.Closed ? "inside" : "along")} {selLine.Id}\". Del removes it, Ctrl+Z brings it back.");
        else (state, note) = (_tool.Mode == SketchKind.Point ? "Click the ground to mark a point" : "Click the ground to place the first corner", "Click a mark, or a row below, to select it.");
        return (state, note, variation);
    }

    /// <summary>The sketch picker lists every sketch with its marks; rebuilt only when that list changed.</summary>
    void RefreshPick(bool hasOrigin, bool named)
    {
        var pickSig = string.Join("|", _tool.Doc.Names.Select(n => $"{n}:{_tool.Doc.CountIn(n)}")) + "#" + _tool.ActiveSketch + "#" + hasOrigin;
        if (pickSig != _pickSig)
        {
            _pickSig = pickSig;
            _pick.Clear();
            var idx = 0;
            foreach (var nm in named ? _tool.Doc.Names : _tool.Doc.Names.Append(_tool.ActiveSketch).ToList()) // an unused active sketch (DEFAULT) is listed until its first mark
            {
                _pick.AddItem($"{nm}  ({SketchTool.Plural(_tool.Doc.CountIn(nm), "mark")})");
                if (nm == _tool.ActiveSketch) _pick.Select(idx);
                idx++;
            }
        }
    }

    public void Refresh()
    {
        var hasOrigin = _host.Map.OriginEpsg5514 != null;
        _noOrigin.Visible = !hasOrigin;
        _btnPoint.Disabled = _btnLine.Disabled = !hasOrigin;
        _btnPoint.SetPressedNoSignal(_tool.Mode == SketchKind.Point);
        _btnLine.SetPressedNoSignal(_tool.Mode == SketchKind.Line);
        var phase = _tool.Phase;
        var n = _tool.Draft.Count;
        var (state, note, variation) = StateText();
        _stateLabel.Text = state;
        _note.Text = note; _note.ThemeTypeVariation = variation;

        var drawing = phase == SketchState.Drawing;
        _finish.Visible = drawing;
        _finish.Text = n == 1 ? "Mark it as a point  (Enter)" : "Finish the line  (Enter)";
        _closeBtn.Visible = drawing && n >= 3;
        _closeBtn.Disabled = drawing && _tool.CloseProblem() != null;
        _back.Visible = _cancel.Visible = drawing;
        _delete.Visible = phase == SketchState.Selected;
        if (_tool.Selected != null) _delete.Text = $"Delete {_tool.Selected}  (Del)";

        var named = _tool.Doc.Names.Contains(_tool.ActiveSketch);
        RefreshPick(hasOrigin, named);
        _pick.Disabled = _newSketch.Disabled = !hasOrigin;
        _renameSketch.Disabled = _deleteSketch.Disabled = !hasOrigin || !named;
        _deleteSketch.Text = named ? $"Delete {_tool.ActiveSketch}" : "Delete";
        _listHead.Text = $"Marks ({_tool.Doc.Count})";
        _empty.Visible = _tool.Doc.Count == 0;
        var inActive = _tool.Doc.CountIn(_tool.ActiveSketch);
        _clear.Visible = inActive > 0;
        _clear.TooltipText = "Remove every mark of the active sketch (asks first; Ctrl+Z brings them back)";
        _clear.Text = _tool.Doc.Names.Count > 1 ? $"Clear sketch {_tool.ActiveSketch}" : "Clear the sketch";
        RebuildList();
    }

    /// <summary>The rows of the marks, one group per sketch; rebuilt when the sketch, the selection or the active sketch changed.</summary>
    void RebuildList()
    {
        var sig = _tool.Json + "#" + _tool.Selected + "#" + _tool.ActiveSketch;
        if (sig == _listSig) return;
        _listSig = sig;
        foreach (var c in _list.GetChildren()) { _list.RemoveChild(c); c.QueueFree(); }
        void Row(string id, string text, string tip)
        {
            var b = new Button { Text = text, Alignment = HorizontalAlignment.Left, ToggleMode = true, FocusMode = Control.FocusModeEnum.All, TooltipText = tip };
            b.SetPressedNoSignal(id == _tool.Selected);
            b.Pressed += () => { if (_tool.Draft.Count > 0) _tool.CancelDraft(); _tool.SelectItem(id); b.ReleaseFocus(); };
            _list.AddChild(b);
        }
        foreach (var name in _tool.Doc.Names)
        {
            if (_tool.Doc.Names.Count > 1) _list.AddChild(new Label { Text = $"{name}{(name == _tool.ActiveSketch ? "  (drawing here)" : "")}", ThemeTypeVariation = "Header" });
            foreach (var p in _tool.Doc.Points.Where(p => p.Sketch == name)) Row(p.Id, $"{p.Id}   point", $"Select point {p.Id} in {name} (x {p.X:F1}, y {p.Y:F1})");
            foreach (var l in _tool.Doc.Lines.Where(l => l.Sketch == name)) Row(l.Id, RowText(l), $"Select {(l.Closed ? "closed line" : "line")} {l.Id} in {name}");
        }
    }
}
