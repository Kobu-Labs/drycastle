using System.Buffers.Binary;
using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// The shot that killed a wild animal with a sniper rifle, for the hunter's hit diagram (GLOSSARY "Hit diagram"): the species, where the bullet
/// entered its body box (<see cref="Along"/> 0 at the tail to 1 at the nose, <see cref="Height"/> 0 at the feet to 1 at the top of the box,
/// <see cref="RightFlank"/> the side it came in by), how far the bullet flew, metres, and the weapon. Server to the shooter only.
/// </summary>
public readonly record struct HuntShotMsg(byte Species, float Along, float Height, bool RightFlank, ushort DistanceM, byte Weapon);

public static class HuntShot
{
    /// <summary>The message of a bullet that met <paramref name="target"/>'s body box at the world point <paramref name="point"/>.</summary>
    public static HuntShotMsg From(in HitTarget target, Vector3 point, int species, float distance, byte weapon)
    {
        var local = HitMath.ToBox(target, point);
        var along = target.Box.Z > 0 ? Math.Clamp(local.Z / target.Box.Z + 0.5f, 0f, 1f) : 0.5f;
        var height = target.Box.Y > 0 ? Math.Clamp(local.Y / target.Box.Y, 0f, 1f) : 0.5f;
        return new HuntShotMsg((byte)species, along, height, local.X >= 0, (ushort)Math.Clamp(MathF.Round(distance), 0, ushort.MaxValue), weapon);
    }

    /// <summary><see cref="EventKind.HuntShot"/>: u8 species, u8 along, u8 height (each 0..255 for 0..1), u8 flags (1 the right flank), u16 distance in metres, u8 weapon.</summary>
    public static byte[] Encode(HuntShotMsg m)
    {
        var b = new byte[7];
        b[0] = m.Species;
        b[1] = (byte)MathF.Round(Math.Clamp(m.Along, 0f, 1f) * 255f);
        b[2] = (byte)MathF.Round(Math.Clamp(m.Height, 0f, 1f) * 255f);
        b[3] = (byte)(m.RightFlank ? 1 : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), m.DistanceM);
        b[6] = m.Weapon;
        return b;
    }

    public static HuntShotMsg? Decode(byte[] d) => d.Length != 7 ? null
        : new HuntShotMsg(d[0], d[1] / 255f, d[2] / 255f, (d[3] & 1) != 0, BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(4)), d[6]);
}
