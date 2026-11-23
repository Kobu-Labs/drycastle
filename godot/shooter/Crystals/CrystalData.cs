using System.Text.Json;
using Godot;

namespace Drycastle.Shooter.Crystals;

/// <summary>One crystal's section of data/crystals.json, read by key with a default for a missing one (so a half-written file in the lab never breaks a crystal).</summary>
public sealed class CrystalParams
{
    public static readonly CrystalParams Empty = new(default);

    readonly JsonElement _o;

    public CrystalParams(JsonElement o) => _o = o;

    JsonElement? Get(string k) => _o.ValueKind == JsonValueKind.Object && _o.TryGetProperty(k, out var v) ? v : null;

    public float F(string k, float d) => Get(k) is { ValueKind: JsonValueKind.Number } v ? v.GetSingle() : d;
    public int I(string k, int d) => Get(k) is { ValueKind: JsonValueKind.Number } v ? (int)Math.Round(v.GetDouble()) : d;
    public bool B(string k, bool d) => Get(k) is { } v ? v.ValueKind == JsonValueKind.True || (v.ValueKind != JsonValueKind.False && d) : d;
    public string S(string k, string d) => Get(k) is { ValueKind: JsonValueKind.String } v ? v.GetString() ?? d : d;

    /// <summary>A "#rrggbb" colour; a bad string keeps the default.</summary>
    public Color C(string k, string d)
    {
        var s = S(k, d);
        return Color.FromString(s, Color.FromHtml(d));
    }

    /// <summary>An [x, y, z] array.</summary>
    public Vector3 V(string k, Vector3 d)
    {
        if (Get(k) is not { ValueKind: JsonValueKind.Array } a || a.GetArrayLength() != 3) return d;
        return new Vector3(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle());
    }
}

/// <summary>
/// data/crystals.json: the tunables of the fantasy crystals. <see cref="Poll"/> reloads the file when it changed on disk (the lab calls it twice a second, so saving the
/// file updates the crystals live); a file that does not parse keeps the last good one and says why in <see cref="Status"/>.
/// </summary>
public static class CrystalData
{
    const string Res = "res://data/crystals.json";

    static JsonElement _root;
    static DateTime _stamp = DateTime.MinValue;
    static bool _loaded;

    public static string Status { get; private set; } = "";

    static string DiskPath => ProjectSettings.GlobalizePath(Res);

    public static CrystalParams Get(string id)
    {
        if (!_loaded) Load();
        return _root.ValueKind == JsonValueKind.Object && _root.TryGetProperty(id, out var v) ? new CrystalParams(v) : CrystalParams.Empty;
    }

    /// <summary>True when the file changed on disk and the new content parsed.</summary>
    public static bool Poll()
    {
        var path = DiskPath;
        if (!File.Exists(path)) return false;
        var stamp = File.GetLastWriteTimeUtc(path);
        if (_loaded && stamp == _stamp) return false;
        return Load();
    }

    static bool Load()
    {
        _loaded = true;
        try
        {
            var path = DiskPath;
            string text;
            if (File.Exists(path)) { text = File.ReadAllText(path); _stamp = File.GetLastWriteTimeUtc(path); }
            else text = Godot.FileAccess.GetFileAsString(Res);
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            _root = doc.RootElement.Clone();
            Status = "ok";
            return true;
        }
        catch (Exception e)
        {
            Status = "crystals.json: " + e.Message;
            GD.PrintErr(Status);
            return false;
        }
    }

    /// <summary>The lab's section ("lab": environment glow, spacing).</summary>
    public static CrystalParams Lab => Get("lab");
}
