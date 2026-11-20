using System.Buffers.Binary;
using System.Text;

namespace Drycastle.ShooterCore;

/// <summary>How a query ended: the answer, a failure to read it (the client offers Retry), or no such player.</summary>
public enum HuntStatus : byte { Ok = 0, Error = 1, Unknown = 2 }

/// <summary>Bits of <see cref="HuntAnnounceMsg.Flags"/>: a player's first of the species, a new heaviest, a new longest shot, a new longest player kill (its view carries the killer, distance and weapon only). A first that is also a record sets both bits: one line.</summary>
[Flags] public enum HuntAnnounceFlags : byte { None = 0, First = 1, Heaviest = 2, Longest = 4, PlayerKill = 8 }

/// <summary>Client to server (<see cref="EventKind.HuntsRequest"/>): one page of the All hunts table. <see cref="Req"/> is echoed so a stale answer can be dropped. Species "" none, <see cref="Mysteries.AllHidden"/> the Mystery hunts option; Weapon "" none; PlayerId 0 none.</summary>
public sealed record HuntsRequest(ushort Req, ushort Page, HuntSort Sort, bool Descending, string Species, string Weapon, long PlayerId);
/// <summary>Server to the asker (<see cref="EventKind.HuntsPage"/>): the rows of that page, already redacted. <see cref="ViewerId"/>: the asker's own player id (0 for a guest), to tint their rows. <see cref="Total"/>: hunts matching in all, <see cref="HuntBook.PageSize"/> to a page.</summary>
public sealed record HuntsAnswer(ushort Req, HuntStatus Status, long ViewerId, ushort Page, uint Total, IReadOnlyList<HuntView> Rows);
/// <summary>Server to the asker (<see cref="EventKind.Records"/>): a card per species of <see cref="HuntSpecies.Keys"/>, already redacted, and the server's longest player kill (null while none).</summary>
public sealed record RecordsAnswer(ushort Req, HuntStatus Status, long ViewerId, IReadOnlyList<SpeciesRecordView> Records, PlayerKillView? Kill = null);
/// <summary>Client to server (<see cref="EventKind.PlayerLogRequest"/>): the log of a player; 0 asks for the asker's own.</summary>
public sealed record PlayerLogRequest(ushort Req, long PlayerId);
/// <summary>Server to the asker (<see cref="EventKind.PlayerLog"/>): the log, already redacted; null unless <see cref="Status"/> is Ok.</summary>
public sealed record PlayerLogAnswer(ushort Req, HuntStatus Status, long ViewerId, PlayerLogView? Log);
/// <summary>Server to everyone (<see cref="EventKind.HuntAnnounce"/>): a record, a first, or both, redacted for the one it is sent to.</summary>
public sealed record HuntAnnounceMsg(HuntAnnounceFlags Flags, HuntView Hunt);

/// <summary>
/// Codecs of the Hunter's Log events, kinds 100-106 (little-endian; strings are u8 length + utf8, clamped to 255 bytes; a decoder returns null on
/// a malformed payload). Layouts:
/// <list type="bullet">
/// <item>HuntsRequest: u16 req, u16 page, u8 sort (<see cref="HuntSort"/>), u8 descending, str species, str weapon, i64 player.</item>
/// <item>HuntsPage: u16 req, u8 status, i64 viewer, u16 page, u32 total, u8 count, count HuntView.</item>
/// <item>Records: u16 req, u8 status, i64 viewer, u8 count, count SpeciesRecordView, u8 has kill, that PlayerKillView.</item>
/// <item>PlayerLogRequest: u16 req, i64 player. PlayerLog: u16 req, u8 status, i64 viewer, then when Ok a PlayerLogView.</item>
/// <item>HuntAnnounce: u8 flags (1 first, 2 heaviest, 4 longest, 8 player kill), HuntView.</item>
/// <item>HuntView: i64 player, str hunter, str species, str exact, str variant, u8 flags (1 kg, 2 distance, 4 diagram, 8 hidden), f32 kg?, f32 distance?, str weapon, diagram? (u8 along, u8 height, u8 flags 1 right flank 2 head, u8 pose, u8 frame), i64 unix seconds.</item>
/// <item>SpeciesRecordView: str species, u8 flags (1 hidden, 2 caught, 4 heaviest, 8 longest, 16 first), then those HuntViews.</item>
/// <item>PlayerKillView: i64 killer, str killer name, i64 victim, str victim name, f32 distance, str weapon, i64 unix seconds.</item>
/// <item>PlayerLogView: i64 player, str name, u32 hunts, u16 species, u8 flags (1 heaviest, 2 longest, 4 player kill), those HuntViews and that PlayerKillView, u8 count, count LifeEntry (str species, u8 flags 1 hidden 2 caught 4 kg 8 longest, f32 kg?, f32 longest?, i64 caught at).</item>
/// </list>
/// </summary>
public static class HuntEvents
{
    public static byte[] EncodeRequest(HuntsRequest r)
    {
        var w = new W();
        w.U16(r.Req); w.U16(r.Page); w.U8((byte)r.Sort); w.U8(r.Descending ? 1 : 0); w.Str(r.Species); w.Str(r.Weapon); w.I64(r.PlayerId);
        return w.Done();
    }

