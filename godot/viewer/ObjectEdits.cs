using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Obj = System.Collections.Generic.SortedDictionary<string, object?>;

/// <summary>
/// Placed objects in an edits.json tree (see EditsFile): ids, entries, adding, removing, and a C# mirror of the
/// Python validator (edits.py `validate`) over the whole document, so the editor never writes a file the pipeline
/// would reject. Entries are written as `id`, `asset`, `position` [x, y] in absolute S-JTSK metres (3 decimals),
/// `rotation_deg` (always), and `scale` / `colour` only when they differ from the defaults (1 / none); `z` is
/// never written (the terrain height is used).
/// </summary>
public static class ObjectEdits
{
    public const string IdPrefix = "o-";
    static readonly Regex IdRx = new("^o-(\\d+)$", RegexOptions.Compiled);
    static readonly Regex AssetRx = new("^(pack:[^/\\s]+/\\S+|prim:[A-Za-z0-9_.-]+)$", RegexOptions.Compiled);
    static readonly Regex ParcelRx = new("^P-\\d{4,}$", RegexOptions.Compiled);
    static readonly Regex GroupRx = new("^G-\\d{4,}$", RegexOptions.Compiled);

    public static Obj Parcels(Obj root) => (Obj)root["parcels"]!;

    static EditsFile.Num N3(double v) => new(EditsFile.PyFloat(Math.Round(v, 3, MidpointRounding.AwayFromZero)));

    public static double? Number(object? v) => FenceDraft.Number(v);

    /// <summary>Every object in the document with the parcel entry that lists it and its index there.</summary>
    public static IEnumerable<(string pid, int index, Obj obj)> All(Obj root)
    {
        foreach (var (pid, entry) in Parcels(root))
            if (entry is Obj e && e.GetValueOrDefault("objects") is List<object?> list)
                for (var i = 0; i < list.Count; i++)
                    if (list[i] is Obj o) yield return (pid, i, o);
    }

    public static (string pid, int index, Obj obj)? Find(Obj root, string id)
    {
        foreach (var t in All(root)) if (t.obj.GetValueOrDefault("id") as string == id) return t;
        return null;
    }

