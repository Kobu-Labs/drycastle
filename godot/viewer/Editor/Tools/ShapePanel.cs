using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using ShapeState = Drycastle.ShooterCore.Editor.EditorShapeState;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>What the pages of the Ground and Zones tools share: the draw action with its feedback, the list of shapes and the delete button.</summary>
public sealed class ShapePanel
{
    /// <summary>The controls of one shape tool's page that the gesture drives (the same in both).</summary>
    public sealed class Controls
    {
        public Button New = null!, Finish = null!, Create = null!, Back = null!, Cancel = null!, Delete = null!;
        public Label State = null!, Valid = null!, NoOrigin = null!, Empty = null!, ListHead = null!, ListNote = null!;
        public HBoxContainer LevelRow = null!;
        public SpinBox Level = null!;
        public VBoxContainer List = null!;
        public readonly Dictionary<string, Button> Rows = new();
        public readonly Dictionary<string, CheckBox> Marks = new(); // the tick of each row, when the list marks (zones: mass delete)
        public string ListSig = "";
        public bool Sync;
    }

    readonly IEditorHost _host;
    readonly ShapeEditor _shapes;
    readonly Dictionary<ShapeKind, Controls> _su = new();

    public ShapePanel(IEditorHost host, ShapeEditor shapes)
    {
        _host = host;
        _shapes = shapes;
    }

    public Controls Of(ShapeKind k) => _su[k];

