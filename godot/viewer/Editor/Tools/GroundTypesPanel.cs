using Drycastle.Viewer.Edits;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GtRow = Drycastle.Viewer.Edits.GroundTypeEdits.Row;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// The Ground page of the sidebar: the list of ground types, the New type row and the form of the selected type (colours, recipe with its
/// detail settings, water, OSM tags, reset and delete). Every control calls a method of <see cref="GroundTypeOps"/> or <see cref="GroundTool"/>;
/// the ground map toggle and the patch section are built by <see cref="GroundMap"/> and <see cref="GroundPatchPage"/>.
/// </summary>
public sealed class GroundTypesPanel
{
    readonly IEditorHost _host;
    readonly GroundTool _tool;

    VBoxContainer _list = null!, _form = null!, _colours = null!, _detailFields = null!, _coloursBox = null!, _recipeBox = null!, _osmBox = null!;
    Label _error = null!, _listHead = null!, _title = null!, _stateNote = null!, _nameValid = null!, _colourNote = null!,
        _osmNote = null!, _osmValid = null!, _detailText = null!, _detailNote = null!, _actionNote = null!, _carryNote = null!;
    LineEdit _name = null!, _landuse = null!, _natural = null!;
    Button _create = null!, _duplicate = null!, _addColour = null!, _reset = null!, _delete = null!;
    OptionButton _recipe = null!;
    CheckBox _carryColours = null!, _carryRecipe = null!, _water = null!;
    bool _sync;
    string _listSig = "", _colourSig = "", _detailSig = "", _osmSeen = "", _scrolledFor = "";
    readonly Dictionary<string, Button> _rowButtons = new();
    readonly Dictionary<string, SpinBox> _detailSpins = new();

    public GroundTypesPanel(IEditorHost host, GroundTool tool)
    {
        _host = host;
        _tool = tool;
    }

    GroundTypeOps Ops => _tool.Ops;

    /// <summary>Shift+N: the name field of a new type.</summary>
    public void FocusName() => _name.GrabFocus();

    /// <summary>A type was made: the name field is empty again.</summary>
    public void ClearName() => _name.Text = "";

    /// <summary>Tab with nothing focused: the selected type in the list, else the first (the keyboard way into the panel).</summary>
    public void FocusList()
    {
        if (_rowButtons.Count == 0) { _name.GrabFocus(); return; }
        (_tool.SelectedType != null && _rowButtons.TryGetValue(_tool.SelectedType, out var b) ? b : _rowButtons.Values.First()).GrabFocus();
    }

    /// <summary>Esc in a text field leaves it (one level up); <paramref name="revert"/> puts the old text back first.</summary>
    void EscapableText(LineEdit le, Action? revert = null)
        => le.GuiInput += e => { if (e is InputEventKey { Pressed: true, Keycode: Key.Escape }) { revert?.Invoke(); le.ReleaseFocus(); le.AcceptEvent(); _host.MarkPanelDirty(); } };

    // ---- build ----

    public void Build(VBoxContainer page)
    {
        page.AddChild(new Label { Text = "Ground", ThemeTypeVariation = "Header" });
        page.AddChild(Ui.Note("Every point of the map has exactly one ground type: terrain colours, detail and whether it is water, nothing more (what a place means in play is a zone). Draw a patch of a type to change the ground by hand; where patches overlap the higher ID wins."));
        var banner = new PanelContainer { ThemeTypeVariation = "BannerPanel" };
        banner.AddChild(Ui.Note("Changes show after the next full map build (make map). The editor saves them at once; Ctrl+Z undoes.", "Warn"));
        page.AddChild(banner);
        _error = Ui.Note("", "Danger");
        page.AddChild(_error);

        _tool.Overlay.BuildToggle(page);
        _listHead = new Label { Text = "All ground types", ThemeTypeVariation = "Header" };
        page.AddChild(_listHead);
        _list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        page.AddChild(_list);

        page.AddChild(new HSeparator());
        _tool.PatchPage.Build(page);
        page.AddChild(new HSeparator());

        BuildNewType(page);
        page.AddChild(new HSeparator());
        _form = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        page.AddChild(_form);
        BuildForm(_form);
    }

