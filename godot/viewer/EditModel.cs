using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Obj = System.Collections.Generic.SortedDictionary<string, object?>;

/// <summary>
/// The schema constants of edits.json that the editor needs, kept in this one place. They mirror
/// pipeline/src/drycastle_pipeline/edits.py (FENCE_MODES, FENCE_TYPES, DEFAULT_FENCE_TYPE); the panel smoke
/// compares them with what Python prints, so a drift fails `make viewer-panel-smoke`. The colour of a type is a
/// tint over its texture (picket, wire and concrete are near-white, so the colour is their paint).
/// </summary>
public static class EditSchema
{
    public static readonly string[] FenceModes = { "auto", "none", "custom" };
    public const string DefaultFenceType = "picket";

    public readonly record struct FenceTypeInfo(string Name, double Height, double Thickness, string Colour);

    public static readonly FenceTypeInfo[] FenceTypes =
    {
        new("picket", 1.0, 0.06, "#9a8064"),
        new("wire", 1.1, 0.03, "#5c5f66"),
        new("brick_red", 1.8, 0.3, "#ffffff"),
        new("brick_white", 1.8, 0.3, "#ffffff"),
        new("stone_wall", 1.4, 0.45, "#ffffff"),
        new("concrete", 1.8, 0.15, "#cfcac0"),
        new("brick_wall", 1.8, 0.3, "#ffffff"),  // the old name of brick_red
    };

    /// <summary>Values of the fence `column` key (edits.py COLUMN_TYPES): the posts or piers, instead of the type's own.</summary>
    public static readonly string[] ColumnTypes = { "none", "wood_post", "metal_post", "brick_red", "brick_white", "stone", "concrete" };

    public static IEnumerable<string> FenceTypeNames => FenceTypes.Select(t => t.Name);

    public static FenceTypeInfo TypeInfo(string? name)
        => FenceTypes.First(t => t.Name == (name ?? DefaultFenceType));

    public static readonly Regex ColourRx = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    public const double GateMin = 1.0, GateSnap = 0.5, GateDefault = 3.0, GatePickRadius = 6.0;
}

/// <summary>A fence gap: centre in absolute S-JTSK metres and width in metres.</summary>
public readonly record struct Gap(double X, double Y, double Width);

/// <summary>
/// The editable state of one fence entry (a parcel's or a group's). Null fields are "not set": the pipeline
/// default applies. Custom-only fields are cleared when the mode is auto or none, so what ToEntry writes
/// always passes the Python validator (edits.py `_check_fence`).
/// </summary>
public sealed class FenceDraft
{
    public string? Mode;
    public string? Type;
    /// <summary>The `column` key. The panel has no control for it yet; it is kept as read so an Apply does not drop it.</summary>
    public string? Column;
    public double? Height, Thickness;
    public string? Colour;
    public List<Gap> Gaps = new();
    /// <summary>Remove the fence entry altogether (inherit the group's / use the rules), whatever the mode shows.</summary>
    public bool Remove;

    public bool HasCustomKeys => Type != null || Column != null || Height != null || Thickness != null || Colour != null || Gaps.Count > 0;
    /// <summary>The mode the pipeline would use: the explicit one, else custom when custom keys are present, else auto.</summary>
    public string EffectiveMode => Mode ?? (HasCustomKeys ? "custom" : "auto");

    public FenceDraft Clone() => new()
    {
        Mode = Mode, Type = Type, Column = Column, Height = Height, Thickness = Thickness, Colour = Colour, Gaps = new List<Gap>(Gaps), Remove = Remove,
    };

    public void ClearCustom() { Type = null; Column = null; Height = null; Thickness = null; Colour = null; Gaps.Clear(); }

    /// <summary>Choose the mode; leaving custom drops the custom-only keys.</summary>
    public void SetMode(string mode)
    {
        Mode = mode;
        Remove = false;
        if (mode != "custom") ClearCustom();
    }

    /// <summary>No fence entry at all: the rules decide (or the group's fence applies).</summary>
    public void Reset() { ClearCustom(); Mode = null; Remove = true; }