    public static HuntsRequest? DecodeRequest(ReadOnlySpan<byte> p)
    {
        var r = new R(p);
        var req = r.U16(); var page = r.U16(); var sort = r.U8(); var desc = r.U8(); var species = r.Str(); var weapon = r.Str(); var player = r.I64();
        return r.Ok && r.End && sort <= (byte)HuntSort.Weapon ? new HuntsRequest(req, page, (HuntSort)sort, desc != 0, species, weapon, player) : null;
    }

    public static byte[] EncodePage(HuntsAnswer a)
    {
        var w = new W();
        w.U16(a.Req); w.U8((byte)a.Status); w.I64(a.ViewerId); w.U16(a.Page); w.U32(a.Total);
        w.U8((byte)Math.Min(a.Rows.Count, byte.MaxValue));
        foreach (var h in a.Rows.Take(byte.MaxValue)) Hunt(w, h);
        return w.Done();
    }

    public static HuntsAnswer? DecodePage(ReadOnlySpan<byte> p)
    {
        var r = new R(p);
        var req = r.U16(); var status = r.U8(); var viewer = r.I64(); var page = r.U16(); var total = r.U32(); var n = r.U8();
        var rows = new List<HuntView>(n);
        for (var i = 0; i < n && r.Ok; i++) if (Hunt(ref r) is { } h) rows.Add(h);
        return r.Ok && r.End && status <= (byte)HuntStatus.Unknown ? new HuntsAnswer(req, (HuntStatus)status, viewer, page, total, rows) : null;
    }

    public static byte[] EncodeRecordsRequest(ushort req)
    {
        var w = new W();
        w.U16(req);
        return w.Done();
    }

    public static ushort? DecodeRecordsRequest(ReadOnlySpan<byte> p) => p.Length == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(p) : null;

    public static byte[] EncodeRecords(RecordsAnswer a)
    {
        var w = new W();
        w.U16(a.Req); w.U8((byte)a.Status); w.I64(a.ViewerId);
        w.U8((byte)Math.Min(a.Records.Count, byte.MaxValue));
        foreach (var c in a.Records.Take(byte.MaxValue))
        {
            w.Str(c.Species);
            w.U8((byte)((c.Hidden ? 1 : 0) | (c.Caught ? 2 : 0) | (c.Heaviest != null ? 4 : 0) | (c.Longest != null ? 8 : 0) | (c.First != null ? 16 : 0)));
            if (c.Heaviest != null) Hunt(w, c.Heaviest);
            if (c.Longest != null) Hunt(w, c.Longest);
            if (c.First != null) Hunt(w, c.First);
        }
        w.U8(a.Kill != null ? 1 : 0);
        if (a.Kill != null) Kill(w, a.Kill);
        return w.Done();
    }

    public static RecordsAnswer? DecodeRecords(ReadOnlySpan<byte> p)
    {
        var r = new R(p);
        var req = r.U16(); var status = r.U8(); var viewer = r.I64(); var n = r.U8();
        var list = new List<SpeciesRecordView>(n);
        for (var i = 0; i < n && r.Ok; i++)
        {
            var species = r.Str(); var f = r.U8();
            var heavy = (f & 4) != 0 ? Hunt(ref r) : null;
            var far = (f & 8) != 0 ? Hunt(ref r) : null;
            var first = (f & 16) != 0 ? Hunt(ref r) : null;
            if (r.Ok) list.Add(new SpeciesRecordView(species, (f & 1) != 0, (f & 2) != 0, heavy, far, first));
        }
        var kill = r.U8() != 0 ? Kill(ref r) : null;
        return r.Ok && r.End && status <= (byte)HuntStatus.Unknown ? new RecordsAnswer(req, (HuntStatus)status, viewer, list, kill) : null;
    }

