using System.Numerics;
using Drycastle.ShooterCore.Bunkers;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Waters;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.ShooterCore.Movement;

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
    /// <summary>A ledge up to this high (a kerb, a doorstep, a stair) is walked onto, not walked into.</summary>
    public const float StepHeight = 0.3f;
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
    public const float StandToCrouchTime = 0.12f;
    public const float CrouchToStandTime = 0.21f;
    public const float CrouchToProneTime = 0.27f;
    /// <summary>Getting up from prone is deliberately slower than the other steps.</summary>
    public const float ProneToCrouchTime = 0.45f;
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
    /// <summary>
    /// Seated in a first-person view (a passenger's own eyes, the driver's cockpit view): the input's yaw and pitch are where the head looks, and the server
    /// shows them on the rider's figure (<see cref="RiderPose"/>). Clear in the chase view, where the yaw is the camera's orbit and the head faces ahead.
    /// </summary>
    HeadLook = 2048,
    /// <summary>Grenade throw key (middle mouse by default), held: aims the throw, and the release throws (see <see cref="GrenadeThrower"/>). With <see cref="Aim"/> held at the release the throw is put away.</summary>
    Throw = 4096,
    /// <summary>The Duffel throw key (<c>duffel_throw</c>, X by default), held: charges a throw while carrying a Duffel; the release throws it, a tap puts it down (see <see cref="Drycastle.ShooterCore.Cartel.DuffelThrowKey"/>).</summary>
    DuffelThrow = 8192,
}

/// <summary>One tick of player input: movement axes (x = strafe right, y = forward, each -1..1), look yaw in radians, buttons.</summary>
public struct MoveInput
{
    public float MoveX;
    public float MoveY;
    /// <summary>Yaw in radians, Godot convention: 0 faces -Z, positive turns left (counter-clockwise seen from above).</summary>
    public float Yaw;
    public MoveButtons Buttons;
    /// <summary>Weapon speed multiplier (rpg 0.85); 1 for none.</summary>
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
    /// <summary>Distance (m) from the feet to the shoreline of the deep water under them; set by <see cref="Water.Sample"/> (float.MaxValue unless in deep water).</summary>
    public float ShoreDistance;
    /// <summary>The Jump key was down last tick (edge detection for the hop out of the water).</summary>
    public bool JumpKeyDown;
    /// <summary>A hop out of the water is under way: the body is airborne and not floating until it starts to fall.</summary>
    public bool Hopping;
    /// <summary>On a ladder (see <see cref="Ladders.Climb"/>): the body is moved along it by the step, not by the physics.</summary>
    public bool OnLadder;
    /// <summary>Index of the ladder in <see cref="Ladders"/> while <see cref="OnLadder"/>.</summary>
    public int LadderIndex;
    /// <summary>The movement keys were let go once since getting on; climbing waits for it.</summary>
    public bool LadderArmed;
    /// <summary>The Use key was down last tick (edge detection for the ladder).</summary>
    public bool UseKeyDown;

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

    /// <summary>Sprint asked for with the forward intent and nothing cancelling it; from a crouch or prone this stands the body up (see <see cref="UpdateStance"/>).</summary>
    public static bool WantsSprint(in MoveInput i) =>
        i.Has(MoveButtons.Sprint) && i.MoveY > 0.1f && !i.Has(MoveButtons.Fire) && !i.Has(MoveButtons.Aim);

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
    /// else stand; a sprint attempt forces stand. Standing up past a level needs headroom: the step waits at zero until the caller clears HeadBlocked.
    /// </summary>
    static void UpdateStance(ref MoveState s, in MoveInput i, float dt)
    {
        if (WantsSprint(i)) s.ProneLatched = false; // trying to sprint stands up, even with Crouch held
        // in deep water the body floats upright: no crouch, no prone (the latch is cleared in Step)
        var goal = WantsSprint(i) ? Stance.Stand
            : s.ProneLatched ? Stance.Prone
            : i.Has(MoveButtons.Crouch) && s.WaterDepth < WaterConsts.SwimDepth ? Stance.Crouch
            : Stance.Stand;
        if (goal == s.Stance) { s.StepTo = s.Stance; s.StanceTimer = 0; return; }

        var next = (Stance)((int)s.Stance + (goal > s.Stance ? 1 : -1));
        if (s.StepTo != next) { s.StepTo = next; s.StanceTimer = TransitionTime(s.Stance, next); }
        if (s.StanceTimer > 0) s.StanceTimer -= dt;
        if (s.StanceTimer > 0) return;
        if (next > s.Stance || !s.HeadBlocked) s.Stance = next;
        if (s.Stance == next) s.StepTo = next;
    }