    public static FenceDraft FromEntry(object? entry)
    {
        var d = new FenceDraft();
        if (entry is not Obj o) return d;
        d.Mode = o.GetValueOrDefault("mode") as string;
        d.Type = o.GetValueOrDefault("type") as string;
        d.Column = o.GetValueOrDefault("column") as string;
        d.Height = Number(o.GetValueOrDefault("height_m"));
        d.Thickness = Number(o.GetValueOrDefault("thickness_m"));
        d.Colour = o.GetValueOrDefault("colour") as string;
        if (o.GetValueOrDefault("gaps") is List<object?> gaps)
            foreach (var g in gaps)
                if (g is Obj go && go.GetValueOrDefault("at") is List<object?> at && at.Count == 2
                    && Number(at[0]) is { } x && Number(at[1]) is { } y && Number(go.GetValueOrDefault("width_m")) is { } w)
                    d.Gaps.Add(new Gap(x, y, w));
        return d;
    }

    internal static double? Number(object? v)
        => v is EditsFile.Num n && double.TryParse(n.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    internal static EditsFile.Num Num(double v) => new(EditsFile.PyFloat(Math.Round(v, 6)));

    /// <summary>
    /// The entry to write, or null when nothing needs to be in the file. `keepAuto`: an explicit auto is kept
    /// (the parcel overrides a group fence); otherwise it is the same as no entry.
    /// </summary>
    public Obj? ToEntry(bool keepAuto)
    {
        if (Remove) return null;
        var mode = EffectiveMode;
        var o = EditsFile.NewObject();
        if (mode == "auto")
        {
            if (!keepAuto) return null;
            o["mode"] = "auto";
            return o;
        }
        o["mode"] = mode;
        if (mode == "none") return o;
        if (Type != null) o["type"] = Type;
        if (Column != null) o["column"] = Column;
        if (Height != null) o["height_m"] = Num(Height.Value);
        if (Thickness != null) o["thickness_m"] = Num(Thickness.Value);
        if (Colour != null) o["colour"] = Colour.ToLowerInvariant();
        if (Gaps.Count > 0)
            o["gaps"] = Gaps.Select(g =>
            {
                var go = EditsFile.NewObject();
                go["at"] = new List<object?> { Num(g.X), Num(g.Y) };
                go["width_m"] = Num(g.Width);
                return (object?)go;
            }).ToList();
        return o;
    }

    /// <summary>The values the pipeline would use for a custom fence: own values over the group's over the type table.</summary>
    public (string type, double height, double thickness, string colour) EffectiveCustom(FenceDraft? group)
    {
        var type = Type ?? group?.Type ?? EditSchema.DefaultFenceType;
        var t = EditSchema.TypeInfo(type);
        return (type, Height ?? group?.Height ?? t.Height, Thickness ?? group?.Thickness ?? t.Thickness, Colour ?? group?.Colour ?? t.Colour);
    }
}

/// <summary>Tree parameters of a parcel: the rules (no entry), a density per 1000 m2 or an exact count, plus the reroll seed.</summary>
public sealed class TreesDraft
{
    public const string Rules = "rules", Density = "density", Count = "count";
    public string Kind = Rules;
    public double DensityValue;
    public int CountValue;
    public int Seed;

    public TreesDraft Clone() => (TreesDraft)MemberwiseClone();

    public static TreesDraft FromEntry(object? entry)
    {
        var d = new TreesDraft();
        if (entry is not Obj o) return d;
        if (FenceDraft.Number(o.GetValueOrDefault("count")) is { } c) { d.Kind = Count; d.CountValue = (int)c; }
        else if (FenceDraft.Number(o.GetValueOrDefault("density_per_1000m2")) is { } v) { d.Kind = Density; d.DensityValue = v; }
        d.Seed = (int)(FenceDraft.Number(o.GetValueOrDefault("seed_offset")) ?? 0);
        return d;
    }

    /// <summary>The entry to write; null means "use the rules" (a seed offset alone is not expressible).</summary>
    public Obj? ToEntry()
    {
        if (Kind == Rules) return null;
        var o = EditsFile.NewObject();
        if (Kind == Density) o["density_per_1000m2"] = FenceDraft.Num(DensityValue);
        else o["count"] = new EditsFile.Num(CountValue.ToString(CultureInfo.InvariantCulture));
        if (Seed != 0) o["seed_offset"] = new EditsFile.Num(Seed.ToString(CultureInfo.InvariantCulture));
        return o;
    }
}

/// <summary>What the panel edits: a property group (its fence; trees per member parcel) or one or more parcels.</summary>
public sealed record EditTarget(string Key, string? GroupId, IReadOnlyList<string> Parcels)
{
    public bool IsGroup => GroupId != null;
    public string Header => GroupId != null ? $"{GroupId} ({Parcels.Count} parcels)" : Parcels.Count == 1 ? Parcels[0] : $"{Parcels.Count} parcels";

