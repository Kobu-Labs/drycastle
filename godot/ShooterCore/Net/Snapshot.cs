using System.Buffers.Binary;

namespace Drycastle.ShooterCore;

/// <summary>
/// The complete entity states of recent ticks. The server keeps what it sent and the client what it committed, so a
/// delta can name its baseline: "against tick B" means "against the state both ends hold for tick B".
/// </summary>
public sealed class SnapshotHistory
{
    /// <summary>Ticks kept. A client that acknowledges an older tick gets a full keyframe set instead.</summary>
    public const int Depth = 64;

    readonly Dictionary<uint, IReadOnlyDictionary<ushort, EntityRecord>> _byTick = new();

    public bool Has(uint tick) => _byTick.ContainsKey(tick);

    /// <summary>The complete state of a tick, or null when it is not kept.</summary>
    public IReadOnlyDictionary<ushort, EntityRecord>? Get(uint tick) => _byTick.GetValueOrDefault(tick);

    /// <summary>Keeps the state of a tick from its entity list (a tick already kept is left as it is).</summary>
    public void Record(uint tick, IReadOnlyList<EntityRecord> entities)
    {
        if (Has(tick)) return;
        var d = new Dictionary<ushort, EntityRecord>(entities.Count);
        foreach (var e in entities) d[e.Id] = e;
        Store(tick, d);
    }

    /// <summary>Keeps the state of a tick. The dictionary must not change afterwards.</summary>
    public void Store(uint tick, IReadOnlyDictionary<ushort, EntityRecord> states)
    {
        _byTick[tick] = states;
        if (_byTick.Count <= Depth) return;
        foreach (var k in _byTick.Keys.ToList()) if (k + Depth <= tick) _byTick.Remove(k);
    }
}

/// <summary>
/// Per-client snapshot encoder. Each tick's packets are relative to the newest state the client acknowledged (its
/// baseline): an entity that changed since then carries its change, one that did not is omitted, one that is new to the
/// baseline is sent as a keyframe, and one that left is sent as a removal. Every packet of a tick carries the same
/// baseline, so a lost packet costs only the entities it held, and the next tick repairs them without waiting for a
/// keyframe. With no usable baseline (none acknowledged, or too old) the whole world goes in full packets.
/// </summary>
public sealed class SnapshotEncoder
{
    public const int MaxEntitiesPerPacket = 40;
    /// <summary>type, tick u32, ack u16, baseline u32 (<see cref="Wire.NoTick"/> when full), index u8, total u8, count u8.</summary>
    public const int HeaderBytes = 14;
    /// <summary>Largest record: a header (4 bytes), a keyframe's position, rotation and status (23 bytes).</summary>
    const int MaxRecordBytes = 32;

    readonly SnapshotHistory _history;
    // scratch reused every tick (T2-03): the records of the tick, and where each one ends
    byte[] _rec = new byte[2048];
    readonly List<int> _ends = new();
    readonly HashSet<ushort> _present = new();

    public long FullEntries, DeltaEntries;

    public SnapshotEncoder(SnapshotHistory? history = null) => _history = history ?? new SnapshotHistory();

