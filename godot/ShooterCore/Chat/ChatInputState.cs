using System.Text;

namespace Drycastle.ShooterCore;

/// <summary>
/// What the player has sent from the chat box, for the up and down arrows. Older steps back (the first step keeps what was
/// being typed), Newer steps forward and ends back at that draft. A repeat of the last line is not stored twice.
/// </summary>
public sealed class ChatHistory
{
    public const int Max = 64;
    readonly List<string> _items = new();
    int _cursor;
    string _draft = "";

    public int Count => _items.Count;

    public void Add(string line)
    {
        line = line.Trim();
        if (line.Length > 0 && (_items.Count == 0 || _items[^1] != line))
        {
            _items.Add(line);
            if (_items.Count > Max) _items.RemoveAt(0);
        }
        Reset();
    }

    /// <summary>Forget where the arrows were: the player edited the text, or sent it.</summary>
    public void Reset() { _cursor = _items.Count; _draft = ""; }

    public bool Browsing => _cursor < _items.Count;

    /// <summary>One step back. <paramref name="current"/> is the text being typed, kept as the draft on the first step. Null when there is no history.</summary>
    public string? Older(string current)
    {
        if (_items.Count == 0) return null;
        if (!Browsing) _draft = current;
        if (_cursor > 0) _cursor--;
        return _items[_cursor];
    }

    /// <summary>One step forward; past the newest line it gives the draft back. Null when not browsing.</summary>
    public string? Newer()
    {
        if (!Browsing) return null;
        _cursor++;
        return _cursor == _items.Count ? _draft : _items[_cursor];
    }
}

/// <summary>
/// Tab completion. The first Tab takes the first suggestion, the next ones cycle through the same list (Shift+Tab goes
/// back); typing anything else starts over. <see cref="Pending"/> is the suggestion the next Tab takes, for the list to highlight.
/// </summary>
public sealed class ChatCompleter
{
    ChatHint? _hint;
    string? _applied;
    int _current;

    /// <summary>The index in the hint's list that a forward Tab will take next, for the text as it is now.</summary>
    public int Pending(string input) => _applied == input && _hint is { Suggestions.Count: > 0 } h ? (_current + 1) % h.Suggestions.Count : 0;

    /// <summary>The input after a Tab, or null when there is nothing to complete.</summary>
    public string? Tab(string input, ChatContext ctx, bool backwards = false)
    {
        var cycling = _applied == input && _hint != null;
        var hint = cycling ? _hint! : ChatCommands.Hint(input, ctx);
        var n = hint.Suggestions.Count;
        if (n == 0) return null;
        var i = cycling ? _current + (backwards ? -1 : 1) : backwards ? n - 1 : 0;
        i = (i % n + n) % n;
        var text = ChatCommands.Complete(input, hint, hint.Suggestions[i]);
        _hint = hint;
        _applied = text;
        _current = i;
        return text;
    }

    /// <summary>Forget the cycle (the text changed by typing, or the box closed).</summary>
    public void Reset() { _hint = null; _applied = null; _current = 0; }
}

/// <summary>A token bucket for what one player may send: a short burst, then about one line a second.</summary>
public sealed class ChatRateLimiter
{
    readonly double _capacity, _perSecond;
    double _tokens, _at;

    public ChatRateLimiter(double capacity = 5, double perSecond = 1) { _capacity = _tokens = capacity; _perSecond = perSecond; }

    public bool TryTake(double now)
    {
        _tokens = Math.Min(_capacity, _tokens + Math.Max(0, now - _at) * _perSecond);
        _at = now;
        if (_tokens < 1) return false;
        _tokens -= 1;
        return true;
    }
}

public enum ChatLineKind : byte
{
    /// <summary>A player's message.</summary>
    Player = 0,
    /// <summary>Said by the server to everyone (an admin changed something).</summary>
    System = 1,
    /// <summary>The server's answer to the one who asked (a command's result).</summary>
    Info = 2,
    /// <summary>The server's answer to the one who asked, when it did not work.</summary>
    Error = 3,
}

/// <summary>One line in the chat log. <see cref="Sender"/> is the player's name; empty for the server's lines.</summary>
public sealed record ChatLine(ChatLineKind Kind, string Sender, string Text);

/// <summary>The server's word to a joiner: whether they are an admin, and the vehicle ids /spawn takes.</summary>
public sealed record ChatStatus(bool Admin, IReadOnlyList<string> Vehicles);