    /// <summary>
    /// Swimming: the feet are under the surface in water at least <see cref="WaterConsts.SwimDepth"/> deep. A body that is
    /// swimming floats: its feet hang <see cref="SwimFeetBelowSurface"/> under the surface.
    /// </summary>
    public static bool IsSwimming(in MoveState s) =>
        s.WaterDepth >= WaterConsts.SwimDepth && s.Position.Y < s.WaterSurfaceY;

    /// <summary>How far the feet hang under the surface while swimming: the standing eye height less the eyes' height above the surface.</summary>
    public static float SwimFeetBelowSurface => MoveConsts.EyeHeight - WaterConsts.SwimEyesAboveSurface;

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

        // the jump key: a press (rising edge) hops out of deep water when the bank is near (see below)
        var jumpKey = i.Has(MoveButtons.Jump);
        var jumpPressed = jumpKey && !s.JumpKeyDown;
        s.JumpKeyDown = jumpKey;
        if (s.Hopping && s.Velocity.Y <= 0f) s.Hopping = false;
        var swimming = IsSwimming(s) && !s.Hopping;
        var hop = swimming && jumpPressed && s.ShoreDistance <= WaterConsts.ShoreHopDistance;

        // no sprint in deep water: the swim speed is the only penalty
        var sprint = CanSprint(i, s.Stance, s.Transitioning) && !deepWater;
        s.WasSprinting = sprint;
        var landSpeed = sprint ? MoveConsts.SprintSpeed : StanceSpeed(s);
        var speed = Wading.Speed(landSpeed, s.WaterDepth);
        speed *= i.SpeedScale > 0f ? i.SpeedScale : 1f;

        var (sin, cos) = MathF.SinCos(i.Yaw);
        var forward = new Vector3(-sin, 0, -cos);
        var right = new Vector3(cos, 0, -sin);
        var wish = right * i.MoveX + forward * i.MoveY;
        if (wish.LengthSquared() > 1f) wish = Vector3.Normalize(wish);
        // a hop with no move keys goes toward where the player looks: the bank is the direction of the view
        if (hop && wish.LengthSquared() < 1e-6f) wish = forward;

        var accel = (s.OnFloor ? MoveConsts.GroundAccel : MoveConsts.AirAccel) * dt;
        var h = new Vector3(s.Velocity.X, 0, s.Velocity.Z);
        var delta = wish * speed - h;
        var len = delta.Length();
        if (len > accel) delta *= accel / len;
        h += delta;

        var vy = s.Velocity.Y;
        if (hop)
        {
            vy = WaterConsts.SwimHopSpeed;
            s.OnFloor = false;
            s.Hopping = true;
        }
        else if (swimming)
        {
            // floating: no gravity and no vertical input (no dive, no jump); the feet ease to their swim height
            var target = s.WaterSurfaceY - SwimFeetBelowSurface;
            vy = Math.Clamp((target - s.Position.Y) * WaterConsts.SwimSettleRate, -WaterConsts.SwimSettleMaxSpeed, WaterConsts.SwimSettleMaxSpeed);
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
    /// <see cref="Water.DepthAt"/> for the new position: the fall damage is scaled by <see cref="Wading.LandingDamageShare"/>,
    /// so a landing in deep water does none. A floating body keeps its fall peak at its height, so a hop out does not carry an old fall.
    /// </summary>
    public static float PostMove(ref MoveState s, Vector3 position, Vector3 velocity, bool onFloor, float landingWaterDepth = 0f)
    {
        var wasOnFloor = s.OnFloor;
        var floating = IsSwimming(s); // judged from the feet before the move, with the water sampled for this tick
        s.Position = position;
        s.Velocity = velocity;
        s.OnFloor = onFloor;
        float damage = 0;
        if (!onFloor)
        {
            s.FallPeakY = wasOnFloor || floating ? position.Y : MathF.Max(s.FallPeakY, position.Y);
        }
        else
        {
            if (!wasOnFloor) damage = FallDamage(s.FallPeakY - position.Y) * Wading.LandingDamageShare(landingWaterDepth);
            s.FallPeakY = position.Y;
        }
        return damage;
    }
}