    /// <summary>
    /// Encodes the entities for one client. <paramref name="baseTick"/> is the newest tick the client has acknowledged,
    /// or <see cref="Wire.NoTick"/>. The tick's state is kept in the history, which the baseline is looked up in.
    /// </summary>
    public List<byte[]> Encode(uint tick, ushort ack, uint baseTick, IReadOnlyList<EntityRecord> entities)
    {
        _history.Record(tick, entities);
        var b = baseTick != Wire.NoTick && baseTick < tick ? _history.Get(baseTick) : null;
        var headerBase = b != null ? baseTick : Wire.NoTick;

        _ends.Clear();
        _present.Clear();
        var o = 0;
        foreach (var q in entities)
        {
            _present.Add(q.Id);
            Reserve(ref o, MaxRecordBytes);
            var at = o;
            o += 4; // the record's header (id, type, flags) is filled once its fields are written
            byte flags;
            if (b != null && b.TryGetValue(q.Id, out var r) && r.Type == q.Type)
            {
                flags = WriteDelta(_rec, ref o, r, q);
                if (flags == 0) { o = at; continue; } // unchanged: omitted
                DeltaEntries++;
            }
            else
            {
                flags = Wire.FKey;
                WriteKeyframe(_rec, ref o, q);
                FullEntries++;
            }
            BinaryPrimitives.WriteUInt16LittleEndian(_rec.AsSpan(at), q.Id);
            _rec[at + 2] = (byte)q.Type;
            _rec[at + 3] = flags;
            _ends.Add(o);
        }
        // entities the baseline had and this tick does not: removals
        if (b != null)
            foreach (var kv in b)
            {
                if (_present.Contains(kv.Key)) continue;
                Reserve(ref o, 4);
                BinaryPrimitives.WriteUInt16LittleEndian(_rec.AsSpan(o), kv.Key);
                _rec[o + 2] = (byte)kv.Value.Type;
                _rec[o + 3] = Wire.FRemove;
                o += 4;
                _ends.Add(o);
            }

        // at least one packet per tick: it carries the ack, and the tick's completion for the client
        var n = _ends.Count;
        var total = Math.Max(1, (n + MaxEntitiesPerPacket - 1) / MaxEntitiesPerPacket);
        var packets = new List<byte[]>(total);
        for (var p = 0; p < total; p++)
        {
            var first = p * MaxEntitiesPerPacket;
            var last = Math.Min(n, first + MaxEntitiesPerPacket);
            var from = first == 0 ? 0 : _ends[first - 1];
            var to = last == 0 ? 0 : _ends[last - 1];
            packets.Add(Finish(_rec, from, to, tick, ack, headerBase, p, total, last - first));
        }
        return packets;
    }

    /// <summary>Writes the changes of <paramref name="q"/> against its baseline record, and returns their flags (0 when nothing changed).</summary>
    static byte WriteDelta(byte[] buf, ref int o, in EntityRecord r, in EntityRecord q)
    {
        byte flags = 0;
        int dx = q.X - r.X, dy = q.Y - r.Y, dz = q.Z - r.Z;
        if (dx != 0 || dy != 0 || dz != 0)
        {
            if (Math.Abs(dx) <= short.MaxValue && Math.Abs(dy) <= short.MaxValue && Math.Abs(dz) <= short.MaxValue)
            {
                flags |= Wire.FPosDelta;
                W16(buf, ref o, (short)dx); W16(buf, ref o, (short)dy); W16(buf, ref o, (short)dz);
            }
            else { flags |= Wire.FPosAbs; WritePosAbs(buf, ref o, q); }
        }
        if (q.Yaw != r.Yaw || q.Pitch != r.Pitch) { flags |= Wire.FRot; WriteRot(buf, ref o, q); }
        if (q.Health != r.Health || q.State != r.State || q.Stance != r.Stance || q.Weapon != r.Weapon) { flags |= Wire.FStatus; WriteStatus(buf, ref o, q); }
        return flags;
    }

    /// <summary>Writes a record's keyframe fields: absolute position, rotation and status.</summary>
    static void WriteKeyframe(byte[] buf, ref int o, in EntityRecord q)
    {
        WritePosAbs(buf, ref o, q); WriteRot(buf, ref o, q); WriteStatus(buf, ref o, q);
    }

    /// <summary>Makes room for <paramref name="need"/> more bytes at <paramref name="o"/>.</summary>
    void Reserve(ref int o, int need)
    {
        if (o + need <= _rec.Length) return;
        Array.Resize(ref _rec, Math.Max(_rec.Length * 2, o + need));
    }

