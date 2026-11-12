using System.Buffers.Binary;
using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>An aircraft as the flak fuse sees it: its entity id and where it will be a number of seconds from now (its orbit is known).</summary>
public readonly record struct FlakTarget(ushort Id, Func<float, Vector3> PositionAfter);

/// <summary>How a shell's flight ends: it ran out of range, it met the ground (or a body, or a vehicle), or its fuse burst it by an aircraft.</summary>
public enum FlakEnd : byte { Expired, Ground, Burst }

/// <summary>Where and when a shell ends, worked out at the moment it is fired. <see cref="Seconds"/> is the flight time to <see cref="At"/>.</summary>
public readonly record struct FlakPlan(FlakEnd End, float Seconds, float Distance, Vector3 At);

/// <summary>
/// The rules of the flak truck's turret (<see cref="VehicleFlakDef"/>), pure so the tests run the server's own arithmetic. The gunner's view is the
/// aim, clamped to the barrels' elevation limits. A shell flies a straight line at a high speed, so the server works out its whole flight when it is
/// fired (<see cref="Plan"/>): the fuse marches the shell and the aircraft along their paths and bursts it at the closest approach once it is within
/// the proximity radius; otherwise the shell ends where it meets the ground, or at its range. The damage lands when the shell arrives.
/// </summary>
public static class FlakRules
{
    /// <summary>The step of the fuse march, seconds: at a shell speed of 700 m/s a step is under 6 m, well inside a 12 m fuse.</summary>
    public const float FuseStepS = 1f / 120f;

    const float Deg2Rad = MathF.PI / 180f;

    /// <summary>The aim pitch (radians, positive up) limited to the barrels' elevation range.</summary>
    public static float ClampPitch(VehicleFlakDef def, float pitchRad) => Math.Clamp(pitchRad, def.MinElevationDeg * Deg2Rad, def.MaxElevationDeg * Deg2Rad);

    /// <summary>An angle wrapped into -pi..pi (the turret's yaw against the chassis).</summary>
    public static float Wrap(float a)
    {
        a %= Quant.TwoPi;
        if (a > MathF.PI) a -= Quant.TwoPi;
        else if (a < -MathF.PI) a += Quant.TwoPi;
        return a;
    }

    /// <summary>Where the gun cradle pivots in the world: the def's pivot in the vehicle's frame.</summary>
    public static Vector3 PivotWorld(VehicleFlakDef def, Vector3 carPos, float carYaw) => VehicleSeat.SeatPosition(carPos, carYaw, def.Pivot);

    /// <summary>
    /// The muzzle tip a round leaves: the pivot plus the barrel length along the aim, and the barrel's side (<paramref name="barrel"/> 0 is the left
    /// one, 1 the right; the rounds alternate). The side is horizontal, along the turret's right.
    /// </summary>
    public static Vector3 Muzzle(VehicleFlakDef def, Vector3 carPos, float carYaw, float aimYaw, Vector3 aimDir, int barrel)
    {
        var right = new Vector3(MathF.Cos(aimYaw), 0, -MathF.Sin(aimYaw));
        var side = (barrel & 1) == 0 ? -def.BarrelSpacing : def.BarrelSpacing;
        return PivotWorld(def, carPos, carYaw) + aimDir * def.MuzzleLength + right * side;
    }

    /// <summary>
    /// A gunner seat's offset once the turret has turned <paramref name="turretYaw"/> against the chassis: the seat sits on the turret, so its offset from the
    /// turret's axis (the def's pivot x and z) is rotated by it, as <see cref="VehicleSeat.SeatPosition"/> rotates by a vehicle's yaw.
    /// </summary>
    public static float[] TurretSeatOffset(VehicleFlakDef def, float[] seat, float turretYaw)
    {
        var rx = seat[0] - def.Pivot[0];
        var rz = seat[2] - def.Pivot[2];
        var (s, c) = MathF.SinCos(turretYaw);
        return new[] { def.Pivot[0] + rx * c + rz * s, seat[1], def.Pivot[2] - rx * s + rz * c };
    }

    /// <summary>An airburst's damage to an aircraft <paramref name="distance"/> metres from the burst: full to the full-damage radius, linear to the edge damage at the burst radius, none beyond.</summary>
    public static float BurstDamage(VehicleFlakDef def, float distance)
    {
        if (distance >= def.BurstRadius) return 0;
        if (distance <= def.BurstFullRadius) return def.Damage;
        var t = (distance - def.BurstFullRadius) / MathF.Max(1e-4f, def.BurstRadius - def.BurstFullRadius);
        return MathF.Round(def.Damage + (def.EdgeDamage - def.Damage) * t);
    }

