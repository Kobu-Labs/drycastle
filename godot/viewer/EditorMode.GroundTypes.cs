using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Obj = System.Collections.Generic.SortedDictionary<string, object?>;
using GtRow = GroundTypeEdits.Row;

/// <summary>
/// The Ground types tool (GLOSSARY "Ground type"): list, edit, create, duplicate, reset and delete the ground types of
/// the map. The user's changes are stored in edits.json `ground_types` over the entries of rules.yaml (see
/// <see cref="GroundTypeEdits"/>); the types of rules.yaml as the last build wrote them come from parcels.json
/// (`Map.GroundTypeTable`). The page has no states of its own beyond the selection: Nothing selected (the list and the
/// New type row), Selected (the form of one type). Every change is ONE Mutate = one save = one undo step. A type change needs a full
/// map build (make map): the banner says so. Esc goes one level up: leave a text field, deselect the type, leave the tool.
/// Names are IDs, so there is no rename.
/// </summary>
public partial class EditorMode
{
    public string? SelectedGroundType { get; private set; }

    string? _restoreGround;
    Obj? _gtFor;
    object? _gtTableFor;
    List<GtRow> _gtRows = new();
    readonly Dictionary<string, Obj?> _gtRecipeDefaults = new();

    VBoxContainer _gtList = null!, _gtForm = null!, _gtColours = null!, _gtDetailFields = null!;
    Label _gtError = null!, _gtListHead = null!, _gtTitle = null!, _gtStateNote = null!, _gtNameValid = null!, _gtColourNote = null!,
        _gtOsmNote = null!, _gtOsmValid = null!, _gtDetailText = null!, _gtDetailNote = null!, _gtActionNote = null!;
    LineEdit _gtName = null!, _gtLanduse = null!, _gtNatural = null!;
    Button _gtCreate = null!, _gtDuplicate = null!, _gtAddColour = null!, _gtRename = null!, _gtReset = null!, _gtDelete = null!;
    OptionButton _gtRecipe = null!;
    CheckBox _gtWater = null!, _gtSpawns = null!;
    VBoxContainer _gtOsmBox = null!;
    bool _gtSync, _gtSliderDrag;
    string _gtListSig = "", _gtColourSig = "", _gtDetailSig = "", _gtOsmSeen = "", _gtScrolledFor = "";
    readonly Dictionary<string, Button> _gtRowButtons = new();
    readonly Dictionary<string, SpinBox> _gtDetailSpins = new();
    static readonly string[] RecipeItems = new[] { "No detail" }.Concat(GroundTypeEdits.Recipes).ToArray();

    // ---- the table ----

    /// <summary>The ground types as they stand: rules.yaml's (as of the last build) with the edits on disk laid over them, then the user's own.</summary>
    public IReadOnlyList<GtRow> GroundRows()
    {
        var disk = Disk();
        if (ReferenceEquals(disk, _gtFor) && ReferenceEquals(Map.GroundTypeTable, _gtTableFor)) return _gtRows;
        _gtFor = disk;
        _gtTableFor = Map.GroundTypeTable;
        var edits = disk.GetValueOrDefault("ground_types") as Obj ?? EditsFile.NewObject();
        var bases = new List<(string, Obj?)>();
        var known = new Dictionary<string, Obj>();
        foreach (var t in Map.GroundTypeTable)
        {
            if (t.Custom) continue;
            Obj? raw;
            if (t.RulesJson != null) raw = EditsFile.ParseValue(t.RulesJson) as Obj;
            else
            {
                // built before ground types were editable: the colour only
                raw = EditsFile.NewObject();
                raw["colours"] = new List<object?> { t.Colour };
            }
            bases.Add((t.Name, raw));
            if (raw != null && !t.Legacy) known[t.Name] = raw;
        }
        GroundTypeEdits.RulesTypes = known;
        GroundTypeEdits.DefaultType = Map.GroundDefault;
        _gtRows = GroundTypeEdits.Table(bases, edits);
        return _gtRows;
    }

    GtRow? GroundRow(string name) => GroundRows().FirstOrDefault(r => r.Name == name);

    /// <summary>The types a region can be given now (name, colour of its first shade): the table without deleted types, rule types and water.</summary>
    public List<(string Name, string Colour)> GroundChoices()
        => GroundRows().Where(r => !r.Deleted && r.Effective.Rule == null && !r.Effective.Water).Select(r => (r.Name, r.Effective.Colour)).ToList();

    static string StateWord(string state) => state switch { "overridden" => "edited", "custom" => "yours", "deleted" => "deleted", _ => "" };

    static string RecipeWord(GtRow r) => r.Effective.Water ? "water" : r.Effective.Recipe ?? "no detail";

    static string RowText(GtRow r) => $"{r.Name}   {RecipeWord(r)}" + (StateWord(r.State) is { Length: > 0 } w ? $"   [{w}]" : "");

