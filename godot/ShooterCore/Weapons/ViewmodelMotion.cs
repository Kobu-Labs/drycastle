using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// One frame of what the first-person weapon needs to know about the body (#259). Filled by the client from the predicted
/// <see cref="MoveState"/> and the mouse; nothing here is sent or simulated.
/// </summary>
public struct ViewmodelInput
{
    /// <summary>The weapon is shown and the body is the player's own (on foot). False resets the motion, so nothing stale plays when it is shown again.</summary>
    public bool Active;
    /// <summary>Standing on something (not in the air, not swimming, not on a ladder): the only time the gun bobs, breathes or leans.</summary>
    public bool Grounded;
    /// <summary>In the air for real (not swimming, not on a ladder): the fall is tracked from here and the landing is the first grounded frame after it.</summary>
    public bool Falling;
    /// <summary>Horizontal speed over the ground, m/s.</summary>
    public float Speed;
    /// <summary>Signed speed along the view's right, m/s (positive: strafing right).</summary>
    public float StrafeSpeed;
    /// <summary>Vertical velocity, m/s (negative: falling).</summary>
    public float VerticalSpeed;
    public Stance Stance;
    /// <summary>The step's own sprint flag (<see cref="MoveState.WasSprinting"/>): held, forward, standing, not firing or aiming.</summary>
    public bool Sprinting;
    /// <summary>Eased aim-down-sights amount, 0 hip to 1 aimed. Everything but the kick is damped by it, so the iron sight stays on the crosshair point.</summary>
    public float Aim;
    /// <summary>
    /// Metres walked since the last footstep sound: the sound's own accumulator (<see cref="SoundRules.FootstepDue"/>). The bob anchors its dips to it, so a
    /// dip lands on each footstep. The sound clamps it whenever the body stops or leaves the ground, so a bob that only counted distance would drift from it.
    /// Only read when <see cref="HasStepAcc"/>: with sounds off the accumulator never moves, and there is nothing to land on.
    /// </summary>
    public float StepAcc;
    public bool HasStepAcc;
    /// <summary>How far the mouse turned the view since the last frame, radians, in the camera's own sense: yaw positive turns left, pitch positive looks up.</summary>
    public float LookYaw, LookPitch;
    public bool ReduceMotion;
}

/// <summary>
/// The first-person weapon's motion (#259): walk and sprint bob, look lag, landing dip, sprint pose, strafe lean and idle breath. Pure and
/// cosmetic, like <see cref="BreathState.VisualSway"/>: the client steps it once per frame and the viewmodel only applies the result. Metres
/// in camera space (x right, y up, z toward the player), radians; the outputs are offsets from the hip pose, already damped by the aim.
/// Godot-free; time is the sum of the steps, so a run is repeatable.
/// </summary>
public sealed class ViewmodelMotion
{
    /// <summary>Bob amplitude (the sideways swing; the dip is <see cref="VerticalBobRatio"/> of it) at walk and at sprint speed.</summary>
    public const float WalkBob = 0.012f, SprintBob = 0.02f, VerticalBobRatio = 0.5f;
    /// <summary>
    /// Below this speed the body is standing: no bob, idle breath instead. The same as <see cref="SoundRules.MinStepSpeed"/>, so the gun bobs exactly
    /// when footsteps sound (the ticket's 0.3 m/s would bob in silence). Steps are as long as the sound's, so a dip lands on each footstep.
    /// </summary>
    public const float MoveSpeed = SoundRules.MinStepSpeed, Stride = SoundRules.WalkStride;
    /// <summary>Time constant, seconds, of the bob's slow pull toward the footstep sound's phase while walking (a stop and start moves the sound's by up to half a stride).</summary>
    public const float PhaseLock = 0.4f;
    /// <summary>Seconds the bob and the idle breath take to fade in or out, so starting and stopping do not snap.</summary>
    public const float Ease = 0.15f;

    /// <summary>Idle breath: amplitudes (metres) and rates (radians per second) of the two axes. Under 3 mm: it only has to stop the gun being perfectly still.</summary>
    public const float IdleX = 0.002f, IdleY = 0.003f, IdleRateX = 0.7f, IdleRateY = 1.2f;

    /// <summary>
    /// Look lag. Every radian of turn adds this much offset (metres, roll in radians) against the turn, and the offset drains at
    /// <see cref="LookRate"/> a second. A running total rather than a per-frame target: the same turn gives the same lag at any frame rate.
    /// </summary>
    public const float LookPerRadian = 0.06f, LookRollPerRadian = 0.15f, LookRate = 10f;
    public const float LookMax = 0.03f, LookRollMax = 0.05f;