    /// <summary>The draw action and its feedback: the New button, the state and validity lines, the water level row and the Finish / Create / Back / Cancel buttons.</summary>
    public void BuildActions(VBoxContainer page, ShapeKind k, Control? form = null)
    {
        var u = new Controls();
        _su[k] = u;
        var noun = ShapeDoc.NounOf(k);
        u.NoOrigin = Ui.Note("This map has no origin_epsg5514, so patches and zones cannot be placed. Export it again (map 0.4.0+).", "Danger");
        page.AddChild(u.NoOrigin);
        u.New = Ui.FocusBtn("", _shapes.StartShape, "PrimaryButton");
        page.AddChild(u.New);
        u.State = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, ThemeTypeVariation = "Header" };
        u.Valid = Ui.Note("");
        page.AddChild(u.State);
        page.AddChild(u.Valid);
        if (form != null) page.AddChild(form);
        if (k == ShapeKind.Patch)
        {
            u.LevelRow = new HBoxContainer();
            u.LevelRow.AddChild(new Label { Text = "Water level (m)", CustomMinimumSize = new Vector2(EditorTheme.Space * 16, 0) });
            u.Level = _host.NumberField(-500, 5000, 0.1, "Height of the water surface in metres, on the terrain's own scale (the ground map and the terrain use it). A patch of a water type needs one. Enter applies; each change is one undo step.");
            u.Level.ValueChanged += v => { if (!u.Sync) _shapes.OnWaterLevelChanged(v); };
            u.LevelRow.AddChild(u.Level);
            page.AddChild(u.LevelRow);
        }
        u.Finish = Ui.FocusBtn("", () => _shapes.CloseShape(), "PrimaryButton");
        u.Create = Ui.FocusBtn($"Create the {noun}  (Enter)", () => _shapes.CommitShape(), "PrimaryButton");
        u.Back = Ui.FocusBtn("Remove the last corner  (Backspace)", () => _shapes.RemoveLastShapePoint());
        u.Cancel = Ui.FocusBtn("Cancel drawing  (Esc)", () => _shapes.ShapeEscape());
        foreach (var b in new[] { u.Finish, u.Create, u.Back, u.Cancel }) page.AddChild(b);
    }

    /// <summary>The list of all shapes of a kind with its head and empty state.</summary>
    public void BuildList(VBoxContainer page, ShapeKind k, string head, string emptyText)
    {
        var u = _su[k];
        u.ListHead = new Label { Text = head, ThemeTypeVariation = "Header" };
        page.AddChild(u.ListHead);
        u.Empty = Ui.Note(emptyText);
        page.AddChild(u.Empty);
        u.ListNote = Ui.Note("");
        u.ListNote.Visible = false;
        page.AddChild(u.ListNote);
        u.List = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        page.AddChild(u.List);
    }

    public void BuildDelete(VBoxContainer page, ShapeKind k)
    {
        var u = _su[k];
        u.Delete = Ui.FocusBtn("", () => _shapes.RequestDeleteShape(), "DangerButton");
        page.AddChild(u.Delete);
    }

    /// <summary>
    /// Refresh the draw action and feedback of the open tool's page. <paramref name="drawText"/> is the New button's text,
    /// <paramref name="describe"/> names the selected shape for the state line (its type or kind).
    /// </summary>
    public void RefreshActions(ShapeKind k, string drawText, Func<ShapeInfo, string> describe)
    {
        var u = _su[k];
        var noun = ShapeDoc.NounOf(k);
        var mode = _shapes.Mode;
        var sel = _shapes.SelectedShape(k);
        var draft = _shapes.ShapeDraft;
        u.NoOrigin.Visible = _host.Map.OriginEpsg5514 == null;
        var why = _shapes.CannotDraw(k);
        u.New.Text = drawText;
        u.New.Disabled = why != null;
        u.New.TooltipText = why != null ? why[0].ToString().ToUpperInvariant() + why[1..] : $"Click corners on the ground, Enter closes the shape (N). Nothing is saved until it is created.";

        u.State.Text = mode switch
        {
            ShapeState.Dragging => $"Moving corner {_shapes.DragCorner + 1} of {sel?.Label}",
            ShapeState.Level => $"Patch closed: {draft.Count} corners. Set its water level.",
            ShapeState.Drawing => draft.Count == 0 ? "Click the ground to place the first corner" : $"Drawing: {draft.Count} corner{(draft.Count == 1 ? "" : "s")}",
            ShapeState.Selected => $"{sel!.Label} ({describe(sel)}): {sel.Points.Count} corners, {sel.AreaM2:F0} m2",
            _ => $"No {noun} selected",
        };
        string text, variation;
        if (mode is ShapeState.Drawing or ShapeState.Level)
        {
            var problem = draft.Count == 0 ? null : _shapes.ShapeDraftProblem();
            (text, variation) = draft.Count == 0 ? ($"Not saved until the shape is closed and created.", "Dim")
                : problem != null ? (draft.Count < 3 ? problem : "Cannot close: " + problem, draft.Count < 3 ? "Warn" : "Danger")
                : (mode == ShapeState.Level ? "Shape is valid. Not saved until you create it." : "Shape is valid; Enter closes it. Not saved yet.", "Dim");
        }
        else if (mode == ShapeState.Selected) (text, variation) = ("Saved. Every change is saved at once and Ctrl+Z undoes it.", "Dim");
        else if (mode == ShapeState.Dragging) (text, variation) = ("Release to save. A red outline would be refused.", "Dim");
        else (text, variation) = ($"Click a {noun} in the list or on the ground to select it.", "Dim");
        u.Valid.Text = text; u.Valid.ThemeTypeVariation = variation;

        var drafting = mode == ShapeState.Drawing && draft.Count > 0;
        var water = k == ShapeKind.Patch && _host.PatchTypeIsWater(_shapes.DrawType);
        u.Finish.Visible = drafting;
        u.Finish.Text = water ? "Close the shape  (Enter)" : $"Create the {noun}  (Enter)";
        u.Finish.Disabled = _shapes.ShapeDraftProblem() != null;
        u.Finish.TooltipText = _shapes.ShapeDraftProblem() is { } p ? $"Not yet: {p}" : water ? "Close the shape; the water level comes next" : $"Close the shape and save the {noun} (one undo step)";
        u.Create.Visible = mode == ShapeState.Level;
        u.Back.Visible = drafting || mode == ShapeState.Level;
        u.Back.Text = mode == ShapeState.Level ? "Back to the corners  (Esc)" : "Remove the last corner  (Backspace)";
        u.Cancel.Visible = mode is ShapeState.Drawing or ShapeState.Level;
        if (k == ShapeKind.Patch)
        {
            u.LevelRow.Visible = mode == ShapeState.Level;
            if (mode == ShapeState.Level && !u.Level.GetLineEdit().HasFocus()) SetLevel(u, _shapes.NewWaterY);
        }
        u.Delete.Visible = mode == ShapeState.Selected && sel != null && (k == ShapeKind.Zone || _host.GroundFocusPatch);
        if (sel != null) u.Delete.Text = $"Delete {sel.Label}  (Del)";
        u.Delete.TooltipText = $"Delete the {noun} (asks first; Ctrl+Z undoes)";
    }

    /// <summary>Show a water level in the spin box without it counting as an edit.</summary>
    public static void SetLevel(Controls u, double v)
    {
        u.Sync = true;
        u.Level.SetValueNoSignal(v);
        u.Sync = false;
    }

    /// <summary>
    /// Refresh a list of shapes; rows are rebuilt only when what they show changes. <paramref name="row"/> gives a row's text, swatch colour and tooltip.
    /// With <paramref name="marks"/> every row has a tick in front (marked for deletion); ticking calls <paramref name="onMark"/> and does not rebuild the rows (the tick keeps the focus).
    /// </summary>
    public void RefreshList(ShapeKind k, string noun, Func<ShapeInfo, (string Text, string Colour, string Tip)> row, string extraSig = "",
        Func<ShapeInfo, bool>? filter = null, string? head = null, int cap = int.MaxValue, IReadOnlySet<string>? marks = null, Action<string, bool>? onMark = null)
    {
        var u = _su[k];
        var all = _shapes.ShapeList(k);
        if (filter != null) all = all.Where(filter).ToList();
        var shapes = all.Count > cap ? all.Take(cap).ToList() : all;
        u.ListHead.Text = head != null ? $"{head} ({all.Count})" : $"All {noun}s ({all.Count})";
        u.Empty.Visible = all.Count == 0;
        u.ListNote.Visible = shapes.Count < all.Count;
        u.ListNote.Text = $"Showing the first {shapes.Count} of {all.Count}. Pick a kind in the list above to see only its {noun}s.";
        var sig = string.Join("|", shapes.Select(s => s.Id + row(s).Text + row(s).Colour + s.Points.Count)) + "#" + _shapes.SelectedOf(k) + extraSig + "#" + all.Count;
        if (sig != u.ListSig)
        {
            u.ListSig = sig;
            foreach (var c in u.List.GetChildren()) { u.List.RemoveChild(c); c.QueueFree(); }
            u.Rows.Clear(); u.Marks.Clear();
            foreach (var s in shapes)
            {
                var id = s.Id;
                var (text, colour, tip) = row(s);
                var b = new Button
                {
                    Text = text, Icon = Ui.Swatch(colour), Alignment = HorizontalAlignment.Left, ClipText = true,
                    ToggleMode = true, FocusMode = Control.FocusModeEnum.All, TooltipText = tip,
                };
                b.SetPressedNoSignal(id == _shapes.SelectedOf(k));
                b.Pressed += () => { _shapes.SelectShape(k, id == _shapes.SelectedOf(k) ? null : id); b.ReleaseFocus(); };
                u.Rows[id] = b;
                if (marks == null) { u.List.AddChild(b); continue; }
                var line = new HBoxContainer();
                var tick = new CheckBox { FocusMode = Control.FocusModeEnum.All, TooltipText = $"Mark {s.Label} for deletion (Space). \"Delete marked {noun}s\" above removes every marked one in one step." };
                tick.Toggled += on => onMark?.Invoke(id, on);
                b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                line.AddChild(tick); line.AddChild(b);
                u.List.AddChild(line);
                u.Marks[id] = tick;
            }
        }
        if (marks != null) foreach (var (id, tick) in u.Marks) if (tick.ButtonPressed != marks.Contains(id)) tick.SetPressedNoSignal(marks.Contains(id));
    }

    // test hooks of the open tool's page
    Controls Open => _su[_shapes.CurrentKind];
    public string State => Open.State.Text;
    public string Validation => Open.Valid.Text;
    public int RowCount => Open.Rows.Count;
    public bool EmptyShown => Open.Empty.Visible;
    public bool DeleteButtonShown => Open.Delete.Visible;
    public bool CreateButtonShown => Open.Create.Visible || Open.Finish.Visible;
    public bool RowSelected(string id) => Open.Rows.TryGetValue(id, out var b) && b.ButtonPressed;
    public bool RowMarked(string id) => Open.Marks.TryGetValue(id, out var t) && t.ButtonPressed;
    public void TickRow(string id, bool on) { if (Open.Marks.TryGetValue(id, out var t)) t.ButtonPressed = on; }
    public void PressRow(string id) { if (Open.Rows.TryGetValue(id, out var b)) b.EmitSignal(BaseButton.SignalName.Pressed); }
}
