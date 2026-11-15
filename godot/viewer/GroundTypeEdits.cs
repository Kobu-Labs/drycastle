using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Obj = System.Collections.Generic.SortedDictionary<string, object?>;

/// <summary>
/// Ground types of the user (edits.json `ground_types`; GLOSSARY "Ground type", ADR 0022): the model without any UI. A
/// type is terrain only: its `attributes` (colours, recipe, detail, water; the fixed catalog below, mirror of pipeline
/// `ground_attributes.py`) say how the ground looks; `osm` says which OSM land it takes. What a place means in play is a zone, not a type. The type table is the rules.yaml entries as the last
/// build wrote them (parcels.json `ground_types[].rules`) with the overrides and additions of the edits laid over them, by
/// the same merge rule as pipeline `ground.merge_ground_types`: a key of the edits entry overrides that key (dicts merge
/// key by key, lists and values replace), an attribute set to null is dropped (the type stops carrying it), a new name is a
/// new type after the others, `deleted: true` removes a type of the rules. The validation mirrors pipeline `edits.py`
/// (`_check_ground_type`); what needs rules.yaml (the shipped detail sections) is checked by Python only.
/// </summary>
public static class GroundTypeEdits
{
    public const int NameMax = 24, ColoursMax = 6;
    public static readonly string[] Recipes = { "field", "forest", "village", "dike" };
    public static readonly string[] OsmClasses = { "landuse", "natural" };
    public static readonly string[] Keys = { "attributes", "osm", "deleted" };
    public static readonly Regex NameRx = new("^[a-z][a-z0-9_]{0,23}$", RegexOptions.Compiled);
    public static readonly Regex OsmValueRx = new("^[A-Za-z0-9_:\\-]+$", RegexOptions.Compiled);
    public const string NewTypeColour = "#7aa860";

    // ---- the attribute catalog (mirror of pipeline ground_attributes.py) ----

    public sealed record AttributeGroup(string Key, string Label, string Note);

    public sealed record AttributeInfo(string Key, string Group, string Label, string Tip);

    public static readonly AttributeGroup[] Groups =
    {
        new("appearance", "Appearance", "How the ground looks: its colours and the detail (grass, clutter, patches) it gets."),
        new("water", "Water", "A water type is a body of water: swimming and drowning, no detail. A patch of it carries its water level."),
    };

    public static readonly AttributeInfo[] Attributes =
    {
        new("colours", "appearance", "Terrain colours", "The shades of the terrain (one is picked per area)."),
        new("recipe", "appearance", "Detail recipe", "What grass, clutter and patches the ground gets."),
        new("detail", "appearance", "Detail settings", "Settings laid over the recipe for this type only."),
        new("water", "water", "Water body", "A body of water (water.json): swimming and drowning, no detail. A patch of this type needs a water level."),
    };

    public static AttributeInfo? Attribute(string key) => Attributes.FirstOrDefault(a => a.Key == key);

    public static bool IsTerrainKey(string key) => Attribute(key) != null;

    /// <summary>The attributes object of a type entry (a new empty one when there is none; not stored in the entry).</summary>
    public static Obj AttributesOf(Obj? spec) => spec?.GetValueOrDefault("attributes") as Obj ?? EditsFile.NewObject();

    /// <summary>What a type of the rules entry names in the old flat shape (colours, recipe, ... at the top): the maps built before ADR 0015 wrote it that way.</summary>
    static readonly string[] LegacyKeys = { "colours", "recipe", "detail", "water" };

    /// <summary>A rules entry as an older build wrote it (the attributes at the top of the entry) in the shape of today: they move under `attributes`. An entry of today is returned as it is.</summary>
    public static Obj UpgradeRules(Obj raw)
    {
        if (raw.ContainsKey("attributes") || !LegacyKeys.Any(raw.ContainsKey)) return raw;
        var o = EditsFile.NewObject();
        var attrs = EditsFile.NewObject();
        foreach (var (k, v) in raw)
        {
            if (!LegacyKeys.Contains(k)) o[k] = v;
            else if (v is not (null or false)) attrs[k] = v;
        }
        if (attrs.Count > 0) o["attributes"] = attrs;
        return o;
    }

