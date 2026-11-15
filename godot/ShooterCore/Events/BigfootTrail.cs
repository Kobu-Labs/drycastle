using System.Buffers.Binary;
using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>One footprint of the trail: where it lies (Godot ground axes, y the terrain height) and which way the foot points.</summary>
public readonly record struct Footprint(Vector3 Position, float Yaw);

/// <summary>
/// The footsteps that lead from where the Bigfoot fell to the corpse that carries its head (issue #264). The server plans them once at the win and sends them
/// in one event (<see cref="EventKind.BigfootTrail"/>); every client draws them for as long as the corpse lasts. Godot-free: the ground is a function.
/// </summary>
public static class BigfootTrail
{
    /// <summary>Most footprints one trail has (the event carries the count in a byte, and the payload stays under 2 kB).</summary>
    public const int MaxPrints = 120;

    /// <summary>The foot's sideways offset from the line of the walk: the feet alternate.</summary>
    public const float StrideHalfWidthM = 0.5f;

    /// <summary>How far a trail wanders to each side of its straight line, and the length of one wave of it.</summary>
    public const float MeanderM = 3f, MeanderWaveM = 60f;

    /// <summary>
    /// Plans a trail from <paramref name="start"/>: tries <paramref name="tries"/> random headings and takes the one whose footprints run over ground for the
    /// longest, whole length if one does. The trail ends at its last footprint with ground; <paramref name="end"/> is that print's position (the corpse
    /// lies there). The result has at least two footprints, or none when no heading has ground for even that.
    /// </summary>
    public static List<Footprint> Plan(Vector3 start, float lengthM, float spacingM, int tries, Func<float, float, float?> groundY, Random rng, out Vector3 end)
    {
        List<Footprint> best = new();
        var wanted = Math.Clamp((int)MathF.Floor(lengthM / spacingM), 2, MaxPrints);
        for (var t = 0; t < tries; t++)
        {
            var heading = (float)rng.NextDouble() * MathF.Tau;
            var phase = (float)rng.NextDouble() * MathF.Tau;
            var dir = new Vector3(MathF.Cos(heading), 0, MathF.Sin(heading));
            var side = new Vector3(-dir.Z, 0, dir.X);
            // the line wanders a little (no ruler-straight trail), more as it gets away from the start
            Vector3 Line(float along) => start + dir * along + side * (MathF.Sin(phase + along / MeanderWaveM * MathF.Tau) * MeanderM * MathF.Min(1f, along / (MeanderWaveM * 0.5f)));
            var list = new List<Footprint>(wanted);
            for (var i = 1; i <= wanted; i++)
            {
                var along = i * spacingM;
                var heading2 = Line(along + 0.5f) - Line(along - 0.5f); // the foot points along the line there
                var foot = (i % 2 == 0 ? 1f : -1f) * StrideHalfWidthM;   // and the feet alternate about it
                var p = Line(along) + side * foot;
                if (groundY(p.X, p.Z) is not { } y) break;
                list.Add(new Footprint(new Vector3(p.X, y, p.Z), MathF.Atan2(-heading2.X, -heading2.Z)));
            }
            if (list.Count > best.Count) best = list;
            if (list.Count == wanted) break;
        }
        if (best.Count < 2) { end = start; return new List<Footprint>(); }
        end = best[^1].Position;
        return best;
    }

    /// <summary>The event payload: u8 count, then for each footprint i32 x, i32 y, i32 z (<see cref="Quant.Pos(float)"/>) and u16 yaw. A count of 0 clears the trail.</summary>
    public static byte[] Encode(IReadOnlyList<Footprint> prints)
    {
        var n = Math.Min(prints.Count, MaxPrints);
        var b = new byte[1 + n * PrintBytes];
        b[0] = (byte)n;
        for (var i = 0; i < n; i++)
        {
            var o = 1 + i * PrintBytes;
            var p = prints[i];
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), Quant.Pos(p.Position.X));
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 4), Quant.Pos(p.Position.Y));
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 8), Quant.Pos(p.Position.Z));
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 12), Quant.Yaw(p.Yaw));
        }
        return b;
    }

    public const int PrintBytes = 14;

    public static bool TryDecode(ReadOnlySpan<byte> d, out List<Footprint> prints)
    {
        prints = new List<Footprint>();
        if (d.Length < 1 || d.Length != 1 + d[0] * PrintBytes || d[0] > MaxPrints) return false;
        for (var i = 0; i < d[0]; i++)
        {
            var o = 1 + i * PrintBytes;
            var pos = new Vector3(Quant.Pos(BinaryPrimitives.ReadInt32LittleEndian(d[o..])), Quant.Pos(BinaryPrimitives.ReadInt32LittleEndian(d[(o + 4)..])), Quant.Pos(BinaryPrimitives.ReadInt32LittleEndian(d[(o + 8)..])));
            prints.Add(new Footprint(pos, Quant.Yaw(BinaryPrimitives.ReadUInt16LittleEndian(d[(o + 12)..]))));
        }
        return true;
    }
}

/// <summary>A one-shot effect of the Bigfoot as it travels: what, where, and the id (a player or a vehicle) and the extra byte some of them carry.</summary>
public readonly record struct BigfootFxMsg(BigfootFxKind Kind, Vector3 Position, ushort Id, byte Extra);

/// <summary>Payload of <see cref="EventKind.BigfootFx"/>: u8 kind, i32 x, i32 y, i32 z (<see cref="Quant.Pos(float)"/>), u16 id, u8 extra (16 bytes).</summary>
public static class BigfootFxCodec
{
    public const int Bytes = 16;

    public static byte[] Encode(BigfootFxMsg m)
    {
        var b = new byte[Bytes];
        b[0] = (byte)m.Kind;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(1), Quant.Pos(m.Position.X));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(5), Quant.Pos(m.Position.Y));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(9), Quant.Pos(m.Position.Z));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(13), m.Id);
        b[15] = m.Extra;
        return b;
    }

    public static BigfootFxMsg? Decode(ReadOnlySpan<byte> d)
    {
        if (d.Length != Bytes || !Enum.IsDefined((BigfootFxKind)d[0])) return null;
        var pos = new Vector3(Quant.Pos(BinaryPrimitives.ReadInt32LittleEndian(d[1..])), Quant.Pos(BinaryPrimitives.ReadInt32LittleEndian(d[5..])), Quant.Pos(BinaryPrimitives.ReadInt32LittleEndian(d[9..])));
        return new BigfootFxMsg((BigfootFxKind)d[0], pos, BinaryPrimitives.ReadUInt16LittleEndian(d[13..]), d[15]);
    }
}