    public static EditTarget ForGroup(string gid, IEnumerable<string> members)
        => new(gid, gid, members.OrderBy(x => x, StringComparer.Ordinal).ToList());

    public static EditTarget ForParcels(IEnumerable<string> ids)
    {
        var l = ids.OrderBy(x => x, StringComparer.Ordinal).ToList();
        return new(string.Join("+", l), null, l);
    }
}

/// <summary>Changes made in the panel to one target, not yet written. Null drafts were not touched.</summary>
public sealed class Pending
{
    public EditTarget Target { get; init; } = null!;
    public FenceDraft? Fence;
    public TreesDraft? Trees;
}

/// <summary>Reading drafts from, and applying pending changes to, an edits.json tree (see EditsFile).</summary>
public static class EditModel
{
    static Obj Parcels(Obj root) => (Obj)root["parcels"]!;

    public static Obj? ParcelEntry(Obj root, string pid) => Parcels(root).GetValueOrDefault(pid) as Obj;

    public static FenceDraft FenceFromDisk(Obj root, EditTarget t)
    {
        if (t.IsGroup) return FenceDraft.FromEntry((EditsFile.Groups(root).GetValueOrDefault(t.GroupId!) as Obj)?.GetValueOrDefault("fence"));
        return FenceDraft.FromEntry(ParcelEntry(root, t.Parcels[0])?.GetValueOrDefault("fence"));
    }

    /// <summary>The fence of the group a lone parcel belongs to, for inheritance display; null when none.</summary>
    public static FenceDraft? GroupFenceOf(Obj root, string parcelId)
    {
        var gid = EditsFile.MembershipOf(root).GetValueOrDefault(parcelId);
        if (gid == null) return null;
        return FenceDraft.FromEntry((EditsFile.Groups(root).GetValueOrDefault(gid) as Obj)?.GetValueOrDefault("fence"));
    }

    public static TreesDraft TreesFromDisk(Obj root, EditTarget t)
        => TreesDraft.FromEntry(ParcelEntry(root, t.Parcels[0])?.GetValueOrDefault("trees"));

    /// <summary>True when the targeted entries differ from each other on disk (the panel then shows the first one).</summary>
    public static bool Mixed(Obj root, EditTarget t)
    {
        if (t.IsGroup || t.Parcels.Count < 2) return false;
        string F(string p) => EditsFile.Dump(Wrap(ParcelEntry(root, p)?.GetValueOrDefault("fence"), ParcelEntry(root, p)?.GetValueOrDefault("trees")));
        var first = F(t.Parcels[0]);
        return t.Parcels.Skip(1).Any(p => F(p) != first);
    }

    static Obj Wrap(object? fence, object? trees)
    {
        var o = EditsFile.NewObject();
        o["fence"] = fence; o["trees"] = trees;
        return o;
    }