    /// <summary>
    /// A setting of the recipe's `detail` section that the form shows as a number field: its dotted path, label, range and
    /// the recipes whose section has it (it must exist there or the Python validator refuses the file). The one table of
    /// "common" detail settings: add a row to expose another.
    /// </summary>
    public sealed record DetailField(string Path, string Label, double Min, double Max, double Step, string Tip, string[] Recipes);

    public static readonly DetailField[] DetailFields =
    {
        new("furrow.probability", "Furrow probability", 0, 1, 0.05, "Share of the larger areas that get a strip of furrows (0 = none, 1 = every one).", new[] { "field" }),
        new("headland.keep", "Headland dirt kept", 0, 1, 0.05, "Share of the dirt patches along the edge that are kept (0 = no dirt headland).", new[] { "field" }),
        new("tone_per_ha", "Tone patches per ha", 0, 40, 0.5, "How many patches of a different shade there are per hectare.", new[] { "field", "forest" }),
    };

    // what the editor knows of rules.yaml, set from the map (EditorMode.GroundRows) so the validator can refuse what Python would
    public static IReadOnlyDictionary<string, Obj> RulesTypes { get; set; } = new Dictionary<string, Obj>();
    public static string DefaultType { get; set; } = "";

    /// <summary>Why a type of the rules cannot be deleted (null: it can): the default type, and a type made by a rule (the dike).</summary>
    public static string? ProtectedReason(string name, Obj? rulesEntry, string defaultType)
        => name == defaultType ? "it is the default type (what no other type claims)"
            : rulesEntry != null && rulesEntry.ContainsKey("rule") ? "it is made by a rule (detail.dike needs it)"
            : null;

    // ---- the merge ----

    static Obj MergeDeep(Obj? rules, Obj? edit)
    {
        var o = rules == null ? EditsFile.NewObject() : ObjectEdits.CloneObj(rules);
        if (edit == null) return o;
        foreach (var (k, v) in edit)
            o[k] = v is Obj eo && o.GetValueOrDefault(k) is Obj bo ? MergeDeep(bo, eo) : ObjectEdits.Clone(v);
        return o;
    }

    /// <summary>The rules entry with the edit laid over it; an attribute the edit sets to null is gone, and so is an empty `attributes`.</summary>
    public static Obj Merge(Obj? rules, Obj? edit)
    {
        var o = MergeDeep(rules, edit);
        if (o.GetValueOrDefault("attributes") is Obj a)
        {
            foreach (var k in a.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) a.Remove(k);
            if (a.Count == 0) o.Remove("attributes");
        }
        return o;
    }

    /// <summary>The effective type as plain values (the attributes it carries, OSM tags, rule).</summary>
    public sealed record Spec(IReadOnlyList<string> Colours, string? Recipe, Obj Detail, bool Water, IReadOnlyList<string> Carried,
        IReadOnlyList<string> Landuse, IReadOnlyList<string> Natural, string? Rule)
    {
        /// <summary>The first shade; a type with no colours shows blue for water, else grey.</summary>
        public string Colour => Colours.Count > 0 ? Colours[0] : Water ? "#3a6ea5" : "#808080";
        public bool Carries(string key) => Carried.Contains(key);
        public bool HasOsm => Landuse.Count + Natural.Count > 0;
        public string Source => Rule != null ? $"rule: {Rule}" : HasOsm ? "OSM" : "drawn by hand only";
    }

    public static List<string> Strings(object? v) => v is List<object?> l ? l.OfType<string>().ToList() : new();

    public static Spec ToSpec(Obj o)
    {
        var osm = o.GetValueOrDefault("osm") as Obj;
        var attrs = AttributesOf(o);
        var carried = Attributes.Where(a => attrs.GetValueOrDefault(a.Key) != null).Select(a => a.Key).ToList();
        return new Spec(Strings(attrs.GetValueOrDefault("colours")), attrs.GetValueOrDefault("recipe") as string,
            attrs.GetValueOrDefault("detail") as Obj ?? EditsFile.NewObject(),
            attrs.GetValueOrDefault("water") is true, carried,
            Strings(osm?.GetValueOrDefault("landuse")), Strings(osm?.GetValueOrDefault("natural")), o.GetValueOrDefault("rule") as string);
    }

