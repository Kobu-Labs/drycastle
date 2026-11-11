using System.Buffers.Binary;

namespace Suchohrad.ShooterCore;

/// <summary>
/// Wire side of the world entity channel. Lifecycle goes over the reliable channel as event envelopes
/// (<see cref="EventKind.WorldSpawn"/>, <see cref="EventKind.WorldDespawn"/>); state goes over the unreliable channel
/// as <see cref="Wire.TWorldSnapshot"/> packets with the same keyframe and delta rules as ADR 0002.
///
/// Snapshot packet: u8 type, u32 tick, u8 count, then per entity: u16 id, u8 type, u8 flags, fields by flag:
///   PosAbs i32 x,y,z | PosDelta i16 dx,dy,dz | Rot u16 yaw, i16 pitch | Status u16 owner, u8 state | Hp u8 hp.
/// A keyframe carries PosAbs, Rot and Status (plus Hp for types with hp). Unchanged entities are omitted.
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
/// Server side, one per client: delta against the state last sent to that client, keyframe every
/// <see cref="Wire.KeyEvery"/> ticks (staggered for new entities). Like <see cref="SnapshotEncoder"/>, a lost packet
/// leaves the client wrong until the next keyframe (netcode issue #100).
/// </summary>
public sealed class WorldSnapshotEncoder
{
    public const int MaxEntitiesPerPacket = 40;
    const int HeaderBytes = 6;

    sealed class Base { public WorldEntry Rec; public long LastFull; }
    readonly Dictionary<ushort, Base> _last = new();
    readonly WorldEntityRegistry _types;
    // scratch reused every tick (T2-03), see SnapshotEncoder
    readonly byte[] _buf = new byte[1400];
    readonly HashSet<ushort> _present = new();
    readonly List<ushort> _gone = new();

    public WorldSnapshotEncoder(WorldEntityRegistry? types = null) => _types = types ?? WorldEntityRegistry.Shared;

    /// <summary>Encodes the live entities into one or more packets (split at <see cref="MaxEntitiesPerPacket"/>). No packets when nothing is live.</summary>
    public List<byte[]> Encode(uint tick, IReadOnlyList<WorldEntry> entities)
    {
        if (entities.Count == 0) { _last.Clear(); return new List<byte[]>(0); } // nothing live: no packet, no buffer (T2-03)
        _present.Clear();
        foreach (var e in entities) _present.Add(e.Id);
        _gone.Clear();
        foreach (var k in _last.Keys) if (!_present.Contains(k)) _gone.Add(k);
        foreach (var id in _gone) _last.Remove(id);

        var packets = new List<byte[]>();
        var buf = _buf;
        var o = HeaderBytes; var count = 0;
        foreach (var q in entities)
        {
            var hasHp = _types.Find(q.Type) is { HasHp: true };
            var hasBase = _last.TryGetValue(q.Id, out var b);
            var o0 = o;
            buf[o++] = (byte)q.Id; buf[o++] = (byte)(q.Id >> 8); buf[o++] = q.Type;
            var flagsAt = o++;
            byte flags;
            if (!hasBase || (long)tick - b!.LastFull >= Wire.KeyEvery)
            {
                flags = (byte)(Wire.FKey | (hasHp ? Wire.FHp : 0));
                WritePos(buf, ref o, q.State); WriteRot(buf, ref o, q.State); WriteStatus(buf, ref o, q.State);
                if (hasHp) buf[o++] = q.State.Hp;
                // stagger new entities' keyframes so they do not all land on one tick
                long lastFull = hasBase ? tick : (long)tick - (q.Id * 7) % Wire.KeyEvery;
                _last[q.Id] = new Base { Rec = q, LastFull = lastFull };
            }
            else
            {
                var r = b!.Rec.State;
                var s = q.State;
                flags = 0;
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
                if (flags == 0) { o = o0; continue; }
                b.Rec = q;
            }
            buf[flagsAt] = flags;
            if (++count >= MaxEntitiesPerPacket) { packets.Add(Finish(buf, o, tick, count)); o = HeaderBytes; count = 0; }
        }
        if (count > 0) packets.Add(Finish(buf, o, tick, count));
        return packets;
    }

    static byte[] Finish(byte[] buf, int len, uint tick, int count)
    {
        buf[0] = Wire.TWorldSnapshot;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1), tick);
        buf[5] = (byte)count;
        return buf.AsSpan(0, len).ToArray();
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
/// A keyframe that beats its spawn event is held until the spawn arrives. Each entity keeps an <see cref="Interpolator"/>.
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
    readonly Dictionary<ushort, (uint Tick, WorldState State)> _pending = new();
    readonly HashSet<ushort> _gone = new();

    public IReadOnlyDictionary<ushort, Entity> Entities => _ents;
    public uint LastTick { get; private set; }
    public event Action<Entity>? Spawned;
    public event Action<ushort>? Despawned;

    /// <summary>Handles a reliable event envelope. Ignores kinds other than the two world lifecycle kinds.</summary>
    public void OnEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.WorldSpawn when WorldCodec.TryDecodeSpawn(payload, out var id, out var type, out var s):
                Spawn(id, type, s);
                break;
            case EventKind.WorldDespawn when WorldCodec.TryDecodeDespawn(payload, out var gid, out _):
                Despawn(gid);
                break;
        }
    }

    void Spawn(ushort id, byte type, WorldState s)
    {
        if (_gone.Contains(id) || _ents.ContainsKey(id)) return;
        var e = new Entity { Id = id, Type = type };
        _ents[id] = e;
        Store(e, LastTick, s);
        if (_pending.Remove(id, out var k)) Store(e, k.Tick, k.State);
        Spawned?.Invoke(e);
    }

    void Despawn(ushort id)
    {
        _gone.Add(id);
        _pending.Remove(id);
        if (_ents.Remove(id)) Despawned?.Invoke(id);
    }

    static void Store(Entity e, uint tick, WorldState s)
    {
        e.State = s;
        e.Buf.Push(tick, s.Position, s.YawRad, s.PitchRad);
    }

    /// <summary>Applies one <see cref="Wire.TWorldSnapshot"/> packet. False if malformed or older than the last applied tick.</summary>
    public bool Apply(ReadOnlySpan<byte> d)
    {
        if (d.Length < 6 || d[0] != Wire.TWorldSnapshot) return false;
        var tick = BinaryPrimitives.ReadUInt32LittleEndian(d[1..]);
        if (tick < LastTick) return false;
        int count = d[5], o = 6;
        for (var i = 0; i < count; i++)
        {
            if (o + 4 > d.Length) return false;
            var id = BinaryPrimitives.ReadUInt16LittleEndian(d[o..]);
            int flags = d[o + 3]; o += 4;
            _ents.TryGetValue(id, out var e);
            // a delta is relative to the state we hold; for an unknown id it is meaningless and gets dropped below
            var s = e?.State ?? default;
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
            if (e != null) Store(e, tick, s);
            else if ((flags & Wire.FKey) == Wire.FKey && !_gone.Contains(id)) _pending[id] = (tick, s);
        }
        LastTick = tick;
        return true;
    }
}