    /// <summary>Landing: below <see cref="LandMinSpeed"/> m/s (a bump, a replay flipping the floor flag) there is no dip; above it the gun drops by
    /// speed * <see cref="LandPerSpeed"/> up to <see cref="LandMax"/> and the eye by <see cref="CameraShare"/> of that. Recovery is a critically damped spring of rate <see cref="LandOmega"/>.</summary>
    public const float LandMinSpeed = 3f, LandPerSpeed = 0.008f, LandMax = 0.06f, LandOmega = 18f, CameraShare = 0.65f;
    /// <summary>Reduce Motion keeps the gun's own landing dip, this much of it (a state cue); the eye does not move.</summary>
    public const float ReducedLand = 0.4f;

    /// <summary>Sprint pose: the lowered, inward-tilted hold ((0.18, -0.30, -0.45) against the hip pose (0.24, -0.22, -0.5)), eased in and out.</summary>
    public static readonly Vector3 SprintOffset = new(0.18f - 0.24f, -0.30f + 0.22f, -0.45f + 0.5f);
    /// <summary>
    /// The muzzle faces -Z, so a positive yaw (counter-clockwise from above) turns it left, toward the centre of the view: inward for a gun on the right.
    /// (The ticket wrote -0.35, which would point it outward.)
    /// </summary>
    public const float SprintYaw = 0.35f, SprintRoll = 0.15f, SprintIn = 0.2f, SprintOut = 0.12f;
    /// <summary>The sprint pose needs this much speed: a sprint pressed into a wall lowers the gun for no reason.</summary>
    public const float SprintMinSpeed = 4f;

    /// <summary>Strafe lean: shifted and rolled against the sideways speed (at walk speed), eased at <see cref="LeanRate"/>.</summary>
    public const float LeanShift = 0.01f, LeanRoll = 0.03f, LeanRate = 8f;

    /// <summary>Offset of the weapon from the hip pose, metres.</summary>
    public Vector3 Position { get; private set; }
    /// <summary>Rotation of the weapon, radians: x pitch, y yaw, z roll.</summary>
    public Vector3 Rotation { get; private set; }
    /// <summary>How far the eye is lowered, metres (>= 0): the camera part of the landing dip. Not damped by the aim; a drop moves the gun and the crosshair together.</summary>
    public float CameraDrop { get; private set; }

    float _t, _bob, _idle, _phase, _sprint, _lean, _airMin, _landPos, _landVel;
    float _lookX, _lookY, _lookRoll;
    bool _wasFalling;

    /// <summary>Back to rest: no offsets, no landing in progress. Called when the weapon is hidden or the body is not the player's own.</summary>
    public void Reset()
    {
        _t = _bob = _idle = _phase = _sprint = _lean = _airMin = _landPos = _landVel = _lookX = _lookY = _lookRoll = 0;
        _wasFalling = false;
        Position = Vector3.Zero; Rotation = Vector3.Zero; CameraDrop = 0;
    }

    /// <summary>The bob's sideways amplitude at <paramref name="speed"/> m/s: linear up to walk speed (<see cref="WalkBob"/>), then up to sprint speed (<see cref="SprintBob"/>).</summary>
    public static float BobAmplitude(float speed) => speed <= MoveConsts.WalkSpeed
        ? WalkBob * Math.Clamp(speed / MoveConsts.WalkSpeed, 0f, 1f)
        : WalkBob + (SprintBob - WalkBob) * Math.Clamp((speed - MoveConsts.WalkSpeed) / (MoveConsts.SprintSpeed - MoveConsts.WalkSpeed), 0f, 1f);

    static float Smooth(float s) => s * s * (3f - 2f * s);

