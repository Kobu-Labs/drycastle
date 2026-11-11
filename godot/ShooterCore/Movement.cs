using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>Movement and health constants from the "Player movement and health model" ticket (starting values, to be playtested).</summary>
public static class MoveConsts
{
    public const float WalkSpeed = 5f;
    public const float SprintSpeed = 8f;
    public const float CrouchSpeed = 2.5f;
    public const float Gravity = 20f;
    /// <summary>Tuned for the 30 Hz tick so the discrete jump peaks at about 1.1 m (the continuous formula would give 6.63).</summary>
    public const float JumpSpeed = 6.9f;
    public const float GroundAccel = 60f;
    /// <summary>Air control is 30 % of ground acceleration.</summary>
    public const float AirAccel = GroundAccel * 0.3f;
    public const float MaxHealth = 100f;
    public const float FallSafeDrop = 4f;
    public const float FallLethalDrop = 14f;
    public const float CapsuleRadius = 0.35f;
    public const float CapsuleHeight = 1.8f;
    public const float CrouchHeight = 1.2f;
    public const float EyeHeight = 1.65f;
    public const float CrouchEyeHeight = 1.05f;
}

[Flags]
public enum MoveButtons
{
    None = 0,
    Jump = 1,
    Sprint = 2,
    Crouch = 4,
    /// <summary>Firing cancels sprint.</summary>
    Fire = 8,
    /// <summary>Aiming cancels sprint.</summary>
    Aim = 16,
    Reload = 32,
    Use = 64,
    /// <summary>Switch to the next weapon (hotbar slot changes also travel as the selected slot).</summary>
    Switch = 128,
    /// <summary>Headlights key (H), held: the server toggles on the press edge while driving (see <see cref="Headlights"/>).</summary>
    Headlights = 256,
}

/// <summary>One tick of player input: movement axes (x = strafe right, y = forward, each -1..1), look yaw in radians, buttons.</summary>
public struct MoveInput
{
    public float MoveX;
    public float MoveY;
    /// <summary>Yaw in radians, Godot convention: 0 faces -Z, positive turns left (counter-clockwise seen from above).</summary>
    public float Yaw;
    public MoveButtons Buttons;
    /// <summary>Weapon speed multiplier (bazooka 0.85); 1 for none.</summary>
    public float SpeedScale;

    public readonly bool Has(MoveButtons b) => (Buttons & b) != 0;
}

/// <summary>Player movement state carried between ticks. Position is at the feet.</summary>
public struct MoveState
{
    public Vector3 Position;
    public Vector3 Velocity;
    public bool OnFloor;
    public bool Crouching;
    /// <summary>Set by the caller each tick: something is over the head, so a crouching player cannot stand.</summary>
    public bool HeadBlocked;
    /// <summary>Highest feet height of the current airborne period.</summary>
    public float FallPeakY;
    public bool WasSprinting;
    /// <summary>Water depth at the feet (0 when dry or the feet are above the surface); set by <see cref="Water.Sample"/>.</summary>
    public float WaterDepth;
    /// <summary>Surface height of the water under the feet (NaN when dry); set by <see cref="Water.Sample"/>.</summary>
    public float WaterSurfaceY;
    /// <summary>Eyes below the water surface; set by <see cref="Water.Sample"/>, drives breath and the weapon lock.</summary>
    public bool HeadUnder;
    /// <summary>Seconds of breath used since the head last surfaced (0 = full breath); see <see cref="Breath"/>.</summary>
    public float AirSpent;
}

/// <summary>
/// The shared movement step, used by the client (prediction) and the server. It is split around the physics move:
/// <see cref="Step"/> turns input into the velocity to move with, the caller moves it against the world
/// (Godot MoveAndSlide, or <see cref="Integrate"/> for tests), then <see cref="PostMove"/> records the result
/// and reports fall damage.
/// </summary>
public static class MovementStep
{
    public static float CapsuleHeight(bool crouching) => crouching ? MoveConsts.CrouchHeight : MoveConsts.CapsuleHeight;
    public static float EyeHeight(bool crouching) => crouching ? MoveConsts.CrouchEyeHeight : MoveConsts.EyeHeight;

    /// <summary>Sprint is forward only, not while crouching, firing or aiming.</summary>
    public static bool CanSprint(in MoveInput i, bool crouching) =>
        i.Has(MoveButtons.Sprint) && !crouching && i.MoveY > 0.1f && !i.Has(MoveButtons.Fire) && !i.Has(MoveButtons.Aim);