    static string RowTip(GtRow r) => $"{r.Name}: recipe {RecipeWord(r)}, {r.Effective.Source}, {r.Effective.Colours.Count} colour{(r.Effective.Colours.Count == 1 ? "" : "s")}"
        + (r.Effective.Spawns ? ", players spawn here" : "") + (r.State switch
        {
            "overridden" => ". Edited by you over rules.yaml.",
            "custom" => ". Made by you.",
            "deleted" => ". Deleted by you (Reset brings it back).",
            _ => ". As in rules.yaml.",
        });

    string? DeleteBlocked(GtRow r)
        => r.Deleted ? "Already deleted" : r.InRules && GroundTypeEdits.ProtectedReason(r.Name, r.BaseRaw, Map.GroundDefault) is { } why ? $"Cannot be deleted: {why}" : null;

    // ---- selection ----

    public bool SelectGroundType(string? name)
    {
        if (name != null && GroundRow(name) == null) { Say($"ground type {name} does not exist", true); return false; }
        if (name == SelectedGroundType) return true;
        SelectedGroundType = name;
        _panelDirty = true;
        SaveUiConfig();
        return true;
    }

    // ---- the edits (each is one Mutate: one save, one undo step) ----

    static Obj GroundTypesOf(Obj root)
    {
        if (root.GetValueOrDefault("ground_types") is not Obj t) root["ground_types"] = t = EditsFile.NewObject();
        return t;
    }

    static void TidyGround(Obj root)
    {
        if (root.GetValueOrDefault("ground_types") is Obj { Count: 0 }) root.Remove("ground_types");
    }

    /// <summary>Change the override of a type: <paramref name="edit"/> works on its entry; what then says the same as rules.yaml is dropped.</summary>
    bool MutateGround(string name, string message, Action<Obj> edit)
    {
        if (GroundRow(name) is not { } row) { Say($"ground type {name} does not exist", true); return false; }
        if (row.Deleted) { Say($"{name} is deleted: Reset to rules brings it back first", true); return false; }
        return Mutate(root =>
        {
            var types = GroundTypesOf(root);
            var entry = types.GetValueOrDefault(name) as Obj ?? EditsFile.NewObject();
            edit(entry);
            GroundTypeEdits.Prune(entry, row.BaseRaw);
            if (entry.Count == 0 && row.InRules) types.Remove(name); else types[name] = entry;
            TidyGround(root);
            return (true, message);
        });
    }

    public bool SetGroundColours(string name, IReadOnlyList<string> colours)
    {
        var parsed = colours.Select(c => ParseColour(c)).ToList();
        if (parsed.Count is < 1 or > GroundTypeEdits.ColoursMax || parsed.Any(c => c == null)) { Say($"a type has 1 to {GroundTypeEdits.ColoursMax} colours, each #rrggbb", true); return false; }
        return MutateGround(name, $"{name}: {parsed.Count} colour{(parsed.Count == 1 ? "" : "s")}", e => e["colours"] = parsed.Cast<object?>().ToList());
    }

    public bool SetGroundColour(string name, int index, string colour)
    {
        if (GroundRow(name) is not { } r || index < 0 || index >= r.Effective.Colours.Count) return false;
        if (ParseColour(colour) is not { } hex) { Say($"colour must look like #rrggbb, got {colour}", true); return false; }
        if (r.Effective.Colours[index] == hex) return true;
        var list = r.Effective.Colours.ToList();
        list[index] = hex;
        return SetGroundColours(name, list);
    }

    public bool AddGroundColour(string name)
    {
        if (GroundRow(name) is not { } r) return false;
        if (r.Effective.Colours.Count >= GroundTypeEdits.ColoursMax) { Say($"at most {GroundTypeEdits.ColoursMax} colours", true); return false; }
        return SetGroundColours(name, r.Effective.Colours.Append(r.Effective.Colours.LastOrDefault() ?? GroundTypeEdits.NewTypeColour).ToList());
    }

    public bool RemoveGroundColour(string name, int index)
    {
        if (GroundRow(name) is not { } r || index < 0 || index >= r.Effective.Colours.Count) return false;
        if (r.Effective.Colours.Count <= 1) { Say("a type keeps at least one colour", true); return false; }
        var list = r.Effective.Colours.ToList();
        list.RemoveAt(index);
        return SetGroundColours(name, list);
    }

    /// <summary>Set the detail recipe (null: no detail). Your own `detail` settings go, they belong to the old recipe.</summary>
    public bool SetGroundRecipe(string name, string? recipe)
    {
        if (recipe != null && !GroundTypeEdits.Recipes.Contains(recipe)) { Say($"unknown recipe {recipe}", true); return false; }
        return MutateGround(name, $"{name}: recipe {recipe ?? "none"}", e => { e["recipe"] = recipe; e.Remove("detail"); });
    }

    public bool SetGroundFlag(string name, string key, bool value)
    {
        if (key is not ("water" or "spawns")) return false;
        return MutateGround(name, $"{name}: {key} {(value ? "on" : "off")}", e => e[key] = value);
    }

