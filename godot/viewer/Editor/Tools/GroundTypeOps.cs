using Drycastle.Viewer.Edits;
using System;
using System.Collections.Generic;
using System.Linq;
using GtRow = Drycastle.Viewer.Edits.GroundTypeEdits.Row;
using Obj = System.Collections.Generic.SortedDictionary<string, object?>;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// The edits of the ground types (GLOSSARY "Ground type", ADR 0022): colours, ticked attributes, recipe, OSM tags and detail settings of a type,
/// make, duplicate, reset and delete one. Every change is ONE Mutate = one save = one undo step, stored in edits.json `ground_types` over the
/// entries of rules.yaml. Each method says why in the status bar when it refuses.
/// </summary>
public sealed class GroundTypeOps
{
    readonly IEditorHost _host;
    readonly GroundTool _tool;
    readonly Dictionary<string, Obj?> _recipeDefaults = new();

    public GroundTypeOps(IEditorHost host, GroundTool tool)
    {
        _host = host;
        _tool = tool;
    }

    /// <summary>Change the override of a type: <paramref name="edit"/> works on its entry; what then says the same as rules.yaml is dropped.</summary>
    bool Mutate(string name, string message, Action<Obj> edit)
    {
        if (_tool.Row(name) is not { } row) { _host.Say($"ground type {name} does not exist", true); return false; }
        if (row.Deleted) { _host.Say($"{name} is deleted: Reset to rules brings it back first", true); return false; }
        return _host.Edits.Mutate(root =>
        {
            var types = GroundTypeRows.TypesOf(root);
            var entry = types.GetValueOrDefault(name) as Obj ?? EditsFile.NewObject();
            edit(entry);
            GroundTypeEdits.Prune(entry, row.BaseRaw);
            if (entry.Count == 0 && row.InRules) types.Remove(name); else types[name] = entry;
            GroundTypeRows.Tidy(root);
            return (true, message);
        });
    }

    /// <summary>Change the attributes of a type: <paramref name="edit"/> gets the `attributes` object of its entry and the attributes of its rules entry (null for a type of yours).</summary>
    bool MutateAttrs(string name, string message, Action<Obj, Obj?> edit)
    {
        var rulesAttrs = _tool.Row(name)?.BaseRaw?.GetValueOrDefault("attributes") as Obj;
        return Mutate(name, message, e =>
        {
            var a = e.GetValueOrDefault("attributes") as Obj ?? EditsFile.NewObject();
            edit(a, rulesAttrs);
            if (a.Count == 0) e.Remove("attributes"); else e["attributes"] = a;
        });
    }

    // ---- colours ----

    public bool SetColours(string name, IReadOnlyList<string> colours)
    {
        var parsed = colours.Select(c => EditSchema.ParseColour(c)).ToList();
        if (parsed.Count is < 1 or > GroundTypeEdits.ColoursMax || parsed.Any(c => c == null)) { _host.Say($"a type has 1 to {GroundTypeEdits.ColoursMax} colours, each #rrggbb", true); return false; }
        return MutateAttrs(name, $"{name}: {parsed.Count} colour{(parsed.Count == 1 ? "" : "s")}", (a, _) => a["colours"] = parsed.Cast<object?>().ToList());
    }

    public bool SetColour(string name, int index, string colour)
    {
        if (_tool.Row(name) is not { } r || index < 0 || index >= r.Effective.Colours.Count) return false;
        if (EditSchema.ParseColour(colour) is not { } hex) { _host.Say($"colour must look like #rrggbb, got {colour}", true); return false; }
        if (r.Effective.Colours[index] == hex) return true;
        var list = r.Effective.Colours.ToList();
        list[index] = hex;
        return SetColours(name, list);
    }

    public bool AddColour(string name)
    {
        if (_tool.Row(name) is not { } r) return false;
        if (r.Effective.Colours.Count >= GroundTypeEdits.ColoursMax) { _host.Say($"at most {GroundTypeEdits.ColoursMax} colours", true); return false; }
        return SetColours(name, r.Effective.Colours.Append(r.Effective.Colours.LastOrDefault() ?? GroundTypeEdits.NewTypeColour).ToList());
    }

    public bool RemoveColour(string name, int index)
    {
        if (_tool.Row(name) is not { } r || index < 0 || index >= r.Effective.Colours.Count) return false;
        if (r.Effective.Colours.Count <= 1) { _host.Say("a type keeps at least one colour", true); return false; }
        var list = r.Effective.Colours.ToList();
        list.RemoveAt(index);
        return SetColours(name, list);
    }

    // ---- attributes, recipe, OSM, detail ----