    /// <summary>
    /// The flight of a shell fired from <paramref name="origin"/> along unit <paramref name="dir"/>. <paramref name="stopDistance"/> is where it would meet
    /// the ground, a body or a vehicle (infinity if nothing). The shell and each aircraft are marched together in <see cref="FuseStepS"/> steps; once the
    /// shell is within the proximity radius of an aircraft, it bursts at the closest approach (the first step the gap stops closing). A shell that meets
    /// no aircraft ends on the ground at <paramref name="stopDistance"/>, or expires at its range.
    /// </summary>
    public static FlakPlan Plan(VehicleFlakDef def, Vector3 origin, Vector3 dir, float stopDistance, IReadOnlyList<FlakTarget> aircraft)
    {
        var reach = MathF.Min(def.MaxRange, stopDistance);
        var tEnd = reach / def.ShellSpeed;
        var inside = false;
        var bestGap = float.PositiveInfinity;
        var bestT = 0f;
        if (aircraft.Count > 0)
        {
            for (var t = 0f; t <= tEnd; t += FuseStepS)
            {
                var p = origin + dir * (def.ShellSpeed * t);
                var gap = float.PositiveInfinity;
                foreach (var a in aircraft) gap = MathF.Min(gap, Vector3.Distance(p, a.PositionAfter(t)));
                if (gap <= def.ProximityRadius)
                {
                    inside = true;
                    if (gap <= bestGap) { bestGap = gap; bestT = t; }
                    else break; // the gap is opening again: the closest approach was the step before
                }
                else if (inside) break;
            }
        }
        if (inside)
        {
            var d = def.ShellSpeed * bestT;
            return new FlakPlan(FlakEnd.Burst, bestT, d, origin + dir * d);
        }
        if (stopDistance <= def.MaxRange) return new FlakPlan(FlakEnd.Ground, stopDistance / def.ShellSpeed, stopDistance, origin + dir * stopDistance);
        return new FlakPlan(FlakEnd.Expired, def.MaxRange / def.ShellSpeed, def.MaxRange, origin + dir * def.MaxRange);
    }
}

/// <summary>
/// How a vehicle gun's aim travels in a snapshot entity record (type <see cref="EntityType.Turret"/>), one per flak vehicle and one per vehicle with a door
/// gun (there the yaw and pitch are the mount's and the gun's against the vehicle, <see cref="DoorGunRules"/>). For a flak: the record's yaw and pitch are the
/// turret's world aim (so everyone animates the turret and the barrels), the position is fixed at the origin (so it is never re-sent), and the gunner's
/// player id (0 = nobody) is split over the health and weapon bytes. The record id is the vehicle's index in the id range <see cref="FirstId"/>.
/// </summary>
public static class FlakRecord
{
    /// <summary>First turret record id: above the vehicles (0x4000 + index) and below the rockets (0x8000).</summary>
    public const ushort FirstId = 0x7000;

    public static ushort IdOf(ushort carId) => (ushort)(FirstId + (carId - VehicleRecord.FirstId));
    public static ushort CarOf(ushort recordId) => (ushort)(VehicleRecord.FirstId + (recordId - FirstId));

    public static EntityRecord Make(ushort carId, ushort gunner, float aimYaw, float aimPitch)
    {
        var r = EntityRecord.Make(IdOf(carId), EntityType.Turret, Vector3.Zero, aimYaw, aimPitch, 0, gunner != 0 ? EntityState.InVehicle : EntityState.Alive, 0);
        r.Health = (byte)(gunner & 0xFF);
        r.Weapon = (byte)(gunner >> 8);
        return r;
    }

    public static ushort Gunner(in EntityRecord r) => (ushort)(r.Health | (r.Weapon << 8));
}

/// <summary>What the flak gunner's HUD shows (event <see cref="EventKind.FlakStatus"/>, to the gunner only): rounds in the magazine and the reserve, and the seconds of reload left.</summary>
public readonly record struct FlakStatus(ushort Magazine, ushort Reserve, float ReloadRemaining);

/// <summary>Payload of <see cref="EventKind.FlakStatus"/>: u16 magazine, u16 reserve, f32 reload seconds left (0 = not reloading), little-endian.</summary>
public static class FlakCodec
{
    public const int Bytes = 8;

    public static byte[] Encode(FlakStatus s)
    {
        var b = new byte[Bytes];
        BinaryPrimitives.WriteUInt16LittleEndian(b, s.Magazine);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), s.Reserve);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4), s.ReloadRemaining);
        return b;
    }

    public static FlakStatus? Decode(ReadOnlySpan<byte> d)
    {
        if (d.Length != Bytes) return null;
        var reload = BinaryPrimitives.ReadSingleLittleEndian(d[4..]);
        if (!float.IsFinite(reload) || reload < 0) return null;
        return new FlakStatus(BinaryPrimitives.ReadUInt16LittleEndian(d), BinaryPrimitives.ReadUInt16LittleEndian(d[2..]), reload);
    }
}
