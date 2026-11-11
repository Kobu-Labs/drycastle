using System.Buffers.Binary;
using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>Shot fired (broadcast, for remote effects). <see cref="Weapon"/> is the 0-based hotbar index; End is where the tracer stops.</summary>
public sealed record ShotMsg(ushort Shooter, byte Weapon, Vector3 Origin, Vector3 End);

/// <summary>Hit (to attacker and victim). Damage is what was dealt (before clamping to remaining health).</summary>
public sealed record HitMsg(ushort Victim, ushort Attacker, byte Weapon, ushort Damage, bool Headshot, bool Backstab, bool Kill);

/// <summary>Rocket launched (broadcast). Id matches the projectile entity in snapshots.</summary>
public sealed record LaunchMsg(ushort Id, ushort Owner, byte Weapon, Vector3 Origin, Vector3 Velocity);

/// <summary>Explosion (broadcast, for effects): where, which projectile and who fired it.</summary>
/// <summary>A rocket's explosion: its id, owner, weapon, centre and the blast radius it used (the launch's own, or weapons.json's).</summary>
public sealed record ExplosionMsg(ushort Id, ushort Owner, byte Weapon, Vector3 Position, float Radius);

/// <summary>A destroyed vehicle's blast (broadcast, for effects): vehicle entity id, attacker (0 = none), centre and radius in metres.</summary>
public sealed record VehicleExplosionMsg(ushort Id, ushort Attacker, Vector3 Position, float Radius);

/// <summary>Shotgun pellet fan (broadcast): one event for the whole fan. Aim is the unit aim direction; the pellets are derived from Seed and the weapon's pellet spread.</summary>
/// <summary>One shotgun shot. <c>Cone</c> is the fan half-angle in degrees the server used (the pellet spread after stance, aim and movement).</summary>
public sealed record PelletFanMsg(ushort Shooter, byte Weapon, Vector3 Origin, Vector3 Aim, uint Seed, float Cone);

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
///   Launch: u16 id, u16 owner, u8 weapon, f32 x3 origin, f32 x3 velocity
///   Explosion: u16 id, u16 owner, u8 weapon, f32 x3 position
///   PelletFan: u16 shooter, u8 weapon, f32 x3 origin, f32 x3 aim, u32 seed, f32 cone
///   Ammo: u8 slot, f32 reloadRemaining, u8 n, n * (u16 mag, u16 reserve)
///   Debug teleport (client to server, dev servers only): u8 2, u16 target, f32 x3 position
/// </summary>
public static class WeaponCodec
{
    public const byte DebugTeleport = 2, DebugVehicleDamage = 3;

    public static byte[] Encode(ShotMsg m)
    {
        var b = new byte[27];
        BinaryPrimitives.WriteUInt16LittleEndian(b, m.Shooter);
        b[2] = m.Weapon;
        Put(b, 3, m.Origin); Put(b, 15, m.End);
        return b;
    }

    public static ShotMsg? DecodeShot(byte[] d) => d.Length < 27 ? null : new ShotMsg(BinaryPrimitives.ReadUInt16LittleEndian(d), d[2], Get(d, 3), Get(d, 15));

    public static byte[] Encode(PelletFanMsg m)
    {
        var b = new byte[35];
        BinaryPrimitives.WriteUInt16LittleEndian(b, m.Shooter);
        b[2] = m.Weapon;
        Put(b, 3, m.Origin); Put(b, 15, m.Aim);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(27), m.Seed);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(31), m.Cone);
        return b;
    }

    public static PelletFanMsg? DecodePelletFan(byte[] d) => d.Length < 35 ? null
        : new PelletFanMsg(BinaryPrimitives.ReadUInt16LittleEndian(d), d[2], Get(d, 3), Get(d, 15), BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(27)), BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(31)));

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

    public static byte[] Encode(LaunchMsg m)
    {
        var b = new byte[29];
        BinaryPrimitives.WriteUInt16LittleEndian(b, m.Id);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), m.Owner);
        b[4] = m.Weapon;
        Put(b, 5, m.Origin); Put(b, 17, m.Velocity);
        return b;
    }

    public static LaunchMsg? DecodeLaunch(byte[] d) => d.Length < 29 ? null : new LaunchMsg(
        BinaryPrimitives.ReadUInt16LittleEndian(d), BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(2)), d[4], Get(d, 5), Get(d, 17));

    public static byte[] Encode(ExplosionMsg m)
    {
        var b = new byte[21];
        BinaryPrimitives.WriteUInt16LittleEndian(b, m.Id);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), m.Owner);
        b[4] = m.Weapon;
        Put(b, 5, m.Position);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(17), m.Radius);
        return b;
    }

    public static ExplosionMsg? DecodeExplosion(byte[] d) => d.Length < 21 ? null : new ExplosionMsg(
        BinaryPrimitives.ReadUInt16LittleEndian(d), BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(2)), d[4], Get(d, 5), BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(17)));

    public static byte[] Encode(VehicleExplosionMsg m)
    {
        var b = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(b, m.Id);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), m.Attacker);
        Put(b, 4, m.Position);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(16), m.Radius);
        return b;
    }

    public static VehicleExplosionMsg? DecodeVehicleExplosion(byte[] d) => d.Length < 20 ? null : new VehicleExplosionMsg(
        BinaryPrimitives.ReadUInt16LittleEndian(d), BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(2)), Get(d, 4), BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(16)));

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
        new((byte)w.Slot, w.ReloadRemaining(now), Enumerable.Range(1, w.WeaponCount).Select(s => ((ushort)w.Mag(s), (ushort)w.Reserve(s))).ToList());

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

    /// <summary>Debug command (dev servers and smokes only): hurt a vehicle by an amount, as if a weapon had.</summary>
    public static byte[] EncodeDebugVehicleDamage(ushort vehicle, float amount)
    {
        var b = new byte[7];
        b[0] = DebugVehicleDamage;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), vehicle);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(3), amount);
        return b;
    }

    public static (ushort Vehicle, float Amount)? DecodeDebugVehicleDamage(byte[] d) =>
        d.Length >= 7 && d[0] == DebugVehicleDamage ? (BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(1)), BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(3))) : null;

    static void Put(byte[] b, int o, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o), v.X);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o + 4), v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o + 8), v.Z);
    }

    static Vector3 Get(byte[] d, int o) => new(
        BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o)), BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o + 4)), BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o + 8)));
}