    /// <summary>
    /// Write the pending changes into a freshly loaded document: set the fence (group entry for a group target, each
    /// parcel otherwise) and the trees (each parcel), drop what became empty, validate what was written.
    /// Returns the errors (empty when fine); the document is then complete and can be saved.
    /// </summary>
    public static List<string> Apply(Obj root, IEnumerable<Pending> items)
    {
        var errors = new List<string>();
        var touchedParcels = new HashSet<string>();
        foreach (var p in items)
        {
            var t = p.Target;
            IReadOnlyList<string> parcels = t.Parcels;
            if (t.IsGroup)
            {
                if (EditsFile.Groups(root).GetValueOrDefault(t.GroupId!) is not Obj g) { errors.Add($"group {t.GroupId} no longer exists"); continue; }
                parcels = EditsFile.Members(g);
                if (p.Fence != null)
                {
                    var e = p.Fence.ToEntry(false);
                    if (e == null) g.Remove("fence"); else g["fence"] = e;
                    foreach (var msg in CheckFence(e, $"groups.{t.GroupId}.fence")) errors.Add(msg);
                }
            }
            else if (p.Fence != null)
            {
                foreach (var pid in parcels)
                {
                    var gf = GroupFenceOf(root, pid);
                    var e = p.Fence.ToEntry(gf != null && gf.ToEntry(false) != null);
                    SetParcelKey(root, pid, "fence", e);
                    foreach (var msg in CheckFence(e, $"parcels.{pid}.fence")) errors.Add(msg);
                }
            }
            if (p.Trees != null)
                foreach (var pid in parcels)
                {
                    var e = p.Trees.ToEntry();
                    SetParcelKey(root, pid, "trees", e);
                    foreach (var msg in CheckTrees(e, $"parcels.{pid}.trees")) errors.Add(msg);
                }
            touchedParcels.UnionWith(parcels);
        }
        foreach (var pid in touchedParcels)
            if (Parcels(root).GetValueOrDefault(pid) is Obj entry && entry.Count == 0) Parcels(root).Remove(pid);
        return errors;
    }

    static void SetParcelKey(Obj root, string pid, string key, Obj? value)
    {
        var parcels = Parcels(root);
        var entry = parcels.GetValueOrDefault(pid) as Obj;
        if (value == null) { entry?.Remove(key); return; }
        if (entry == null) parcels[pid] = entry = EditsFile.NewObject();
        entry[key] = value;
    }

    // ---- the Python validator's rules for the pieces the panel writes (edits.py _check_fence / _check_trees) ----

    static bool Positive(object? v) => FenceDraft.Number(v) is { } d && d > 0;

    public static List<string> CheckFence(Obj? f, string path)
    {
        var errs = new List<string>();
        if (f == null) return errs;
        var mode = f.GetValueOrDefault("mode") as string;
        if (mode != null && !EditSchema.FenceModes.Contains(mode)) errs.Add($"{path}.mode: bad mode");
        string[] custom = { "type", "height_m", "thickness_m", "colour", "gaps" };
        if (mode is "auto" or "none")
            foreach (var k in custom) if (f.ContainsKey(k)) errs.Add($"{path}.{k}: not used with mode '{mode}'");
        if (f.GetValueOrDefault("type") is { } ty && !(ty is string s && EditSchema.FenceTypeNames.Contains(s))) errs.Add($"{path}.type: unknown type");
        foreach (var k in new[] { "height_m", "thickness_m" })
            if (f.ContainsKey(k) && !Positive(f[k])) errs.Add($"{path}.{k}: must be a number > 0");
        if (f.GetValueOrDefault("colour") is { } c && !(c is string cs && EditSchema.ColourRx.IsMatch(cs))) errs.Add($"{path}.colour: must be #rrggbb");
        if (f.GetValueOrDefault("gaps") is List<object?> gaps)
            for (var i = 0; i < gaps.Count; i++)
            {
                if (gaps[i] is not Obj g || !(g.GetValueOrDefault("at") is List<object?> at && at.Count == 2 && at.All(a => a is EditsFile.Num))
                    || !Positive(g.GetValueOrDefault("width_m")))
                    errs.Add($"{path}.gaps.{i}: needs at [x, y] and width_m > 0");
            }
        return errs;
    }

    public static List<string> CheckTrees(Obj? t, string path)
    {
        var errs = new List<string>();
        if (t == null) return errs;
        var hasD = t.ContainsKey("density_per_1000m2"); var hasC = t.ContainsKey("count");
        if (hasD == hasC) errs.Add($"{path}: give exactly one of density_per_1000m2 or count");
        if (hasD && !(FenceDraft.Number(t["density_per_1000m2"]) is { } d && d >= 0)) errs.Add($"{path}.density_per_1000m2: must be >= 0");
        if (hasC && !(t["count"] is EditsFile.Num n && int.TryParse(n.Text, out var c) && c >= 0)) errs.Add($"{path}.count: must be an integer >= 0");
        if (t.ContainsKey("seed_offset") && !(t["seed_offset"] is EditsFile.Num sn && int.TryParse(sn.Text, out _))) errs.Add($"{path}.seed_offset: must be an integer");
        return errs;
    }
}