    public static byte[] EncodePlayerRequest(PlayerLogRequest q)
    {
        var w = new W();
        w.U16(q.Req); w.I64(q.PlayerId);
        return w.Done();
    }

    public static PlayerLogRequest? DecodePlayerRequest(ReadOnlySpan<byte> p) =>
        p.Length == 10 ? new PlayerLogRequest(BinaryPrimitives.ReadUInt16LittleEndian(p), BinaryPrimitives.ReadInt64LittleEndian(p[2..])) : null;

    public static byte[] EncodePlayerLog(PlayerLogAnswer a)
    {
        var w = new W();
        w.U16(a.Req); w.U8((byte)a.Status); w.I64(a.ViewerId);
        if (a.Status != HuntStatus.Ok || a.Log is not { } l) return w.Done();
        w.I64(l.PlayerId); w.Str(l.Name); w.U32((uint)l.Hunts); w.U16((ushort)l.Species);
        w.U8((byte)((l.Heaviest != null ? 1 : 0) | (l.Longest != null ? 2 : 0) | (l.Kill != null ? 4 : 0)));
        if (l.Heaviest != null) Hunt(w, l.Heaviest);
        if (l.Longest != null) Hunt(w, l.Longest);
        if (l.Kill != null) Kill(w, l.Kill);
        w.U8((byte)Math.Min(l.Life.Count, byte.MaxValue));
        foreach (var e in l.Life.Take(byte.MaxValue))
        {
            w.Str(e.Species);
            w.U8((byte)((e.Hidden ? 1 : 0) | (e.Caught ? 2 : 0) | (e.BestKg != null ? 4 : 0) | (e.LongestM != null ? 8 : 0) | ((int)e.Tier & 3) << 4)); // bits 4-5: the rarity tier
            if (e.BestKg is { } kg) w.F32(kg);
            if (e.LongestM is { } m) w.F32(m);
            w.I64(e.CaughtAtUnix);
        }
        return w.Done();
    }

    public static PlayerLogAnswer? DecodePlayerLog(ReadOnlySpan<byte> p)
    {
        var r = new R(p);
        var req = r.U16(); var status = r.U8(); var viewer = r.I64();
        if (!r.Ok || status > (byte)HuntStatus.Unknown) return null;
        if (status != (byte)HuntStatus.Ok) return r.End ? new PlayerLogAnswer(req, (HuntStatus)status, viewer, null) : null;
        var player = r.I64(); var name = r.Str(); var hunts = r.U32(); var species = r.U16(); var f = r.U8();
        var heavy = (f & 1) != 0 ? Hunt(ref r) : null;
        var far = (f & 2) != 0 ? Hunt(ref r) : null;
        var kill = (f & 4) != 0 ? Kill(ref r) : null;
        var n = r.U8();
        var life = new List<LifeEntry>(n);
        for (var i = 0; i < n && r.Ok; i++)
        {
            var key = r.Str(); var ef = r.U8();
            float? kg = (ef & 4) != 0 ? r.F32() : null;
            float? m = (ef & 8) != 0 ? r.F32() : null;
            var at = r.I64();
            if (r.Ok) life.Add(new LifeEntry(key, (ef & 1) != 0, (ef & 2) != 0, kg, m, at, (HuntTier)(ef >> 4 & 3)));
        }
        return r.Ok && r.End ? new PlayerLogAnswer(req, HuntStatus.Ok, viewer, new PlayerLogView(player, name, (int)hunts, species, heavy, far, life, kill)) : null;
    }

    public static byte[] EncodeAnnounce(HuntAnnounceMsg m)
    {
        var w = new W();
        w.U8((byte)m.Flags);
        Hunt(w, m.Hunt);
        return w.Done();
    }

    public static HuntAnnounceMsg? DecodeAnnounce(ReadOnlySpan<byte> p)
    {
        var r = new R(p);
        var flags = r.U8();
        var h = Hunt(ref r);
        return r.Ok && r.End && h != null && flags <= 15 ? new HuntAnnounceMsg((HuntAnnounceFlags)flags, h) : null;
    }

