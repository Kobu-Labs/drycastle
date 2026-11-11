using System.Globalization;

namespace Suchohrad.ShooterCore;

/// <summary>Result of validating the connect form: the parsed target or the translation key of what is wrong (per field).</summary>
public readonly record struct ConnectCheck(string Host, int Port, string? AddressError, string? PortError, string? NameError)
{
    public bool Ok => AddressError == null && PortError == null && NameError == null;
}

/// <summary>Godot-free connect form rules (unit tested): address, port and name validation, "host:port" splitting.</summary>
public static class ConnectForm
{
    public const int DefaultPort = 27015, MaxName = 24, MaxHost = 253;

    public static ConnectCheck Validate(string address, string port, string name)
    {
        var (host, embedded) = Split(address.Trim());
        string? ae = null, pe = null, ne = null;
        if (host.Length == 0) ae = "connect.err.address.empty";
        else if (!ValidHost(host)) ae = "connect.err.address.bad";
        var p = DefaultPort;
        var portText = embedded ?? port.Trim();
        if (portText.Length > 0 && !(int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out p) && p is >= 1 and <= 65535))
            pe = "connect.err.port.bad";
        else if (portText.Length == 0) p = DefaultPort;
        var n = name.Trim();
        if (n.Length == 0) ne = "connect.err.name.empty";
        else if (n.Length > MaxName) ne = "connect.err.name.long";
        return new ConnectCheck(host, p, ae, pe, ne);
    }

    /// <summary>"host:port" (a single colon) splits; anything else (plain host, IPv6 literal) is the host.</summary>
    public static (string Host, string? Port) Split(string a)
    {
        var i = a.LastIndexOf(':');
        if (i > 0 && a.IndexOf(':') == i) return (a[..i], a[(i + 1)..]);
        return (a, null);
    }

    static bool ValidHost(string h)
    {
        if (h.Length > MaxHost) return false;
        if (System.Net.IPAddress.TryParse(h, out _)) return true;
        foreach (var c in h) if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) return false;
        return h[0] != '.' && h[0] != '-' && h[^1] != '.' && !h.Contains("..");
    }
}

/// <summary>Most-recently-used server list: newest first, no duplicates, capped.</summary>
public sealed class RecentServers
{
    public const int Max = 6;
    readonly List<string> _items = new();
    public IReadOnlyList<string> Items => _items;

    public static string Format(string host, int port) => $"{host}:{port}";

    public void Add(string entry)
    {
        _items.RemoveAll(e => string.Equals(e, entry, StringComparison.OrdinalIgnoreCase));
        _items.Insert(0, entry);
        if (_items.Count > Max) _items.RemoveRange(Max, _items.Count - Max);
    }

    public bool Remove(string entry) => _items.RemoveAll(e => e == entry) > 0;
    public void Clear() => _items.Clear();
    public string Serialize() => string.Join(";", _items);

    public static RecentServers Parse(string s)
    {
        var r = new RecentServers();
        foreach (var e in s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse()) r.Add(e);
        return r;
    }
}

/// <summary>A bound input: a (physical) key or a mouse button, by Godot's integer codes.</summary>
public readonly record struct Binding(bool Mouse, int Code)
{
    public static Binding Key(int code) => new(false, code);
    public static Binding MouseButton(int code) => new(true, code);
    public override string ToString() => (Mouse ? "m:" : "k:") + Code.ToString(CultureInfo.InvariantCulture);

    public static Binding? Parse(string s)
    {
        if (s.Length < 3 || s[1] != ':' || !int.TryParse(s.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var c)) return null;
        return s[0] switch { 'k' => Key(c), 'm' => MouseButton(c), _ => null };
    }
}

/// <summary>
/// The one input map: action id to binding. Assigning a binding that another action already holds swaps the two
/// (conflict detection with no unbound action left behind). Defaults are injected so this stays Godot-free.
/// </summary>
public sealed class BindingMap
{
    readonly IReadOnlyList<(string Action, Binding Default)> _defaults;
    readonly Dictionary<string, Binding> _map = new();

    public BindingMap(IReadOnlyList<(string Action, Binding Default)> defaults) { _defaults = defaults; Reset(); }

    public IEnumerable<string> Actions => _defaults.Select(d => d.Action);
    public Binding Get(string action) => _map[action];
    public string? ActionFor(Binding b) => _map.FirstOrDefault(kv => kv.Value == b).Key;
    public bool IsDefault => _defaults.All(d => _map[d.Action] == d.Default);
    public bool IsDefaultFor(string action) => _map[action] == _defaults.First(d => d.Action == action).Default;

    public void Reset() { foreach (var (a, b) in _defaults) _map[a] = b; }

    /// <summary>Binds <paramref name="action"/> to <paramref name="b"/>; returns the action it was swapped with (null: no conflict).</summary>
    public string? Assign(string action, Binding b)
    {
        var other = ActionFor(b);
        if (other == action) return null;
        if (other != null) _map[other] = _map[action];
        _map[action] = b;
        return other;
    }

    public BindingMap Clone()
    {
        var c = new BindingMap(_defaults);
        foreach (var kv in _map) c._map[kv.Key] = kv.Value;
        return c;
    }

    public void CopyFrom(BindingMap o) { foreach (var kv in o._map) _map[kv.Key] = kv.Value; }
    public bool SameAs(BindingMap o) => _map.All(kv => o._map[kv.Key] == kv.Value);

    /// <summary>
    /// Loads every saved binding as one set, so a saved swap survives: entries are validated against each other (and against the
    /// defaults of actions with no usable saved entry), not against the bindings currently in the map. Only true duplicates are
    /// rejected (the earlier action wins); a rejected or missing entry keeps its default.
    /// </summary>
    public void LoadAll(IReadOnlyDictionary<string, string?> saved)
    {
        var chosen = new Dictionary<string, Binding>();
        foreach (var a in Actions)
            if (saved.TryGetValue(a, out var str) && Binding.Parse(str ?? "") is { } b && !chosen.ContainsValue(b)) chosen[a] = b;
        // an action left on its default holds that key; drop saved entries that would collide with it until stable
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var (a, def) in _defaults)
                if (!chosen.ContainsKey(a) && chosen.FirstOrDefault(kv => kv.Value == def) is { Key: not null } hit)
                { chosen.Remove(hit.Key); changed = true; break; }
        }
        Reset();
        foreach (var (a, b) in chosen) _map[a] = b;
    }

    /// <summary>Loads a saved binding when it parses, is known and does not duplicate an earlier one; otherwise the default stays.</summary>
    public void Load(string action, string? saved)
    {
        if (!_map.ContainsKey(action) || Binding.Parse(saved ?? "") is not { } b) return;
        if (ActionFor(b) is { } o && o != action) return;
        _map[action] = b;
    }
}

/// <summary>Slider and range helpers shared by the settings screen and persistence.</summary>
public static class SettingsRanges
{
    public const float SensMin = 0.1f, SensMax = 3f, FovMin = 70, FovMax = 110;
    public static float ClampSens(float v) => Math.Clamp(v, SensMin, SensMax);
    public static float ClampFov(float v) => Math.Clamp(v, FovMin, FovMax);
    public static float ClampVolume(float v) => Math.Clamp(v, 0f, 1f);
    public const float BrightnessMin = 0.5f, BrightnessMax = 1.5f;
    public static float ClampBrightness(float v) => Math.Clamp(v, BrightnessMin, BrightnessMax);
}
