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
    /// <summary>Prone capsule: the minimum height for its radius (2 x 0.35 m); the head zone sits low at 0.5 m.</summary>
    public const float ProneHeight = 0.7f;
    public const float ProneEyeHeight = 0.5f;
    /// <summary>Crawl speed while prone (m/s); no sprint.</summary>
    public const float CrawlSpeed = 1.2f;
    /// <summary>Steeper ground than this (degrees) forces a prone player to stand (auto stand).</summary>
    public const float ProneMaxSlopeDegrees = 20f;
    /// <summary>Stance step durations in seconds. The body changes stance when a step ends; standing up waits for headroom.</summary>
    public const float StandToCrouchTime = 0.2f;
    public const float CrouchToStandTime = 0.35f;
    public const float CrouchToProneTime = 0.45f;
    /// <summary>Getting up from prone is deliberately slow.</summary>
    public const float ProneToCrouchTime = 0.9f;
}

/// <summary>Body stance. Steps are one level at a time: Stand to Crouch to Prone, and back.</summary>
public enum Stance : byte { Stand = 0, Crouch = 1, Prone = 2 }

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
    /// <summary>Prone key: a press (rising edge) toggles the prone latch.</summary>
    Prone = 512,
    /// <summary>Killstreak key (Q by default), held: the server uses the next held streak on its press edge.</summary>
    Killstreak = 1024,
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
    /// <summary>The stance the body has now; sets the capsule and eye height. Changes when a transition step ends.</summary>
    public Stance Stance;
    /// <summary>The stance the body is stepping toward; equal to <see cref="Stance"/> when settled.</summary>
    public Stance StepTo;
    /// <summary>Seconds left in the current stance step.</summary>
    public float StanceTimer;
    /// <summary>Prone is latched on (toggled by presses of the Prone key).</summary>
    public bool ProneLatched;
    /// <summary>The Prone key was down last tick (edge detection).</summary>
    public bool ProneKeyDown;
    /// <summary>Set by the caller each tick: no prone here (in a vehicle). Clears the latch. Deep water is checked in the step itself.</summary>
    public bool ProneBlocked;
    /// <summary>Set by the caller each tick: the ground is too steep to lie on. Clears the latch (auto stand).</summary>
    public bool FloorTooSteep;
    /// <summary>Set by the caller each tick: something is over the head for the stance one level up, so a crouched or prone player cannot stand up past it.</summary>
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

    /// <summary>True while a stance step is under way (no sprint or jump, speed of the slower stance).</summary>
    public readonly bool Transitioning => Stance != StepTo;
}

/// <summary>
/// The shared movement step, used by the client (prediction) and the server. It is split around the physics move:
/// <see cref="Step"/> turns input into the velocity to move with, the caller moves it against the world
/// (Godot MoveAndSlide, or <see cref="Integrate"/> for tests), then <see cref="PostMove"/> records the result
/// and reports fall damage.
/// </summary>
public static class MovementStep
{
    public static float CapsuleHeight(Stance stance) => stance switch
    {
        Stance.Crouch => MoveConsts.CrouchHeight,
        Stance.Prone => MoveConsts.ProneHeight,
        _ => MoveConsts.CapsuleHeight,
    };

    public static float EyeHeight(Stance stance) => stance switch
    {
        Stance.Crouch => MoveConsts.CrouchEyeHeight,
        Stance.Prone => MoveConsts.ProneEyeHeight,
        _ => MoveConsts.EyeHeight,
    };

    /// <summary>Sprint is forward only: standing, not mid-transition, not firing or aiming.</summary>
    public static bool CanSprint(in MoveInput i, Stance stance, bool transitioning) =>
        i.Has(MoveButtons.Sprint) && stance == Stance.Stand && !transitioning && i.MoveY > 0.1f && !i.Has(MoveButtons.Fire) && !i.Has(MoveButtons.Aim);