    /// <summary>Tick or untick an attribute of a type: colours, recipe or water. Unticking the recipe takes its detail settings along. Refused where <see cref="GroundTypeRows.UntickBlocked"/> says so.</summary>
    public bool SetCarried(string name, string key, bool carried)
    {
        if (_tool.Row(name) is not { } row) { _host.Say($"ground type {name} does not exist", true); return false; }
        if (key is not ("colours" or "recipe" or "water")) { _host.Say($"{key} is not an attribute that is ticked on its own", true); return false; }
        if (row.Effective.Carries(key) == carried) return true;
        if (key == "water" && PatchesOf(name) is { Count: > 0 } usedBy)
        {
            _host.Say($"{name}: {usedBy.Count} patch{(usedBy.Count == 1 ? "" : "es")} use it ({string.Join(", ", usedBy.Take(4))}); a water type needs a water level on each patch, a land type refuses one. Delete them or give them another type first", true);
            return false;
        }
        if (!carried && GroundTypeRows.UntickBlocked(row, key) is { } why) { _host.Say($"{name}: {why}", true); return false; }
        if (key == "recipe" && row.Effective.Rule != null) { _host.Say($"{name} is made by a rule; its recipe is fixed", true); return false; }
        return MutateAttrs(name, $"{name}: {GroundTypeEdits.Attribute(key)!.Label} {(carried ? "on" : "off")}", (a, rules) =>
        {
            GroundTypeRows.Carry(a, rules, key, carried, () => key switch
            {
                "colours" => new List<object?> { GroundTypeEdits.NewTypeColour },
                "recipe" => "field",
                _ => true,
            });
            if (key == "recipe" && !carried) GroundTypeRows.Carry(a, rules, "detail", false, () => null); // the settings belong to the recipe
        });
    }

    /// <summary>Set the detail recipe (null: no recipe, so no detail). Your own `detail` settings go, they belong to the old recipe.</summary>
    public bool SetRecipe(string name, string? recipe)
    {
        if (recipe == null) return SetCarried(name, "recipe", false);
        if (!GroundTypeEdits.Recipes.Contains(recipe)) { _host.Say($"unknown recipe {recipe}", true); return false; }
        return MutateAttrs(name, $"{name}: recipe {recipe}", (a, rules) =>
        {
            a["recipe"] = recipe;
            GroundTypeRows.Carry(a, rules, "detail", false, () => null); // the rules' own settings belong to the rules' recipe too
        });
    }

    public bool SetOsm(string name, IReadOnlyList<string> landuse, IReadOnlyList<string> natural)
    {
        if (_tool.Row(name) is not { } row) return false;
        if (row.Effective.Rule != null) { _host.Say($"{name} is made by a rule; its source is not editable", true); return false; }
        return Mutate(name, $"{name}: OSM tags", e =>
        {
            var baseOsm = row.BaseRaw?.GetValueOrDefault("osm") as Obj;
            var osm = EditsFile.NewObject();
            foreach (var (cls, list) in new[] { ("landuse", landuse), ("natural", natural) })
                if (!list.SequenceEqual(GroundTypeEdits.Strings(baseOsm?.GetValueOrDefault(cls)))) osm[cls] = list.Cast<object?>().ToList();
            if (osm.Count == 0) e.Remove("osm"); else e["osm"] = osm;
        });
    }

    /// <summary>The shipped value of a detail setting for a recipe (from parcels.json `ground_recipes`), or null.</summary>
    public double? RecipeDefault(string? recipe, string path)
    {
        if (recipe == null || !_host.Map.GroundRecipes.TryGetValue(recipe, out var json)) return null;
        if (!_recipeDefaults.TryGetValue(recipe, out var tree)) _recipeDefaults[recipe] = tree = EditsFile.ParseValue(json) as Obj;
        return ObjectEdits.Number(GroundTypeEdits.GetPath(tree, path));
    }

    public double EffectiveDetail(GtRow r, GroundTypeEdits.DetailField f)
        => ObjectEdits.Number(GroundTypeEdits.GetPath(r.Effective.Detail, f.Path)) ?? RecipeDefault(r.Effective.Recipe, f.Path) ?? f.Min;

    public bool SetDetail(string name, string path, double value)
    {
        if (GroundTypeEdits.DetailFields.FirstOrDefault(f => f.Path == path) is not { } field) { _host.Say($"{path} is not a detail setting the editor exposes", true); return false; }
        if (_tool.Row(name) is not { } row) return false;
        var v = Math.Round(Math.Clamp(value, field.Min, field.Max), 6);
        if (field.Recipes.All(r => r != row.Effective.Recipe)) { _host.Say($"{field.Label} does not apply to the recipe {row.Effective.Recipe ?? "none"}", true); return false; }
        var recipeDefault = RecipeDefault(row.Effective.Recipe, path);
        var rulesSet = GroundTypeEdits.GetPath(GroundTypeEdits.AttributesOf(row.BaseRaw).GetValueOrDefault("detail") as Obj, path) != null;
        return MutateAttrs(name, $"{name}: {field.Label} {v}", (a, _) =>
        {
            if (a.GetValueOrDefault("detail") is not Obj d) a["detail"] = d = EditsFile.NewObject();
            GroundTypeEdits.SetPath(d, path, FenceDraft.Num(v));
            if (!rulesSet && recipeDefault is { } def && Math.Abs(def - v) < 1e-9) GroundTypeEdits.RemovePath(d, path); // the recipe's own value: no override
            if (d.Count == 0) a.Remove("detail");
        });
    }