    static byte[] Finish(byte[] rec, int from, int to, uint tick, ushort ack, uint baseTick, int index, int total, int count)
    {
        var pkt = new byte[HeaderBytes + (to - from)];
        pkt[0] = Wire.TSnapshot;
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(1), tick);
        BinaryPrimitives.WriteUInt16LittleEndian(pkt.AsSpan(5), ack);
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(7), baseTick);
        pkt[11] = (byte)index; pkt[12] = (byte)total; pkt[13] = (byte)count;
        Buffer.BlockCopy(rec, from, pkt, HeaderBytes, to - from);
        return pkt;
    }

    static void W16(byte[] b, ref int o, short v) { BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(o), v); o += 2; }
    static void W32(byte[] b, ref int o, int v) { BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v); o += 4; }
    static void WritePosAbs(byte[] b, ref int o, in EntityRecord q) { W32(b, ref o, q.X); W32(b, ref o, q.Y); W32(b, ref o, q.Z); }
    static void WriteRot(byte[] b, ref int o, in EntityRecord q) { W16(b, ref o, (short)q.Yaw); W16(b, ref o, q.Pitch); }
    static void WriteStatus(byte[] b, ref int o, in EntityRecord q) { b[o++] = q.Health; b[o++] = (byte)((byte)q.State | ((byte)q.Stance << 4)); b[o++] = q.Weapon; }
}

public readonly record struct SnapshotHeader(uint Tick, ushort Ack);

/// <summary>
/// Client-side snapshot decoder. A tick is committed only when all of its packets have arrived: each packet's records
/// are applied to a copy of the baseline tick's state, so the committed state is complete or absent, never half-applied.
/// <see cref="AckTick"/> (the newest committed tick) goes back to the server with the inputs.
/// </summary>
public sealed class SnapshotDecoder
{
    readonly SnapshotHistory _history = new();
    Dictionary<ushort, EntityRecord> _states = new();
    public IReadOnlyDictionary<ushort, EntityRecord> States => _states;
    public uint LastTick { get; private set; }
    /// <summary>True once a tick has been committed.</summary>
    public bool HasState { get; private set; }
    /// <summary>The ids that left in the last committed tick (for the caller to drop their presentation).</summary>
    public List<ushort> Removed { get; } = new();

    /// <summary>The tick to acknowledge with the inputs, or <see cref="Wire.NoTick"/> before the first commit.</summary>
    public uint AckTick => HasState ? LastTick : Wire.NoTick;

    /// <summary>Perf counters: ticks whose first packet was seen, and ticks that committed (all packets arrived). The difference is what the all-or-nothing tick loses.</summary>
    public int TicksStarted, TicksCompleted;
    // the tick being assembled from its packets
    bool _assembling;
    uint _asmTick;
    int _asmTotal;
    ulong _asmGot;
    Dictionary<ushort, EntityRecord>? _asm;

    readonly record struct Staged(ushort Id, bool Remove, EntityRecord Rec);

