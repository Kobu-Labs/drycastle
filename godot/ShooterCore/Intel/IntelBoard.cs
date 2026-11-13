using System.Numerics;
using System.Text;

namespace Drycastle.ShooterCore;

/// <summary>
/// One announced event as the intel screen shows it: what it is, when it begins, its risks and, when it has one, where.
/// It is the whole interface between an event and the announcement: the event lifecycle (a later ticket) puts one on the
/// <see cref="IntelBoard"/> when the event is announced and takes it off when it is over, and every event looks the same
/// to the screen. <see cref="StartsAt"/> is in seconds on the clock of whoever holds the board (the server's elapsed time, a
/// client's own clock); the wire carries the time left, so clocks need not agree.
/// </summary>
/// <param name="Id">Stable name of this event; the same id replaces the earlier entry.</param>
/// <param name="Kind">Picks the emblem on the screen ("generic" for any kind without one).</param>
/// <param name="Name">Short headline, for example "Bigfoot sighting".</param>
/// <param name="Description">What it is, in a few sentences.</param>
/// <param name="Risks">The dangers, one short line each.</param>
/// <param name="StartsAt">When it begins, in seconds; a time in the past is an event that is under way.</param>
/// <param name="Where">Ground position (x, z in the game's axes) when the event has a place; null when it has none (village-wide).</param>
/// <param name="Test">An admin's test event: the screen marks it as not real.</param>
/// <param name="EndsAt">Server only, never sent: when the board drops the event by itself. Null: it stays until it is removed.</param>
public sealed record IntelEvent(
    string Id, string Kind, string Name, string Description, IReadOnlyList<string> Risks,
    double StartsAt, Vector2? Where = null, bool Test = false, double? EndsAt = null);

/// <summary>Where an event stands relative to its start time.</summary>
public enum IntelStatus { Upcoming, Underway }

/// <summary>What the announced events look like on a clock: the countdown text and whether one has begun.</summary>
public static class IntelTime
{
    public static IntelStatus Status(IntelEvent e, double now) => now >= e.StartsAt ? IntelStatus.Underway : IntelStatus.Upcoming;

    /// <summary>Whole seconds until the start, rounded up (so the last second reads 1 and zero means it has begun); never negative.</summary>
    public static int SecondsLeft(IntelEvent e, double now) => (int)Math.Ceiling(Math.Max(0, e.StartsAt - now));

    /// <summary>"01:52", or "1:02:03" from an hour up.</summary>
    public static string Countdown(int seconds)
    {
        seconds = Math.Max(0, seconds);
        var h = seconds / 3600;
        var m = seconds % 3600 / 60;
        var s = seconds % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
    }
}

/// <summary>
/// The events that are announced right now. The server owns the real one (the lifecycle adds and removes events on it);
/// every client keeps a copy the server sends it, which the intel screen draws. Godot-free.
/// </summary>
public sealed class IntelBoard
{
    /// <summary>The most events on a board; a newer one pushes the one furthest in the future off.</summary>
    public const int MaxEvents = 6;

    readonly List<IntelEvent> _events = new();

    /// <summary>The events in the order they begin: the soonest (or the one under way longest) first.</summary>
    public IReadOnlyList<IntelEvent> Events => _events;

    /// <summary>Counts every change, so a view redraws only when this moves.</summary>
    public int Version { get; private set; }

    public bool IsEmpty => _events.Count == 0;

    /// <summary>Adds the event, or replaces the one with the same id.</summary>
    public void Put(IntelEvent e)
    {
        var at = _events.FindIndex(x => x.Id == e.Id);
        if (at >= 0) _events.RemoveAt(at);
        _events.Add(e);
        _events.Sort((a, b) => a.StartsAt.CompareTo(b.StartsAt));
        if (_events.Count > MaxEvents) _events.RemoveAt(_events.Count - 1);
        Version++;
    }

    /// <summary>Takes one event off. False when there was none by that id.</summary>
    public bool Remove(string id)
    {
        if (_events.RemoveAll(e => e.Id == id) == 0) return false;
        Version++;
        return true;
    }

    /// <summary>Takes every event off. False when the board was already empty.</summary>
    public bool Clear()
    {
        if (_events.Count == 0) return false;
        _events.Clear();
        Version++;
        return true;
    }

    /// <summary>Drops the events whose <see cref="IntelEvent.EndsAt"/> has passed. True when any went.</summary>
    public bool Prune(double now)
    {
        if (_events.RemoveAll(e => e.EndsAt is { } end && now >= end) == 0) return false;
        Version++;
        return true;
    }

    /// <summary>A client's copy: the board becomes exactly <paramref name="events"/>.</summary>
    public void Replace(IEnumerable<IntelEvent> events)
    {
        _events.Clear();
        _events.AddRange(events.Take(MaxEvents).OrderBy(e => e.StartsAt));
        Version++;
    }
}

/// <summary>What the server sent: the whole board, and the event it is announcing just now (empty when this is only a refresh).</summary>
public sealed record IntelUpdate(IReadOnlyList<IntelEvent> Events, string AnnouncedId)
{
    public IntelEvent? Announced => AnnouncedId.Length == 0 ? null : Events.FirstOrDefault(e => e.Id == AnnouncedId);
}

/// <summary>
/// Payload of <see cref="EventKind.IntelBoard"/>: the whole board each time (so a late joiner and a refresh are the same
/// message) plus which event, if any, is being announced now. Text is cut to the limits below and stripped of control
/// characters, so what arrives is always short enough to fit the screen. Decoders return null for a payload that is cut short.
/// </summary>
public static class IntelCodec
{
    public const byte Format = 1;
    public const int MaxId = 32, MaxKind = 24, MaxName = 40, MaxDescription = 240, MaxRisks = 5, MaxRisk = 48;
    /// <summary>Time left is clamped to a day either way, which keeps a wrong clock from drawing nonsense.</summary>
    const float MaxSeconds = 86400f;