    /// <summary>Swimming: the feet are under the surface in water at least <see cref="WaterConsts.SwimDepth"/> deep.</summary>
    public static bool IsSwimming(in MoveState s) =>
        s.WaterDepth >= WaterConsts.SwimDepth && s.Position.Y < s.WaterSurfaceY;

    /// <summary>Damage for a fall of <paramref name="drop"/> metres: none below 4 m, linear to death at 14 m.</summary>
    public static float FallDamage(float drop)
    {
        if (drop <= MoveConsts.FallSafeDrop) return 0f;
        var t = (drop - MoveConsts.FallSafeDrop) / (MoveConsts.FallLethalDrop - MoveConsts.FallSafeDrop);
        return MathF.Min(1f, t) * MoveConsts.MaxHealth;
    }

    /// <summary>Update crouch and velocity from the input; the caller then moves the body by <c>s.Velocity</c>.</summary>
    public static void Step(ref MoveState s, in MoveInput i, float dt)
    {
        s.Crouching = i.Has(MoveButtons.Crouch) || (s.Crouching && s.HeadBlocked);

        var sprint = CanSprint(i, s.Crouching);
        s.WasSprinting = sprint;
        var swimming = IsSwimming(s);
        // in deep water crouch means dive (vertical, below), not a slow crouch walk
        var speed = swimming ? (sprint ? WaterConsts.SwimSprintSpeed : WaterConsts.SwimSpeed)
            : s.Crouching ? MoveConsts.CrouchSpeed : sprint ? MoveConsts.SprintSpeed : MoveConsts.WalkSpeed;
        if (!swimming && s.WaterDepth > 0f) speed *= WaterConsts.WadeSpeedScale;
        speed *= i.SpeedScale > 0f ? i.SpeedScale : 1f;

        var (sin, cos) = MathF.SinCos(i.Yaw);
        var forward = new Vector3(-sin, 0, -cos);
        var right = new Vector3(cos, 0, -sin);
        var wish = right * i.MoveX + forward * i.MoveY;
        if (wish.LengthSquared() > 1f) wish = Vector3.Normalize(wish);

        var accel = (s.OnFloor ? MoveConsts.GroundAccel : MoveConsts.AirAccel) * dt;
        var h = new Vector3(s.Velocity.X, 0, s.Velocity.Z);
        var delta = wish * speed - h;
        var len = delta.Length();
        if (len > accel) delta *= accel / len;
        h += delta;

        var vy = s.Velocity.Y;
        if (swimming)
        {
            // buoyant: no gravity; vertical speed eases toward the input (crouch dives, jump rises, else holds)
            var target = s.Crouching ? -WaterConsts.SwimVerticalSpeed : i.Has(MoveButtons.Jump) ? WaterConsts.SwimVerticalSpeed : 0f;
            var step = WaterConsts.SwimVerticalAccel * dt;
            vy += Math.Clamp(target - vy, -step, step);
        }
        else
        {
            if (s.OnFloor)
            {
                vy = 0;
                if (i.Has(MoveButtons.Jump) && !s.Crouching) { vy = MoveConsts.JumpSpeed; s.OnFloor = false; }
            }
            if (!s.OnFloor) vy -= MoveConsts.Gravity * dt;
        }

        s.Velocity = new Vector3(h.X, vy, h.Z);
    }

    /// <summary>Plain integration without collision, for tests and server-side extrapolation.</summary>
    public static void Integrate(ref MoveState s, float dt) => s.Position += s.Velocity * dt;

    /// <summary>
    /// Record the result of the physics move (final position, velocity, floor contact) and track the fall.
    /// Returns the fall damage dealt by a landing in this tick (0 if none). <paramref name="landingWaterDepth"/> is
    /// <see cref="Water.DepthAt"/> for the new position: a landing in water at least swim-deep does no damage.
    /// </summary>
    public static float PostMove(ref MoveState s, Vector3 position, Vector3 velocity, bool onFloor, float landingWaterDepth = 0f)
    {
        var wasOnFloor = s.OnFloor;
        s.Position = position;
        s.Velocity = velocity;
        s.OnFloor = onFloor;
        float damage = 0;
        if (!onFloor)
        {
            s.FallPeakY = wasOnFloor ? position.Y : MathF.Max(s.FallPeakY, position.Y);
        }
        else
        {
            if (!wasOnFloor && landingWaterDepth < WaterConsts.SwimDepth) damage = FallDamage(s.FallPeakY - position.Y);
            s.FallPeakY = position.Y;
        }
        return damage;
    }
}
