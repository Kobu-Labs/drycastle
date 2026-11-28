using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Obj = System.Collections.Generic.SortedDictionary<string, object?>;

namespace Drycastle.Viewer.Edits;

/// <summary>
/// The schema constants of edits.json that the editor needs, kept in this one place. They mirror
/// pipeline/src/drycastle_pipeline/edits/schema.py (FENCE_MODES, FENCE_TYPES, DEFAULT_FENCE_TYPE); the panel smoke
/// compares them with what Python prints, so a drift fails `make viewer-panel-smoke`. The colour of a type is a
/// tint over its texture (picket, wire and concrete are near-white, so the colour is their paint).
/// </summary>
public static class EditSchema
{
    public static readonly string[] FenceModes = { "auto", "none", "custom" };
    public const string DefaultFenceType = "picket";

    /// <summary>A type's defaults and its panel texture (fencestyles.FENCE_STYLES, assets/drycastle-generated-textures/), for the dropdown icon.</summary>
    public readonly record struct FenceTypeInfo(string Name, double Height, double Thickness, string Colour, string Texture);

    public static readonly FenceTypeInfo[] FenceTypes =
    {
        new("picket", 1.0, 0.06, "#9a8064", "picket"),
        new("wire", 1.1, 0.03, "#5c5f66", "wire"),
        new("brick_red", 1.8, 0.3, "#ffffff", "brick_red"),
        new("brick_white", 1.8, 0.3, "#ffffff", "brick_white"),
        new("stone_wall", 1.4, 0.45, "#ffffff", "stone"),
        new("concrete", 1.8, 0.15, "#cfcac0", "concrete"),
        new("brick_wall", 1.8, 0.3, "#ffffff", "brick_red"),  // the old name of brick_red
    };

    /// <summary>Types whose fence can take the house's wall colour (schema.py HOUSE_COLOUR_TYPES).</summary>
    public static readonly string[] HouseColourTypes = { "brick_red", "brick_white", "concrete", "brick_wall" };

    public static IEnumerable<string> FenceTypeNames => FenceTypes.Select(t => t.Name);

    public static FenceTypeInfo TypeInfo(string? name)
        => FenceTypes.First(t => t.Name == (name ?? DefaultFenceType));

    public static readonly Regex ColourRx = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    /// <summary>"#rrggbb" or "rrggbb" as lower-case "#rrggbb"; null when it is not a colour.</summary>
    public static string? ParseColour(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith('#')) t = "#" + t;
        return ColourRx.IsMatch(t) ? t.ToLowerInvariant() : null;
    }

    public const double GateMin = 1.0, GateSnap = 0.5, GateDefault = 3.0, GatePickRadius = 6.0;
}

/// <summary>A fence gap: centre in absolute S-JTSK metres and width in metres.</summary>
public readonly record struct Gap(double X, double Y, double Width);

/// <summary>
/// The editable state of one fence entry (a parcel's or a group's). Null fields are "not set": the pipeline
/// default applies. Custom-only fields are cleared when the mode is auto or none, so what ToEntry writes
/// always passes the Python validator (edits/schema.py `_check_fence`).
/// </summary>
public sealed class FenceDraft
{
    public string? Mode;
    public string? Type;
    /// <summary>The `column` key. The panel has no control for it yet; it is kept as read so an Apply does not drop it.</summary>
    public string? Column;
    public double? Height, Thickness;
    public string? Colour;
    /// <summary>The `house_colour` key: true = the colour is the house's wall colour (only for HouseColourTypes); null = not set.</summary>
    public bool? HouseColour;
    public List<Gap> Gaps = new();
    /// <summary>The `sides` key: sides of the boundary (between corners) with a look of their own, or no fence.</summary>
    public List<SideDraft> Sides = new();
    /// <summary>Remove the fence entry altogether (inherit the group's / use the rules), whatever the mode shows.</summary>
    public bool Remove;

    public bool HasCustomKeys => Type != null || Column != null || Height != null || Thickness != null || Colour != null || HouseColour != null || Gaps.Count > 0 || Sides.Count > 0;
    /// <summary>The mode the pipeline would use: the explicit one, else custom when custom keys are present, else auto.</summary>
    public string EffectiveMode => Mode ?? (HasCustomKeys ? "custom" : "auto");

    public FenceDraft Clone() => new()
    {
        Mode = Mode, Type = Type, Column = Column, Height = Height, Thickness = Thickness, Colour = Colour, HouseColour = HouseColour, Gaps = new List<Gap>(Gaps), Sides = Sides.Select(s => s.Clone()).ToList(), Remove = Remove,
    };

