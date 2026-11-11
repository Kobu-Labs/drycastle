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

    /// <summary>A random colour index, preferring ones not in <paramref name="taken"/> (all taken: any).</summary>
    public static byte Pick(Random rng, IEnumerable<byte> taken)
    {
        var t = new HashSet<byte>(taken);
        var free = Enumerable.Range(0, Palette.Length).Where(i => !t.Contains((byte)i)).ToList();
        return free.Count > 0 ? (byte)free[rng.Next(free.Count)] : (byte)rng.Next(Palette.Length);
    }
}

/// <summary>How a vehicle travels in a snapshot entity record: yaw as usual, driver id in the (otherwise unused) pitch field, colour index in the weapon byte.</summary>
public static class VehicleRecord
{
    public const ushort FirstId = 0x4000;

    public static EntityRecord Make(ushort id, Vector3 pos, float yaw, ushort driver, byte colour)
    {
        var r = EntityRecord.Make(id, EntityType.Vehicle, pos, yaw, 0, 0, driver != 0 ? EntityState.InVehicle : EntityState.Alive, colour);
        r.Pitch = unchecked((short)driver);
        return r;
    }

    public static ushort Driver(in EntityRecord r) => unchecked((ushort)r.Pitch);
    public static byte Colour(in EntityRecord r) => r.Weapon;
}

/// <summary>Planar arcade car model, Godot-free. The host supplies collision (the real speed after the physics move is fed back).</summary>
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