    /// <summary>One line of the table: <see cref="State"/> is rules (as in rules.yaml), overridden, custom (made by the user) or deleted.</summary>
    public sealed record Row(string Name, string State, Spec Effective, Obj? BaseRaw, Obj? Entry)
    {
        public bool InRules => BaseRaw != null;
        public bool Deleted => State == "deleted";
    }

    public static string State(bool inRules, Obj? entry)
        => !inRules ? "custom" : entry == null ? "rules" : entry.GetValueOrDefault("deleted") is true ? "deleted" : "overridden";

    /// <summary>The rules' types in their order, then the user's own types (by name); a deleted type stays in the table with its rules values.</summary>
    public static List<Row> Table(IEnumerable<(string name, Obj? rules)> baseTypes, Obj edits)
    {
        var rows = new List<Row>();
        var seen = new HashSet<string>();
        foreach (var (name, rules) in baseTypes)
        {
            if (!seen.Add(name)) continue;
            var entry = edits.GetValueOrDefault(name) as Obj;
            var raw = rules ?? EditsFile.NewObject();
            var eff = entry == null || entry.GetValueOrDefault("deleted") is true ? raw : Merge(raw, entry);
            rows.Add(new Row(name, State(true, entry), ToSpec(eff), raw, entry));
        }
        foreach (var (name, e) in edits)
            if (seen.Add(name) && e is Obj entry) rows.Add(new Row(name, "custom", ToSpec(Merge(null, entry)), null, entry));
        return rows;
    }

    // ---- comparing and pruning ----

    public static bool Same(object? a, object? b)
    {
        if (a is Obj oa && b is Obj ob) return oa.Count == ob.Count && oa.All(kv => ob.TryGetValue(kv.Key, out var v) && Same(kv.Value, v));
        if (a is List<object?> la && b is List<object?> lb) return la.Count == lb.Count && la.Zip(lb).All(p => Same(p.First, p.Second));
        if (a is EditsFile.Num && b is EditsFile.Num) return ObjectEdits.Number(a) == ObjectEdits.Number(b);
        return Equals(a, b);
    }

    /// <summary>Drop what an override says that rules.yaml already says (so setting a value back to the rules' removes the override); an entry with no rules entry is left alone.</summary>
    public static void Prune(Obj entry, Obj? rules)
    {
        if (rules == null) return;
        foreach (var k in entry.Keys.ToList())
        {
            if (!rules.TryGetValue(k, out var bv))
            {
                if (entry[k] is null or false) entry.Remove(k); // saying "none" where the rules say nothing
                continue;
            }
            if (entry[k] is Obj eo && bv is Obj bo) { Prune(eo, bo); if (eo.Count == 0) entry.Remove(k); }
            else if (Same(entry[k], bv)) entry.Remove(k);
        }
    }

    // ---- detail paths ----

    public static object? GetPath(Obj? o, string path)
    {
        object? cur = o;
        foreach (var part in path.Split('.')) cur = (cur as Obj)?.GetValueOrDefault(part);
        return cur;
    }

