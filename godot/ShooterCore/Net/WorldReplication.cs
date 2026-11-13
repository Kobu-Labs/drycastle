using System.Buffers.Binary;

namespace Drycastle.ShooterCore;

/// <summary>
/// Wire side of the world entity channel. Lifecycle goes over the reliable channel as event envelopes
/// (<see cref="EventKind.WorldSpawn"/>, <see cref="EventKind.WorldDespawn"/>); state goes over the unreliable channel
/// as <see cref="Wire.TWorldSnapshot"/> packets with the same acknowledged-baseline rules as <see cref="SnapshotEncoder"/> (ADR 0002).
///
/// Snapshot packet: u8 type, u32 tick, u32 baseTick (<see cref="Wire.NoTick"/> for a full set), u8 index, u8 total, u8 count,
/// then per record: u16 id, u8 type, u8 flags, fields by flag:
///   PosAbs i32 x,y,z | PosDelta i16 dx,dy,dz | Rot u16 yaw, i16 pitch | Status u16 owner, u8 state | Hp u8 hp | Remove (no fields).
/// A keyframe carries PosAbs, Rot and Status (plus Hp for types with hp) and goes only to an entity new to the baseline.
/// Unchanged entities are omitted; an entity that left the baseline is a Remove record.
/// </summary>
public static class WorldCodec
{
    /// <summary>Full state: i32 x3, u16 yaw, i16 pitch, u16 owner, u8 state, u8 hp.</summary>
    public const int FullStateBytes = 20;

    static void WriteFull(Span<byte> s, WorldState w)
    {
        BinaryPrimitives.WriteInt32LittleEndian(s, w.X);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], w.Y);
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], w.Z);
        BinaryPrimitives.WriteUInt16LittleEndian(s[12..], w.Yaw);
        BinaryPrimitives.WriteInt16LittleEndian(s[14..], w.Pitch);
        BinaryPrimitives.WriteUInt16LittleEndian(s[16..], w.Owner);
        s[18] = w.State; s[19] = w.Hp;
    }

    static WorldState ReadFull(ReadOnlySpan<byte> s) => new(
        BinaryPrimitives.ReadInt32LittleEndian(s), BinaryPrimitives.ReadInt32LittleEndian(s[4..]), BinaryPrimitives.ReadInt32LittleEndian(s[8..]),
        BinaryPrimitives.ReadUInt16LittleEndian(s[12..]), BinaryPrimitives.ReadInt16LittleEndian(s[14..]),
        BinaryPrimitives.ReadUInt16LittleEndian(s[16..]), s[18], s[19]);

    /// <summary>EventKind.WorldSpawn payload: u16 id, u8 type, full state (the spawn carries the state, so a lost first keyframe does not hide it).</summary>
    public static byte[] EncodeSpawn(ushort id, byte type, WorldState s)
    {
        var b = new byte[3 + FullStateBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(b, id);
        b[2] = type;
        WriteFull(b.AsSpan(3), s);
        return b;
    }

    public static bool TryDecodeSpawn(ReadOnlySpan<byte> p, out ushort id, out byte type, out WorldState s)
    {
        id = 0; type = 0; s = default;
        if (p.Length != 3 + FullStateBytes) return false;
        id = BinaryPrimitives.ReadUInt16LittleEndian(p);
        type = p[2];
        s = ReadFull(p[3..]);
        return true;
    }

    /// <summary>EventKind.WorldDespawn payload: u16 id, u8 reason.</summary>
    public static byte[] EncodeDespawn(ushort id, WorldDespawnReason reason)
    {
        var b = new byte[3];
        BinaryPrimitives.WriteUInt16LittleEndian(b, id);
        b[2] = (byte)reason;
        return b;
    }

    public static bool TryDecodeDespawn(ReadOnlySpan<byte> p, out ushort id, out WorldDespawnReason reason)
    {
        id = 0; reason = WorldDespawnReason.Removed;
        if (p.Length != 3 || p[2] > (byte)WorldDespawnReason.Removed) return false;
        id = BinaryPrimitives.ReadUInt16LittleEndian(p);
        reason = (WorldDespawnReason)p[2];
        return true;
    }
}

/// <summary>
/// The complete world states of recent ticks: what the server sent (per client encoder) and what a client committed. The
/// world channel's counterpart of <see cref="SnapshotHistory"/>, with the same depth, so a baseline is kept as long as an entity one is.
/// </summary>
public sealed class WorldHistory
{
    /// <summary>Ticks kept. A client that acknowledges an older tick gets a full set instead.</summary>
    public const int Depth = SnapshotHistory.Depth;

