using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;

/// <summary>
/// Reader, group operations and canonical writer for edits.json. The text written is byte-identical to
/// pipeline `edits.dump_text` for the same data: keys sorted, indent 1, ", " free separators as json.dumps
/// does with an indent (",\n" between items, ": " after keys), non-ASCII kept, Python's float repr,
/// trailing newline. Everything in the file (unknown parcels, fences, trees, objects) is kept as read.
/// </summary>
public static class EditsFile
{
    /// <summary>A JSON number held as the canonical text Python would print for it.</summary>
    public readonly record struct Num(string Text);

    public const string EmptyText = "{\n \"groups\": {},\n \"parcels\": {},\n \"version\": 1\n}\n";

    // ---- tree ----

    public static SortedDictionary<string, object?> NewObject() => new(StringComparer.Ordinal);

    /// <summary>Parse an edits document into a tree of SortedDictionary / List / string / bool / Num / null.</summary>
    public static SortedDictionary<string, object?> Parse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("top level is not an object");
        var root = (SortedDictionary<string, object?>)Convert(doc.RootElement)!;
        Normalise(root);
        return root;
    }

    /// <summary>Make sure version, parcels and groups exist, as dump_text always writes them.</summary>
    public static void Normalise(SortedDictionary<string, object?> root)
    {
        if (!root.ContainsKey("version")) root["version"] = new Num("1");
        if (root.GetValueOrDefault("parcels") is not SortedDictionary<string, object?>) root["parcels"] = NewObject();
        if (root.GetValueOrDefault("groups") is not SortedDictionary<string, object?>) root["groups"] = NewObject();
    }

    static object? Convert(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var o = NewObject();
                foreach (var p in e.EnumerateObject()) o[p.Name] = Convert(p.Value);
                return o;
            case JsonValueKind.Array:
                return e.EnumerateArray().Select(Convert).ToList();
            case JsonValueKind.String: return e.GetString();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Number: return new Num(PyNumber(e.GetRawText()));
            default: return null;
        }
    }

    // ---- canonical writer ----

    public static string Dump(SortedDictionary<string, object?> root)
    {
        var sb = new StringBuilder();
        Write(sb, root, 0);
        sb.Append('\n');
        return sb.ToString();
    }

    static void Indent(StringBuilder sb, int depth) => sb.Append(' ', depth);

    static void Write(StringBuilder sb, object? v, int depth)
    {
        switch (v)
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case Num n: sb.Append(n.Text); break;
            case string s: WriteString(sb, s); break;
            case SortedDictionary<string, object?> o:
                if (o.Count == 0) { sb.Append("{}"); break; }
                sb.Append("{\n");
                var first = true;
                foreach (var kv in o)
                {
                    // like dump_text: the regions key is only written when there are regions
                    if (depth == 0 && kv.Key == "regions" && kv.Value is SortedDictionary<string, object?> { Count: 0 }) continue;
                    if (!first) sb.Append(",\n");
                    first = false;
                    Indent(sb, depth + 1);
                    WriteString(sb, kv.Key);
                    sb.Append(": ");
                    Write(sb, kv.Value, depth + 1);
                }
                sb.Append('\n');
                Indent(sb, depth);
                sb.Append('}');
                break;
            case List<object?> l:
                if (l.Count == 0) { sb.Append("[]"); break; }
                sb.Append("[\n");
                for (var i = 0; i < l.Count; i++)
                {
                    if (i > 0) sb.Append(",\n");
                    Indent(sb, depth + 1);
                    Write(sb, l[i], depth + 1);
                }
                sb.Append('\n');
                Indent(sb, depth);
                sb.Append(']');
                break;
            default: throw new InvalidOperationException($"cannot write {v.GetType()}");
        }
    }

    // json.dumps(ensure_ascii=False): escape quote, backslash and control characters only.
    static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>The text Python's json.dumps prints for a number read by json.loads (int or float repr).</summary>
    public static string PyNumber(string raw)
    {
        if (raw.IndexOfAny(['.', 'e', 'E']) < 0) return BigInteger.Parse(raw, System.Globalization.CultureInfo.InvariantCulture).ToString();
        var d = double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
        return PyFloat(d);
    }

    /// <summary>Python repr(float): shortest round-trip digits; exponent form below 1e-4 and from 1e16.</summary>
    public static string PyFloat(double d)
    {
        if (d == 0) return double.IsNegative(d) ? "-0.0" : "0.0";
        var r = Math.Abs(d).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var exp = 0;
        var ei = r.IndexOfAny(['E', 'e']);
        if (ei >= 0) { exp = int.Parse(r[(ei + 1)..], System.Globalization.CultureInfo.InvariantCulture); r = r[..ei]; }
        var dot = r.IndexOf('.');
        var intPart = dot < 0 ? r : r[..dot];
        var frac = dot < 0 ? "" : r[(dot + 1)..];
        var digits = intPart + frac;
        var pt = intPart.Length + exp; // value = 0.DIGITS x 10^pt
        var lead = digits.Length - digits.TrimStart('0').Length;
        digits = digits[lead..];
        pt -= lead;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) digits = "0";
        string body;
        if (pt <= -4 || pt > 16)
        {
            var e = pt - 1;
            body = digits[..1] + (digits.Length > 1 ? "." + digits[1..] : "")
                   + "e" + (e < 0 ? "-" : "+") + Math.Abs(e).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        }
        else if (pt <= 0) body = "0." + new string('0', -pt) + digits;
        else if (pt >= digits.Length) body = digits + new string('0', pt - digits.Length) + ".0";
        else body = digits[..pt] + "." + digits[pt..];
        return (d < 0 ? "-" : "") + body;
    }

    // ---- file access ----

    /// <summary>Load the file (missing = empty document). Throws on invalid JSON.</summary>
    public static SortedDictionary<string, object?> Load(string path)
    {
        var text = File.Exists(path) ? File.ReadAllText(path) : EmptyText;
        return Parse(text);
    }

    /// <summary>Write atomically: temp file in the same folder, then rename over the target.</summary>
    public static void Save(string path, SortedDictionary<string, object?> root)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, Path.GetFileName(path) + $".{Environment.ProcessId}.tmp");
        File.WriteAllText(tmp, Dump(root), new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    // ---- groups ----

    public static SortedDictionary<string, object?> Groups(SortedDictionary<string, object?> root)
        => (SortedDictionary<string, object?>)root["groups"]!;

    public static List<string> Members(object? group)
        => group is SortedDictionary<string, object?> g && g.GetValueOrDefault("parcels") is List<object?> l
            ? l.OfType<string>().ToList() : new();

    /// <summary>Parcel id to group id for every member listed in the document.</summary>
    public static Dictionary<string, string> MembershipOf(SortedDictionary<string, object?> root)
    {
        var map = new Dictionary<string, string>();
        foreach (var (gid, g) in Groups(root))
            foreach (var pid in Members(g)) map.TryAdd(pid, gid);
        return map;
    }

    static void SetMembers(SortedDictionary<string, object?> group, IEnumerable<string> ids)
        => group["parcels"] = ids.Distinct().OrderBy(x => x, StringComparer.Ordinal).Cast<object?>().ToList();

    /// <summary>Next free group id: one above the highest G-NNNN in use.</summary>
    public static string NextGroupId(SortedDictionary<string, object?> root)
    {
        var max = 0;
        foreach (var gid in Groups(root).Keys)
            if (gid.StartsWith("G-") && int.TryParse(gid[2..], out var n)) max = Math.Max(max, n);
        return $"G-{max + 1:0000}";
    }

    /// <summary>A new group with the given parcels. Fails when any is already in a group.</summary>
    public static (bool ok, string message, string? groupId) CreateGroup(SortedDictionary<string, object?> root, IReadOnlyCollection<string> parcels)
    {
        if (parcels.Count < 2) return (false, "select 2 or more parcels to group", null);
        var member = MembershipOf(root);
        var taken = parcels.Where(member.ContainsKey).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (taken.Count > 0)
            return (false, $"{string.Join(", ", taken)} already in a group ({member[taken[0]]})", null);
        var gid = NextGroupId(root);
        var g = NewObject();
        SetMembers(g, parcels);
        Groups(root)[gid] = g;
        return (true, $"created {gid}", gid);
    }

    /// <summary>Add parcels (none of which may be in another group) to an existing group.</summary>
    public static (bool ok, string message) AddToGroup(SortedDictionary<string, object?> root, string groupId, IReadOnlyCollection<string> parcels)
    {
        if (!Groups(root).TryGetValue(groupId, out var g) || g is not SortedDictionary<string, object?> go)
            return (false, $"group {groupId} no longer exists");
        var member = MembershipOf(root);
        var other = parcels.Where(p => member.TryGetValue(p, out var m) && m != groupId).ToList();
        if (other.Count > 0) return (false, $"{other[0]} is already in {member[other[0]]}");
        SetMembers(go, Members(g).Concat(parcels));
        return (true, $"added to {groupId}");
    }

    /// <summary>Remove parcels from whatever group holds them; a group left empty is deleted.</summary>
    public static (bool ok, string message) RemoveFromGroups(SortedDictionary<string, object?> root, IReadOnlyCollection<string> parcels)
    {
        var changed = false;
        foreach (var gid in Groups(root).Keys.ToList())
        {
            var g = Groups(root)[gid];
            var members = Members(g);
            var kept = members.Where(m => !parcels.Contains(m)).ToList();
            if (kept.Count == members.Count) continue;
            changed = true;
            if (kept.Count == 0) Groups(root).Remove(gid);
            else SetMembers((SortedDictionary<string, object?>)g!, kept);
        }
        return changed ? (true, "removed from group") : (false, "none of the selected parcels is in a group");
    }

    public static (bool ok, string message) Dissolve(SortedDictionary<string, object?> root, string groupId)
        => Groups(root).Remove(groupId) ? (true, $"dissolved {groupId}") : (false, $"group {groupId} no longer exists");
}
