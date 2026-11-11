using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>Run-over damage (ticket #77): above the minimum speed, a hit player takes damage per m/s over it, capped.</summary>
public static class RunOver
{
    /// <param name="massScale">A type's <see cref="VehiclesFile.RunOverScale"/> (1 for the v1 numbers).</param>
    public static float Damage(VehiclesFile f, float speed, float massScale = 1f)
    {
        var over = MathF.Abs(speed) - f.RunOverMinSpeed;
        return over <= 0 ? 0 : MathF.Min(f.RunOverMaxDamage, over * f.RunOverDamagePerSpeed * massScale);
    }
}

/// <summary>Hidden vehicle HP (ticket #103): smoke stage from the remaining fraction, blast damage falloff and the box distance of a blast to a vehicle.</summary>
public static class VehicleDamage
{
    /// <summary>0 = fine, 1 = light smoke, 2 = dark smoke, 3 = smoke and sparks; at 0 HP the vehicle is destroyed (not a stage).</summary>
    public static int Stage(float hp, float maxHp, IReadOnlyList<float> fractions)
    {
        var f = hp / MathF.Max(1e-4f, maxHp);
        var s = 0;
        for (var i = 0; i < fractions.Count; i++) if (f <= fractions[i]) s = i + 1;
        return s;
    }

    /// <summary>Blast damage at surface distance <paramref name="distance"/>: full damage inside the full-damage radius, linear to the edge damage at the radius, none beyond.</summary>
    public static float BlastDamage(VehicleExplosionDef e, float distance)
    {
        var s = e.Splash;
        if (distance >= s.Radius) return 0;
        if (distance <= s.FullDamageRadius) return e.Damage;
        var t = (distance - s.FullDamageRadius) / MathF.Max(1e-4f, s.Radius - s.FullDamageRadius);
        return MathF.Max(1f, MathF.Round(e.Damage + (s.EdgeDamage - e.Damage) * t));
    }

    /// <summary>Distance from a blast centre to the surface of an upright box (the vehicle: centre on the ground, yaw as in <see cref="CarState"/>, 1.5 m tall).</summary>
    public static float DistanceToBox(Vector3 blast, Vector3 centre, float yaw, float halfWidth, float halfLength)
    {
        var d = blast - centre;
        var fwd = new Vector3(-MathF.Sin(yaw), 0, -MathF.Cos(yaw));
        var right = new Vector3(MathF.Cos(yaw), 0, -MathF.Sin(yaw));
        var lx = d.X * right.X + d.Z * right.Z; var lz = d.X * fwd.X + d.Z * fwd.Z;
        var dx = MathF.Max(0, MathF.Abs(lx) - halfWidth);
        var dz = MathF.Max(0, MathF.Abs(lz) - halfLength);
        var dy = d.Y < 0 ? -d.Y : MathF.Max(0, d.Y - 1.5f);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

/// <summary>
/// Seat state of one vehicle: one occupant per seat, seat 0 the driver (player ids start at 1, 0 = empty). The seat framework for
/// every type: a type's seats come from its <see cref="VehicleDef.Seats"/> list, and <see cref="NearestFreeSeat"/> picks the seat for
/// someone who presses enter. Pure logic; the server places the occupant at <see cref="SeatPosition"/>.
/// </summary>
public sealed class VehicleSeat
{
    public const int MaxSeats = 4;
    readonly ushort[] _occupant;

    public VehicleSeat(int seats = 1)
    {
        if (seats < 1 || seats > MaxSeats) throw new ArgumentOutOfRangeException(nameof(seats));
        _occupant = new ushort[seats];
    }

    public int Count => _occupant.Length;
    /// <summary>Player id of the driver (seat 0), 0 when empty.</summary>
    public ushort Driver => _occupant[0];
    /// <summary>Player id in <paramref name="seat"/>, 0 when empty.</summary>
    public ushort Occupant(int seat) => _occupant[seat];
    public bool Occupied => _occupant.Any(p => p != 0);
    public bool HasFree => _occupant.Any(p => p == 0);
    public bool IsFree(int seat) => _occupant[seat] == 0;

    /// <summary>The seat <paramref name="player"/> sits in, or -1.</summary>
    public int SeatOf(ushort player)
    {
        if (player == 0) return -1;
        for (var i = 0; i < _occupant.Length; i++) if (_occupant[i] == player) return i;
        return -1;
    }

    /// <summary>Takes <paramref name="seat"/> (-1: the first free one). False for player 0, a taken seat, a player already seated or no free seat.</summary>
    public bool TryEnter(ushort player, int seat = -1)
    {
        if (player == 0 || SeatOf(player) >= 0) return false;
        if (seat < 0) seat = Array.FindIndex(_occupant, p => p == 0);
        if (seat < 0 || seat >= _occupant.Length || _occupant[seat] != 0) return false;
        _occupant[seat] = player;
        return true;
    }

    /// <summary>The player leaves (E, death, disconnect, round restart). False if <paramref name="player"/> is not seated here.</summary>
    public bool Exit(ushort player)
    {
        var s = SeatOf(player);
        if (s < 0) return false;
        _occupant[s] = 0;
        return true;
    }

    public void Clear(int seat) => _occupant[seat] = 0;
    public void Clear() => Array.Clear(_occupant);

    /// <summary>World position of a seat: <paramref name="offset"/> in the vehicle's frame (forward -Z, yaw as in <see cref="CarState"/>) from the car's ground centre.</summary>
    public static Vector3 SeatPosition(Vector3 carPos, float yaw, float[] offset)
    {
        var c = MathF.Cos(yaw); var s = MathF.Sin(yaw);
        return new Vector3(carPos.X + offset[0] * c + offset[2] * s, carPos.Y + offset[1], carPos.Z - offset[0] * s + offset[2] * c);
    }

    /// <summary>
    /// The free seat of <paramref name="def"/> nearest to <paramref name="from"/> (horizontal distance, metres), within <paramref name="range"/>;
    /// -1 if none. Ties go to the lower seat index, so the driver's seat wins when the player stands at the centre.
    /// </summary>
    public int NearestFreeSeat(VehicleDef def, Vector3 carPos, float yaw, Vector3 from, float range)
    {
        var best = -1; var bd = range;
        for (var i = 0; i < _occupant.Length && i < def.Seats.Count; i++)
        {
            if (_occupant[i] != 0) continue;
            var p = SeatPosition(carPos, yaw, def.Seats[i].Offset);
            var d = MathF.Sqrt((p.X - from.X) * (p.X - from.X) + (p.Z - from.Z) * (p.Z - from.Z));
            if (d <= bd && (best < 0 || d < bd)) { best = i; bd = d; }
        }
        return best;
    }
}

/// <summary>A vehicle gun's magazine, reserve and reload. Hitscan rounds are resolved by the server; this only says when a round leaves the barrel.</summary>
public sealed class VehicleGun
{
    public VehicleGunDef Def { get; }
    public int Magazine { get; private set; }
    public int Reserve { get; private set; }
    double _nextShot;
    double _reloadDone = -1;

    public VehicleGun(VehicleGunDef def)
    {
        Def = def;
        Magazine = def.Magazine;
        Reserve = def.Reserve;
    }

    public bool Reloading => _reloadDone >= 0;

    /// <summary>Starts a reload: not when full, empty of reserve or already reloading.</summary>
    public bool TryReload(double now)
    {
        if (Reloading || Magazine == Def.Magazine || Reserve == 0) return false;
        _reloadDone = now + Def.ReloadTime;
        return true;
    }

    /// <summary>
    /// True when a round fires this tick: trigger held, not reloading, the fire interval has passed and the magazine has a round.
    /// An empty magazine starts a reload instead.
    /// </summary>
    public bool TryFire(double now, bool trigger)
    {
        if (_reloadDone >= 0 && now >= _reloadDone)
        {
            var take = Math.Min(Def.Magazine - Magazine, Reserve);
            Magazine += take; Reserve -= take; _reloadDone = -1;
        }
        if (!trigger || Reloading || now < _nextShot) return false;
        if (Magazine == 0) { TryReload(now); return false; }
        Magazine--;
        _nextShot = now + Def.FireInterval;
        return true;
    }
}

/// <summary>Counts seconds a vehicle has stood empty; fires once when it reaches the return time.</summary>
public sealed class AbandonTimer
{
    public float Empty { get; private set; }

    /// <summary>Advance by <paramref name="dt"/>. True on the tick the vehicle should return to its origin.</summary>
    public bool Update(bool occupied, bool atOrigin, float dt, float returnTime)
    {
        if (occupied || atOrigin) { Empty = 0; return false; }
        Empty += dt;
        if (Empty < returnTime) return false;
        Empty = 0;
        return true;
    }

    public void Reset() => Empty = 0;
}

public static class VehicleColours
{
    /// <summary>Paint palette (sRGB): tints the Kenney body material. Kept bright so the palette texture's shading still reads.</summary>
    public static readonly (float R, float G, float B)[] Palette =
    {
        (0.90f, 0.20f, 0.18f), (0.20f, 0.45f, 0.90f), (0.95f, 0.80f, 0.15f), (0.25f, 0.75f, 0.35f),
        (0.95f, 0.50f, 0.12f), (0.95f, 0.95f, 0.95f), (0.55f, 0.30f, 0.75f), (0.12f, 0.12f, 0.14f),
    };

    /// <summary>A random colour index below <paramref name="variants"/> (default: the car palette), preferring ones not in <paramref name="taken"/> (all taken: any).</summary>
    public static byte Pick(Random rng, IEnumerable<byte> taken, int variants = 0)
    {
        var n = variants > 0 ? variants : Palette.Length;
        var t = new HashSet<byte>(taken);
        var free = Enumerable.Range(0, n).Where(i => !t.Contains((byte)i)).ToList();
        return free.Count > 0 ? (byte)free[rng.Next(free.Count)] : (byte)rng.Next(n);
    }
}

/// <summary>How a vehicle travels in a snapshot entity record: yaw as usual, driver id in the (otherwise unused) pitch field, colour index (low 4 bits) and type index into vehicles.json (high 4 bits) in the weapon byte.</summary>
public static class VehicleRecord
{
    public const ushort FirstId = 0x4000;

    /// <summary>Health byte bit 0x80: headlights on (one bit, see <see cref="Headlights"/>). Bits 0..1 are the damage stage.</summary>
    public const byte LightsBit = 0x80, StageMask = 0x03;

    /// <summary>The Health byte of a vehicle record carries the damage stage (0..3) and the headlights bit; a destroyed vehicle is State Dead.</summary>
    public static EntityRecord Make(ushort id, Vector3 pos, float yaw, ushort driver, byte colourAndKind, int stage = 0, bool destroyed = false, bool lights = false)
    {
        var health = (stage & StageMask) | (lights ? LightsBit : 0);
        var r = EntityRecord.Make(id, EntityType.Vehicle, pos, yaw, 0, health, destroyed ? EntityState.Dead : driver != 0 ? EntityState.InVehicle : EntityState.Alive, colourAndKind);
        r.Pitch = unchecked((short)driver);
        return r;
    }

    /// <summary>Weapon byte of a vehicle record: type index in the high nibble, colour variant in the low one.</summary>
    public static byte Pack(int kind, byte colour) => (byte)((kind << 4) | (colour & 0xF));

    public static ushort Driver(in EntityRecord r) => unchecked((ushort)r.Pitch);
    public static int Stage(in EntityRecord r) => r.Health & StageMask;
    public static bool Lights(in EntityRecord r) => (r.Health & LightsBit) != 0;
    public static byte Colour(in EntityRecord r) => (byte)(r.Weapon & 0xF);
    public static int Kind(in EntityRecord r) => r.Weapon >> 4;
}

/// <summary>
/// Planar arcade vehicle model (car, motorcycle, bicycle: same model, different constants in vehicles.json), Godot-free.
/// Only the server runs it: clients render snapshots, so there is no client-side mirror to keep in step.
/// The host supplies collision (the real speed after the physics move is fed back).
/// </summary>
public struct CarState
{
    /// <summary>Yaw in radians, Godot convention (0 faces -Z, positive turns left).</summary>
    public float Yaw;
    /// <summary>Signed speed along the facing direction, m/s (negative = reversing).</summary>
    public float Speed;
    /// <summary>Current yaw rate, rad/s, eased toward the steering target at <see cref="VehicleHandling.YawSlew"/> so the nose never snaps round.</summary>
    public float YawRate;
}

public static class CarModel
{
    /// <summary>
    /// Turn radius (m) at a speed: the steering lock radius at low speed, growing with v² once the lateral grip
    /// (<see cref="VehicleDef.MaxLateralAccel"/>) is what limits the turn. Smooth: R = hypot(TurnRadius, v² / grip).
    /// </summary>
    public static float TurnRadiusAt(VehicleDef def, float speed)
    {
        var grip = MathF.Max(0.1f, def.MaxLateralAccel);
        var lat = speed * speed / grip;
        return MathF.Sqrt(def.TurnRadius * def.TurnRadius + lat * lat);
    }

    

    /// <summary>
    /// One tick. <paramref name="throttle"/> and <paramref name="steer"/> are -1..1 (forward, right), <paramref name="handbrake"/> is Jump.
    /// Longitudinal: linear acceleration to top speed (reverse capped by the handling), brake against travel, coast when idle.
    /// Lateral: yaw rate is speed / <see cref="TurnRadiusAt"/>, so it is zero when standing still, tight at walking pace and
    /// capped at high speed; the actual yaw rate chases that target at a limited slew rate.
    /// <paramref name="onWater"/> is set while the vehicle floats: an amphibious then drives at its water speed, and a boat has its drive
    /// (with planing); a boat not on water has none and coasts to a stop at its land deceleration.
    /// </summary>
    public static void Step(ref CarState s, VehicleDef def, float throttle, float steer, bool handbrake, float dt, bool onWater = false)
    {
        var h = def.Handling;
        var v = s.Speed;
        var top = def.TopSpeed;
        var accel = def.Acceleration;
        var coast = h.CoastDecel;
        if (def.Medium == VehicleMedium.Boat && !onWater)
        {
            // a boat has no drive on land: it coasts to a stop at its land deceleration
            throttle = 0; handbrake = false; coast = def.LandDecel;
        }
        else if (onWater && def.Medium == VehicleMedium.Amphibious)
        {
            top = def.WaterTopSpeed; accel = def.WaterAcceleration;
        }
        else if (onWater && def.Medium == VehicleMedium.Boat && def.PlaningSpeed > 0 && MathF.Abs(v) < def.PlaningSpeed)
        {
            accel *= 0.5f; // below the planing speed the hull pushes water aside: half the thrust until it lifts onto the plane
        }
        if (handbrake) v = Approach(v, 0, h.HandbrakeDecel * dt);
        else if (throttle > 0.05f)
            v = v < 0 ? Approach(v, 0, def.Brake * dt) : MathF.Min(top, v + accel * throttle * dt);
        else if (throttle < -0.05f)
            v = v > 0 ? Approach(v, 0, def.Brake * dt) : MathF.Max(-top * h.ReverseTopFraction, v + accel * throttle * dt);
        else v = Approach(v, 0, coast * dt);
        s.Speed = v;

        // steering: the wheel input sets the curvature; the handbrake sharpens it for a slide
        var target = -steer * v / TurnRadiusAt(def, v) * (handbrake ? h.HandbrakeSteerBoost : 1f);
        s.YawRate = Approach(s.YawRate, target, h.YawSlew * dt);
        s.Yaw += s.YawRate * dt;
    }

    static float Approach(float v, float target, float step) => v < target ? MathF.Min(target, v + step) : MathF.Max(target, v - step);
}