    readonly Dictionary<uint, IReadOnlyDictionary<ushort, WorldEntry>> _byTick = new();

    public bool Has(uint tick) => _byTick.ContainsKey(tick);

    /// <summary>The complete state of a tick, or null when it is not kept.</summary>
    public IReadOnlyDictionary<ushort, WorldEntry>? Get(uint tick) => _byTick.GetValueOrDefault(tick);

    /// <summary>Keeps the state of a tick from its entity list (a tick already kept is left as it is).</summary>
    public void Record(uint tick, IReadOnlyList<WorldEntry> entities)
    {
        if (Has(tick)) return;
        var d = new Dictionary<ushort, WorldEntry>(entities.Count);
        foreach (var e in entities) d[e.Id] = e;
        Store(tick, d);
    }

    /// <summary>Keeps the state of a tick. The dictionary must not change afterwards.</summary>
    public void Store(uint tick, IReadOnlyDictionary<ushort, WorldEntry> states)
    {
        _byTick[tick] = states;
        if (_byTick.Count <= Depth) return;
        foreach (var k in _byTick.Keys.ToList()) if (k + Depth <= tick) _byTick.Remove(k);
    }
}

/// <summary>
/// Server side, one per client. Each tick's packets are relative to the newest world tick the client has acknowledged (its
/// baseline): an entity that changed since then carries its change, one that did not is omitted, one new to the baseline is a
/// keyframe, and one that left the baseline is a removal. Every packet of a tick carries the same baseline, so a lost packet
/// costs only the entities it held and the next tick repairs them against the same baseline. With no usable baseline (none
/// acknowledged, or it fell out of <see cref="WorldHistory"/>) the whole world goes in full. There are no periodic keyframes.
/// Spawn and despawn are reliable events of their own (<see cref="WorldCodec"/>); the removal record only keeps the baseline honest.
/// </summary>
public sealed class WorldSnapshotEncoder
{
    public const int MaxEntitiesPerPacket = 40;
    /// <summary>type, tick u32, baseTick u32, index u8, total u8, count u8.</summary>
    public const int HeaderBytes = 12;
    /// <summary>Largest record: a header (4 bytes), a keyframe's position, rotation, status and hp (24 bytes).</summary>
    const int MaxRecordBytes = 32;

    readonly WorldHistory _history;
    readonly WorldEntityRegistry _types;
    // the states of the baseline this client last diffed against. They are held by reference (a history dictionary never changes
    // once stored) and replaced from the history only when the client's ack moves to a tick the history has. While the ack does not
    // move, the stored states are used even after the history has dropped the tick: an idle world sends nothing, so the ack outlives
    // the 64-tick history, and a full set every so often would be wasted. Only a new client or a baseline the server does not have
    // (a reconnect) gets a full set.
    uint _keptTick = Wire.NoTick;
    IReadOnlyDictionary<ushort, WorldEntry>? _kept;
    // scratch reused every tick (T2-03), see SnapshotEncoder
    byte[] _rec = new byte[2048];
    readonly List<int> _ends = new();
    readonly HashSet<ushort> _present = new();

    public long FullEntries, DeltaEntries;

    /// <summary>The server shares one <paramref name="history"/> across its clients (the states are the same for everyone).</summary>
    public WorldSnapshotEncoder(WorldEntityRegistry? types = null, WorldHistory? history = null)
    {
        _types = types ?? WorldEntityRegistry.Shared;
        _history = history ?? new WorldHistory();
    }