    /// <summary>Seconds for one stance step (one level up or down).</summary>
    public static float TransitionTime(Stance from, Stance to) => from switch
    {
        Stance.Stand => MoveConsts.StandToCrouchTime,
        Stance.Crouch => to == Stance.Prone ? MoveConsts.CrouchToProneTime : MoveConsts.CrouchToStandTime,
        _ => MoveConsts.ProneToCrouchTime,
    };

    /// <summary>Walking speed of a settled stance; mid-transition the slower of the two stances applies.</summary>
    static float StanceSpeed(in MoveState s)
    {
        var a = SpeedOf(s.Stance);
        return s.Transitioning ? MathF.Min(a, SpeedOf(s.StepTo)) : a;
    }

    static float SpeedOf(Stance stance) => stance switch
    {
        Stance.Crouch => MoveConsts.CrouchSpeed,
        Stance.Prone => MoveConsts.CrawlSpeed,
        _ => MoveConsts.WalkSpeed,
    };

    /// <summary>
    /// Move the stance toward the goal one step at a time. The goal is prone if latched, else crouch while Crouch is held,
    /// else stand. Standing up past a level needs headroom: the step waits at zero until the caller clears HeadBlocked.
    /// </summary>
    static void UpdateStance(ref MoveState s, in MoveInput i, float dt)
    {
        var goal = s.ProneLatched ? Stance.Prone : i.Has(MoveButtons.Crouch) ? Stance.Crouch : Stance.Stand;
        if (goal == s.Stance) { s.StepTo = s.Stance; s.StanceTimer = 0; return; }

        var next = (Stance)((int)s.Stance + (goal > s.Stance ? 1 : -1));
        if (s.StepTo != next) { s.StepTo = next; s.StanceTimer = TransitionTime(s.Stance, next); }
        if (s.StanceTimer > 0) s.StanceTimer -= dt;
        if (s.StanceTimer > 0) return;
        if (next > s.Stance || !s.HeadBlocked) s.Stance = next;
        if (s.Stance == next) s.StepTo = next;
    }

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

    /// <summary>Update stance and velocity from the input; the caller then moves the body by <c>s.Velocity</c>.</summary>
    public static void Step(ref MoveState s, in MoveInput i, float dt)
    {
        // Prone key: a press toggles the latch. Vehicles (ProneBlocked, set by the caller), deep water and steep ground
        // take it away. Deep water is decided here, from the water fields, so client prediction and server agree.
        var proneKey = i.Has(MoveButtons.Prone);
        if (proneKey && !s.ProneKeyDown) s.ProneLatched = !s.ProneLatched;
        s.ProneKeyDown = proneKey;
        var deepWater = s.WaterDepth >= WaterConsts.SwimDepth; // deeper than wading
        if (s.ProneBlocked || deepWater || s.FloorTooSteep) s.ProneLatched = false;
        UpdateStance(ref s, i, dt);

        var sprint = CanSprint(i, s.Stance, s.Transitioning);
        s.WasSprinting = sprint;
        var swimming = IsSwimming(s);
        // in deep water crouch means dive (vertical, below), not a slow crouch walk
        var speed = swimming ? (sprint ? WaterConsts.SwimSprintSpeed : WaterConsts.SwimSpeed)
            : sprint ? MoveConsts.SprintSpeed : StanceSpeed(s);
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
            var diving = i.Has(MoveButtons.Crouch) || s.Stance != Stance.Stand;
            var target = diving ? -WaterConsts.SwimVerticalSpeed : i.Has(MoveButtons.Jump) ? WaterConsts.SwimVerticalSpeed : 0f;
            var step = WaterConsts.SwimVerticalAccel * dt;
            vy += Math.Clamp(target - vy, -step, step);
        }
        else
        {
            if (s.OnFloor)
            {
                vy = 0;
                if (i.Has(MoveButtons.Jump) && s.Stance == Stance.Stand && !s.Transitioning) { vy = MoveConsts.JumpSpeed; s.OnFloor = false; }
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