    public void ClearCustom() { Type = null; Column = null; Height = null; Thickness = null; Colour = null; HouseColour = null; Gaps.Clear(); Sides.Clear(); }

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
        d.HouseColour = o.GetValueOrDefault("house_colour") as bool?;
        if (o.GetValueOrDefault("gaps") is List<object?> gaps)
            foreach (var g in gaps)
                if (g is Obj go && go.GetValueOrDefault("at") is List<object?> at && at.Count == 2
                    && Number(at[0]) is { } x && Number(at[1]) is { } y && Number(go.GetValueOrDefault("width_m")) is { } w)
                    d.Gaps.Add(new Gap(x, y, w));
        if (o.GetValueOrDefault("sides") is List<object?> sides)
            foreach (var s in sides)
                if (SideDraft.FromEntry(s) is { } sd) d.Sides.Add(sd);
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
        if (HouseColour != null) o["house_colour"] = HouseColour.Value;
        if (Gaps.Count > 0)
            o["gaps"] = Gaps.Select(g =>
            {
                var go = EditsFile.NewObject();
                go["at"] = new List<object?> { Num(g.X), Num(g.Y) };
                go["width_m"] = Num(g.Width);
                return (object?)go;
            }).ToList();
        if (Sides.Count > 0) o["sides"] = Sides.Select(s => (object?)s.ToEntry()).ToList();
        return o;
    }

    /// <summary>True when the pipeline colours this fence with its house (own key over the group's; the type must allow it).</summary>
    public bool EffectiveHouseColour(FenceDraft? group)
        => (HouseColour ?? group?.HouseColour ?? false) && EditSchema.HouseColourTypes.Contains(EffectiveCustom(group).type);

    /// <summary>A side's values as the pipeline uses them: the side's own over the fence's over the group's over its type's table.</summary>
    public (string type, double height, double thickness, string colour) EffectiveSide(SideDraft s, FenceDraft? group)
    {
        var type = s.Type ?? Type ?? group?.Type ?? EditSchema.DefaultFenceType;
        var t = EditSchema.TypeInfo(type);
        return (type, s.Height ?? Height ?? group?.Height ?? t.Height, s.Thickness ?? Thickness ?? group?.Thickness ?? t.Thickness, s.Colour ?? Colour ?? group?.Colour ?? t.Colour);
    }

    public bool EffectiveSideHouseColour(SideDraft s, FenceDraft? group)
        => (s.HouseColour ?? HouseColour ?? group?.HouseColour ?? false) && EditSchema.HouseColourTypes.Contains(EffectiveSide(s, group).type);

    /// <summary>The values the pipeline would use for a custom fence: own values over the group's over the type table.</summary>
    public (string type, double height, double thickness, string colour) EffectiveCustom(FenceDraft? group)
    {
        var type = Type ?? group?.Type ?? EditSchema.DefaultFenceType;
        var t = EditSchema.TypeInfo(type);
        return (type, Height ?? group?.Height ?? t.Height, Thickness ?? group?.Thickness ?? t.Thickness, Colour ?? group?.Colour ?? t.Colour);
    }
}

/// <summary>
/// One entry of a fence's `sides`: the side of the boundary nearest to (X, Y) (absolute S-JTSK metres, as gaps) and its
/// own look; null fields take the fence's. Mode "none": no fence on that side (then the look keys are not written).
/// </summary>
public sealed class SideDraft
{
    public double X, Y;
    public string? Mode, Type, Column, Colour;
    public double? Height, Thickness;
    public bool? HouseColour;

    public bool Unfenced => Mode == "none";
    public SideDraft Clone() => (SideDraft)MemberwiseClone();

    public void ClearLook() { Type = null; Column = null; Colour = null; Height = null; Thickness = null; HouseColour = null; }

    public static SideDraft? FromEntry(object? entry)
    {
        if (entry is not Obj o || o.GetValueOrDefault("at") is not List<object?> { Count: 2 } at
            || FenceDraft.Number(at[0]) is not { } x || FenceDraft.Number(at[1]) is not { } y) return null;
        return new SideDraft
        {
            X = x, Y = y,
            Mode = o.GetValueOrDefault("mode") as string,
            Type = o.GetValueOrDefault("type") as string,
            Column = o.GetValueOrDefault("column") as string,
            Colour = o.GetValueOrDefault("colour") as string,
            Height = FenceDraft.Number(o.GetValueOrDefault("height_m")),
            Thickness = FenceDraft.Number(o.GetValueOrDefault("thickness_m")),
            HouseColour = o.GetValueOrDefault("house_colour") as bool?,
        };
    }