    /// <summary>
    /// Encodes the world for one client. <paramref name="baseTick"/> is the newest world tick the client has acknowledged, or
    /// <see cref="Wire.NoTick"/>. The tick's state is kept in the history, which the baseline is looked up in. Returns no packet
    /// when nothing changed against the baseline.
    /// </summary>
    public List<byte[]> Encode(uint tick, uint baseTick, IReadOnlyList<WorldEntry> entities)
    {
        _history.Record(tick, entities);
        IReadOnlyDictionary<ushort, WorldEntry>? b = null;
        if (baseTick != Wire.NoTick && baseTick < tick)
        {
            b = baseTick == _keptTick ? _kept : _history.Get(baseTick);
            if (b != null) { _keptTick = baseTick; _kept = b; }
        }
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
            var hasHp = _types.Find(q.Type) is { HasHp: true };
            byte flags;
            if (b != null && b.TryGetValue(q.Id, out var r) && r.Type == q.Type)
            {
                flags = WriteDelta(_rec, ref o, r.State, q.State, hasHp);
                if (flags == 0) { o = at; continue; } // unchanged: omitted
                DeltaEntries++;
            }
            else
            {
                flags = (byte)(Wire.FKey | (hasHp ? Wire.FHp : 0));
                WritePos(_rec, ref o, q.State); WriteRot(_rec, ref o, q.State); WriteStatus(_rec, ref o, q.State);
                if (hasHp) _rec[o++] = q.State.Hp;
                FullEntries++;
            }
            BinaryPrimitives.WriteUInt16LittleEndian(_rec.AsSpan(at), q.Id);
            _rec[at + 2] = q.Type;
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
                _rec[o + 2] = kv.Value.Type;
                _rec[o + 3] = Wire.FRemove;
                o += 4;
                _ends.Add(o);
            }

        var n = _ends.Count;
        // nothing changed against the baseline: no packet. A full set always sends one, even when empty, because it is what empties the client's state
        if (n == 0 && b != null) return new List<byte[]>(0);
        var total = Math.Max(1, (n + MaxEntitiesPerPacket - 1) / MaxEntitiesPerPacket);
        var packets = new List<byte[]>(total);
        for (var p = 0; p < total; p++)
        {
            var first = p * MaxEntitiesPerPacket;
            var last = Math.Min(n, first + MaxEntitiesPerPacket);
            var from = first == 0 ? 0 : _ends[first - 1];
            var to = last == 0 ? 0 : _ends[last - 1];
            packets.Add(Finish(_rec, from, to, tick, headerBase, p, total, last - first));
        }
        return packets;
    }

    /// <summary>Writes the changes of <paramref name="s"/> against the baseline state <paramref name="r"/>, and returns their flags (0 when nothing changed).</summary>
    static byte WriteDelta(byte[] buf, ref int o, in WorldState r, in WorldState s, bool hasHp)
    {
        byte flags = 0;
        int dx = s.X - r.X, dy = s.Y - r.Y, dz = s.Z - r.Z;
        if (dx != 0 || dy != 0 || dz != 0)
        {
            if (Math.Abs(dx) <= short.MaxValue && Math.Abs(dy) <= short.MaxValue && Math.Abs(dz) <= short.MaxValue)
            {
                flags |= Wire.FPosDelta;
                W16(buf, ref o, (short)dx); W16(buf, ref o, (short)dy); W16(buf, ref o, (short)dz);
            }
            else { flags |= Wire.FPosAbs; WritePos(buf, ref o, s); }
        }
        if (s.Yaw != r.Yaw || s.Pitch != r.Pitch) { flags |= Wire.FRot; WriteRot(buf, ref o, s); }
        if (s.Owner != r.Owner || s.State != r.State) { flags |= Wire.FStatus; WriteStatus(buf, ref o, s); }
        if (hasHp && s.Hp != r.Hp) { flags |= Wire.FHp; buf[o++] = s.Hp; }
        return flags;
    }

    /// <summary>Makes room for <paramref name="need"/> more bytes at <paramref name="o"/>.</summary>
    void Reserve(ref int o, int need)
    {
        if (o + need <= _rec.Length) return;
        Array.Resize(ref _rec, Math.Max(_rec.Length * 2, o + need));
    }

    static byte[] Finish(byte[] rec, int from, int to, uint tick, uint baseTick, int index, int total, int count)
    {
        var pkt = new byte[HeaderBytes + (to - from)];
        pkt[0] = Wire.TWorldSnapshot;
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(1), tick);
        BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(5), baseTick);
        pkt[9] = (byte)index; pkt[10] = (byte)total; pkt[11] = (byte)count;
        Buffer.BlockCopy(rec, from, pkt, HeaderBytes, to - from);
        return pkt;
    }

    static void W16(byte[] b, ref int o, short v) { BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(o), v); o += 2; }
    static void W32(byte[] b, ref int o, int v) { BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v); o += 4; }
    static void WritePos(byte[] b, ref int o, WorldState s) { W32(b, ref o, s.X); W32(b, ref o, s.Y); W32(b, ref o, s.Z); }
    static void WriteRot(byte[] b, ref int o, WorldState s) { W16(b, ref o, (short)s.Yaw); W16(b, ref o, s.Pitch); }
    static void WriteStatus(byte[] b, ref int o, WorldState s)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), s.Owner); o += 2;
        b[o++] = s.State;
    }
}

