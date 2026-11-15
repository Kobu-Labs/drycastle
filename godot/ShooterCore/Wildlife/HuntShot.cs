using System.Buffers.Binary;
using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// The shot that killed a wild animal with a sniper rifle, for the hunter's hit diagram (GLOSSARY "Hit diagram"): the species, where the bullet
/// entered (<see cref="Along"/> 0 at the rear to 1 at the front, <see cref="Height"/> 0 at the ground to 1 at the top, of the hit shape's grid
/// when the animal had one, else of its body box), the flank it came in by, whether it met the head, how far the bullet flew, metres, and the
/// weapon. <see cref="Pose"/> and <see cref="Frame"/> name the hit shape the bullet was tested against, which the diagram draws
/// (<see cref="NoShape"/>: the body box), of the look <see cref="Variant"/> of the species. Server to the shooter only.
/// </summary>
public readonly record struct HuntShotMsg(byte Species, float Along, float Height, bool RightFlank, ushort DistanceM, byte Weapon, byte Pose = HuntShot.NoShape, byte Frame = 0, bool Head = false, byte Variant = 0);

public static class HuntShot
{
    /// <summary><see cref="HuntShotMsg.Pose"/> of an animal that was tested against its body box.</summary>
    public const byte NoShape = 255;

    /// <summary>The message of a bullet that met <paramref name="target"/> at the world point <paramref name="point"/>.</summary>
    public static HuntShotMsg From(in HitTarget target, Vector3 point, int species, float distance, byte weapon, bool head = false, int variant = 0)
    {
        var metres = (ushort)Math.Clamp(MathF.Round(distance), 0, ushort.MaxValue);
        if (target.Shape is { } shape)
        {
            var at = HitMath.ToFrame(target, point);
            return new HuntShotMsg((byte)species, Math.Clamp((at.Z - shape.Z0) / shape.Length, 0f, 1f), Math.Clamp(at.Y / shape.Height, 0f, 1f), at.X >= 0, metres, weapon,
                (byte)target.ShapePose, (byte)Math.Clamp(target.ShapeFrame, 0, 254), head, (byte)variant);
        }
        var local = HitMath.ToBox(target, point);
        var along = target.Box.Z > 0 ? Math.Clamp(local.Z / target.Box.Z + 0.5f, 0f, 1f) : 0.5f;
        var height = target.Box.Y > 0 ? Math.Clamp(local.Y / target.Box.Y, 0f, 1f) : 0.5f;
        return new HuntShotMsg((byte)species, along, height, local.X >= 0, metres, weapon, Variant: (byte)variant);
    }

    /// <summary><see cref="EventKind.HuntShot"/>: u8 species, u8 along, u8 height (each 0..255 for 0..1), u8 flags (1 the right flank, 2 the head), u16 distance in metres, u8 weapon, u8 pose, u8 frame, u8 variant (the look of the species).</summary>
    public static byte[] Encode(HuntShotMsg m)
    {
        var b = new byte[10];
        b[0] = m.Species;
        b[1] = (byte)MathF.Round(Math.Clamp(m.Along, 0f, 1f) * 255f);
        b[2] = (byte)MathF.Round(Math.Clamp(m.Height, 0f, 1f) * 255f);
        b[3] = (byte)((m.RightFlank ? 1 : 0) | (m.Head ? 2 : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), m.DistanceM);
        b[6] = m.Weapon;
        b[7] = m.Pose;
        b[8] = m.Frame;
        b[9] = m.Variant;
        return b;
    }

    public static HuntShotMsg? Decode(byte[] d) => d.Length != 10 ? null
        : new HuntShotMsg(d[0], d[1] / 255f, d[2] / 255f, (d[3] & 1) != 0, BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(4)), d[6], d[7], d[8], (d[3] & 2) != 0, d[9]);
}
