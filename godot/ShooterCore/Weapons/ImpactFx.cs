using System.Buffers.Binary;
using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>What a shot or shell hit, for the impact particles. Chosen on the server so every client draws the same thing.</summary>
public enum SurfaceKind : byte { Ground = 0, Road = 1, Building = 2, Water = 3, Metal = 4, Flesh = 5 }

/// <summary>How big an impact burst is: a small puff, a burst, or a plume with a shockwave ring.</summary>
public enum ImpactSize : byte { Puff = 0, Burst = 1, Plume = 2 }

public static class SurfaceClassifier
{
    /// <summary>
    /// The surface of a hit. A body (a player or a target dummy) wins, then a vehicle (metal), then water, then the collider's
    /// group ("road", "building"; anything else, including no group, is ground).
    /// </summary>
    public static SurfaceKind Classify(bool body, bool vehicle, bool inWater, string? group)
    {
        if (body) return SurfaceKind.Flesh;
        if (vehicle) return SurfaceKind.Metal;
        if (inWater) return SurfaceKind.Water;
        return group switch
        {
            "road" => SurfaceKind.Road,
            "building" or "wall" => SurfaceKind.Building,
            _ => SurfaceKind.Ground,
        };
    }

    /// <summary>The burst size for a gunship round: a 25 mm-class round (up to 25 damage) is a puff, a 40 mm-class one a burst, shells a plume.</summary>
    public static ImpactSize SizeFor(GunshipWeaponKind kind, float damage)
    {
        if (kind == GunshipWeaponKind.Shell) return ImpactSize.Plume;
        if (kind == GunshipWeaponKind.Rocket) return ImpactSize.Burst;
        return damage <= 25f ? ImpactSize.Puff : ImpactSize.Burst;
    }
}

/// <summary>An impact: where, what surface, and how big. Payload of <see cref="EventKind.ImpactFx"/>.</summary>
public readonly record struct ImpactInfo(Vector3 At, SurfaceKind Surface, ImpactSize Size);

/// <summary>EventKind.ImpactFx payload: three f32 point, u8 surface, u8 size.</summary>
public static class ImpactCodec
{
    public const int Bytes = 14;

    public static byte[] Encode(ImpactInfo i)
    {
        var b = new byte[Bytes];
        BinaryPrimitives.WriteSingleLittleEndian(b, i.At.X);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4), i.At.Y);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8), i.At.Z);
        b[12] = (byte)i.Surface;
        b[13] = (byte)i.Size;
        return b;
    }

    public static ImpactInfo? Decode(ReadOnlySpan<byte> d)
    {
        if (d.Length != Bytes) return null;
        var at = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(d), BinaryPrimitives.ReadSingleLittleEndian(d.Slice(4)), BinaryPrimitives.ReadSingleLittleEndian(d.Slice(8)));
        if (d[12] > (byte)SurfaceKind.Flesh || d[13] > (byte)ImpactSize.Plume) return null;
        return new ImpactInfo(at, (SurfaceKind)d[12], (ImpactSize)d[13]);
    }
}