    /// <summary>
    /// Apply one snapshot packet. Returns true when this packet completed a tick, which is then committed (<see cref="States"/>
    /// and <see cref="Removed"/> change). False for a packet that is malformed, stale, a repeat, part of a tick still
    /// missing packets, or relative to a baseline this client no longer keeps.
    /// </summary>
    public bool Apply(ReadOnlySpan<byte> d, out SnapshotHeader header)
    {
        header = default;
        if (d.Length < SnapshotEncoder.HeaderBytes || d[0] != Wire.TSnapshot) return false;
        var tick = BinaryPrimitives.ReadUInt32LittleEndian(d[1..]);
        var ack = BinaryPrimitives.ReadUInt16LittleEndian(d[5..]);
        var baseTick = BinaryPrimitives.ReadUInt32LittleEndian(d[7..]);
        int index = d[11], total = d[12], count = d[13];
        header = new SnapshotHeader(tick, ack);
        if (HasState && tick <= LastTick) return false;
        if (total < 1 || total > 64 || index >= total) return false;

        if (!_assembling || tick > _asmTick)
        {
            Dictionary<ushort, EntityRecord> start;
            if (baseTick == Wire.NoTick) start = new Dictionary<ushort, EntityRecord>();
            else if (_history.Get(baseTick) is { } b) start = new Dictionary<ushort, EntityRecord>(b); // a copy: the history keeps its own
            else return false; // the baseline is gone: wait for a full set
            _asm = start; _asmTick = tick; _asmTotal = total; _asmGot = 0; _assembling = true; TicksStarted++;
        }
        else if (tick < _asmTick) return false; // older than the tick being assembled
        if (total != _asmTotal || (_asmGot & (1UL << index)) != 0) return false;

        // parse first: a malformed packet changes nothing
        var staged = new List<Staged>(count);
        var o = SnapshotEncoder.HeaderBytes;
        for (var i = 0; i < count; i++)
        {
            if (o + 4 > d.Length) return false;
            var id = BinaryPrimitives.ReadUInt16LittleEndian(d[o..]);
            var type = (EntityType)d[o + 2]; int flags = d[o + 3]; o += 4;
            if ((flags & Wire.FRemove) != 0) { staged.Add(new Staged(id, true, default)); continue; }
            var known = _asm!.TryGetValue(id, out var r) && r.Type == type;
            if (!known) r = new EntityRecord { Id = id, Type = type };
            // a delta for an entity the baseline does not have is meaningless: the packet is not usable
            if (!known && (flags & Wire.FKey) != Wire.FKey) return false;
            if ((flags & Wire.FPosAbs) != 0)
            {
                if (o + 12 > d.Length) return false;
                r.X = BinaryPrimitives.ReadInt32LittleEndian(d[o..]); r.Y = BinaryPrimitives.ReadInt32LittleEndian(d[(o + 4)..]); r.Z = BinaryPrimitives.ReadInt32LittleEndian(d[(o + 8)..]);
                o += 12;
            }
            if ((flags & Wire.FPosDelta) != 0)
            {
                if (o + 6 > d.Length) return false;
                r.X += BinaryPrimitives.ReadInt16LittleEndian(d[o..]); r.Y += BinaryPrimitives.ReadInt16LittleEndian(d[(o + 2)..]); r.Z += BinaryPrimitives.ReadInt16LittleEndian(d[(o + 4)..]);
                o += 6;
            }
            if ((flags & Wire.FRot) != 0)
            {
                if (o + 4 > d.Length) return false;
                r.Yaw = BinaryPrimitives.ReadUInt16LittleEndian(d[o..]); r.Pitch = BinaryPrimitives.ReadInt16LittleEndian(d[(o + 2)..]);
                o += 4;
            }
            if ((flags & Wire.FStatus) != 0)
            {
                if (o + 3 > d.Length) return false;
                r.Health = d[o]; r.State = (EntityState)(d[o + 1] & 0x0F); r.Stance = (Stance)(d[o + 1] >> 4); r.Weapon = d[o + 2];
                o += 3;
            }
            staged.Add(new Staged(id, false, r));
        }
        foreach (var s in staged)
        {
            if (s.Remove) _asm!.Remove(s.Id);
            else _asm![s.Id] = s.Rec;
        }
        _asmGot |= 1UL << index;
        if (_asmGot != (ulong.MaxValue >> (64 - _asmTotal))) return false;
        Commit(tick);
        TicksCompleted++;
        return true;
    }

    /// <summary>Forgets an entity at once, for a message that says it is gone (an explosion, a leave). Copies the state: the history keeps its own.</summary>
    public void Forget(ushort id)
    {
        if (!_states.ContainsKey(id)) return;
        _states = new Dictionary<ushort, EntityRecord>(_states);
        _states.Remove(id);
    }

    void Commit(uint tick)
    {
        Removed.Clear();
        foreach (var id in _states.Keys) if (!_asm!.ContainsKey(id)) Removed.Add(id);
        _states = _asm!; // the committed dictionary is never changed again: the history keeps it too
        _asm = null; _assembling = false;
        LastTick = tick; HasState = true;
        _history.Store(tick, _states);
    }
}