/// <summary>The day clock after an admin changed it, with the server tick it holds at.</summary>
public sealed record ClockChange(DayClock Clock, uint Tick);

/// <summary>Payloads of the chat events (kinds 180-183). Decoders return null for a payload that is too short.</summary>
public static class ChatEvents
{
    public const int MaxChars = 200;

    /// <summary>Drops control characters, trims, and cuts to <see cref="MaxChars"/>: what a line may be once it is on the server.</summary>
    public static string Clean(string s)
    {
        var sb = new StringBuilder(Math.Min(s.Length, MaxChars));
        foreach (var ch in s)
            if (!char.IsControl(ch)) sb.Append(ch);
        var t = sb.ToString().Trim();
        if (t.Length > MaxChars)
        {
            var n = char.IsHighSurrogate(t[MaxChars - 1]) ? MaxChars - 1 : MaxChars; // never split a pair
            t = t[..n].TrimEnd();
        }
        return t;
    }

    public static byte[] EncodeSend(string text) => Encoding.UTF8.GetBytes(Clean(text));

    public static string? DecodeSend(ReadOnlySpan<byte> d)
    {
        if (d.Length > MaxChars * 4) return null;
        var t = Clean(Encoding.UTF8.GetString(d));
        return t.Length == 0 ? null : t;
    }

    static void PutText8(List<byte> o, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        var n = Math.Min(b.Length, 255);
        o.Add((byte)n);
        o.AddRange(b.AsSpan(0, n).ToArray());
    }

    static string? GetText8(ReadOnlySpan<byte> d, ref int at)
    {
        if (at >= d.Length || at + 1 + d[at] > d.Length) return null;
        var s = Encoding.UTF8.GetString(d.Slice(at + 1, d[at]));
        at += 1 + d[at];
        return s;
    }

    /// <summary>ChatLine: u8 kind, sender (u8-length UTF-8), text (u16-length UTF-8).</summary>
    public static byte[] EncodeLine(ChatLine l)
    {
        var o = new List<byte> { (byte)l.Kind };
        PutText8(o, l.Sender);
        var t = Encoding.UTF8.GetBytes(Clean(l.Text));
        o.Add((byte)(t.Length & 0xFF)); o.Add((byte)(t.Length >> 8));
        o.AddRange(t);
        return o.ToArray();
    }

    public static ChatLine? DecodeLine(ReadOnlySpan<byte> d)
    {
        if (d.Length < 2 || !Enum.IsDefined((ChatLineKind)d[0])) return null;
        var at = 1;
        var sender = GetText8(d, ref at);
        if (sender == null || at + 2 > d.Length) return null;
        int n = d[at] | d[at + 1] << 8;
        at += 2;
        if (at + n > d.Length) return null;
        return new ChatLine((ChatLineKind)d[0], sender, Encoding.UTF8.GetString(d.Slice(at, n)));
    }

    /// <summary>ChatStatus: u8 flags (bit 0 admin), u8 vehicle count, each id u8-length UTF-8.</summary>
    public static byte[] EncodeStatus(ChatStatus s)
    {
        var o = new List<byte> { (byte)(s.Admin ? 1 : 0), (byte)Math.Min(s.Vehicles.Count, 255) };
        foreach (var v in s.Vehicles.Take(255)) PutText8(o, v);
        return o.ToArray();
    }

    public static ChatStatus? DecodeStatus(ReadOnlySpan<byte> d)
    {
        if (d.Length < 2) return null;
        var at = 2;
        var list = new List<string>();
        for (var i = 0; i < d[1]; i++)
        {
            if (GetText8(d, ref at) is not { } v) return null;
            list.Add(v);
        }
        return new ChatStatus((d[0] & 1) != 0, list);
    }

    /// <summary>ClockChange: u16 start minute, u32 day length in ms, u8 frozen, u32 server tick.</summary>
    public static byte[] EncodeClock(ClockChange c)
    {
        var o = new byte[11];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(o, (ushort)c.Clock.StartMinute);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(2), (uint)Math.Round(c.Clock.DayLengthSeconds * 1000));
        o[6] = (byte)(c.Clock.Frozen ? 1 : 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(7), c.Tick);
        return o;
    }

    public static ClockChange? DecodeClock(ReadOnlySpan<byte> d)
    {
        if (d.Length < 11) return null;
        var clock = new DayClock
        {
            StartMinute = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(d),
            DayLengthSeconds = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(d[2..]) / 1000.0,
            Frozen = d[6] != 0,
        };
        return new ClockChange(clock, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(d[7..]));
    }
}