    public Obj ToEntry()
    {
        var o = EditsFile.NewObject();
        o["at"] = new List<object?> { FenceDraft.Num(X), FenceDraft.Num(Y) };
        if (Unfenced) { o["mode"] = "none"; return o; }
        if (Type != null) o["type"] = Type;
        if (Column != null) o["column"] = Column;
        if (Height != null) o["height_m"] = FenceDraft.Num(Height.Value);
        if (Thickness != null) o["thickness_m"] = FenceDraft.Num(Thickness.Value);
        if (Colour != null) o["colour"] = Colour.ToLowerInvariant();
        if (HouseColour != null) o["house_colour"] = HouseColour.Value;
        return o;
    }
}

/// <summary>What the panel edits: a property group (its fence) or one or more parcels.</summary>
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

    /// <summary>True when the targeted entries differ from each other on disk (the panel then shows the first one).</summary>
    public static bool Mixed(Obj root, EditTarget t)
    {
        if (t.IsGroup || t.Parcels.Count < 2) return false;
        string F(string p) => EditsFile.Dump(Wrap(ParcelEntry(root, p)?.GetValueOrDefault("fence")));
        var first = F(t.Parcels[0]);
        return t.Parcels.Skip(1).Any(p => F(p) != first);
    }

    static Obj Wrap(object? fence)
    {
        var o = EditsFile.NewObject();
        o["fence"] = fence;
        return o;
    }

    /// <summary>
    /// Write the pending changes into a freshly loaded document: set the fence (group entry for a group target, each
    /// parcel otherwise), drop what became empty, validate what was written.
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

    // ---- the Python validator's rules for the piece the panel writes (edits/schema.py _check_fence) ----

    static bool Positive(object? v) => FenceDraft.Number(v) is { } d && d > 0;

    public static List<string> CheckFence(Obj? f, string path)
    {
        var errs = new List<string>();
        if (f == null) return errs;
        var mode = f.GetValueOrDefault("mode") as string;
        if (mode != null && !EditSchema.FenceModes.Contains(mode)) errs.Add($"{path}.mode: bad mode");
        string[] custom = { "type", "height_m", "thickness_m", "colour", "house_colour", "gaps", "sides" };
        if (mode is "auto" or "none")
            foreach (var k in custom) if (f.ContainsKey(k)) errs.Add($"{path}.{k}: not used with mode '{mode}'");
        if (f.GetValueOrDefault("type") is { } ty && !(ty is string s && EditSchema.FenceTypeNames.Contains(s))) errs.Add($"{path}.type: unknown type");
        foreach (var k in new[] { "height_m", "thickness_m" })
            if (f.ContainsKey(k) && !Positive(f[k])) errs.Add($"{path}.{k}: must be a number > 0");
        if (f.GetValueOrDefault("colour") is { } c && !(c is string cs && EditSchema.ColourRx.IsMatch(cs))) errs.Add($"{path}.colour: must be #rrggbb");
        if (f.ContainsKey("house_colour") && f["house_colour"] is not bool) errs.Add($"{path}.house_colour: must be true or false");
        else if (f.GetValueOrDefault("house_colour") is true && f.GetValueOrDefault("type") is string ht && !EditSchema.HouseColourTypes.Contains(ht))
            errs.Add($"{path}.house_colour: only with type brick_red, brick_white, concrete");
        if (f.GetValueOrDefault("sides") is List<object?> sides)
            for (var i = 0; i < sides.Count; i++)
            {
                if (SideDraft.FromEntry(sides[i]) is not { } sd) { errs.Add($"{path}.sides.{i}: needs at [x, y]"); continue; }
                if (sd.Mode != null && sd.Mode is not ("custom" or "none")) errs.Add($"{path}.sides.{i}.mode: custom or none");
                if (sd.Type != null && !EditSchema.FenceTypeNames.Contains(sd.Type)) errs.Add($"{path}.sides.{i}.type: unknown type");
                if (sd.Colour != null && !EditSchema.ColourRx.IsMatch(sd.Colour)) errs.Add($"{path}.sides.{i}.colour: must be #rrggbb");
                if (sd.HouseColour == true && sd.Type != null && !EditSchema.HouseColourTypes.Contains(sd.Type)) errs.Add($"{path}.sides.{i}.house_colour: only with type brick_red, brick_white, concrete");
            }
        if (f.GetValueOrDefault("gaps") is List<object?> gaps)
            for (var i = 0; i < gaps.Count; i++)
            {
                if (gaps[i] is not Obj g || !(g.GetValueOrDefault("at") is List<object?> at && at.Count == 2 && at.All(a => a is EditsFile.Num))
                    || !Positive(g.GetValueOrDefault("width_m")))
                    errs.Add($"{path}.gaps.{i}: needs at [x, y] and width_m > 0");
            }
        return errs;
    }
}
