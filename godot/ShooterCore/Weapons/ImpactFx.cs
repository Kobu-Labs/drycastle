using System.Buffers.Binary;
using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>What a shot or shell hit, for the impact particles. Chosen on the server so every client draws the same thing.</summary>
public enum SurfaceKind : byte { Ground = 0, Road = 1, Building = 2, Water = 3, Metal = 4, Flesh = 5, Wood = 6, Foliage = 7 }

/// <summary>How big an impact burst is: a small puff, a burst, or a plume with a shockwave ring.</summary>
public enum ImpactSize : byte { Puff = 0, Burst = 1, Plume = 2 }

/// <summary>
/// The collider group of a hit that is not a body or a vehicle, from what the server knows about the point: whether it is on a
/// road, the kind of the parcel it is in, and how far above the ground it is. Ground-level hits are the terrain (no group).
/// </summary>
public static class ImpactGroups
{
    /// <summary>Height above the ground from which a hit is a structure or a prop, not the terrain (metres).</summary>
    public const float StructureHeightM = 0.5f;

    public static string? For(bool onRoad, string? parcelKind, float aboveGroundM)
    {
        if (onRoad) return "road";
        if (parcelKind == null || aboveGroundM < StructureHeightM) return null;
        return parcelKind switch
        {
            "building_plot" => "building",
            "forest" => "foliage",
            _ => "wood", // fences and the like in gardens, fields and other parcels
        };
    }
}

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
            "wood" => SurfaceKind.Wood,
            "foliage" => SurfaceKind.Foliage,
            _ => SurfaceKind.Ground,
        };
    }

    /// <summary>
    /// The burst size for a gunship round, by its blast radius: a shell or a blast of 10 m or more is a plume, 4 m or more a
    /// burst, smaller a puff. A round with no blast (<paramref name="splashRadius"/> 0) is sized by its damage.
    /// </summary>
    public static ImpactSize SizeFor(GunshipWeaponKind kind, float damage, float splashRadius)
    {
        if (kind == GunshipWeaponKind.Shell) return ImpactSize.Plume;
        if (kind == GunshipWeaponKind.Rocket) return ImpactSize.Burst;
        if (splashRadius >= 10f) return ImpactSize.Plume;
        if (splashRadius >= 4f) return ImpactSize.Burst;
        if (splashRadius > 0f) return ImpactSize.Puff;
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
        if (d[12] > (byte)SurfaceKind.Foliage || d[13] > (byte)ImpactSize.Plume) return null;
        return new ImpactInfo(at, (SurfaceKind)d[12], (ImpactSize)d[13]);
    }
}