    public bool SetGroundOsm(string name, IReadOnlyList<string> landuse, IReadOnlyList<string> natural)
    {
        if (GroundRow(name) is not { } row) return false;
        if (row.Effective.Rule != null) { Say($"{name} is made by a rule; its source is not editable", true); return false; }
        return MutateGround(name, $"{name}: OSM tags", e =>
        {
            var baseOsm = row.BaseRaw?.GetValueOrDefault("osm") as Obj;
            var osm = EditsFile.NewObject();
            foreach (var (cls, list) in new[] { ("landuse", landuse), ("natural", natural) })
                if (!list.SequenceEqual(GroundTypeEdits.Strings(baseOsm?.GetValueOrDefault(cls)))) osm[cls] = list.Cast<object?>().ToList();
            if (osm.Count == 0) e.Remove("osm"); else e["osm"] = osm;
        });
    }

    /// <summary>The shipped value of a detail setting for a recipe (from parcels.json `ground_recipes`), or null.</summary>
    double? RecipeDefault(string? recipe, string path)
    {
        if (recipe == null || !Map.GroundRecipes.TryGetValue(recipe, out var json)) return null;
        if (!_gtRecipeDefaults.TryGetValue(recipe, out var tree)) _gtRecipeDefaults[recipe] = tree = EditsFile.ParseValue(json) as Obj;
        return ObjectEdits.Number(GroundTypeEdits.GetPath(tree, path));
    }

    double EffectiveDetail(GtRow r, GroundTypeEdits.DetailField f)
        => ObjectEdits.Number(GroundTypeEdits.GetPath(r.Effective.Detail, f.Path)) ?? RecipeDefault(r.Effective.Recipe, f.Path) ?? f.Min;

    public bool SetGroundDetail(string name, string path, double value)
    {
        if (GroundTypeEdits.DetailFields.FirstOrDefault(f => f.Path == path) is not { } field) { Say($"{path} is not a detail setting the editor exposes", true); return false; }
        if (GroundRow(name) is not { } row) return false;
        var v = Math.Round(Math.Clamp(value, field.Min, field.Max), 6);
        if (field.Recipes.All(r => r != row.Effective.Recipe)) { Say($"{field.Label} does not apply to the recipe {row.Effective.Recipe ?? "none"}", true); return false; }
        var recipeDefault = RecipeDefault(row.Effective.Recipe, path);
        var rulesSet = GroundTypeEdits.GetPath(row.BaseRaw?.GetValueOrDefault("detail") as Obj, path) != null;
        return MutateGround(name, $"{name}: {field.Label} {v}", e =>
        {
            if (e.GetValueOrDefault("detail") is not Obj d) e["detail"] = d = EditsFile.NewObject();
            GroundTypeEdits.SetPath(d, path, FenceDraft.Num(v));
            if (!rulesSet && recipeDefault is { } def && Math.Abs(def - v) < 1e-9) GroundTypeEdits.RemovePath(d, path); // the recipe's own value: no override
            if (d.Count == 0) e.Remove("detail");
        });
    }

    /// <summary>Make a new type, or with <paramref name="from"/> a copy of that type's effective settings (not its OSM tags: the copy is drawn by hand only).</summary>
    public bool CreateGroundType(string name, string? from = null)
    {
        var rows = GroundRows();
        if (GroundTypeEdits.NameProblem(name, rows.Select(r => r.Name)) is { } problem) { Say($"not created: {problem}", true); return false; }
        GtRow? src = from == null ? null : rows.FirstOrDefault(r => r.Name == from);
        if (from != null && (src == null || src.Deleted || src.Effective.Rule != null)) { Say($"{from} cannot be duplicated", true); return false; }
        var ok = Mutate(root =>
        {
            var types = GroundTypesOf(root);
            if (types.ContainsKey(name)) return (false, $"{name} already exists");
            var e = EditsFile.NewObject();
            if (src == null)
            {
                e["colours"] = new List<object?> { GroundTypeEdits.NewTypeColour };
                e["recipe"] = "field";
            }
            else
            {
                e["colours"] = src.Effective.Colours.Cast<object?>().ToList();
                if (src.Effective.Recipe != null) e["recipe"] = src.Effective.Recipe;
                if (src.Effective.Detail.Count > 0) e["detail"] = ObjectEdits.CloneObj(src.Effective.Detail);
                if (src.Effective.Water) e["water"] = true;
                if (src.Effective.Spawns) e["spawns"] = true;
            }
            types[name] = e;
            return (true, src == null ? $"created ground type {name}" : $"duplicated {from} as {name}");
        });
        if (ok) { SelectGroundType(name); _gtName.Text = ""; }
        return ok;
    }

