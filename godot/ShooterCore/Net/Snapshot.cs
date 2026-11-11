using System.Buffers.Binary;

namespace Suchohrad.ShooterCore;

/// <summary>Per-client snapshot encoder: delta against the last state sent to that client, keyframe every <see cref="Wire.KeyEvery"/> ticks.</summary>
public sealed class SnapshotEncoder
{
    public const int MaxEntitiesPerPacket = 40;
    const int HeaderBytes = 8;

    sealed class Base { public EntityRecord Rec; public long LastFull; }
    readonly Dictionary<ushort, Base> _last = new();

    public long FullEntries, DeltaEntries;

    /// <summary>Encode the entities visible to this client into one or more packets (split at 40 entities).</summary>
    public List<byte[]> Encode(uint tick, ushort ack, IReadOnlyList<EntityRecord> entities)
    {
        var present = new HashSet<ushort>();
        foreach (var e in entities) present.Add(e.Id);
        foreach (var id in _last.Keys.Where(k => !present.Contains(k)).ToList()) _last.Remove(id);

        var packets = new List<byte[]>();
        var buf = new byte[1400];
        var o = HeaderBytes; var count = 0;
        foreach (var q in entities)
        {
            var hasBase = _last.TryGetValue(q.Id, out var b);
            var o0 = o;
            buf[o++] = (byte)q.Id; buf[o++] = (byte)(q.Id >> 8); buf[o++] = (byte)q.Type;
            var flagsAt = o++;
            byte flags;
            if (!hasBase || (long)tick - b!.LastFull >= Wire.KeyEvery)
            {
                flags = (byte)(Wire.FPosAbs | Wire.FRot | Wire.FStatus);
                WritePosAbs(buf, ref o, q); WriteRot(buf, ref o, q); WriteStatus(buf, ref o, q);
                // stagger new entities' keyframes so they do not all land on one tick
                long lastFull = hasBase ? tick : (long)tick - (q.Id * 7) % Wire.KeyEvery;
                _last[q.Id] = new Base { Rec = q, LastFull = lastFull };
                FullEntries++;
            }
            else
            {
                var r = b!.Rec;
                flags = 0;
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
                if (flags == 0) { o = o0; continue; }
                b.Rec = q;
                DeltaEntries++;
            }
            buf[flagsAt] = flags;
            if (++count >= MaxEntitiesPerPacket) { packets.Add(Finish(buf, o, tick, ack, count)); o = HeaderBytes; count = 0; }
        }
        // an empty snapshot still carries the ack, so the client can drop acknowledged inputs
        if (count > 0 || packets.Count == 0) packets.Add(Finish(buf, o, tick, ack, count));
        return packets;
    }

    static byte[] Finish(byte[] buf, int len, uint tick, ushort ack, int count)
    {
        buf[0] = Wire.TSnapshot;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(1), tick);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(5), ack);
        buf[7] = (byte)count;
        return buf.AsSpan(0, len).ToArray();
    }

    static void W16(byte[] b, ref int o, short v) { BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(o), v); o += 2; }
    static void W32(byte[] b, ref int o, int v) { BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v); o += 4; }
    static void WritePosAbs(byte[] b, ref int o, in EntityRecord q) { W32(b, ref o, q.X); W32(b, ref o, q.Y); W32(b, ref o, q.Z); }
    static void WriteRot(byte[] b, ref int o, in EntityRecord q) { W16(b, ref o, (short)q.Yaw); W16(b, ref o, q.Pitch); }
    static void WriteStatus(byte[] b, ref int o, in EntityRecord q) { b[o++] = q.Health; b[o++] = (byte)((byte)q.State | ((byte)q.Stance << 4)); b[o++] = q.Weapon; }
}

public readonly record struct SnapshotHeader(uint Tick, ushort Ack);

/// <summary>Client-side snapshot decoder: applies partial records to the last known full state of each entity.</summary>
public sealed class SnapshotDecoder
{
    readonly Dictionary<ushort, EntityRecord> _states = new();
    public IReadOnlyDictionary<ushort, EntityRecord> States => _states;
    public uint LastTick { get; private set; }

    readonly Dictionary<ushort, uint> _seen = new();

    public void Remove(ushort id) { _states.Remove(id); _seen.Remove(id); }

    /// <summary>Forget entities not updated for <paramref name="maxAge"/> ticks (keyframes arrive every 30, so they are gone).</summary>
    public List<ushort> RemoveStale(uint tick, uint maxAge)
    {
        var gone = _seen.Where(kv => tick > kv.Value && tick - kv.Value > maxAge).Select(kv => kv.Key).ToList();
        foreach (var id in gone) Remove(id);
        return gone;
    }

    /// <summary>Apply one snapshot packet. Returns false if malformed.</summary>
    public bool Apply(ReadOnlySpan<byte> d, out SnapshotHeader header)
    {
        header = default;
        if (d.Length < 8 || d[0] != Wire.TSnapshot) return false;
        var tick = BinaryPrimitives.ReadUInt32LittleEndian(d[1..]);
        var ack = BinaryPrimitives.ReadUInt16LittleEndian(d[5..]);
        int count = d[7], o = 8;
        var staged = new List<(EntityRecord rec, bool known, byte flags)>(count);
        for (var i = 0; i < count; i++)
        {
            if (o + 4 > d.Length) return false;
            var id = BinaryPrimitives.ReadUInt16LittleEndian(d[o..]);
            var type = (EntityType)d[o + 2]; int flags = d[o + 3]; o += 4;
            var known = _states.TryGetValue(id, out var r);
            if (!known) r = new EntityRecord { Id = id, Type = type };
            r.Type = type;
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
            staged.Add((r, known, (byte)flags));
        }
        foreach (var (rec, known, flags) in staged)
        {
            // a delta for an entity we never saw a keyframe for is meaningless: wait for the next keyframe
            if (!known && (flags & Wire.FPosAbs) == 0) continue;
            _states[rec.Id] = rec;
            _seen[rec.Id] = tick;
        }
        LastTick = tick;
        header = new SnapshotHeader(tick, ack);
        return true;
    }
}
