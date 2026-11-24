using Drycastle.Viewer.Edits;
using System;
using System.Collections.Generic;
using System.Linq;
using GtRow = Drycastle.Viewer.Edits.GroundTypeEdits.Row;
using Obj = System.Collections.Generic.SortedDictionary<string, object?>;

namespace Drycastle.Viewer.Editor;

/// <summary>What the Ground tool says and writes about a ground type, without touching the editor: the words of a row, and the shape of the `ground_types` entries of edits.json.</summary>
public static class GroundTypeRows
{
    public static string StateWord(string state) => state switch { "overridden" => "edited", "custom" => "yours", "deleted" => "deleted", _ => "" };

    /// <summary>What the type does, in words: water, its detail recipe, or that it only has colours.</summary>
    public static string RecipeWord(GtRow r)
    {
        var s = r.Effective;
        if (s.Water) return "water";
        return s.Recipe ?? "no detail";
    }

    public static string RowText(GtRow r) => $"{r.Name}   {RecipeWord(r)}" + (StateWord(r.State) is { Length: > 0 } w ? $"   [{w}]" : "");

    public static string RowTip(GtRow r) => $"{r.Name}: {RecipeWord(r)}, {r.Effective.Source}" + (r.Effective.Colours.Count > 0 ? $", {r.Effective.Colours.Count} colour{(r.Effective.Colours.Count == 1 ? "" : "s")}" : "")
        + (r.State switch
        {
            "overridden" => ". Edited by you over rules.yaml.",
            "custom" => ". Made by you.",
            "deleted" => ". Deleted by you (Reset brings it back).",
            _ => ". As in rules.yaml.",
        });

    /// <summary>Why a terrain attribute of this type cannot be unticked (null: it can): a type that takes OSM land or is made by a rule needs one.</summary>
    public static string? UntickBlocked(GtRow r, string key)
    {
        var s = r.Effective;
        if (!s.Carries(key)) return null;
        if (s.Rule != null && key == "recipe") return "The recipe of a rule type is fixed (the dike is built from detail.dike)";
        if ((s.HasOsm || s.Rule != null) && s.Carried.Count(GroundTypeEdits.IsTerrainKey) == 1)
            return "A type that takes OSM land needs appearance or water attributes: the land it takes has to look like something";
        return null;
    }

    /// <summary>The `ground_types` object of the edits root (made when missing).</summary>
    public static Obj TypesOf(Obj root)
    {
        if (root.GetValueOrDefault("ground_types") is not Obj t) root["ground_types"] = t = EditsFile.NewObject();
        return t;
    }

    /// <summary>Drop an empty `ground_types` object.</summary>
    public static void Tidy(Obj root)
    {
        if (root.GetValueOrDefault("ground_types") is Obj { Count: 0 }) root.Remove("ground_types");
    }

    /// <summary>Tick or untick an attribute in an entry's attributes: ticking one the rules carry drops the override (back to the rules' value), unticking one the rules carry says null.</summary>
    public static void Carry(Obj attrs, Obj? rulesAttrs, string key, bool carried, Func<object?> value)
    {
        var inRules = rulesAttrs?.ContainsKey(key) == true;
        if (carried) { if (inRules) attrs.Remove(key); else attrs[key] = value(); }
        else if (inRules) attrs[key] = null;
        else attrs.Remove(key);
    }
}
