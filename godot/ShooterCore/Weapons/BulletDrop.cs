using System.Buffers.Binary;
using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// Bullet drop of the sniper rifles (a weapon with <c>muzzle_velocity</c>), shared by server and client. The shot is still resolved at once (no
/// travel time, so ADR 0013's rewind holds), but along a curve: the bullet falls under gravity as if it flew at the muzzle velocity, and the sight
/// is set to a <b>zero</b> distance at which the bullet crosses the line of sight. <see cref="Rise"/> is the bullet's height above that line after
/// <c>x</c> metres along it: above it short of the zero, on it at the zero, under it beyond. The rise is along the world's up whatever the pitch,
/// so the distance to a target is the zero to dial, uphill and downhill alike. The server walks the curve in straight <see cref="SegmentM"/> pieces
/// (the chord is under 5 mm off the curve). The player sets the zero in <see cref="ZeroStepM"/> steps (the scope's dial); a bot's shots do not drop.
/// </summary>
public static class BulletDrop
{
    public const float Gravity = 9.81f;
    /// <summary>Length of one straight piece of the curve along the line of sight, metres.</summary>
    public const float SegmentM = 25f;
    /// <summary>One click of the zero dial, the smallest zero and the zero of a weapon that names none, metres.</summary>
    public const float ZeroStepM = 10f, MinZeroM = 50f, DefaultZeroM = 100f;

    public static bool HasDrop(WeaponDef w) => w.Kind == WeaponKind.Hitscan && w.Pellets <= 1 && w.MuzzleVelocity > 0;

    /// <summary>The zero a weapon has before its dial is touched (and after a death).</summary>
    public static float DefaultZero(WeaponDef w) => ClampZero(w, w.ZeroDistance > 0 ? w.ZeroDistance : DefaultZeroM);

    /// <summary>The largest zero of a weapon: its range, down to a whole step.</summary>
    public static float MaxZero(WeaponDef w) => MathF.Max(MinZeroM, MathF.Floor((w.Range > 0 ? w.Range : 300f) / ZeroStepM) * ZeroStepM);

    /// <summary>A zero the dial can show: a whole step between <see cref="MinZeroM"/> and the weapon's range.</summary>
    public static float ClampZero(WeaponDef w, float metres) =>
        float.IsFinite(metres) ? Math.Clamp(MathF.Round(metres / ZeroStepM) * ZeroStepM, MinZeroM, MaxZero(w)) : DefaultZeroM;

    /// <summary>The zero after <paramref name="clicks"/> of the dial (positive: farther).</summary>
    public static float Dial(WeaponDef w, float zero, int clicks) => ClampZero(w, zero + clicks * ZeroStepM);

    /// <summary>Metres the bullet is above the line of sight (negative: under it) after <paramref name="x"/> metres along it.</summary>
    public static float Rise(WeaponDef w, float zero, float x) => Gravity / (2f * w.MuzzleVelocity * w.MuzzleVelocity) * x * (zero - x);

    /// <summary>Where the bullet is after <paramref name="x"/> metres along the line of sight from <paramref name="eye"/> along the unit <paramref name="aim"/>.</summary>
    public static Vector3 Point(Vector3 eye, Vector3 aim, WeaponDef w, float zero, float x) => eye + aim * x + Vector3.UnitY * Rise(w, zero, x);

    /// <summary>How far under the crosshair the bullet lands at <paramref name="x"/> metres, in mils (1 mil = 1 m at 1000 m); negative above it. The hold-over a mil-dot reticle reads.</summary>
    public static float HoldoverMils(WeaponDef w, float zero, float x) => x <= 0 ? 0 : -Rise(w, zero, x) / x * 1000f;

    /// <summary>Client to server, <see cref="EventKind.SetZero"/>: u8 weapon index, u16 zero in metres.</summary>
    public static byte[] EncodeZero(int weaponIndex, float zero)
    {
        var b = new byte[3];
        b[0] = (byte)weaponIndex;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), (ushort)Math.Clamp(MathF.Round(zero), 0, ushort.MaxValue));
        return b;
    }

    public static bool TryDecodeZero(byte[] payload, out int weaponIndex, out float zero)
    {
        weaponIndex = 0; zero = 0;
        if (payload.Length != 3) return false;
        weaponIndex = payload[0];
        zero = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(1));
        return true;
    }
}

/// <summary>The zero of each weapon a player has dialled this life (by weapon index); a weapon not dialled has its default. Server and client keep one each.</summary>
public sealed class ZeroDial
{
    readonly Dictionary<int, float> _zero = new();

    public float Of(int weaponIndex, WeaponDef w) => _zero.TryGetValue(weaponIndex, out var z) ? BulletDrop.ClampZero(w, z) : BulletDrop.DefaultZero(w);
    public void Set(int weaponIndex, WeaponDef w, float zero) => _zero[weaponIndex] = BulletDrop.ClampZero(w, zero);
    /// <summary>A death: every weapon is back at its default zero.</summary>
    public void Reset() => _zero.Clear();
}