    void BuildNewType(VBoxContainer page)
    {
        page.AddChild(new Label { Text = "New type", ThemeTypeVariation = "Header" });
        _name = new LineEdit { PlaceholderText = "my_meadow", MaxLength = GroundTypeEdits.NameMax + 8, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClearButtonEnabled = true,
            TooltipText = "Name of the new type (Shift+N): lower case letters, digits and _, starting with a letter, at most 24. A name is an ID and cannot be changed later." };
        _name.TextChanged += _ => _host.MarkPanelDirty();
        _name.TextSubmitted += t => { if (Ops.Create(t.Trim())) _name.ReleaseFocus(); };
        EscapableText(_name);
        page.AddChild(_name);
        _nameValid = Ui.Note("");
        page.AddChild(_nameValid);
        var newRow = new HFlowContainer();
        _create = Ui.FocusBtn("Create", () => Ops.Create(_name.Text.Trim()), "PrimaryButton");
        _create.TooltipText = "Make a new ground type with one green shade and the field recipe (Enter in the name field does the same). Untick the recipe for plain paint";
        _duplicate = Ui.FocusBtn("Duplicate selected", () => { if (_tool.SelectedType != null) Ops.Create(_name.Text.Trim(), _tool.SelectedType); });
        newRow.AddChild(_create);
        newRow.AddChild(_duplicate);
        page.AddChild(newRow);
        page.AddChild(Ui.Note("A new type is for patches: a colour you want that no type has is a type of its own. It takes OSM land only if you give it OSM values, and the types above take theirs first."));
    }

    CheckBox CarryBox(string text, string tip, Action<bool> toggled)
    {
        var c = new CheckBox { Text = text, FocusMode = Control.FocusModeEnum.All, TooltipText = tip };
        c.Toggled += on => { if (!_sync && _tool.SelectedType != null) toggled(on); };
        return c;
    }

    static void GroupHeader(VBoxContainer form, string group)
    {
        var g = GroundTypeEdits.Groups.First(x => x.Key == group);
        form.AddChild(new Label { Text = g.Label, ThemeTypeVariation = "Header" });
        form.AddChild(Ui.Note(g.Note));
    }