    /// <summary>Remove your override of a type of rules.yaml (also brings back a deleted one).</summary>
    public bool ResetGroundType(string name)
    {
        if (GroundRow(name) is not { } r) return false;
        if (!r.InRules) { Say($"{name} is your own type: there are no rules to reset to (Delete removes it)", true); return false; }
        if (r.State == "rules") { Say($"{name} is already as in rules.yaml"); return true; }
        return Mutate(root =>
        {
            GroundTypesOf(root).Remove(name);
            TidyGround(root);
            return (true, $"{name} is as in rules.yaml again (Ctrl+Z undoes)");
        });
    }

    /// <summary>Delete a type: your own is removed, one of rules.yaml is marked deleted (Reset brings it back). Refused for the default type and the dike.</summary>
    public bool DeleteGroundType(string name)
    {
        if (GroundRow(name) is not { } r) { Say($"ground type {name} does not exist", true); return false; }
        if (DeleteBlocked(r) is { } why) { Say($"{name}: {why}", true); return false; }
        var ok = Mutate(root =>
        {
            var types = GroundTypesOf(root);
            if (r.InRules) { var gone = EditsFile.NewObject(); gone["deleted"] = true; types[name] = gone; } else types.Remove(name);
            TidyGround(root);
            return (true, $"deleted ground type {name} (Ctrl+Z undoes)");
        });
        if (ok && SelectedGroundType == name) SelectGroundType(null);
        return ok;
    }

    /// <summary>Del: ask first, naming what is lost; accepting deletes (undo brings it back).</summary>
    public bool RequestDeleteGroundType()
    {
        if (SelectedGroundType is not { } name || GroundRow(name) is not { } r) { Say("no ground type selected", true); return false; }
        if (DeleteBlocked(r) is { } why) { Say($"{name}: {why}", true); return false; }
        var used = Regions().Count(x => x.Ground == name);
        ShowConfirm($"Delete ground type {name}?",
            (r.InRules ? $"{name} is removed from the map at the next build (Reset to rules brings it back)." : $"{name} is removed.")
            + (used > 0 ? $" {used} region{(used == 1 ? "" : "s")} use it and will be skipped with a warning." : "")
            + " Undo (Ctrl+Z) brings it back.", "Delete", () => DeleteGroundType(name));
        return true;
    }

    // ---- keys ----

    /// <summary>Ground types tool keys (not reached while a text field has the focus). True when used.</summary>
    bool HandleGroundKey(InputEventKey k)
    {
        if (Tool != EditorTool.Ground || _cheat.Visible || k.CtrlPressed || k.AltPressed) return false;
        switch (k.Keycode)
        {
            case Key.N: _gtName.GrabFocus(); return true;
            case Key.Delete when SelectedGroundType != null: RequestDeleteGroundType(); return true;
            case Key.Tab when GetViewport().GuiGetFocusOwner() == null: FocusGroundList(); return true;
        }
        return false;
    }

    /// <summary>Tab with nothing focused: the selected type in the list, else the first (the keyboard way into the panel).</summary>
    void FocusGroundList()
    {
        if (_gtRowButtons.Count == 0) { _gtName.GrabFocus(); return; }
        (SelectedGroundType != null && _gtRowButtons.TryGetValue(SelectedGroundType, out var b) ? b : _gtRowButtons.Values.First()).GrabFocus();
    }

    public string GroundHintText() => SelectedGroundType == null
        ? "ground types: Tab or click the list, N a new type, Ctrl+P commands, Esc leaves the tool"
        : $"{SelectedGroundType}: Tab moves between the fields, Enter applies a typed value, Del deletes, Ctrl+Z undoes, Esc deselects";

    /// <summary>Esc: one level up (the selection, then the tool is left by the main handler). True when it deselected.</summary>
    bool GroundEscape()
    {
        if (SelectedGroundType == null) return false;
        SelectGroundType(null);
        return true;
    }

    // ---- sidebar page ----

    SpinBox NumberField(double min, double max, double step, string tip)
    {
        var s = new SpinBox { MinValue = min, MaxValue = max, Step = step, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = tip, Rounded = false, Alignment = HorizontalAlignment.Right };
        s.GetLineEdit().GuiInput += e => { if (e is InputEventKey { Pressed: true, Keycode: Key.Escape }) { s.GetLineEdit().ReleaseFocus(); s.GetLineEdit().AcceptEvent(); _panelDirty = true; } };
        return s;
    }

    /// <summary>Esc in a text field leaves it (one level up); <paramref name="revert"/> puts the old text back first.</summary>
    void EscapableText(LineEdit le, Action? revert = null)
        => le.GuiInput += e => { if (e is InputEventKey { Pressed: true, Keycode: Key.Escape }) { revert?.Invoke(); le.ReleaseFocus(); le.AcceptEvent(); _panelDirty = true; } };

    void RestoreOsmText()
    {
        if (SelectedGroundType == null || GroundRow(SelectedGroundType) is not { } r) return;
        _gtLanduse.Text = string.Join(", ", r.Effective.Landuse);
        _gtNatural.Text = string.Join(", ", r.Effective.Natural);
        OsmValidate();
    }