/// <summary>
/// Client side of the world entity channel. Spawn creates an entity, despawn removes it and marks its id dead for the
/// rest of the match (ids are never reused), so a snapshot that was in flight across the despawn cannot bring it back.
/// State is assembled like <see cref="SnapshotDecoder"/>: a tick's packets are applied to a copy of its baseline tick's state, and
/// the tick commits only when all of its packets have arrived. <see cref="AckTick"/> (the newest committed tick) goes back with the
/// inputs. An entity's state is kept from its first keyframe, so one that beats its spawn event is picked up at the spawn.
/// Each entity keeps an <see cref="Interpolator"/>, which takes a sample for every committed tick that changed it.
/// </summary>
public sealed class WorldReplica
{
    public sealed class Entity
    {
        public ushort Id;
        public byte Type;
        public WorldState State;
        /// <summary>Samples at snapshot ticks. Read it at a render tick a little in the past with <see cref="Interpolator.At"/>.</summary>
        public readonly Interpolator Buf = new();
    }

    readonly Dictionary<ushort, Entity> _ents = new();
    readonly HashSet<ushort> _gone = new();
    readonly WorldHistory _history = new();
    IReadOnlyDictionary<ushort, WorldEntry> _states = new Dictionary<ushort, WorldEntry>();
    // the tick being assembled from its packets
    bool _assembling;
    uint _asmTick;
    int _asmTotal;
    ulong _asmGot;
    Dictionary<ushort, WorldEntry>? _asm;
    // the entities the tick being assembled changed (a tick's records name each id once)
    readonly List<ushort> _changed = new();

    readonly record struct Staged(ushort Id, bool Remove, WorldEntry Entry);

    public IReadOnlyDictionary<ushort, Entity> Entities => _ents;
    /// <summary>The committed state of every world entity the server has sent, spawned or not: what the next deltas are relative to.</summary>
    public IReadOnlyDictionary<ushort, WorldEntry> States => _states;
    public uint LastTick { get; private set; }
    /// <summary>True once a tick has been committed.</summary>
    public bool HasState { get; private set; }
    /// <summary>The world tick to acknowledge with the inputs, or <see cref="Wire.NoTick"/> before the first commit.</summary>
    public uint AckTick => HasState ? LastTick : Wire.NoTick;
    /// <summary>Ticks whose first packet was seen, and ticks that committed.</summary>
    public int TicksStarted, TicksCompleted;

    public event Action<Entity>? Spawned;
    public event Action<ushort>? Despawned;
    /// <summary>Raised just before <see cref="Despawned"/> with the server's reason, for a visual that depends on it (a shot-down aircraft explodes, an expired one just leaves).</summary>
    public event Action<ushort, WorldDespawnReason>? DespawnedFor;

