using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>
/// Target: a stationary target dummy (#148), drawn like a player with the target look. Turret: the aim of a vehicle's gun (<see cref="FlakRecord"/>),
/// one record per flak truck and per utility helicopter (its door gun). Flight: a flying vehicle's velocity, pitch, roll and rotor
/// (<see cref="FlightRecord"/>), one record per utility helicopter next to its Vehicle record.
/// </summary>
public enum EntityType : byte { Player = 0, Projectile = 1, Vehicle = 2, Target = 3, Turret = 4, Flight = 5 }

/// <summary>State byte of a snapshot entity.</summary>
public enum EntityState : byte { Alive = 0, Protected = 1, InVehicle = 2, Dead = 3 }

/// <summary>Quantisation shared by the wire format and prediction (client and server must agree bit for bit).</summary>
public static class Quant
{
    public const float PosScale = 128f; // 1/128 m units
    public const float TwoPi = MathF.PI * 2f;

    public static int Pos(float m) => (int)MathF.Round(m * PosScale);
    public static float Pos(int q) => q / PosScale;

    public static ushort Yaw(float rad)
    {
        var t = rad % TwoPi;
        if (t < 0) t += TwoPi;
        return (ushort)((int)MathF.Round(t / TwoPi * 65536f) & 0xFFFF);
    }
    public static float Yaw(ushort q) => q * TwoPi / 65536f;

    /// <summary>Pitch in -pi/2..pi/2 as a signed 16 bit fraction.</summary>
    public static short Pitch(float rad)
    {
        var f = Math.Clamp(rad / (MathF.PI / 2f), -1f, 1f);
        return (short)MathF.Round(f * 32767f);
    }
    public static float Pitch(short q) => q / 32767f * (MathF.PI / 2f);

    public static sbyte Axis(float a) => (sbyte)MathF.Round(Math.Clamp(a, -1f, 1f) * 127f);
    public static float Axis(sbyte q) => q / 127f;
}

/// <summary>One client input as it travels on the wire (already quantised).</summary>
public struct NetInput
{
    public ushort Seq;
    public sbyte MoveX, MoveY;
    public ushort Yaw;
    public short Pitch;
    public ushort Buttons;
    public byte Slot;
    /// <summary>
    /// True when the input carries the gunner's aim: a world point on the ground (#161), sent while a turret is steered. The
    /// coordinates are the quantised positions (<see cref="Quant.Pos"/>), so an input round-trips bit for bit.
    /// </summary>
    public bool HasAim;
    public int AimX, AimY, AimZ;

    /// <summary>The aim point in metres (only meaningful when <see cref="HasAim"/>).</summary>
    public readonly Vector3 Aim => new(Quant.Pos(AimX), Quant.Pos(AimY), Quant.Pos(AimZ));

    public static NetInput From(ushort seq, in MoveInput m, float pitch, byte slot, Vector3? aim = null)
    {
        var n = new NetInput
        {
            Seq = seq,
            MoveX = Quant.Axis(m.MoveX),
            MoveY = Quant.Axis(m.MoveY),
            Yaw = Quant.Yaw(m.Yaw),
            Pitch = Quant.Pitch(pitch),
            Buttons = (ushort)m.Buttons,
            Slot = slot,
        };
        if (aim is { } a)
        {
            n.HasAim = true;
            n.AimX = Quant.Pos(a.X); n.AimY = Quant.Pos(a.Y); n.AimZ = Quant.Pos(a.Z);
        }
        return n;
    }

    /// <summary>The movement input both sides simulate with (the dequantised values, so prediction matches the server).</summary>
    public readonly MoveInput ToMove(float speedScale = 1f) => new()
    {
        MoveX = Quant.Axis(MoveX),
        MoveY = Quant.Axis(MoveY),
        Yaw = Quant.Yaw(Yaw),
        Buttons = (MoveButtons)Buttons,
        SpeedScale = speedScale,
    };

    /// <summary>True if <paramref name="a"/> is newer than <paramref name="b"/> in wrapping 16 bit order.</summary>
    public static bool Newer(ushort a, ushort b) => (short)(a - b) > 0;
}

/// <summary>Quantised replicated state of one entity.</summary>
public struct EntityRecord : IEquatable<EntityRecord>
{
    public ushort Id;
    public EntityType Type;
    public int X, Y, Z;
    public ushort Yaw;
    public short Pitch;
    public byte Health;
    public EntityState State;
    /// <summary>Body stance, sent in the high nibble of the status byte with <see cref="State"/> (no extra bytes).</summary>
    public Stance Stance;
    public byte Weapon;

    public readonly Vector3 Position => new(Quant.Pos(X), Quant.Pos(Y), Quant.Pos(Z));
    public readonly float YawRad => Quant.Yaw(Yaw);
    public readonly float PitchRad => Quant.Pitch(Pitch);

    public static EntityRecord Make(ushort id, EntityType type, Vector3 pos, float yaw, float pitch, float health, EntityState state, byte weapon, Stance stance = Stance.Stand) => new()
    {
        Id = id, Type = type,
        X = Quant.Pos(pos.X), Y = Quant.Pos(pos.Y), Z = Quant.Pos(pos.Z),
        Yaw = Quant.Yaw(yaw), Pitch = Quant.Pitch(pitch),
        Health = (byte)Math.Clamp((int)MathF.Round(health), 0, 255),
        State = state, Stance = stance, Weapon = weapon,
    };

    public readonly bool Equals(EntityRecord o) =>
        Id == o.Id && Type == o.Type && X == o.X && Y == o.Y && Z == o.Z && Yaw == o.Yaw && Pitch == o.Pitch
        && Health == o.Health && State == o.State && Stance == o.Stance && Weapon == o.Weapon;
    public override readonly bool Equals(object? obj) => obj is EntityRecord r && Equals(r);
    public override readonly int GetHashCode() => HashCode.Combine(Id, X, Y, Z, Yaw, Pitch, Health);
}
