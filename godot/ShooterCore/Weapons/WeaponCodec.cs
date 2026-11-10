using System.Buffers.Binary;
using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>Shot fired (broadcast, for remote effects). <see cref="Weapon"/> is the 0-based hotbar index; End is where the tracer stops.</summary>
public sealed record ShotMsg(ushort Shooter, byte Weapon, Vector3 Origin, Vector3 End);

/// <summary>Hit (to attacker and victim). Damage is what was dealt (before clamping to remaining health).</summary>
public sealed record HitMsg(ushort Victim, ushort Attacker, byte Weapon, ushort Damage, bool Headshot, bool Backstab, bool Kill);

/// <summary>Owner-only ammo state: selected slot (1-based), seconds of reload left (0 = not reloading) and per slot magazine and reserve.</summary>
public sealed record AmmoMsg(byte Slot, float ReloadRemaining, IReadOnlyList<(ushort Mag, ushort Reserve)> Slots)
{
    public bool Equals(AmmoMsg? o) => o is not null && Slot == o.Slot && ReloadRemaining == o.ReloadRemaining && Slots.SequenceEqual(o.Slots);
    public override int GetHashCode() => HashCode.Combine(Slot, ReloadRemaining, Slots.Count);
}

/// <summary>
/// Payload codecs for the weapon event kinds (little-endian).
///   Shot: u16 shooter, u8 weapon, f32 x3 origin, f32 x3 end
///   Hit:  u16 victim, u16 attacker, u8 weapon, u16 damage, u8 flags (1 headshot, 2 backstab, 4 kill)
///   Ammo: u8 slot, f32 reloadRemaining, u8 n, n * (u16 mag, u16 reserve)
///   Debug teleport (client to server, dev servers only): u8 2, u16 target, f32 x3 position
/// </summary>
public static class WeaponCodec
{
    public const byte DebugTeleport = 2;

    public static byte[] Encode(ShotMsg m)
    {
        var b = new byte[27];
        BinaryPrimitives.WriteUInt16LittleEndian(b, m.Shooter);
        b[2] = m.Weapon;
        Put(b, 3, m.Origin); Put(b, 15, m.End);
        return b;
    }

    public static ShotMsg? DecodeShot(byte[] d) => d.Length < 27 ? null : new ShotMsg(BinaryPrimitives.ReadUInt16LittleEndian(d), d[2], Get(d, 3), Get(d, 15));

    public static byte[] Encode(HitMsg m)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(b, m.Victim);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), m.Attacker);
        b[4] = m.Weapon;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(5), m.Damage);
        b[7] = (byte)((m.Headshot ? 1 : 0) | (m.Backstab ? 2 : 0) | (m.Kill ? 4 : 0));
        return b;
    }

    public static HitMsg? DecodeHit(byte[] d) => d.Length < 8 ? null : new HitMsg(
        BinaryPrimitives.ReadUInt16LittleEndian(d), BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(2)), d[4],
        BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(5)), (d[7] & 1) != 0, (d[7] & 2) != 0, (d[7] & 4) != 0);

    public static byte[] Encode(AmmoMsg m)
    {
        var b = new byte[6 + m.Slots.Count * 4];
        b[0] = m.Slot;
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(1), m.ReloadRemaining);
        b[5] = (byte)m.Slots.Count;
        for (var i = 0; i < m.Slots.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6 + i * 4), m.Slots[i].Mag);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8 + i * 4), m.Slots[i].Reserve);
        }
        return b;
    }

    public static AmmoMsg? DecodeAmmo(byte[] d)
    {
        if (d.Length < 6 || d.Length < 6 + d[5] * 4) return null;
        var slots = new List<(ushort, ushort)>();
        for (var i = 0; i < d[5]; i++)
            slots.Add((BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(6 + i * 4)), BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(8 + i * 4))));
        return new AmmoMsg(d[0], BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(1)), slots);
    }

    public static AmmoMsg BuildAmmo(PlayerWeapons w, double now) =>
        new((byte)w.Slot, w.ReloadRemaining(now), Enumerable.Range(1, w.Hotbar.Count).Select(s => ((ushort)w.Mag(s), (ushort)w.Reserve(s))).ToList());

    public static byte[] EncodeDebugTeleport(ushort target, Vector3 pos)
    {
        var b = new byte[15];
        b[0] = DebugTeleport;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), target);
        Put(b, 3, pos);
        return b;
    }

    public static (ushort Target, Vector3 Pos)? DecodeDebugTeleport(byte[] d) =>
        d.Length >= 15 && d[0] == DebugTeleport ? (BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(1)), Get(d, 3)) : null;

    static void Put(byte[] b, int o, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o), v.X);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o + 4), v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o + 8), v.Z);
    }

    static Vector3 Get(byte[] d, int o) => new(
        BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o)), BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o + 4)), BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o + 8)));
}