    /// <summary>Advances the motion by <paramref name="dt"/> seconds (a hitch is cut at 0.1 s so the springs cannot fling) with this frame's <paramref name="i"/>.</summary>
    public void Step(float dt, in ViewmodelInput i)
    {
        if (!i.Active) { Reset(); return; }
        dt = Math.Clamp(dt, 0f, 0.1f);
        var rm = i.ReduceMotion;
        _t += dt;

        // landing: the fastest fall of the airborne stretch, taken when the body is next on the ground (not on water or a ladder)
        if (i.Falling) { _airMin = Math.Min(_airMin, i.VerticalSpeed); _wasFalling = true; }
        else
        {
            if (_wasFalling && i.Grounded && -_airMin >= LandMinSpeed) Land(-_airMin, rm);
            _airMin = 0; _wasFalling = false;
        }
        var e = MathF.Exp(-LandOmega * dt);
        var x = _landPos; var v = _landVel; var k = v + LandOmega * x; // exact step of a critically damped spring: no ringing at any frame time
        _landPos = e * (x + k * dt); _landVel = e * (v - LandOmega * k * dt);

        // walk and sprint bob: a figure eight, one dip per step, two steps to a swing. The phase advances with the distance walked, one step (pi) per
        // stride, and is anchored so the dip (y lowest, phi = pi/4 mod pi) falls where the footstep sound does: set outright when the bob starts from nothing,
        // then pulled gently, because the sound's accumulator is clamped on every stop and in the air, which this one is not.
        var walking = !rm && i.Grounded && i.Stance != Stance.Prone && i.Speed >= MoveSpeed;
        var fresh = _bob <= 0f;
        _bob = MoveToward(_bob, walking ? 1f : 0f, dt / Ease);
        if (walking)
        {
            _phase += MathF.PI * i.Speed * dt / Stride;
            if (i.HasStepAcc)
            {
                var anchor = MathF.PI * Math.Clamp(i.StepAcc / Stride, 0f, 1f) + MathF.PI / 4f;
                var off = anchor - _phase;
                off -= MathF.PI * MathF.Floor(off / MathF.PI + 0.5f); // the nearest dip, a whole step either way: at most a quarter swing
                _phase += fresh ? off : off * (1f - MathF.Exp(-dt / PhaseLock));
            }
            _phase -= MathF.Tau * MathF.Floor(_phase / MathF.Tau);
        }
        var phi = _phase;
        var amp = BobAmplitude(i.Speed) * Smooth(_bob);
        var bobX = amp * MathF.Sin(phi);
        var bobY = -amp * VerticalBobRatio * MathF.Sin(2f * phi);

        // idle breath, when standing
        var idling = !rm && i.Grounded && i.Speed < MoveSpeed;
        _idle = MoveToward(_idle, idling ? 1f : 0f, dt / Ease);
        var idleAmt = Smooth(_idle);
        var idleX = IdleX * MathF.Sin(IdleRateX * _t) * idleAmt;
        var idleY = IdleY * MathF.Sin(IdleRateY * _t) * idleAmt;

        // strafe lean
        var lean = rm || !i.Grounded ? 0f : Math.Clamp(i.StrafeSpeed / MoveConsts.WalkSpeed, -1f, 1f);
        _lean += (lean - _lean) * (1f - MathF.Exp(-LeanRate * dt));

        // look lag: the gun trails the view, so a left turn (yaw up) shifts it right, and looking up (pitch up) shifts it down
        var drain = MathF.Exp(-LookRate * dt);
        var half = MathF.Sqrt(drain); // a turn made mid-frame has drained for half of it: the same total lag at any frame rate
        _lookX *= drain; _lookY *= drain; _lookRoll *= drain;
        if (!rm)
        {
            _lookX += i.LookYaw * LookPerRadian * half;
            _lookY -= i.LookPitch * LookPerRadian * half;
            _lookRoll += i.LookYaw * LookRollPerRadian * half;
        }
        _lookX = Math.Clamp(_lookX, -LookMax, LookMax);
        _lookY = Math.Clamp(_lookY, -LookMax, LookMax);
        _lookRoll = Math.Clamp(_lookRoll, -LookRollMax, LookRollMax);

        // sprint pose (a state cue: kept under Reduce Motion)
        var pose = i.Sprinting && i.Grounded && i.Stance == Stance.Stand && i.Speed >= SprintMinSpeed;
        _sprint = MoveToward(_sprint, pose ? 1f : 0f, dt / (pose ? SprintIn : SprintOut));
        var sp = Smooth(_sprint);

        // all of it fades out as the gun comes up to the sight (the kick is the viewmodel's own)
        var g = 1f - Math.Clamp(i.Aim, 0f, 1f);
        Position = (new Vector3(bobX + idleX + _lookX - _lean * LeanShift, bobY + idleY + _lookY - _landPos, 0f) + SprintOffset * sp) * g;
        Rotation = new Vector3(0f, SprintYaw * sp, SprintRoll * sp + _lookRoll + _lean * LeanRoll) * g;
        CameraDrop = rm ? 0f : Math.Max(0f, _landPos * CameraShare);
    }

    /// <summary>A landing at <paramref name="fall"/> m/s: an impulse that makes the gun sink to its depth (peaking after about 1 / <see cref="LandOmega"/> s) and come back.</summary>
    void Land(float fall, bool reduceMotion)
    {
        var depth = Math.Min(LandMax, fall * LandPerSpeed) * (reduceMotion ? ReducedLand : 1f);
        _landVel += depth * LandOmega * MathF.E; // v0 * t * e^(-w t) peaks at v0 / (w e)
    }

    static float MoveToward(float from, float to, float step) => from < to ? Math.Min(from + step, to) : Math.Max(from - step, to);
}
