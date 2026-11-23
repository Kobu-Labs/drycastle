using Drycastle.Viewer.Edits;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using GtRow = Drycastle.Viewer.Edits.GroundTypeEdits.Row;
using Obj = System.Collections.Generic.SortedDictionary<string, object?>;

namespace Drycastle.Viewer.Editor;

/// <summary>
/// The types half of the Ground tool (GLOSSARY "Ground type", ADR 0022): list, edit, create, duplicate, reset and delete the ground types
/// of the map. A type is terrain only: its colours, detail recipe and whether it is water (the fixed catalog of
/// <see cref="GroundTypeEdits"/>, Appearance and Water groups); nothing a game system reads, that is a zone. The user's changes are
/// stored in edits.json `ground_types` over the entries of rules.yaml; the types of rules.yaml as the last build wrote them
/// come from parcels.json (`Map.GroundTypeTable`). The tool's other halves are the ground map (EditorMode.GroundMap.cs) and the
/// ground patches (EditorMode.Patches.cs). Selecting a type (list or click on the ground) says which type the next patch is
/// of and shows its form. Every change is ONE Mutate = one save = one undo step. A type change needs a full map build
/// (make map): the banner says so. Esc goes one level up: leave a text field, deselect the patch, deselect the type, leave the
/// tool. Names are IDs, so there is no rename.
/// </summary>
public partial class EditorMode
{
    public string? SelectedGroundType { get; private set; }
    /// <summary>True while the last thing selected was a patch (Del, Esc and the hints then mean the patch), false while it was a type.</summary>
    bool _groundFocusPatch;

    string? _restoreGround;
    Obj? _gtFor;
    object? _gtTableFor;
    List<GtRow> _gtRows = new();
    readonly Dictionary<string, Obj?> _gtRecipeDefaults = new();

    VBoxContainer _gtList = null!, _gtForm = null!, _gtColours = null!, _gtDetailFields = null!, _gtColoursBox = null!, _gtRecipeBox = null!;
    Label _gtError = null!, _gtListHead = null!, _gtTitle = null!, _gtStateNote = null!, _gtNameValid = null!, _gtColourNote = null!,
        _gtOsmNote = null!, _gtOsmValid = null!, _gtDetailText = null!, _gtDetailNote = null!, _gtActionNote = null!, _gtCarryNote = null!;
    LineEdit _gtName = null!, _gtLanduse = null!, _gtNatural = null!;
    Button _gtCreate = null!, _gtDuplicate = null!, _gtAddColour = null!, _gtRename = null!, _gtReset = null!, _gtDelete = null!;
    OptionButton _gtRecipe = null!;
    CheckBox _gtCarryColours = null!, _gtCarryRecipe = null!, _gtWater = null!;
    VBoxContainer _gtOsmBox = null!;
    bool _gtSync, _gtSliderDrag;
    string _gtListSig = "", _gtColourSig = "", _gtDetailSig = "", _gtOsmSeen = "", _gtScrolledFor = "";
    readonly Dictionary<string, Button> _gtRowButtons = new();
    readonly Dictionary<string, SpinBox> _gtDetailSpins = new();

    // ---- the table ----