    // ---- make, reset, delete ----

    /// <summary>
    /// Make a new type: with one green shade and the field recipe (a ground type of its own; a colour no type has is a type of its
    /// own, so change the shade and untick the recipe for plain paint), or with <paramref name="from"/> a copy of
    /// that type's effective attributes (not its OSM tags: the copy is drawn by hand only).
    /// </summary>
    public bool Create(string name, string? from = null)
    {
        var rows = _tool.Rows();
        if (GroundTypeEdits.NameProblem(name, rows.Select(r => r.Name)) is { } problem) { _host.Say($"not created: {problem}", true); return false; }
        GtRow? src = from == null ? null : rows.FirstOrDefault(r => r.Name == from);
        if (from != null && (src == null || src.Deleted || src.Effective.Rule != null)) { _host.Say($"{from} cannot be duplicated", true); return false; }
        var ok = _host.Edits.Mutate(root =>
        {
            var types = GroundTypeRows.TypesOf(root);
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
        if (ok) { _tool.SelectType(name); _tool.Panel.ClearName(); }
        return ok;
    }

    /// <summary>Remove your override of a type of rules.yaml (also brings back a deleted one).</summary>
    public bool Reset(string name)
    {
        if (_tool.Row(name) is not { } r) return false;
        if (!r.InRules) { _host.Say($"{name} is your own type: there are no rules to reset to (Delete removes it)", true); return false; }
        if (r.State == "rules") { _host.Say($"{name} is already as in rules.yaml"); return true; }
        return _host.Edits.Mutate(root =>
        {
            GroundTypeRows.TypesOf(root).Remove(name);
            GroundTypeRows.Tidy(root);
            return (true, $"{name} is as in rules.yaml again (Ctrl+Z undoes)");
        });
    }

    /// <summary>Delete a type: your own is removed, one of rules.yaml is marked deleted (Reset brings it back). Refused for the default type and the dike.</summary>
    public bool Delete(string name)
    {
        if (_tool.Row(name) is not { } r) { _host.Say($"ground type {name} does not exist", true); return false; }
        if (DeleteBlocked(r) is { } why) { _host.Say($"{name}: {why}", true); return false; }
        var ok = _host.Edits.Mutate(root =>
        {
            var types = GroundTypeRows.TypesOf(root);
            if (r.InRules) { var gone = EditsFile.NewObject(); gone["deleted"] = true; types[name] = gone; } else types.Remove(name);
            GroundTypeRows.Tidy(root);
            return (true, $"deleted ground type {name} (Ctrl+Z undoes)");
        });
        if (ok && _tool.SelectedType == name) _tool.SelectType(null);
        return ok;
    }

    /// <summary>Del: ask first, naming what is lost; accepting deletes (undo brings it back).</summary>
    public bool RequestDelete()
    {
        if (_tool.SelectedType is not { } name || _tool.Row(name) is not { } r) { _host.Say("no ground type selected", true); return false; }
        if (DeleteBlocked(r) is { } why) { _host.Say($"{name}: {why}", true); return false; }
        _host.Confirm.Show($"Delete ground type {name}?",
            (r.InRules ? $"{name} is removed from the map at the next build (Reset to rules brings it back)." : $"{name} is removed.")
            + " Undo (Ctrl+Z) brings it back.", "Delete", () => Delete(name));
        return true;
    }

    /// <summary>Why a type cannot be deleted (null: it can): already gone, protected by rules.yaml, or a patch is of it.</summary>
    public string? DeleteBlocked(GtRow r)
    {
        if (r.Deleted) return "Already deleted";
        if (r.InRules && GroundTypeEdits.ProtectedReason(r.Name, r.BaseRaw, _host.Map.GroundDefault) is { } why) return $"Cannot be deleted: {why}";
        var used = PatchesOf(r.Name);
        return used.Count > 0 ? $"Cannot be deleted: {used.Count} patch{(used.Count == 1 ? "" : "es")} use it ({string.Join(", ", used.Take(4))}{(used.Count > 4 ? ", ..." : "")}); delete them or give them another type first" : null;
    }

    List<string> PatchesOf(string type) => _host.Shapes.PatchList().Where(p => p.Type == type).Select(p => p.Id).ToList();
}