    /// <summary>Handles a reliable event envelope. Ignores kinds other than the two world lifecycle kinds.</summary>
    public void OnEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.WorldSpawn when WorldCodec.TryDecodeSpawn(payload, out var id, out var type, out var s):
                Spawn(id, type, s);
                break;
            case EventKind.WorldDespawn when WorldCodec.TryDecodeDespawn(payload, out var gid, out var why):
                if (_ents.ContainsKey(gid)) DespawnedFor?.Invoke(gid, why);
                Despawn(gid);
                break;
        }
    }

    void Spawn(ushort id, byte type, WorldState s)
    {
        if (_gone.Contains(id) || _ents.ContainsKey(id)) return;
        // a keyframe that beat the spawn is newer than the spawn's own state
        if (_states.TryGetValue(id, out var known) && known.Type == type) s = known.State;
        var e = new Entity { Id = id, Type = type };
        _ents[id] = e;
        Store(e, LastTick, s);
        Spawned?.Invoke(e);
    }

    void Despawn(ushort id)
    {
        _gone.Add(id);
        if (_ents.Remove(id)) Despawned?.Invoke(id);
    }

    static void Store(Entity e, uint tick, WorldState s)
    {
        e.State = s;
        e.Buf.Push(tick, s.Position, s.YawRad, s.PitchRad);
    }

    /// <summary>
    /// Applies one <see cref="Wire.TWorldSnapshot"/> packet. Returns true when this packet completed a tick, which is then
    /// committed. False for a malformed packet, a stale or repeated one, a tick still missing packets, or a delta against a
    /// baseline this client no longer keeps.
    /// </summary>
    public bool Apply(ReadOnlySpan<byte> d)
    {
        if (d.Length < WorldSnapshotEncoder.HeaderBytes || d[0] != Wire.TWorldSnapshot) return false;
        var tick = BinaryPrimitives.ReadUInt32LittleEndian(d[1..]);
        var baseTick = BinaryPrimitives.ReadUInt32LittleEndian(d[5..]);
        int index = d[9], total = d[10], count = d[11];
        if (HasState && tick <= LastTick) return false;
        if (total < 1 || total > 64 || index >= total) return false;

        if (!_assembling || tick > _asmTick)
        {
            Dictionary<ushort, WorldEntry> start;
            if (baseTick == Wire.NoTick) start = new Dictionary<ushort, WorldEntry>();
            else if (_history.Get(baseTick) is { } b) start = new Dictionary<ushort, WorldEntry>(b); // a copy: the history keeps its own
            else return false; // the baseline is gone: wait for a full set
            _asm = start; _asmTick = tick; _asmTotal = total; _asmGot = 0; _assembling = true; _changed.Clear(); TicksStarted++;
        }
        else if (tick < _asmTick) return false; // older than the tick being assembled
        if (total != _asmTotal || (_asmGot & (1UL << index)) != 0) return false;

        // parse first: a malformed packet changes nothing
        var staged = new List<Staged>(count);
        var o = WorldSnapshotEncoder.HeaderBytes;
        for (var i = 0; i < count; i++)
        {
            if (o + 4 > d.Length) return false;
            var id = BinaryPrimitives.ReadUInt16LittleEndian(d[o..]);
            var type = d[o + 2]; int flags = d[o + 3]; o += 4;
            if ((flags & Wire.FRemove) != 0) { staged.Add(new Staged(id, true, default)); continue; }
            var known = _asm!.TryGetValue(id, out var prev) && prev.Type == type;
            // a delta for an entity the baseline does not have is meaningless: the packet is not usable
            if (!known && (flags & Wire.FKey) != Wire.FKey) return false;
            var s = known ? prev.State : default;
            if ((flags & Wire.FPosAbs) != 0)
            {
                if (o + 12 > d.Length) return false;
                s = s with { X = BinaryPrimitives.ReadInt32LittleEndian(d[o..]), Y = BinaryPrimitives.ReadInt32LittleEndian(d[(o + 4)..]), Z = BinaryPrimitives.ReadInt32LittleEndian(d[(o + 8)..]) };
                o += 12;
            }
            if ((flags & Wire.FPosDelta) != 0)
            {
                if (o + 6 > d.Length) return false;
                s = s with { X = s.X + BinaryPrimitives.ReadInt16LittleEndian(d[o..]), Y = s.Y + BinaryPrimitives.ReadInt16LittleEndian(d[(o + 2)..]), Z = s.Z + BinaryPrimitives.ReadInt16LittleEndian(d[(o + 4)..]) };
                o += 6;
            }
            if ((flags & Wire.FRot) != 0)
            {
                if (o + 4 > d.Length) return false;
                s = s with { Yaw = BinaryPrimitives.ReadUInt16LittleEndian(d[o..]), Pitch = BinaryPrimitives.ReadInt16LittleEndian(d[(o + 2)..]) };
                o += 4;
            }
            if ((flags & Wire.FStatus) != 0)
            {
                if (o + 3 > d.Length) return false;
                s = s with { Owner = BinaryPrimitives.ReadUInt16LittleEndian(d[o..]), State = d[o + 2] };
                o += 3;
            }
            if ((flags & Wire.FHp) != 0)
            {
                if (o + 1 > d.Length) return false;
                s = s with { Hp = d[o] };
                o++;
            }
            staged.Add(new Staged(id, false, new WorldEntry(id, type, s)));
        }
        foreach (var x in staged)
        {
            if (x.Remove) _asm!.Remove(x.Id);
            else { _asm![x.Id] = x.Entry; _changed.Add(x.Id); }
        }
        _asmGot |= 1UL << index;
        if (_asmGot != (ulong.MaxValue >> (64 - _asmTotal))) return false;
        Commit(tick);
        TicksCompleted++;
        return true;
    }

    void Commit(uint tick)
    {
        _states = _asm!; // the committed dictionary is never changed again: the history keeps it too
        _history.Store(tick, _states);
        _asm = null; _assembling = false;
        LastTick = tick; HasState = true;
        foreach (var id in _changed)
            if (_ents.TryGetValue(id, out var e)) Store(e, tick, _states[id].State);
    }
}