    /// <summary>Control characters out, trimmed, cut to <paramref name="max"/> characters without splitting a surrogate pair.</summary>
    public static string Clean(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, max));
        foreach (var ch in s) if (!char.IsControl(ch)) sb.Append(ch);
        var t = sb.ToString().Trim();
        if (t.Length > max) t = t[..(char.IsHighSurrogate(t[max - 1]) ? max - 1 : max)].TrimEnd();
        return t;
    }

    /// <summary>The board as it is at <paramref name="now"/> (the holder's clock), with the id being announced ("" for none).</summary>
    public static byte[] Encode(IReadOnlyList<IntelEvent> events, double now, string announcedId = "")
    {
        var o = new List<byte> { Format };
        PutText8(o, Clean(announcedId, MaxId));
        var list = events.Take(IntelBoard.MaxEvents).ToList();
        o.Add((byte)list.Count);
        foreach (var e in list)
        {
            PutText8(o, Clean(e.Id, MaxId));
            PutText8(o, Clean(e.Kind, MaxKind));
            PutText8(o, Clean(e.Name, MaxName));
            PutText16(o, Clean(e.Description, MaxDescription));
            o.Add((byte)((e.Test ? 1 : 0) | (e.Where != null ? 2 : 0)));
            PutF32(o, (float)Math.Clamp(e.StartsAt - now, -MaxSeconds, MaxSeconds));
            if (e.Where is { } w) { PutF32(o, w.X); PutF32(o, w.Y); }
            var risks = e.Risks.Select(r => Clean(r, MaxRisk)).Where(r => r.Length > 0).Take(MaxRisks).ToList();
            o.Add((byte)risks.Count);
            foreach (var r in risks) PutText8(o, r);
        }
        return o.ToArray();
    }

    /// <summary>Reads a board; <paramref name="now"/> (the receiver's clock) turns the times left back into start times.</summary>
    public static IntelUpdate? Decode(ReadOnlySpan<byte> d, double now)
    {
        var at = 0;
        if (d.Length < 3 || d[at++] != Format) return null;
        if (GetText8(d, ref at) is not { } announced || at >= d.Length) return null;
        int count = d[at++];
        if (count > IntelBoard.MaxEvents) return null;
        var list = new List<IntelEvent>(count);
        for (var i = 0; i < count; i++)
        {
            if (GetText8(d, ref at) is not { } id || GetText8(d, ref at) is not { } kind || GetText8(d, ref at) is not { } name
                || GetText16(d, ref at) is not { } description || at + 5 > d.Length) return null;
            var flags = d[at++];
            var left = GetF32(d, ref at);
            if (!float.IsFinite(left)) return null;
            Vector2? where = null;
            if ((flags & 2) != 0)
            {
                if (at + 8 > d.Length) return null;
                var x = GetF32(d, ref at);
                var z = GetF32(d, ref at);
                if (!float.IsFinite(x) || !float.IsFinite(z)) return null;
                where = new Vector2(x, z);
            }
            if (at >= d.Length) return null;
            int n = d[at++];
            var risks = new List<string>(n);
            for (var k = 0; k < n; k++)
            {
                if (GetText8(d, ref at) is not { } risk) return null;
                risks.Add(risk);
            }
            list.Add(new IntelEvent(id, kind, name, description, risks, now + Math.Clamp(left, -MaxSeconds, MaxSeconds), where, (flags & 1) != 0));
        }
        return new IntelUpdate(list, announced);
    }

    static void PutText8(List<byte> o, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        var n = Math.Min(b.Length, 255);
        o.Add((byte)n);
        o.AddRange(b.AsSpan(0, n).ToArray());
    }

    static void PutText16(List<byte> o, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        var n = Math.Min(b.Length, ushort.MaxValue);
        o.Add((byte)(n & 0xFF)); o.Add((byte)(n >> 8));
        o.AddRange(b.AsSpan(0, n).ToArray());
    }

    static void PutF32(List<byte> o, float v)
    {
        var b = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(b, v);
        o.AddRange(b);
    }

    static float GetF32(ReadOnlySpan<byte> d, ref int at)
    {
        var v = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(d[at..]);
        at += 4;
        return v;
    }

    static string? GetText8(ReadOnlySpan<byte> d, ref int at)
    {
        if (at >= d.Length || at + 1 + d[at] > d.Length) return null;
        var s = Encoding.UTF8.GetString(d.Slice(at + 1, d[at]));
        at += 1 + d[at];
        return s;
    }

    static string? GetText16(ReadOnlySpan<byte> d, ref int at)
    {
        if (at + 2 > d.Length) return null;
        int n = d[at] | d[at + 1] << 8;
        if (at + 2 + n > d.Length) return null;
        var s = Encoding.UTF8.GetString(d.Slice(at + 2, n));
        at += 2 + n;
        return s;
    }
}

/// <summary>The admin's test event (<c>/event intel</c>): a made-up event that carries every field, so the announcement and the screen can be tried before a real event exists.</summary>
public static class IntelMock
{
    public const string TestId = "test";
    /// <summary>Seconds from the announcement to the made-up event's start, and how long it then stays on the board.</summary>
    public const double LeadSeconds = 120, StaysSeconds = 600;

    public static IntelEvent Test(double now, Vector2? where) => new(
        TestId, "generic", "TEST ALERT",
        "Suspicious activity was detected near the village. This is a test event an admin announced: nothing is coming. A real event is described here.",
        new[] { "No real danger: this is a test", "A real event lists here what can hurt you" },
        now + LeadSeconds, where, Test: true, EndsAt: now + LeadSeconds + StaysSeconds);
}