    /// <summary>"o-NNNN" one above the highest number in use (files may also hold ids of another form, those are ignored).</summary>
    public static string NextId(Obj root)
    {
        var max = 0;
        foreach (var (_, _, o) in All(root))
            if (o.GetValueOrDefault("id") is string s && IdRx.Match(s) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n))
                max = Math.Max(max, n);
        return $"{IdPrefix}{max + 1:0000}";
    }

    public static Obj Make(string id, string asset, double x, double y, double rotationDeg, double scale = 1.0, string? colour = null)
    {
        var o = EditsFile.NewObject();
        o["id"] = id;
        o["asset"] = asset;
        o["position"] = new List<object?> { N3(x), N3(y) };
        o["rotation_deg"] = new EditsFile.Num(EditsFile.PyFloat(Math.Round(rotationDeg, 3)));
        if (Math.Abs(scale - 1.0) > 1e-12) o["scale"] = new EditsFile.Num(EditsFile.PyFloat(Math.Round(scale, 6)));
        if (colour != null) o["colour"] = colour.ToLowerInvariant();
        return o;
    }

    public static void Add(Obj root, string pid, Obj o)
    {
        var parcels = Parcels(root);
        if (parcels.GetValueOrDefault(pid) is not Obj entry) parcels[pid] = entry = EditsFile.NewObject();
        if (entry.GetValueOrDefault("objects") is not List<object?> list) entry["objects"] = list = new List<object?>();
        list.Add(o);
    }

    /// <summary>Remove the object; an emptied objects list and an emptied parcel entry go away with it.</summary>
    public static bool Remove(Obj root, string id)
    {
        if (Find(root, id) is not { } f) return false;
        var entry = (Obj)Parcels(root)[f.pid]!;
        var list = (List<object?>)entry["objects"]!;
        list.RemoveAt(f.index);
        if (list.Count == 0) entry.Remove("objects");
        if (entry.Count == 0) Parcels(root).Remove(f.pid);
        return true;
    }

    public static void SetPosition(Obj o, double x, double y) => o["position"] = new List<object?> { N3(x), N3(y) };
    public static void SetRotation(Obj o, double deg) => o["rotation_deg"] = new EditsFile.Num(EditsFile.PyFloat(Math.Round(deg, 3)));

    public static void SetScale(Obj o, double s)
    {
        if (Math.Abs(s - 1.0) < 1e-12) o.Remove("scale");
        else o["scale"] = new EditsFile.Num(EditsFile.PyFloat(Math.Round(s, 6)));
    }

    public static void SetColour(Obj o, string? colour)
    {
        if (colour == null) o.Remove("colour"); else o["colour"] = colour.ToLowerInvariant();
    }

    public static (double x, double y) Position(Obj o)
        => o.GetValueOrDefault("position") is List<object?> p && p.Count == 2 ? (Number(p[0]) ?? 0, Number(p[1]) ?? 0) : (0, 0);

    public static double Rotation(Obj o) => Number(o.GetValueOrDefault("rotation_deg")) ?? 0;
    public static double Scale(Obj o) => Number(o.GetValueOrDefault("scale")) ?? 1;

    /// <summary>Deep copy of a tree made of SortedDictionary / List / string / bool / Num / null.</summary>
    public static object? Clone(object? v) => v switch
    {
        Obj o => CloneObj(o),
        List<object?> l => l.Select(Clone).ToList(),
        _ => v,
    };

    public static Obj CloneObj(Obj o)
    {
        var c = EditsFile.NewObject();
        foreach (var (k, v) in o) c[k] = Clone(v);
        return c;
    }

    // ---- the validator ----

    static bool IsNum(object? v) => v is EditsFile.Num n && double.TryParse(n.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d);

    public static List<string> CheckObjects(object? v, string path, Dictionary<string, string> seen)
    {
        var errs = new List<string>();
        if (v is not List<object?> list) { errs.Add($"{path}: must be a list"); return errs; }
        string[] allowed = { "id", "asset", "position", "z", "rotation_deg", "scale", "colour" };
        for (var i = 0; i < list.Count; i++)
        {
            var p = $"{path}.{i}";
            if (list[i] is not Obj o) { errs.Add($"{p}: must be an object"); continue; }
            foreach (var k in o.Keys) if (!allowed.Contains(k)) errs.Add($"{p}.{k}: unknown key");
            foreach (var k in new[] { "id", "asset", "position" }) if (!o.ContainsKey(k)) errs.Add($"{p}.{k}: required");
            if (o.TryGetValue("id", out var id))
            {
                if (id is not string s || s == "") errs.Add($"{p}.id: must be a non-empty string");
                else if (seen.TryGetValue(s, out var first)) errs.Add($"{p}.id: duplicate object id '{s}' (first at {first})");
                else seen[s] = p;
            }
            if (o.TryGetValue("asset", out var asset) && !(asset is string a && AssetRx.IsMatch(a))) errs.Add($"{p}.asset: must be pack:<pack>/<model> or prim:<name>");
            if (o.TryGetValue("position", out var pos) && !(pos is List<object?> pl && pl.Count == 2 && pl.All(IsNum))) errs.Add($"{p}.position: must be [x, y]");
            if (o.TryGetValue("z", out var z) && !IsNum(z)) errs.Add($"{p}.z: must be a number");
            if (o.TryGetValue("rotation_deg", out var r) && !IsNum(r)) errs.Add($"{p}.rotation_deg: must be a number");
            if (o.TryGetValue("scale", out var sc) && !(IsNum(sc) && Number(sc) > 0)) errs.Add($"{p}.scale: must be a number > 0");
            if (o.TryGetValue("colour", out var c) && !(c is string cs && EditSchema.ColourRx.IsMatch(cs))) errs.Add($"{p}.colour: must be #rrggbb");
        }
        return errs;
    }

    /// <summary>Every problem the Python validator would report (same rules, C# messages); empty means valid.</summary>
    public static List<string> ValidateDocument(Obj root)
    {
        var errs = new List<string>();
        if (!(root.GetValueOrDefault("version") is EditsFile.Num v && v.Text == "1")) errs.Add("version: must be 1");
        foreach (var k in root.Keys) if (k is not ("version" or "parcels" or "groups")) errs.Add($"{k}: unknown key");
        var seen = new Dictionary<string, string>();
        foreach (var (pid, entry) in Parcels(root))
        {
            var path = $"parcels.{pid}";
            if (!ParcelRx.IsMatch(pid)) errs.Add($"{path}: parcel IDs look like P-0412");
            if (entry is not Obj e) { errs.Add($"{path}: must be an object"); continue; }
            foreach (var k in e.Keys) if (k is not ("fence" or "trees" or "objects")) errs.Add($"{path}.{k}: unknown key");
            if (e.TryGetValue("fence", out var f)) errs.AddRange(f is Obj fo ? EditModel.CheckFence(fo, $"{path}.fence") : new List<string> { $"{path}.fence: must be an object" });
            if (e.TryGetValue("trees", out var t)) errs.AddRange(t is Obj to ? EditModel.CheckTrees(to, $"{path}.trees") : new List<string> { $"{path}.trees: must be an object" });
            if (e.TryGetValue("objects", out var ob)) errs.AddRange(CheckObjects(ob, $"{path}.objects", seen));
        }
        var owner = new Dictionary<string, string>();
        foreach (var (gid, entry) in EditsFile.Groups(root))
        {
            var path = $"groups.{gid}";
            if (!GroupRx.IsMatch(gid)) errs.Add($"{path}: group IDs look like G-0001");
            if (entry is not Obj g) { errs.Add($"{path}: must be an object"); continue; }
            foreach (var k in g.Keys) if (k is not ("parcels" or "fence")) errs.Add($"{path}.{k}: unknown key");
            if (g.GetValueOrDefault("parcels") is not List<object?> members || members.Count == 0) errs.Add($"{path}.parcels: must be a non-empty list of parcel IDs");
            else
                foreach (var m in members)
                {
                    if (!(m is string pid && ParcelRx.IsMatch(pid))) errs.Add($"{path}.parcels: must be a parcel ID like P-0412");
                    else if (owner.TryGetValue(pid, out var og)) errs.Add($"{path}.parcels: parcel {pid} is already in group {og}");
                    else owner[pid] = gid;
                }
            if (g.TryGetValue("fence", out var gf)) errs.AddRange(gf is Obj gfo ? EditModel.CheckFence(gfo, $"{path}.fence") : new List<string> { $"{path}.fence: must be an object" });
        }
        return errs;
    }
}