    /// <summary>The ground types as they stand: rules.yaml's (as of the last build) with the edits on disk laid over them, then the user's own.</summary>
    public IReadOnlyList<GtRow> GroundRows()
    {
        var disk = Edits.Disk();
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
            if (t.RulesJson != null) raw = EditsFile.ParseValue(t.RulesJson) is Obj parsed ? GroundTypeEdits.UpgradeRules(parsed) : null; // a map built before ADR 0015 has them flat
            else
            {
                // built before ground types were editable: the colour only
                raw = EditsFile.NewObject();
                var attrs = EditsFile.NewObject();
                attrs["colours"] = new List<object?> { t.Colour };
                raw["attributes"] = attrs;
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

    /// <summary>The types a patch can be given now (name, colour of its first shade, whether it is water): the table without deleted types and the types a rule makes.</summary>
    public List<(string Name, string Colour, bool Water)> PatchTypeChoices()
        => GroundRows().Where(r => !r.Deleted && r.Effective.Rule == null).Select(r => (r.Name, r.Effective.Colour, r.Effective.Water)).ToList();

    static string StateWord(string state) => state switch { "overridden" => "edited", "custom" => "yours", "deleted" => "deleted", _ => "" };

    /// <summary>What the type does, in words: water, its detail recipe, or that it only has colours.</summary>
    static string RecipeWord(GtRow r)
    {
        var s = r.Effective;
        if (s.Water) return "water";
        return s.Recipe ?? "no detail";
    }

    static string RowText(GtRow r) => $"{r.Name}   {RecipeWord(r)}" + (StateWord(r.State) is { Length: > 0 } w ? $"   [{w}]" : "");

    static string RowTip(GtRow r) => $"{r.Name}: {RecipeWord(r)}, {r.Effective.Source}" + (r.Effective.Colours.Count > 0 ? $", {r.Effective.Colours.Count} colour{(r.Effective.Colours.Count == 1 ? "" : "s")}" : "")
        + (r.State switch
        {
            "overridden" => ". Edited by you over rules.yaml.",
            "custom" => ". Made by you.",
            "deleted" => ". Deleted by you (Reset brings it back).",
            _ => ". As in rules.yaml.",
        });

    string? DeleteBlocked(GtRow r)
    {
        if (r.Deleted) return "Already deleted";
        if (r.InRules && GroundTypeEdits.ProtectedReason(r.Name, r.BaseRaw, Map.GroundDefault) is { } why) return $"Cannot be deleted: {why}";
        var used = PatchList().Where(p => p.Type == r.Name).Select(p => p.Id).ToList();
        return used.Count > 0 ? $"Cannot be deleted: {used.Count} patch{(used.Count == 1 ? "" : "es")} use it ({string.Join(", ", used.Take(4))}{(used.Count > 4 ? ", ..." : "")}); delete them or give them another type first" : null;
    }

    /// <summary>Why a terrain attribute of this type cannot be unticked (null: it can): a type that takes OSM land or is made by a rule needs one.</summary>
    static string? UntickBlocked(GtRow r, string key)
    {
        var s = r.Effective;
        if (!s.Carries(key)) return null;
        if (s.Rule != null && key == "recipe") return "The recipe of a rule type is fixed (the dike is built from detail.dike)";
        if ((s.HasOsm || s.Rule != null) && s.Carried.Count(GroundTypeEdits.IsTerrainKey) == 1)
            return "A type that takes OSM land needs appearance or water attributes: the land it takes has to look like something";
        return null;
    }

    // ---- selection ----

    public bool SelectGroundType(string? name)
    {
        if (name != null && GroundRow(name) == null) { Say($"ground type {name} does not exist", true); return false; }
        if (name != null) _groundFocusPatch = false;
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
        return Edits.Mutate(root =>
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

    /// <summary>Change the attributes of a type: <paramref name="edit"/> gets the `attributes` object of its entry and the attributes of its rules entry (null for a type of yours).</summary>
    bool MutateGroundAttrs(string name, string message, Action<Obj, Obj?> edit)
    {
        var rulesAttrs = GroundRow(name)?.BaseRaw?.GetValueOrDefault("attributes") as Obj;
        return MutateGround(name, message, e =>
        {
            var a = e.GetValueOrDefault("attributes") as Obj ?? EditsFile.NewObject();
            edit(a, rulesAttrs);
            if (a.Count == 0) e.Remove("attributes"); else e["attributes"] = a;
        });
    }

    /// <summary>Tick or untick an attribute in an entry's attributes: ticking one the rules carry drops the override (back to the rules' value), unticking one the rules carry says null.</summary>
    static void Carry(Obj attrs, Obj? rulesAttrs, string key, bool carried, Func<object?> value)
    {
        var inRules = rulesAttrs?.ContainsKey(key) == true;
        if (carried) { if (inRules) attrs.Remove(key); else attrs[key] = value(); }
        else if (inRules) attrs[key] = null;
        else attrs.Remove(key);
    }

    public bool SetGroundColours(string name, IReadOnlyList<string> colours)
    {
        var parsed = colours.Select(c => ParseColour(c)).ToList();
        if (parsed.Count is < 1 or > GroundTypeEdits.ColoursMax || parsed.Any(c => c == null)) { Say($"a type has 1 to {GroundTypeEdits.ColoursMax} colours, each #rrggbb", true); return false; }
        return MutateGroundAttrs(name, $"{name}: {parsed.Count} colour{(parsed.Count == 1 ? "" : "s")}", (a, _) => a["colours"] = parsed.Cast<object?>().ToList());
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

    /// <summary>Tick or untick an attribute of a type: colours, recipe or water. Unticking the recipe takes its detail settings along. Refused where <see cref="UntickBlocked"/> says so.</summary>
    public bool SetGroundCarried(string name, string key, bool carried)
    {
        if (GroundRow(name) is not { } row) { Say($"ground type {name} does not exist", true); return false; }
        if (key is not ("colours" or "recipe" or "water")) { Say($"{key} is not an attribute that is ticked on its own", true); return false; }
        if (row.Effective.Carries(key) == carried) return true;
        if (key == "water" && PatchList().Where(p => p.Type == name).Select(p => p.Id).ToList() is { Count: > 0 } usedBy)
        {
            Say($"{name}: {usedBy.Count} patch{(usedBy.Count == 1 ? "" : "es")} use it ({string.Join(", ", usedBy.Take(4))}); a water type needs a water level on each patch, a land type refuses one. Delete them or give them another type first", true);
            return false;
        }
        if (!carried && UntickBlocked(row, key) is { } why) { Say($"{name}: {why}", true); return false; }
        if (key == "recipe" && row.Effective.Rule != null) { Say($"{name} is made by a rule; its recipe is fixed", true); return false; }
        return MutateGroundAttrs(name, $"{name}: {GroundTypeEdits.Attribute(key)!.Label} {(carried ? "on" : "off")}", (a, rules) =>
        {
            Carry(a, rules, key, carried, () => key switch
            {
                "colours" => new List<object?> { GroundTypeEdits.NewTypeColour },
                "recipe" => "field",
                _ => true,
            });
            if (key == "recipe" && !carried) Carry(a, rules, "detail", false, () => null); // the settings belong to the recipe
        });
    }

    /// <summary>Set the detail recipe (null: no recipe, so no detail). Your own `detail` settings go, they belong to the old recipe.</summary>
    public bool SetGroundRecipe(string name, string? recipe)
    {
        if (recipe == null) return SetGroundCarried(name, "recipe", false);
        if (!GroundTypeEdits.Recipes.Contains(recipe)) { Say($"unknown recipe {recipe}", true); return false; }
        return MutateGroundAttrs(name, $"{name}: recipe {recipe}", (a, rules) =>
        {
            a["recipe"] = recipe;
            Carry(a, rules, "detail", false, () => null); // the rules' own settings belong to the rules' recipe too
        });
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
        var rulesSet = GroundTypeEdits.GetPath(GroundTypeEdits.AttributesOf(row.BaseRaw).GetValueOrDefault("detail") as Obj, path) != null;
        return MutateGroundAttrs(name, $"{name}: {field.Label} {v}", (a, _) =>
        {
            if (a.GetValueOrDefault("detail") is not Obj d) a["detail"] = d = EditsFile.NewObject();
            GroundTypeEdits.SetPath(d, path, FenceDraft.Num(v));
            if (!rulesSet && recipeDefault is { } def && Math.Abs(def - v) < 1e-9) GroundTypeEdits.RemovePath(d, path); // the recipe's own value: no override
            if (d.Count == 0) a.Remove("detail");
        });
    }

    /// <summary>
    /// Make a new type: with one green shade and the field recipe (a ground type of its own; a colour no type has is a type of its
    /// own, so change the shade and untick the recipe for plain paint), or with <paramref name="from"/> a copy of
    /// that type's effective attributes (not its OSM tags: the copy is drawn by hand only).
    /// </summary>
    public bool CreateGroundType(string name, string? from = null)
    {
        var rows = GroundRows();
        if (GroundTypeEdits.NameProblem(name, rows.Select(r => r.Name)) is { } problem) { Say($"not created: {problem}", true); return false; }
        GtRow? src = from == null ? null : rows.FirstOrDefault(r => r.Name == from);
        if (from != null && (src == null || src.Deleted || src.Effective.Rule != null)) { Say($"{from} cannot be duplicated", true); return false; }
        var ok = Edits.Mutate(root =>
        {
            var types = GroundTypesOf(root);
            if (types.ContainsKey(name)) return (false, $"{name} already exists");
            var e = EditsFile.NewObject();
            var a = EditsFile.NewObject();
            if (src != null)
            {
                var s = src.Effective;
                if (s.Carries("colours")) a["colours"] = s.Colours.Cast<object?>().ToList();
                if (s.Recipe != null) a["recipe"] = s.Recipe;
                if (s.Detail.Count > 0) a["detail"] = ObjectEdits.CloneObj(s.Detail);
                if (s.Water) a["water"] = true;
            }
            else
            {
                a["colours"] = new List<object?> { GroundTypeEdits.NewTypeColour };
                a["recipe"] = "field";
            }
            if (a.Count > 0) e["attributes"] = a;
            types[name] = e;
            return (true, src != null ? $"duplicated {from} as {name}" : $"created ground type {name}");
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
        return Edits.Mutate(root =>
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
        var ok = Edits.Mutate(root =>
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
        Confirm.Show($"Delete ground type {name}?",
            (r.InRules ? $"{name} is removed from the map at the next build (Reset to rules brings it back)." : $"{name} is removed.")
            + " Undo (Ctrl+Z) brings it back.", "Delete", () => DeleteGroundType(name));
        return true;
    }

    // ---- keys ----

    /// <summary>Ground tool keys besides the shape keys (not reached while a text field has the focus). True when used.</summary>
    bool HandleGroundKey(InputEventKey k)
    {
        if (Tool != EditorTool.Ground || _cheat.Visible || k.CtrlPressed || k.AltPressed) return false;
        switch (k.Keycode)
        {
            case Key.N when k.ShiftPressed: _gtName.GrabFocus(); return true;
            case Key.M: ToggleGroundMap(); return true;
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

    public string GroundHintText() => ShapeHintText()
        ?? (SelectedGroundType == null
            ? "ground: Tab or click the list, click the ground to pick its type or a patch, N draws a patch of the selected type, M shows or hides the map, Shift+N a new type, Ctrl+P commands, Esc leaves the tool"
            : $"{SelectedGroundType}: N draws a patch of it, Tab moves between the fields, Space ticks an attribute, Enter applies a typed value, Del deletes the type, Ctrl+Z undoes, Esc deselects");

    /// <summary>Esc: one level up (the type; the patch and the drawing went first, the tool is left by the main handler). True when it deselected.</summary>
    bool GroundEscape()
    {
        if (SelectedGroundType != null) { SelectGroundType(null); return true; }
        if (SelectedPatchId != null) { SelectShape(ShapeKind.Patch, null); return true; } // a patch left selected while the type had the focus
        return false;
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
        page.AddChild(new Label { Text = "Ground", ThemeTypeVariation = "Header" });
        page.AddChild(Note("Every point of the map has exactly one ground type: terrain colours, detail and whether it is water, nothing more (what a place means in play is a zone). Draw a patch of a type to change the ground by hand; where patches overlap the higher ID wins."));
        var banner = new PanelContainer { ThemeTypeVariation = "BannerPanel" };
        banner.AddChild(Note("Changes show after the next full map build (make map). The editor saves them at once; Ctrl+Z undoes.", "Warn"));
        page.AddChild(banner);
        _gtError = Note("", "Danger");
        page.AddChild(_gtError);

        BuildGroundMapToggle(page);
        _gtListHead = new Label { Text = "All ground types", ThemeTypeVariation = "Header" };
        page.AddChild(_gtListHead);
        _gtList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        page.AddChild(_gtList);

        page.AddChild(new HSeparator());
        BuildPatchSection(page);
        page.AddChild(new HSeparator());

        page.AddChild(new Label { Text = "New type", ThemeTypeVariation = "Header" });
        _gtName = new LineEdit { PlaceholderText = "my_meadow", MaxLength = GroundTypeEdits.NameMax + 8, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClearButtonEnabled = true,
            TooltipText = "Name of the new type (Shift+N): lower case letters, digits and _, starting with a letter, at most 24. A name is an ID and cannot be changed later." };
        _gtName.TextChanged += _ => _panelDirty = true;
        _gtName.TextSubmitted += t => { if (CreateGroundType(t.Trim())) _gtName.ReleaseFocus(); };
        EscapableText(_gtName);
        page.AddChild(_gtName);
        _gtNameValid = Note("");
        page.AddChild(_gtNameValid);
        var newRow = new HFlowContainer();
        _gtCreate = FocusBtn("Create", () => CreateGroundType(_gtName.Text.Trim()), "PrimaryButton");
        _gtCreate.TooltipText = "Make a new ground type with one green shade and the field recipe (Enter in the name field does the same). Untick the recipe for plain paint";
        _gtDuplicate = FocusBtn("Duplicate selected", () => { if (SelectedGroundType != null) CreateGroundType(_gtName.Text.Trim(), SelectedGroundType); });
        newRow.AddChild(_gtCreate);
        newRow.AddChild(_gtDuplicate);
        page.AddChild(newRow);
        page.AddChild(Note("A new type is for patches: a colour you want that no type has is a type of its own. It takes OSM land only if you give it OSM values, and the types above take theirs first."));

        page.AddChild(new HSeparator());
        _gtForm = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        page.AddChild(_gtForm);
        BuildGroundForm(_gtForm);
    }

    CheckBox CarryBox(string text, string tip, Action<bool> toggled)
    {
        var c = new CheckBox { Text = text, FocusMode = Control.FocusModeEnum.All, TooltipText = tip };
        c.Toggled += on => { if (!_gtSync && SelectedGroundType != null) toggled(on); };
        return c;
    }

    void GroupHeader(VBoxContainer form, string group)
    {
        var g = GroundTypeEdits.Groups.First(x => x.Key == group);
        form.AddChild(new Label { Text = g.Label, ThemeTypeVariation = "Header" });
        form.AddChild(Note(g.Note));
    }

    void BuildGroundForm(VBoxContainer form)
    {
        _gtTitle = new Label { ThemeTypeVariation = "Title", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        form.AddChild(_gtTitle);
        _gtStateNote = Note("");
        form.AddChild(_gtStateNote);
        _gtRename = new Button { Text = "Rename", Disabled = true, FocusMode = Control.FocusModeEnum.None,
            TooltipText = "Names are IDs: patches and edits refer to a type by its name, so it cannot be renamed. Duplicate the type under the new name and delete the old one." };
        form.AddChild(_gtRename);
        _gtCarryNote = Note("");
        form.AddChild(_gtCarryNote);

        // Appearance: colours, recipe (with its detail settings)
        GroupHeader(form, "appearance");
        _gtCarryColours = CarryBox("Terrain colours", GroundTypeEdits.Attribute("colours")!.Tip, on => SetGroundCarried(SelectedGroundType!, "colours", on));
        form.AddChild(_gtCarryColours);
        _gtColoursBox = new VBoxContainer();
        form.AddChild(_gtColoursBox);
        _gtColours = new VBoxContainer();
        _gtColoursBox.AddChild(_gtColours);
        _gtColourNote = Note("");
        _gtColoursBox.AddChild(_gtColourNote);
        _gtAddColour = FocusBtn("Add a colour", () => { if (SelectedGroundType != null) AddGroundColour(SelectedGroundType); });
        _gtAddColour.TooltipText = "Add another terrain shade (a type has 1 to 6; one is picked per area)";
        _gtColoursBox.AddChild(_gtAddColour);

        _gtCarryRecipe = CarryBox("Detail recipe", GroundTypeEdits.Attribute("recipe")!.Tip, on => SetGroundCarried(SelectedGroundType!, "recipe", on));
        form.AddChild(_gtCarryRecipe);
        _gtRecipeBox = new VBoxContainer();
        form.AddChild(_gtRecipeBox);
        _gtRecipe = new OptionButton { FocusMode = Control.FocusModeEnum.All, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var r in GroundTypeEdits.Recipes) _gtRecipe.AddItem(r);
        _gtRecipe.ItemSelected += i =>
        {
            if (!_gtSync && SelectedGroundType != null) SetGroundRecipe(SelectedGroundType, GroundTypeEdits.Recipes[(int)i]);
            _gtRecipe.ReleaseFocus();
        };
        _gtRecipeBox.AddChild(FieldRow("Recipe", _gtRecipe, "The detail recipe: what grass, clutter and patches the ground gets"));
        _gtRecipeBox.AddChild(new Label { Text = "Advanced detail settings", ThemeTypeVariation = "Dim" });
        _gtDetailNote = Note("");
        _gtRecipeBox.AddChild(_gtDetailNote);
        _gtDetailFields = new VBoxContainer();
        _gtRecipeBox.AddChild(_gtDetailFields);
        var box = new PanelContainer { ThemeTypeVariation = "ReadOnlyBox" };
        _gtDetailText = new Label { ThemeTypeVariation = "Dim", AutowrapMode = TextServer.AutowrapMode.WordSmart, TooltipText = "The effective detail settings of this type (rules.yaml with your changes), read only" };
        box.AddChild(_gtDetailText);
        _gtRecipeBox.AddChild(box);

        // Water
        GroupHeader(form, "water");
        _gtWater = CarryBox(GroundTypeEdits.Attribute("water")!.Label, GroundTypeEdits.Attribute("water")!.Tip, on => SetGroundCarried(SelectedGroundType!, "water", on));
        form.AddChild(_gtWater);

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
            remove.TooltipText = n <= 1 ? "A type keeps at least one colour (untick Terrain colours to carry none)" : $"Remove colour {i + 1}";
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
        RefreshPatches();
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
        var listSig = string.Join("|", rows.Select(r => RowText(r) + r.Effective.Colour + GroundAreaText(r.Name))) + "#" + SelectedGroundType;
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
                    Text = RowText(r) + (GroundAreaText(r.Name) is { Length: > 0 } at ? $"   ·  {at}" : ""), Icon = Swatch(r.Effective.Colour), Alignment = HorizontalAlignment.Left, ToggleMode = true, ClipText = true,
                    FocusMode = Control.FocusModeEnum.All, TooltipText = RowTip(r) + (GroundAreaText(r.Name) is { Length: > 0 } at2 ? $" On the map: {at2}." : ""),
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
            : $"Copy {sel.Name} (its attributes; not its OSM tags) under the name typed above";

        _gtForm.Visible = sel != null;
        if (sel == null) { _gtOsmSeen = ""; _gtScrolledFor = ""; return; }

        var editable = !sel.Deleted;
        var s = sel.Effective;
        _gtTitle.Text = sel.Name;
        _gtStateNote.Text = sel.State switch
        {
            "overridden" => "Edited by you: your changes are saved in edits.json over rules.yaml. Reset to rules removes them.",
            "custom" => "Your own type, saved in edits.json. Used by patches and by the OSM values you give it.",
            "deleted" => "Deleted by you: gone from the next build and from the type list of a patch. Reset to rules brings it back.",
            _ => "As in rules.yaml. Any change you make is saved as an override in edits.json.",
        };
        _gtStateNote.ThemeTypeVariation = sel.Deleted ? "Warn" : "Dim";
        _gtCarryNote.Text = s.Carried.Count > 0
            ? "Tick the attributes this type carries. Unticked ones are not carried."
            : "This type carries nothing yet. Tick the attributes it carries (a type with only colours is plain paint).";

        var selSig = sel.Name + "|" + sel.State;
        var colSig = selSig + "|" + string.Join(",", s.Colours);
        if (colSig != _gtColourSig) { _gtColourSig = colSig; RebuildGroundColours(sel, editable); }
        _gtAddColour.Disabled = !editable || s.Colours.Count >= GroundTypeEdits.ColoursMax;
        _gtAddColour.TooltipText = s.Colours.Count >= GroundTypeEdits.ColoursMax ? "A type has at most 6 colours" : "Add another terrain shade (a type has 1 to 6; one is picked per area)";

        _gtSync = true;
        _gtCarryColours.SetPressedNoSignal(s.Carries("colours"));
        _gtCarryRecipe.SetPressedNoSignal(s.Carries("recipe"));
        _gtWater.SetPressedNoSignal(s.Water);
        foreach (var (key, check) in new[] { ("colours", _gtCarryColours), ("recipe", _gtCarryRecipe), ("water", _gtWater) })
        {
            var blocked = UntickBlocked(sel, key);
            check.Disabled = !editable || (blocked != null && check.ButtonPressed);
            check.TooltipText = blocked != null && s.Carries(key) ? blocked : GroundTypeEdits.Attribute(key)!.Tip;
        }
        _gtColoursBox.Visible = s.Carries("colours");
        _gtRecipeBox.Visible = s.Carries("recipe");

        var recipeAt = s.Recipe == null ? -1 : Array.IndexOf(GroundTypeEdits.Recipes, s.Recipe);
        if (!_gtRecipe.GetPopup().Visible) _gtRecipe.Selected = recipeAt;
        _gtRecipe.Disabled = !editable || s.Rule != null;
        _gtRecipe.TooltipText = s.Rule != null ? "The recipe of a rule type is fixed (the dike is built from detail.dike)" : "The detail recipe: what grass, clutter and patches the ground gets";

        var rule = s.Rule != null;
        _gtOsmNote.Visible = rule;
        _gtOsmNote.Text = rule ? $"Made by the rule {s.Rule} (the flood dike of detail.dike in rules.yaml). Not editable here." : "";
        _gtOsmBox.Visible = !rule;
        var osmSig = selSig + "|" + string.Join(",", s.Landuse) + ";" + string.Join(",", s.Natural);
        if (!_gtLanduse.HasFocus() && !_gtNatural.HasFocus() && _gtOsmSeen != osmSig)
        {
            _gtOsmSeen = osmSig;
            _gtLanduse.Text = string.Join(", ", s.Landuse);
            _gtNatural.Text = string.Join(", ", s.Natural);
            OsmValidate();
        }
        _gtLanduse.Editable = _gtNatural.Editable = editable;

        var detailSig = selSig + "|" + s.Recipe;
        if (detailSig != _gtDetailSig) { _gtDetailSig = detailSig; RebuildGroundDetailFields(sel); }
        foreach (var f in GroundTypeEdits.FieldsFor(s.Recipe))
            if (_gtDetailSpins.TryGetValue(f.Path, out var spin))
            {
                if (!spin.GetLineEdit().HasFocus()) spin.SetValueNoSignal(EffectiveDetail(sel, f));
                spin.Editable = editable;
            }
        _gtSync = false;
        _gtDetailNote.Text = s.Recipe == null ? "This type has no detail recipe, so it has no detail settings."
            : s.Recipe is "village" or "dike" ? $"The {s.Recipe} recipe has no settings to edit here. Others are in rules.yaml (detail.{s.Recipe}) and through the prompt."
            : "Settings laid over the recipe for this type. Others are in rules.yaml and through the prompt.";
        var text = GroundTypeEdits.DetailText(s.Detail);
        _gtDetailText.Text = text.Length == 0 ? "No detail overlay: the recipe's own settings apply." : text;

        _gtReset.Disabled = sel.State is not ("overridden" or "deleted");
        _gtReset.TooltipText = !sel.InRules ? "Your own type has no rules to reset to; Delete removes it"
            : _gtReset.Disabled ? "Nothing to reset: this type is as in rules.yaml" : "Remove your changes: the type is as in rules.yaml again (Ctrl+Z undoes)";
        var blockedDelete = DeleteBlocked(sel);
        _gtDelete.Disabled = blockedDelete != null;
        _gtDelete.TooltipText = blockedDelete ?? "Delete this type (asks first; Ctrl+Z undoes)";
        _gtActionNote.Text = blockedDelete != null && !sel.Deleted ? blockedDelete + "." : "";

        if (_gtScrolledFor != sel.Name)
        {
            _gtScrolledFor = sel.Name;
            if (!_groundFocusPatch) Callable.From(() => _scroll.EnsureControlVisible(_gtTitle)).CallDeferred();
        }
    }

    // test hooks
    public int GroundRowCount => _gtRowButtons.Count;
    public string GroundPanelTitle => _gtTitle.Text;
    public bool GroundFormShown => _gtForm.Visible;
    public bool GroundDeleteEnabled => !_gtDelete.Disabled;
    public void PressGroundRow(string name) { if (_gtRowButtons.TryGetValue(name, out var b)) b.EmitSignal(BaseButton.SignalName.Pressed); }
}
