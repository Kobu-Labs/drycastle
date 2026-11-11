using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>Run-over damage (ticket #77): above the minimum speed, a hit player takes damage per m/s over it, capped.</summary>
public static class RunOver
{
    public static float Damage(VehiclesFile f, float speed)
    {
        var over = MathF.Abs(speed) - f.RunOverMinSpeed;
        return over <= 0 ? 0 : MathF.Min(f.RunOverMaxDamage, over * f.RunOverDamagePerSpeed);
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

/// <summary>Seat state machine of one vehicle: one driver only, no passengers.</summary>
public sealed class VehicleSeat
{
    /// <summary>Player id of the driver, 0 when empty (player ids start at 1).</summary>
    public ushort Driver { get; private set; }
    public bool Occupied => Driver != 0;

    public bool TryEnter(ushort player)
    {
        if (player == 0 || Occupied) return false;
        Driver = player;
        return true;
    }

    /// <summary>The driver leaves (E, death, disconnect, round restart). False if <paramref name="player"/> is not the driver.</summary>
    public bool Exit(ushort player)
    {
        if (!Occupied || Driver != player) return false;
        Driver = 0;
        return true;
    }

    public void Clear() => Driver = 0;
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

    /// <summary>The Health byte of a vehicle record carries the damage stage (0..3); a destroyed vehicle is State Dead.</summary>
    public static EntityRecord Make(ushort id, Vector3 pos, float yaw, ushort driver, byte colourAndKind, int stage = 0, bool destroyed = false)
    {
        var r = EntityRecord.Make(id, EntityType.Vehicle, pos, yaw, 0, stage, destroyed ? EntityState.Dead : driver != 0 ? EntityState.InVehicle : EntityState.Alive, colourAndKind);
        r.Pitch = unchecked((short)driver);
        return r;
    }

    /// <summary>Weapon byte of a vehicle record: type index in the high nibble, colour variant in the low one.</summary>
    public static byte Pack(int kind, byte colour) => (byte)((kind << 4) | (colour & 0xF));

    public static ushort Driver(in EntityRecord r) => unchecked((ushort)r.Pitch);
    public static int Stage(in EntityRecord r) => r.Health;
    public static byte Colour(in EntityRecord r) => (byte)(r.Weapon & 0xF);
    public static int Kind(in EntityRecord r) => r.Weapon >> 4;
}

/// <summary>Planar arcade vehicle model (car, motorcycle, bicycle: same model, different constants in vehicles.json), Godot-free. The host supplies collision (the real speed after the physics move is fed back).</summary>
public struct CarState
{
    /// <summary>Yaw in radians, Godot convention (0 faces -Z, positive turns left).</summary>
    public float Yaw;
    /// <summary>Signed speed along the facing direction, m/s (negative = reversing).</summary>
    public float Speed;
}

public static class CarModel
{
    // ---- driving feel knobs (code constants; vehicles.json carries top speed, acceleration and steering) ----
    public const float BrakeDecel = 22f;       // m/s^2 braking against the direction of travel
    public const float CoastDecel = 4f;        // m/s^2 rolling resistance with no input
    public const float HandbrakeDecel = 30f;   // m/s^2 on Jump
    public const float ReverseTopFraction = 0.35f;
    public const float SteerFullSpeed = 12f;   // m/s at which steering reaches its full rate...
    public const float SteerHighSpeedScale = 0.45f; // ...and falls to this fraction at top speed
    public const float HandbrakeSteerBoost = 1.5f;

    

    /// <summary>
    /// One tick. <paramref name="throttle"/> and <paramref name="steer"/> are -1..1 (forward, right), <paramref name="handbrake"/> is Jump.
    /// Steering rate (rad/s) is def.Steering at speed, scaled down by a speed curve and zero when standing still.
    /// </summary>
    public static void Step(ref CarState s, VehicleDef def, float throttle, float steer, bool handbrake, float dt)
    {
        var accel = def.Acceleration;
        var v = s.Speed;
        if (handbrake) v = Approach(v, 0, HandbrakeDecel * dt);
        else if (throttle > 0.05f)
            v = v < 0 ? Approach(v, 0, BrakeDecel * dt) : MathF.Min(def.TopSpeed, v + accel * throttle * dt);
        else if (throttle < -0.05f)
            v = v > 0 ? Approach(v, 0, BrakeDecel * dt) : MathF.Max(-def.TopSpeed * ReverseTopFraction, v + accel * throttle * dt);
        else v = Approach(v, 0, CoastDecel * dt);
        s.Speed = v;

        var speedAbs = MathF.Abs(v);
        var ramp = Math.Clamp(speedAbs / SteerFullSpeed, 0f, 1f);
        var fall = 1f - (1f - SteerHighSpeedScale) * Math.Clamp((speedAbs - SteerFullSpeed) / MathF.Max(1f, def.TopSpeed - SteerFullSpeed), 0f, 1f);
        var rate = def.Steering * ramp * fall * (handbrake ? HandbrakeSteerBoost : 1f);
        s.Yaw += -steer * rate * MathF.Sign(v) * dt;
    }

    static float Approach(float v, float target, float step) => v < target ? MathF.Min(target, v + step) : MathF.Max(target, v - step);
}