    void BuildForm(VBoxContainer form)
    {
        _title = new Label { ThemeTypeVariation = "Title", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        form.AddChild(_title);
        _stateNote = Ui.Note("");
        form.AddChild(_stateNote);
        form.AddChild(new Button { Text = "Rename", Disabled = true, FocusMode = Control.FocusModeEnum.None,
            TooltipText = "Names are IDs: patches and edits refer to a type by its name, so it cannot be renamed. Duplicate the type under the new name and delete the old one." });
        _carryNote = Ui.Note("");
        form.AddChild(_carryNote);

        GroupHeader(form, "appearance");
        BuildColours(form);
        BuildRecipe(form);

        GroupHeader(form, "water");
        _water = CarryBox(GroundTypeEdits.Attribute("water")!.Label, GroundTypeEdits.Attribute("water")!.Tip, on => Ops.SetCarried(_tool.SelectedType!, "water", on));
        form.AddChild(_water);

        BuildOsm(form);

        form.AddChild(new HSeparator());
        _reset = Ui.FocusBtn("Reset to rules", () => { if (_tool.SelectedType != null) Ops.Reset(_tool.SelectedType); });
        _delete = Ui.FocusBtn("Delete  (Del)", () => Ops.RequestDelete(), "DangerButton");
        form.AddChild(_reset);
        form.AddChild(_delete);
        _actionNote = Ui.Note("");
        form.AddChild(_actionNote);
    }

    /// <summary>Appearance: the terrain colours.</summary>
    void BuildColours(VBoxContainer form)
    {
        _carryColours = CarryBox("Terrain colours", GroundTypeEdits.Attribute("colours")!.Tip, on => Ops.SetCarried(_tool.SelectedType!, "colours", on));
        form.AddChild(_carryColours);
        _coloursBox = new VBoxContainer();
        form.AddChild(_coloursBox);
        _colours = new VBoxContainer();
        _coloursBox.AddChild(_colours);
        _colourNote = Ui.Note("");
        _coloursBox.AddChild(_colourNote);
        _addColour = Ui.FocusBtn("Add a colour", () => { if (_tool.SelectedType != null) Ops.AddColour(_tool.SelectedType); });
        _addColour.TooltipText = "Add another terrain shade (a type has 1 to 6; one is picked per area)";
        _coloursBox.AddChild(_addColour);
    }

    /// <summary>Appearance: the detail recipe with its detail settings.</summary>
    void BuildRecipe(VBoxContainer form)
    {
        _carryRecipe = CarryBox("Detail recipe", GroundTypeEdits.Attribute("recipe")!.Tip, on => Ops.SetCarried(_tool.SelectedType!, "recipe", on));
        form.AddChild(_carryRecipe);
        _recipeBox = new VBoxContainer();
        form.AddChild(_recipeBox);
        _recipe = new OptionButton { FocusMode = Control.FocusModeEnum.All, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var r in GroundTypeEdits.Recipes) _recipe.AddItem(r);
        _recipe.ItemSelected += i =>
        {
            if (!_sync && _tool.SelectedType != null) Ops.SetRecipe(_tool.SelectedType, GroundTypeEdits.Recipes[(int)i]);
            _recipe.ReleaseFocus();
        };
        _recipeBox.AddChild(Ui.FieldRow("Recipe", _recipe, "The detail recipe: what grass, clutter and patches the ground gets"));
        _recipeBox.AddChild(new Label { Text = "Advanced detail settings", ThemeTypeVariation = "Dim" });
        _detailNote = Ui.Note("");
        _recipeBox.AddChild(_detailNote);
        _detailFields = new VBoxContainer();
        _recipeBox.AddChild(_detailFields);
        var box = new PanelContainer { ThemeTypeVariation = "ReadOnlyBox" };
        _detailText = new Label { ThemeTypeVariation = "Dim", AutowrapMode = TextServer.AutowrapMode.WordSmart, TooltipText = "The effective detail settings of this type (rules.yaml with your changes), read only" };
        box.AddChild(_detailText);
        _recipeBox.AddChild(box);
    }

    void BuildOsm(VBoxContainer form)
    {
        form.AddChild(new Label { Text = "Source (OSM)", ThemeTypeVariation = "Header" });
        _osmNote = Ui.Note("");
        form.AddChild(_osmNote);
        _osmBox = new VBoxContainer();
        form.AddChild(_osmBox);
        _landuse = new LineEdit { PlaceholderText = "forest, meadow", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "OSM landuse values this type takes, separated by commas. Enter applies." };
        _natural = new LineEdit { PlaceholderText = "wood, scrub", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "OSM natural values this type takes, separated by commas. Enter applies." };
        foreach (var le in new[] { _landuse, _natural })
        {
            le.TextChanged += t => OsmValidate();
            le.TextSubmitted += _ => { OsmApply(); };
            le.FocusExited += () => { if (_osmValid.ThemeTypeVariation != "Danger") OsmApply(); };
            EscapableText(le, RestoreOsmText);
        }
        _osmBox.AddChild(Ui.FieldRow("landuse", _landuse));
        _osmBox.AddChild(Ui.FieldRow("natural", _natural));
        _osmValid = Ui.Note("");
        _osmBox.AddChild(_osmValid);
    }

    // ---- OSM text ----

    void RestoreOsmText()
    {
        if (_tool.SelectedType == null || _tool.Row(_tool.SelectedType) is not { } r) return;
        _landuse.Text = string.Join(", ", r.Effective.Landuse);
        _natural.Text = string.Join(", ", r.Effective.Natural);
        OsmValidate();
    }

    void OsmValidate()
    {
        var (_, p1) = GroundTypeEdits.ParseOsmList(_landuse.Text);
        var (_, p2) = GroundTypeEdits.ParseOsmList(_natural.Text);
        var problem = p1 ?? p2;
        _osmValid.Text = problem ?? "Enter applies. Several values go with commas.";
        _osmValid.ThemeTypeVariation = problem != null ? "Danger" : "Dim";
    }

    void OsmApply()
    {
        if (_tool.SelectedType is not { } name) return;
        var (lu, _) = GroundTypeEdits.ParseOsmList(_landuse.Text);
        var (nat, _) = GroundTypeEdits.ParseOsmList(_natural.Text);
        if (lu == null || nat == null) { OsmValidate(); return; }
        if (_tool.Row(name) is { } r && lu.SequenceEqual(r.Effective.Landuse) && nat.SequenceEqual(r.Effective.Natural)) return;
        Ops.SetOsm(name, lu, nat);
    }

    // ---- rebuilt rows ----

    void RebuildColours(GtRow sel, bool canEdit)
    {
        foreach (var c in _colours.GetChildren()) { _colours.RemoveChild(c); c.QueueFree(); }
        var n = sel.Effective.Colours.Count;
        for (var i = 0; i < n; i++)
        {
            var idx = i;
            var hex = sel.Effective.Colours[i];
            var row = new HBoxContainer();
            var picker = new ColorPickerButton
            {
                CustomMinimumSize = new Vector2(EditorTheme.RailButton, EditorTheme.Space * 4), EditAlpha = false, FocusMode = Control.FocusModeEnum.All,
                Color = Color.FromHtml(hex), Disabled = !canEdit,
                TooltipText = $"Colour {i + 1}{(i == 0 ? " (shown in the lists)" : "")}. Enter opens the picker; closing it saves the change as one undo step.",
            };
            var edit = new LineEdit { Text = hex, MaxLength = 7, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Editable = canEdit, TooltipText = "Colour as #rrggbb; Enter applies" };
            picker.ColorChanged += c => edit.Text = "#" + c.ToHtml(false); // saved when the picker closes
            picker.PopupClosed += () => { if (_tool.SelectedType != null) Ops.SetColour(_tool.SelectedType, idx, "#" + picker.Color.ToHtml(false)); };
            edit.TextChanged += t =>
            {
                var ok = EditSchema.ParseColour(t) != null;
                _colourNote.Text = ok ? "" : "Colour must look like #rrggbb, for example #7aa860";
                _colourNote.ThemeTypeVariation = ok ? "Dim" : "Danger";
            };
            edit.TextSubmitted += t => { if (_tool.SelectedType != null && Ops.SetColour(_tool.SelectedType, idx, t)) edit.ReleaseFocus(); };
            edit.GuiInput += e => { if (e is InputEventKey { Pressed: true, Keycode: Key.Escape }) { edit.ReleaseFocus(); edit.AcceptEvent(); _colourNote.Text = ""; edit.Text = hex; } };
            var remove = Ui.FocusBtn("Remove", () => { if (_tool.SelectedType != null) Ops.RemoveColour(_tool.SelectedType, idx); });
            remove.Disabled = !canEdit || n <= 1;
            remove.TooltipText = n <= 1 ? "A type keeps at least one colour (untick Terrain colours to carry none)" : $"Remove colour {i + 1}";
            row.AddChild(picker);
            row.AddChild(edit);
            row.AddChild(remove);
            _colours.AddChild(row);
        }
        _colourNote.Text = "";
    }

    void RebuildDetailFields(GtRow sel)
    {
        foreach (var c in _detailFields.GetChildren()) { _detailFields.RemoveChild(c); c.QueueFree(); }
        _detailSpins.Clear();
        foreach (var f in GroundTypeEdits.FieldsFor(sel.Effective.Recipe))
        {
            var field = f;
            var spin = Ui.NumberField(f.Min, f.Max, f.Step, $"{f.Tip} Recipe default: {Ops.RecipeDefault(sel.Effective.Recipe, f.Path)?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}. Enter applies.", _host.MarkPanelDirty);
            spin.ValueChanged += v => { if (!_sync && _tool.SelectedType != null) Ops.SetDetail(_tool.SelectedType, field.Path, v); };
            _detailSpins[f.Path] = spin;
            _detailFields.AddChild(Ui.FieldRow(f.Label, spin, f.Tip));
        }
    }

    // ---- refresh ----

    public void Refresh()
    {
        if (_host.Tool != EditorTool.Ground) return;
        _tool.PatchPage.Refresh();
        var rows = _tool.Rows();
        _tool.SettleSelection(rows);
        var sel = rows.FirstOrDefault(r => r.Name == _tool.SelectedType);

        RefreshHead(rows);
        RefreshList(rows);
        RefreshNewType(rows, sel);

        _form.Visible = sel != null;
        if (sel == null) { _osmSeen = ""; _scrolledFor = ""; return; }
        RefreshForm(sel);
    }

    /// <summary>Empty and error states of the map, and the count over the list.</summary>
    void RefreshHead(IReadOnlyList<GtRow> rows)
    {
        var table = _host.Map.GroundTypeTable;
        var legacy = table.Any(t => t.Legacy);
        _error.Visible = table.Count == 0 || legacy;
        _error.Text = table.Count == 0
            ? "This map's parcels.json lists no ground types (it was built before they could be edited). Build it again (make map), then reopen the editor. Types you make now are saved and used by that build."
            : "This map was built before ground types could be edited, so only their colours are known here. Build it again (make map) to see and edit the rest.";
        _listHead.Text = $"All ground types ({rows.Count})";
    }

    void RefreshList(IReadOnlyList<GtRow> rows)
    {
        var selected = _tool.SelectedType;
        var overlay = _tool.Overlay;
        var listSig = string.Join("|", rows.Select(r => GroundTypeRows.RowText(r) + r.Effective.Colour + overlay.AreaText(r.Name))) + "#" + selected;
        if (listSig == _listSig) return;
        _listSig = listSig;
        foreach (var c in _list.GetChildren()) { _list.RemoveChild(c); c.QueueFree(); }
        _rowButtons.Clear();
        if (rows.Count == 0) _list.AddChild(new Label { Text = "No ground types yet. Name one below and press Create.", ThemeTypeVariation = "Dim" });
        foreach (var r in rows)
        {
            var name = r.Name;
            var b = new Button
            {
                Text = GroundTypeRows.RowText(r) + (overlay.AreaText(r.Name) is { Length: > 0 } at ? $"   ·  {at}" : ""), Icon = Ui.Swatch(r.Effective.Colour), Alignment = HorizontalAlignment.Left, ToggleMode = true, ClipText = true,
                FocusMode = Control.FocusModeEnum.All, TooltipText = GroundTypeRows.RowTip(r) + (overlay.AreaText(r.Name) is { Length: > 0 } at2 ? $" On the map: {at2}." : ""),
            };
            b.SetPressedNoSignal(name == selected);
            b.Pressed += () => { _tool.SelectType(name == _tool.SelectedType ? null : name); b.ReleaseFocus(); };
            _list.AddChild(b);
            _rowButtons[name] = b;
        }
    }

    void RefreshNewType(IReadOnlyList<GtRow> rows, GtRow? sel)
    {
        var typed = _name.Text.Trim();
        var problem = GroundTypeEdits.NameProblem(typed, rows.Select(r => r.Name));
        _nameValid.Text = typed.Length == 0 ? "Names are IDs: lower case letters, digits and _, at most 24." : problem ?? "Name is free.";
        _nameValid.ThemeTypeVariation = typed.Length == 0 ? "Dim" : problem != null ? "Danger" : "Dim";
        _create.Disabled = problem != null;
        _duplicate.Disabled = problem != null || sel == null || sel.Deleted || sel.Effective.Rule != null;
        _duplicate.TooltipText = sel == null ? "Select a type in the list to copy it under the name typed above"
            : sel.Effective.Rule != null ? "The dike is made by a rule and cannot be copied"
            : $"Copy {sel.Name} (its attributes; not its OSM tags) under the name typed above";
    }

    /// <summary>The form of the selected type.</summary>
    void RefreshForm(GtRow sel)
    {
        var editable = !sel.Deleted;
        var s = sel.Effective;
        _title.Text = sel.Name;
        _stateNote.Text = sel.State switch
        {
            "overridden" => "Edited by you: your changes are saved in edits.json over rules.yaml. Reset to rules removes them.",
            "custom" => "Your own type, saved in edits.json. Used by patches and by the OSM values you give it.",
            "deleted" => "Deleted by you: gone from the next build and from the type list of a patch. Reset to rules brings it back.",
            _ => "As in rules.yaml. Any change you make is saved as an override in edits.json.",
        };
        _stateNote.ThemeTypeVariation = sel.Deleted ? "Warn" : "Dim";
        _carryNote.Text = s.Carried.Count > 0
            ? "Tick the attributes this type carries. Unticked ones are not carried."
            : "This type carries nothing yet. Tick the attributes it carries (a type with only colours is plain paint).";

        var selSig = sel.Name + "|" + sel.State;
        var colSig = selSig + "|" + string.Join(",", s.Colours);
        if (colSig != _colourSig) { _colourSig = colSig; RebuildColours(sel, editable); }
        _addColour.Disabled = !editable || s.Colours.Count >= GroundTypeEdits.ColoursMax;
        _addColour.TooltipText = s.Colours.Count >= GroundTypeEdits.ColoursMax ? "A type has at most 6 colours" : "Add another terrain shade (a type has 1 to 6; one is picked per area)";

        _sync = true;
        RefreshAttributes(sel, editable);
        RefreshOsm(sel, selSig, editable);
        RefreshDetailFields(sel, selSig, editable);
        _sync = false;
        _detailNote.Text = s.Recipe == null ? "This type has no detail recipe, so it has no detail settings."
            : s.Recipe is "village" or "dike" ? $"The {s.Recipe} recipe has no settings to edit here. Others are in rules.yaml (detail.{s.Recipe}) and through the prompt."
            : "Settings laid over the recipe for this type. Others are in rules.yaml and through the prompt.";
        var text = GroundTypeEdits.DetailText(s.Detail);
        _detailText.Text = text.Length == 0 ? "No detail overlay: the recipe's own settings apply." : text;

        RefreshActions(sel);
        if (_scrolledFor != sel.Name)
        {
            _scrolledFor = sel.Name;
            if (!_tool.FocusPatch) _host.Chrome.RevealInPanel(_title);
        }
    }

    /// <summary>The three ticks (colours, recipe, water) and the recipe dropdown.</summary>
    void RefreshAttributes(GtRow sel, bool editable)
    {
        var s = sel.Effective;
        _carryColours.SetPressedNoSignal(s.Carries("colours"));
        _carryRecipe.SetPressedNoSignal(s.Carries("recipe"));
        _water.SetPressedNoSignal(s.Water);
        foreach (var (key, check) in new[] { ("colours", _carryColours), ("recipe", _carryRecipe), ("water", _water) })
        {
            var blocked = GroundTypeRows.UntickBlocked(sel, key);
            check.Disabled = !editable || (blocked != null && check.ButtonPressed);
            check.TooltipText = blocked != null && s.Carries(key) ? blocked : GroundTypeEdits.Attribute(key)!.Tip;
        }
        _coloursBox.Visible = s.Carries("colours");
        _recipeBox.Visible = s.Carries("recipe");

        var recipeAt = s.Recipe == null ? -1 : Array.IndexOf(GroundTypeEdits.Recipes, s.Recipe);
        if (!_recipe.GetPopup().Visible) _recipe.Selected = recipeAt;
        _recipe.Disabled = !editable || s.Rule != null;
        _recipe.TooltipText = s.Rule != null ? "The recipe of a rule type is fixed (the dike is built from detail.dike)" : "The detail recipe: what grass, clutter and patches the ground gets";
    }

    void RefreshOsm(GtRow sel, string selSig, bool editable)
    {
        var s = sel.Effective;
        var rule = s.Rule != null;
        _osmNote.Visible = rule;
        _osmNote.Text = rule ? $"Made by the rule {s.Rule} (the flood dike of detail.dike in rules.yaml). Not editable here." : "";
        _osmBox.Visible = !rule;
        var osmSig = selSig + "|" + string.Join(",", s.Landuse) + ";" + string.Join(",", s.Natural);
        if (!_landuse.HasFocus() && !_natural.HasFocus() && _osmSeen != osmSig)
        {
            _osmSeen = osmSig;
            _landuse.Text = string.Join(", ", s.Landuse);
            _natural.Text = string.Join(", ", s.Natural);
            OsmValidate();
        }
        _landuse.Editable = _natural.Editable = editable;
    }

    void RefreshDetailFields(GtRow sel, string selSig, bool editable)
    {
        var s = sel.Effective;
        var detailSig = selSig + "|" + s.Recipe;
        if (detailSig != _detailSig) { _detailSig = detailSig; RebuildDetailFields(sel); }
        foreach (var f in GroundTypeEdits.FieldsFor(s.Recipe))
            if (_detailSpins.TryGetValue(f.Path, out var spin))
            {
                if (!spin.GetLineEdit().HasFocus()) spin.SetValueNoSignal(Ops.EffectiveDetail(sel, f));
                spin.Editable = editable;
            }
    }

    /// <summary>Reset and Delete: enabled as the type allows, and why not.</summary>
    void RefreshActions(GtRow sel)
    {
        _reset.Disabled = sel.State is not ("overridden" or "deleted");
        _reset.TooltipText = !sel.InRules ? "Your own type has no rules to reset to; Delete removes it"
            : _reset.Disabled ? "Nothing to reset: this type is as in rules.yaml" : "Remove your changes: the type is as in rules.yaml again (Ctrl+Z undoes)";
        var blockedDelete = Ops.DeleteBlocked(sel);
        _delete.Disabled = blockedDelete != null;
        _delete.TooltipText = blockedDelete ?? "Delete this type (asks first; Ctrl+Z undoes)";
        _actionNote.Text = blockedDelete != null && !sel.Deleted ? blockedDelete + "." : "";
    }
}