    static void Hunt(W w, HuntView h)
    {
        w.I64(h.PlayerId); w.Str(h.Hunter); w.Str(h.Species); w.Str(h.Exact); w.Str(h.Variant);
        w.U8((byte)((h.Kg != null ? 1 : 0) | (h.DistanceM != null ? 2 : 0) | (h.Diagram != null ? 4 : 0) | (h.Hidden ? 8 : 0)));
        if (h.Kg is { } kg) w.F32(kg);
        if (h.DistanceM is { } d) w.F32(d);
        w.Str(h.Weapon);
        if (h.Diagram is { } g) { w.U8(g.Along); w.U8(g.Height); w.U8((byte)((g.RightFlank ? 1 : 0) | (g.Head ? 2 : 0))); w.U8(g.Pose); w.U8(g.Frame); }
        w.I64(h.AtUnix);
    }

    static void Kill(W w, PlayerKillView k)
    {
        w.I64(k.KillerId); w.Str(k.Killer); w.I64(k.VictimId); w.Str(k.Victim); w.F32(k.DistanceM); w.Str(k.Weapon); w.I64(k.AtUnix);
    }

    static PlayerKillView? Kill(ref R r)
    {
        var killer = r.I64(); var killerName = r.Str(); var victim = r.I64(); var victimName = r.Str(); var d = r.F32(); var weapon = r.Str(); var at = r.I64();
        return r.Ok ? new PlayerKillView(killer, killerName, victim, victimName, d, weapon, at) : null;
    }

    static HuntView? Hunt(ref R r)
    {
        var player = r.I64(); var hunter = r.Str(); var species = r.Str(); var exact = r.Str(); var variant = r.Str(); var f = r.U8();
        float? kg = (f & 1) != 0 ? r.F32() : null;
        float? d = (f & 2) != 0 ? r.F32() : null;
        var weapon = r.Str();
        HitDiagram? g = null;
        if ((f & 4) != 0) { var a = r.U8(); var h = r.U8(); var fl = r.U8(); var pose = r.U8(); var frame = r.U8(); g = new HitDiagram(a, h, (fl & 1) != 0, (fl & 2) != 0, pose, frame); }
        var at = r.I64();
        return r.Ok ? new HuntView(player, hunter, species, exact, variant, kg, d, weapon, g, at, (f & 8) != 0) : null;
    }

    sealed class W
    {
        readonly List<byte> _b = new();
        public void U8(int v) => _b.Add((byte)v);
        public void U16(int v) { _b.Add((byte)v); _b.Add((byte)(v >> 8)); }
        public void U32(uint v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(s, v); _b.AddRange(s.ToArray()); }
        public void I64(long v) { Span<byte> s = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(s, v); _b.AddRange(s.ToArray()); }
        public void F32(float v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(s, v); _b.AddRange(s.ToArray()); }

        public void Str(string v)
        {
            var bytes = Encoding.UTF8.GetBytes(v ?? "");
            var n = Math.Min(bytes.Length, byte.MaxValue);
            while (n > 0 && n < bytes.Length && (bytes[n] & 0xC0) == 0x80) n--; // never cut a character in two
            _b.Add((byte)n);
            for (var i = 0; i < n; i++) _b.Add(bytes[i]);
        }

        public byte[] Done() => _b.ToArray();
    }

    ref struct R
    {
        readonly ReadOnlySpan<byte> _p;
        int _at;
        public bool Ok { get; private set; }

        public R(ReadOnlySpan<byte> p) { _p = p; _at = 0; Ok = true; }

        public bool End => _at == _p.Length;

        bool Take(int n)
        {
            if (!Ok || _p.Length - _at < n) { Ok = false; return false; }
            return true;
        }

        public byte U8() { if (!Take(1)) return 0; return _p[_at++]; }
        public ushort U16() { if (!Take(2)) return 0; var v = BinaryPrimitives.ReadUInt16LittleEndian(_p[_at..]); _at += 2; return v; }
        public uint U32() { if (!Take(4)) return 0; var v = BinaryPrimitives.ReadUInt32LittleEndian(_p[_at..]); _at += 4; return v; }
        public long I64() { if (!Take(8)) return 0; var v = BinaryPrimitives.ReadInt64LittleEndian(_p[_at..]); _at += 8; return v; }
        public float F32() { if (!Take(4)) return 0; var v = BinaryPrimitives.ReadSingleLittleEndian(_p[_at..]); _at += 4; return v; }

        public string Str()
        {
            var n = U8();
            if (!Take(n)) return "";
            var s = Encoding.UTF8.GetString(_p.Slice(_at, n));
            _at += n;
            return s;
        }
    }
}