    public static void SetPath(Obj o, string path, object? value)
    {
        var parts = path.Split('.');
        var cur = o;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (cur.GetValueOrDefault(parts[i]) is not Obj next) cur[parts[i]] = next = EditsFile.NewObject();
            cur = next;
        }
        cur[parts[^1]] = value;
    }

    public static void RemovePath(Obj o, string path)
    {
        var parts = path.Split('.');
        var chain = new List<Obj> { o };
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (chain[^1].GetValueOrDefault(parts[i]) is not Obj next) return;
            chain.Add(next);
        }
        chain[^1].Remove(parts[^1]);
        for (var i = chain.Count - 1; i > 0; i--)
            if (chain[i].Count == 0) chain[i - 1].Remove(parts[i - 1]);
    }

    /// <summary>The detail fields of the table that apply to a recipe.</summary>
    public static IEnumerable<DetailField> FieldsFor(string? recipe) => DetailFields.Where(f => recipe != null && f.Recipes.Contains(recipe));

    /// <summary>The effective `detail` overlay as read-only text, one line per setting ("furrow.probability: 0.0").</summary>
    public static string DetailText(Obj detail)
    {
        var lines = new List<string>();
        void Walk(Obj o, string prefix)
        {
            foreach (var (k, v) in o)
            {
                if (v is Obj child) Walk(child, prefix + k + ".");
                else lines.Add($"{prefix}{k}: {Show(v)}");
            }
        }
        Walk(detail, "");
        return string.Join("\n", lines);
    }

    static string Show(object? v) => v switch
    {
        null => "none",
        bool b => b ? "true" : "false",
        EditsFile.Num n => n.Text,
        string s => s,
        List<object?> l => "[" + string.Join(", ", l.Select(Show)) + "]",
        _ => v.ToString() ?? "",
    };

    // ---- validation (mirror of edits.py _check_ground_type) ----

    /// <summary>The attributes a type has once its entry is laid over the rules' (a null removes one): mirror of edits.py `_effective_attributes`.</summary>
    public static Obj EffectiveAttributes(string name, Obj entry)
    {
        var o = ObjectEdits.CloneObj(AttributesOf(RulesTypes.GetValueOrDefault(name)));
        foreach (var (k, v) in AttributesOf(entry))
            if (v == null) o.Remove(k); else o[k] = v;
        return o;
    }

    public static List<string> Validate(Obj types)
    {
        var errs = new List<string>();
        foreach (var (name, entry) in types)
        {
            var path = $"ground_types.{name}";
            if (!NameRx.IsMatch(name)) errs.Add($"{path}: type names are lower case letters, digits and _, starting with a letter, at most {NameMax} long");
            if (entry is not Obj e) { errs.Add($"{path}: must be an object"); continue; }
            if (e.ContainsKey("rule")) errs.Add($"{path}.rule: rule types stay in rules.yaml");
            foreach (var k in e.Keys) if (k != "rule" && !Keys.Contains(k)) errs.Add($"{path}.{k}: unknown key");
            if (e.ContainsKey("deleted"))
            {
                if (e["deleted"] is not true) errs.Add($"{path}.deleted: must be true (remove the entry to bring the type back)");
                if (e.Keys.Count(k => k != "rule") > 1) errs.Add($"{path}: a deleted type has no other keys");
                if (RulesTypes.Count > 0)
                {
                    if (!RulesTypes.TryGetValue(name, out var rt)) errs.Add($"{path}.deleted: {name} is not a type of rules.yaml; remove this entry instead");
                    else if (ProtectedReason(name, rt, DefaultType) is { } why) errs.Add($"{path}.deleted: {name} cannot be deleted, {why}");
                }
                continue;
            }
            var before = errs.Count;
            if (e.TryGetValue("attributes", out var av))
            {
                if (av is not Obj attrs) errs.Add($"{path}.attributes: must be an object");
                else CheckAttributes(name, attrs, $"{path}.attributes", errs);
            }
            var ownSource = false;
            if (e.TryGetValue("osm", out var ov))
            {
                if (ov is not Obj osm) errs.Add($"{path}.osm: must be an object");
                else
                    foreach (var (cls, values) in osm)
                    {
                        if (!OsmClasses.Contains(cls)) errs.Add($"{path}.osm.{cls}: unknown key");
                        else if (!(values is List<object?> vl && vl.All(x => x is string s && s.Trim() != ""))) errs.Add($"{path}.osm.{cls}: must be a list of OSM values (text)");
                        else if (vl.Count > 0) ownSource = true;
                    }
            }
            var rulesSource = RulesTypes.GetValueOrDefault(name) is { } rt2 && (rt2.ContainsKey("osm") || rt2.ContainsKey("rule"));
            if (errs.Count == before && (ownSource || rulesSource) && !EffectiveAttributes(name, e).Keys.Any(IsTerrainKey))
                errs.Add($"{path}.attributes: a type that takes OSM land needs appearance or water attributes (colours, recipe, detail, water)");
        }
        return errs;
    }

    static void CheckAttributes(string name, Obj attrs, string path, List<string> errs)
    {
        var known = Attributes.Select(a => a.Key).ToArray();
        var rulesAttrs = AttributesOf(RulesTypes.GetValueOrDefault(name));
        foreach (var (key, value) in attrs)
        {
            var kpath = $"{path}.{key}";
            if (!known.Contains(key)) { errs.Add($"{kpath}: unknown attribute (the catalog has {string.Join(", ", known)})"); continue; }
            if (value == null)
            {
                if (RulesTypes.Count > 0 && !rulesAttrs.ContainsKey(key)) errs.Add($"{kpath}: null removes an attribute of the type in rules.yaml, and {name} has no {key} there");
                continue;
            }
            switch (key)
            {
                case "colours":
                    if (!(value is List<object?> cl && cl.Count is >= 1 and <= ColoursMax)) errs.Add($"{kpath}: must be a list of 1 to {ColoursMax} colours");
                    else
                        for (var i = 0; i < cl.Count; i++)
                            if (!(cl[i] is string cs && EditSchema.ColourRx.IsMatch(cs))) errs.Add($"{kpath}.{i}: must be a colour like \"#rrggbb\"");
                    break;
                case "recipe":
                    if (!(value is string rs && Recipes.Contains(rs))) errs.Add($"{kpath}: must be one of {string.Join(", ", Recipes)}");
                    break;
                case "water":
                    if (value is not true) errs.Add($"{kpath}: must be true (null removes it)");
                    break;
            }
        }
        if (attrs.TryGetValue("detail", out var dv) && dv != null)
        {
            if (dv is not Obj d) errs.Add($"{path}.detail: must be an object");
            else
            {
                var recipe = attrs.ContainsKey("recipe") ? attrs["recipe"] as string : rulesAttrs.GetValueOrDefault("recipe") as string;
                if (d.Count > 0 && recipe == null && (RulesTypes.Count > 0 || attrs.ContainsKey("recipe")))
                    errs.Add($"{path}.detail: needs a recipe ({string.Join(", ", Recipes)}) to lay the settings over");
            }
        }
    }

    /// <summary>
    /// Whether the type of a ground patch exists and carries water (null: unknown, the editor has not been told the rules' types): mirror of the
    /// patch check in edits.py. The rules' types come from the map (<see cref="RulesTypes"/>), the user's from <paramref name="edits"/> (`ground_types`);
    /// a deleted type does not exist.
    /// </summary>
    public static (bool Exists, bool? Water) PatchType(string name, Obj? edits)
    {
        var entry = edits?.GetValueOrDefault(name) as Obj;
        if (entry?.GetValueOrDefault("deleted") is true) return (false, null);
        var inRules = RulesTypes.ContainsKey(name);
        if (!inRules && entry == null) return RulesTypes.Count == 0 ? (true, null) : (false, null);
        return (true, EffectiveAttributes(name, entry ?? EditsFile.NewObject()).GetValueOrDefault("water") is true);
    }

    /// <summary>Why a new type cannot have this name, or null; <paramref name="taken"/> are the names in use (also deleted ones).</summary>
    public static string? NameProblem(string name, IEnumerable<string> taken)
    {
        if (name.Length == 0) return "type a name: lower case letters, digits and _";
        if (name.Length > NameMax) return $"at most {NameMax} characters ({name.Length} typed)";
        if (!NameRx.IsMatch(name)) return "use lower case letters, digits and _, starting with a letter";
        if (taken.Contains(name)) return $"{name} already exists (a deleted type comes back with Reset)";
        return null;
    }

    /// <summary>Comma-separated OSM values to a list; null with a message when one is not a valid value.</summary>
    public static (List<string>? values, string? problem) ParseOsmList(string text)
    {
        var list = new List<string>();
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!OsmValueRx.IsMatch(part)) return (null, $"\"{part}\" is not an OSM value: letters, digits, _ : and - only");
            if (!list.Contains(part)) list.Add(part);
        }
        return (list, null);
    }
}