    HBoxContainer FieldRow(string label, Control field, string? tip = null)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, TooltipText = tip ?? "", CustomMinimumSize = new Vector2(EditorTheme.Space * 12, 0) });
        row.AddChild(field);
        return row;
    }

    void BuildGroundPage(VBoxContainer page)
    {
        page.AddChild(new Label { Text = "Ground types", ThemeTypeVariation = "Header" });
        page.AddChild(Note("A ground type is the whole description of one kind of ground: terrain colours, detail recipe, where it comes from and whether players spawn on it. Hand-drawn areas (Regions) use them."));
        var banner = new PanelContainer { ThemeTypeVariation = "BannerPanel" };
        banner.AddChild(Note("Changes show after the next full map build (make map). The editor saves them at once; Ctrl+Z undoes.", "Warn"));
        page.AddChild(banner);
        _gtError = Note("", "Danger");
        page.AddChild(_gtError);

        _gtListHead = new Label { Text = "All ground types", ThemeTypeVariation = "Header" };
        page.AddChild(_gtListHead);
        _gtList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        page.AddChild(_gtList);

        page.AddChild(new Label { Text = "New type", ThemeTypeVariation = "Header" });
        _gtName = new LineEdit { PlaceholderText = "my_meadow", MaxLength = GroundTypeEdits.NameMax + 8, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClearButtonEnabled = true,
            TooltipText = "Name of the new type: lower case letters, digits and _, starting with a letter, at most 24. A name is an ID and cannot be changed later." };
        _gtName.TextChanged += _ => _panelDirty = true;
        _gtName.TextSubmitted += t => { if (CreateGroundType(t.Trim())) _gtName.ReleaseFocus(); };
        EscapableText(_gtName);
        page.AddChild(_gtName);
        _gtNameValid = Note("");
        page.AddChild(_gtNameValid);
        var newRow = new HBoxContainer();
        _gtCreate = FocusBtn("Create", () => CreateGroundType(_gtName.Text.Trim()), "PrimaryButton");
        _gtCreate.TooltipText = "Make a new type with one green shade and the field recipe (Enter in the name field does the same)";
        _gtDuplicate = FocusBtn("Duplicate selected", () => { if (SelectedGroundType != null) CreateGroundType(_gtName.Text.Trim(), SelectedGroundType); });
        newRow.AddChild(_gtCreate);
        newRow.AddChild(_gtDuplicate);
        page.AddChild(newRow);
        page.AddChild(Note("A new type is for hand-drawn areas. It takes OSM land only if you give it OSM values, and the types above take theirs first."));

        page.AddChild(new HSeparator());
        _gtForm = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        page.AddChild(_gtForm);
        BuildGroundForm(_gtForm);
    }

    void BuildGroundForm(VBoxContainer form)
    {
        _gtTitle = new Label { ThemeTypeVariation = "Title", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        form.AddChild(_gtTitle);
        _gtStateNote = Note("");
        form.AddChild(_gtStateNote);
        _gtRename = new Button { Text = "Rename", Disabled = true, FocusMode = Control.FocusModeEnum.None,
            TooltipText = "Names are IDs: regions and edits refer to a type by its name, so it cannot be renamed. Duplicate the type under the new name and delete the old one." };
        form.AddChild(_gtRename);

        form.AddChild(new Label { Text = "Terrain colours", ThemeTypeVariation = "Header" });
        _gtColours = new VBoxContainer();
        form.AddChild(_gtColours);
        _gtColourNote = Note("");
        form.AddChild(_gtColourNote);
        _gtAddColour = FocusBtn("Add a colour", () => { if (SelectedGroundType != null) AddGroundColour(SelectedGroundType); });
        _gtAddColour.TooltipText = "Add another terrain shade (a type has 1 to 6; one is picked per area)";
        form.AddChild(_gtAddColour);

        form.AddChild(new Label { Text = "Detail", ThemeTypeVariation = "Header" });
        _gtRecipe = new OptionButton { FocusMode = Control.FocusModeEnum.All, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var r in RecipeItems) _gtRecipe.AddItem(r);
        _gtRecipe.ItemSelected += i =>
        {
            if (!_gtSync && SelectedGroundType != null) SetGroundRecipe(SelectedGroundType, i <= 0 ? null : GroundTypeEdits.Recipes[(int)i - 1]);
            _gtRecipe.ReleaseFocus();
        };
        form.AddChild(FieldRow("Recipe", _gtRecipe, "The detail recipe: what grass, clutter and patches the ground gets"));
        _gtWater = new CheckBox { Text = "Water body", FocusMode = Control.FocusModeEnum.All, TooltipText = "A body of water (water.json): swimming and drowning, no detail" };
        _gtWater.Toggled += on => { if (!_gtSync && SelectedGroundType != null) SetGroundFlag(SelectedGroundType, "water", on); };
        _gtSpawns = new CheckBox { Text = "Players may spawn here", FocusMode = Control.FocusModeEnum.All, TooltipText = "Spawn points may be placed on this ground" };
        _gtSpawns.Toggled += on => { if (!_gtSync && SelectedGroundType != null) SetGroundFlag(SelectedGroundType, "spawns", on); };
        form.AddChild(_gtWater);
        form.AddChild(_gtSpawns);

        form.AddChild(new Label { Text = "Source (OSM)", ThemeTypeVariation = "Header" });
        _gtOsmNote = Note("");
        form.AddChild(_gtOsmNote);
        _gtOsmBox = new VBoxContainer();
        form.AddChild(_gtOsmBox);
        _gtLanduse = new LineEdit { PlaceholderText = "forest, meadow", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "OSM landuse values this type takes, separated by commas. Enter applies." };
        _gtNatural = new LineEdit { PlaceholderText = "wood, scrub", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "OSM natural values this type takes, separated by commas. Enter applies." };
        foreach (var le in new[] { _gtLanduse, _gtNatural })
        {
            le.TextChanged += t => OsmValidate();
            le.TextSubmitted += _ => { OsmApply(); };
            le.FocusExited += () => { if (_gtOsmValid.ThemeTypeVariation != "Danger") OsmApply(); };
            EscapableText(le, RestoreOsmText);
        }
        _gtOsmBox.AddChild(FieldRow("landuse", _gtLanduse));
        _gtOsmBox.AddChild(FieldRow("natural", _gtNatural));
        _gtOsmValid = Note("");
        _gtOsmBox.AddChild(_gtOsmValid);

        form.AddChild(new Label { Text = "Advanced detail settings", ThemeTypeVariation = "Header" });
        _gtDetailNote = Note("");
        form.AddChild(_gtDetailNote);
        _gtDetailFields = new VBoxContainer();
        form.AddChild(_gtDetailFields);
        var box = new PanelContainer { ThemeTypeVariation = "ReadOnlyBox" };
        _gtDetailText = new Label { ThemeTypeVariation = "Dim", AutowrapMode = TextServer.AutowrapMode.WordSmart, TooltipText = "The effective detail settings of this type (rules.yaml with your changes), read only" };
        box.AddChild(_gtDetailText);
        form.AddChild(box);

        form.AddChild(new HSeparator());
        _gtReset = FocusBtn("Reset to rules", () => { if (SelectedGroundType != null) ResetGroundType(SelectedGroundType); });
        _gtDelete = FocusBtn("Delete  (Del)", () => RequestDeleteGroundType(), "DangerButton");
        form.AddChild(_gtReset);
        form.AddChild(_gtDelete);
        _gtActionNote = Note("");
        form.AddChild(_gtActionNote);
    }

    void OsmValidate()
    {
        var (_, p1) = GroundTypeEdits.ParseOsmList(_gtLanduse.Text);
        var (_, p2) = GroundTypeEdits.ParseOsmList(_gtNatural.Text);
        var problem = p1 ?? p2;
        _gtOsmValid.Text = problem ?? "Enter applies. Several values go with commas.";
        _gtOsmValid.ThemeTypeVariation = problem != null ? "Danger" : "Dim";
    }

    void OsmApply()
    {
        if (SelectedGroundType is not { } name) return;
        var (lu, _) = GroundTypeEdits.ParseOsmList(_gtLanduse.Text);
        var (nat, _) = GroundTypeEdits.ParseOsmList(_gtNatural.Text);
        if (lu == null || nat == null) { OsmValidate(); return; }
        if (GroundRow(name) is { } r && lu.SequenceEqual(r.Effective.Landuse) && nat.SequenceEqual(r.Effective.Natural)) return;
        SetGroundOsm(name, lu, nat);
    }

    void RebuildGroundColours(GtRow sel, bool canEdit)
    {
        foreach (var c in _gtColours.GetChildren()) { _gtColours.RemoveChild(c); c.QueueFree(); }
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
            picker.PopupClosed += () => { if (SelectedGroundType != null) SetGroundColour(SelectedGroundType, idx, "#" + picker.Color.ToHtml(false)); };
            edit.TextChanged += t =>
            {
                var ok = ParseColour(t) != null;
                _gtColourNote.Text = ok ? "" : "Colour must look like #rrggbb, for example #7aa860";
                _gtColourNote.ThemeTypeVariation = ok ? "Dim" : "Danger";
            };
            edit.TextSubmitted += t => { if (SelectedGroundType != null && SetGroundColour(SelectedGroundType, idx, t)) edit.ReleaseFocus(); };
            edit.GuiInput += e => { if (e is InputEventKey { Pressed: true, Keycode: Key.Escape }) { edit.ReleaseFocus(); edit.AcceptEvent(); _gtColourNote.Text = ""; edit.Text = hex; } };
            var remove = FocusBtn("Remove", () => { if (SelectedGroundType != null) RemoveGroundColour(SelectedGroundType, idx); });
            remove.Disabled = !canEdit || n <= 1;
            remove.TooltipText = n <= 1 ? "A type keeps at least one colour" : $"Remove colour {i + 1}";
            row.AddChild(picker);
            row.AddChild(edit);
            row.AddChild(remove);
            _gtColours.AddChild(row);
        }
        _gtColourNote.Text = "";
    }

    void RebuildGroundDetailFields(GtRow sel)
    {
        foreach (var c in _gtDetailFields.GetChildren()) { _gtDetailFields.RemoveChild(c); c.QueueFree(); }
        _gtDetailSpins.Clear();
        foreach (var f in GroundTypeEdits.FieldsFor(sel.Effective.Recipe))
        {
            var field = f;
            var spin = NumberField(f.Min, f.Max, f.Step, $"{f.Tip} Recipe default: {RecipeDefault(sel.Effective.Recipe, f.Path)?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}. Enter applies.");
            spin.ValueChanged += v => { if (!_gtSync && SelectedGroundType != null) SetGroundDetail(SelectedGroundType, field.Path, v); };
            _gtDetailSpins[f.Path] = spin;
            _gtDetailFields.AddChild(FieldRow(f.Label, spin, f.Tip));
        }
    }

    void RefreshGroundPanel()
    {
        if (Tool != EditorTool.Ground) return;
        var rows = GroundRows();
        if (_restoreGround != null) { if (rows.Any(r => r.Name == _restoreGround)) SelectedGroundType = _restoreGround; _restoreGround = null; }
        if (SelectedGroundType != null && rows.All(r => r.Name != SelectedGroundType)) SelectedGroundType = null;
        var sel = rows.FirstOrDefault(r => r.Name == SelectedGroundType);
        var table = Map.GroundTypeTable;

        // empty and error states
        var legacy = table.Any(t => t.Legacy);
        _gtError.Visible = table.Count == 0 || legacy;
        _gtError.Text = table.Count == 0
            ? "This map's parcels.json lists no ground types (it was built before they could be edited). Build it again (make map), then reopen the editor. Types you make now are saved and used by that build."
            : "This map was built before ground types could be edited, so only their colours are known here. Build it again (make map) to see and edit the rest.";
        _gtListHead.Text = $"All ground types ({rows.Count})";

        // the list
        var listSig = string.Join("|", rows.Select(r => RowText(r) + r.Effective.Colour)) + "#" + SelectedGroundType;
        if (listSig != _gtListSig)
        {
            _gtListSig = listSig;
            foreach (var c in _gtList.GetChildren()) { _gtList.RemoveChild(c); c.QueueFree(); }
            _gtRowButtons.Clear();
            if (rows.Count == 0) _gtList.AddChild(new Label { Text = "No ground types yet. Name one below and press Create.", ThemeTypeVariation = "Dim" });
            foreach (var r in rows)
            {
                var name = r.Name;
                var b = new Button
                {
                    Text = RowText(r), Icon = Swatch(r.Effective.Colour), Alignment = HorizontalAlignment.Left, ToggleMode = true, ClipText = true,
                    FocusMode = Control.FocusModeEnum.All, TooltipText = RowTip(r),
                };
                b.SetPressedNoSignal(name == SelectedGroundType);
                b.Pressed += () => { SelectGroundType(name == SelectedGroundType ? null : name); b.ReleaseFocus(); };
                _gtList.AddChild(b);
                _gtRowButtons[name] = b;
            }
        }

        // new type row
        var typed = _gtName.Text.Trim();
        var problem = GroundTypeEdits.NameProblem(typed, rows.Select(r => r.Name));
        _gtNameValid.Text = typed.Length == 0 ? "Names are IDs: lower case letters, digits and _, at most 24." : problem ?? "Name is free.";
        _gtNameValid.ThemeTypeVariation = typed.Length == 0 ? "Dim" : problem != null ? "Danger" : "Dim";
        _gtCreate.Disabled = problem != null;
        _gtDuplicate.Disabled = problem != null || sel == null || sel.Deleted || sel.Effective.Rule != null;
        _gtDuplicate.TooltipText = sel == null ? "Select a type in the list to copy it under the name typed above"
            : sel.Effective.Rule != null ? "The dike is made by a rule and cannot be copied"
            : $"Copy {sel.Name} (colours, recipe, detail settings, flags; not its OSM tags) under the name typed above";

        _gtForm.Visible = sel != null;
        if (sel == null) { _gtOsmSeen = ""; _gtScrolledFor = ""; return; }

        var editable = !sel.Deleted;
        _gtTitle.Text = sel.Name;
        _gtStateNote.Text = sel.State switch
        {
            "overridden" => "Edited by you: your changes are saved in edits.json over rules.yaml. Reset to rules removes them.",
            "custom" => "Your own type, saved in edits.json. Used by hand-drawn areas and by the OSM values you give it.",
            "deleted" => "Deleted by you: gone from the next build and from the Ground list of a region. Reset to rules brings it back.",
            _ => "As in rules.yaml. Any change you make is saved as an override in edits.json.",
        };
        _gtStateNote.ThemeTypeVariation = sel.Deleted ? "Warn" : "Dim";

        var selSig = sel.Name + "|" + sel.State;
        var colSig = selSig + "|" + string.Join(",", sel.Effective.Colours);
        if (colSig != _gtColourSig) { _gtColourSig = colSig; RebuildGroundColours(sel, editable); }
        _gtAddColour.Disabled = !editable || sel.Effective.Colours.Count >= GroundTypeEdits.ColoursMax;
        _gtAddColour.TooltipText = sel.Effective.Colours.Count >= GroundTypeEdits.ColoursMax ? "A type has at most 6 colours" : "Add another terrain shade (a type has 1 to 6; one is picked per area)";

        _gtSync = true;
        var recipeAt = sel.Effective.Recipe == null ? 0 : Array.IndexOf(GroundTypeEdits.Recipes, sel.Effective.Recipe) + 1;
        if (!_gtRecipe.GetPopup().Visible) _gtRecipe.Selected = recipeAt;
        _gtRecipe.Disabled = !editable || sel.Effective.Rule != null;
        _gtRecipe.TooltipText = sel.Effective.Rule != null ? "The recipe of a rule type is fixed (the dike is built from detail.dike)" : "The detail recipe: what grass, clutter and patches the ground gets";
        _gtWater.SetPressedNoSignal(sel.Effective.Water);
        _gtSpawns.SetPressedNoSignal(sel.Effective.Spawns);
        _gtWater.Disabled = _gtSpawns.Disabled = !editable;

        var rule = sel.Effective.Rule != null;
        _gtOsmNote.Visible = rule;
        _gtOsmNote.Text = rule ? $"Made by the rule {sel.Effective.Rule} (the flood dike of detail.dike in rules.yaml). Not editable here." : "";
        _gtOsmBox.Visible = !rule;
        var osmSig = selSig + "|" + string.Join(",", sel.Effective.Landuse) + ";" + string.Join(",", sel.Effective.Natural);
        if (!_gtLanduse.HasFocus() && !_gtNatural.HasFocus() && _gtOsmSeen != osmSig)
        {
            _gtOsmSeen = osmSig;
            _gtLanduse.Text = string.Join(", ", sel.Effective.Landuse);
            _gtNatural.Text = string.Join(", ", sel.Effective.Natural);
            OsmValidate();
        }
        _gtLanduse.Editable = _gtNatural.Editable = editable;

        var detailSig = selSig + "|" + sel.Effective.Recipe;
        if (detailSig != _gtDetailSig) { _gtDetailSig = detailSig; RebuildGroundDetailFields(sel); }
        foreach (var f in GroundTypeEdits.FieldsFor(sel.Effective.Recipe))
            if (_gtDetailSpins.TryGetValue(f.Path, out var spin))
            {
                if (!spin.GetLineEdit().HasFocus()) spin.SetValueNoSignal(EffectiveDetail(sel, f));
                spin.Editable = editable;
            }
        _gtSync = false;
        _gtDetailNote.Text = sel.Effective.Recipe == null ? "This type has no detail recipe, so it has no detail settings."
            : sel.Effective.Recipe is "village" or "dike" ? $"The {sel.Effective.Recipe} recipe has no settings to edit here. Others are in rules.yaml (detail.{sel.Effective.Recipe}) and through the prompt."
            : "Settings laid over the recipe for this type. Others are in rules.yaml and through the prompt.";
        var text = GroundTypeEdits.DetailText(sel.Effective.Detail);
        _gtDetailText.Text = text.Length == 0 ? "No detail overlay: the recipe's own settings apply." : text;

        _gtReset.Disabled = sel.State is not ("overridden" or "deleted");
        _gtReset.TooltipText = !sel.InRules ? "Your own type has no rules to reset to; Delete removes it"
            : _gtReset.Disabled ? "Nothing to reset: this type is as in rules.yaml" : "Remove your changes: the type is as in rules.yaml again (Ctrl+Z undoes)";
        var blocked = DeleteBlocked(sel);
        _gtDelete.Disabled = blocked != null;
        _gtDelete.TooltipText = blocked ?? "Delete this type (asks first; Ctrl+Z undoes)";
        _gtActionNote.Text = blocked != null && !sel.Deleted ? blocked + "." : "";

        if (_gtScrolledFor != sel.Name)
        {
            _gtScrolledFor = sel.Name;
            Callable.From(() => _scroll.EnsureControlVisible(_gtTitle)).CallDeferred();
        }
    }

    // test hooks
    public int GroundRowCount => _gtRowButtons.Count;
    public string GroundPanelTitle => _gtTitle.Text;
    public bool GroundFormShown => _gtForm.Visible;
    public bool GroundDeleteEnabled => !_gtDelete.Disabled;
    public void PressGroundRow(string name) { if (_gtRowButtons.TryGetValue(name, out var b)) b.EmitSignal(BaseButton.SignalName.Pressed); }
}
