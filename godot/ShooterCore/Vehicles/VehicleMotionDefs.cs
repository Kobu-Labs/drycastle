using Drycastle.ShooterCore.Net;

namespace Drycastle.ShooterCore.Vehicles;

/// <summary>
/// The handling of a vehicle that flies (vehicles.json "flight", with medium "air"): every number <see cref="FlightModel"/> reads beyond the type's
/// <see cref="VehicleDef.TopSpeed"/> (level flight, m/s) and <see cref="VehicleDef.Acceleration"/> (m/s² at full nose-down tilt). Angles are degrees,
/// rates per second. The summaries say what turning a number up does.
/// </summary>
public class VehicleFlightDef
{
    // ---- cyclic: tilt and what it buys
    /// <summary>Sideways acceleration at full bank, m/s². Higher: faster strafing and a higher sideways top speed.</summary>
    public float StrafeAcceleration { get; set; } = 9;
    /// <summary>Nose-down tilt at full forward input. Visual only above a few degrees (the acceleration is the type's own number): higher looks more aggressive.</summary>
    public float MaxPitchDeg { get; set; } = 22;
    /// <summary>Nose-up tilt at full backward input. Backward acceleration is the forward one times sin(this) / sin(max_pitch_deg), so higher flies backward faster.</summary>
    public float MaxPitchBackDeg { get; set; } = 14;
    /// <summary>Bank at full sideways input (visual, as max_pitch_deg).</summary>
    public float MaxRollDeg { get; set; } = 26;
    /// <summary>How fast the airframe reaches the tilt asked for, rad/s (a critically damped spring: about 4 / this seconds to settle). Higher: snappier and lighter; lower: heavier.</summary>
    public float AttitudeResponse { get; set; } = 5.5f;
    /// <summary>Keyboard ramp, per second: a held key reaches full deflection in 1 / input_rise seconds, a released one is gone in 1 / input_fall. Higher: taps bite harder.</summary>
    public float InputRise { get; set; } = 5;
    public float InputFall { get; set; } = 8;
    /// <summary>Air drag at low speed, per second. Higher: it bleeds speed faster on its own and feels stickier around the hover. The drag at speed is derived so that full tilt ends at top_speed.</summary>
    public float DragLow { get; set; } = 0.12f;
    /// <summary>How many times more drag it meets flying sideways or backwards than nose first. Higher: slower sideways and backward top speeds, and a turn costs more speed.</summary>
    public float OffAxisDrag { get; set; } = 3;

    // ---- hands off: the stabiliser
    /// <summary>Hands off, the airframe tilts against its own motion by this many degrees per m/s (the flare). Higher: stops shorter; too high and it rocks back (the tests hold the bound).</summary>
    public float BrakeGainDeg { get; set; } = 1.3f;
    /// <summary>The most the flare tilts, degrees. Higher: a harder stop from high speed.</summary>
    public float BrakeTiltDeg { get; set; } = 18;
    /// <summary>A small constant deceleration hands off, m/s², that ends the last creep so the hover is exactly still.</summary>
    public float HoverDecel { get; set; } = 0.6f;

    // ---- yaw and turns
    /// <summary>Fastest the nose turns after the view, degrees per second.</summary>
    public float MaxYawRateDeg { get; set; } = 110;
    /// <summary>How eagerly the nose follows the view, rad/s (critically damped, as attitude_response).</summary>
    public float YawResponse { get; set; } = 6;
    /// <summary>Bank into a turn, degrees per m/s² of turn acceleration (forward speed times yaw rate). Higher: it leans further for the same turn.</summary>
    public float TurnBankGain { get; set; } = 1.2f;
    /// <summary>The most a turn adds to the bank, degrees.</summary>
    public float TurnBankMaxDeg { get; set; } = 24;
    /// <summary>How fast the direction of travel swings round to the nose in forward flight, per second. Higher: it carves like on rails; 0: the nose turns and it keeps sliding the old way.</summary>
    public float TurnGrip { get; set; } = 2.4f;
    /// <summary>Forward speed (m/s) at which turn_grip is fully in; below it the helicopter is free to slide sideways and backwards.</summary>
    public float CoordinationSpeed { get; set; } = 12;

    // ---- collective: up and down
    /// <summary>Climb and descent rates with the key held, m/s.</summary>
    public float ClimbRate { get; set; } = 9;
    public float DescendRate { get; set; } = 8;
    /// <summary>How fast the vertical speed follows the key, per second, and the most it may change, m/s². Higher: the lift bites at once; lower: it floats.</summary>
    public float VerticalResponse { get; set; } = 3.5f;
    public float VerticalAccel { get; set; } = 9;
    /// <summary>Below this height over the ground the descent is slowed (the cushion), down to touchdown_speed at the ground, m and m/s.</summary>
    public float CushionHeight { get; set; } = 7;
    public float TouchdownSpeed { get; set; } = 1.5f;
    /// <summary>A flying vehicle with run_over runs people over only while it is no higher than this over the ground, m (skids and fuselage skimming them).</summary>
    public float RunOverAgl { get; set; } = 1.5f;
    /// <summary>Deceleration of the skids sliding on the ground, m/s².</summary>
    public float GroundFriction { get; set; } = 10;
    /// <summary>Highest it climbs above the ground under it, metres; the climb fades out over the last ceiling_fade metres.</summary>
    public float Ceiling { get; set; } = 160;
    public float CeilingFade { get; set; } = 25;
    /// <summary>Within this many metres of the map's edge the speed towards the edge is held down, to nothing at the edge itself.</summary>
    public float EdgeMargin { get; set; } = 60;

    // ---- rotor and the empty pilot seat
    /// <summary>Seconds the rotor takes to full speed once a pilot sits down, and to stop once it stands on the ground without one.</summary>
    public float SpoolUpTime { get; set; } = 3;
    public float SpoolDownTime { get; set; } = 6;
    /// <summary>Rotor speed (0 to 1) it needs before it lifts off.</summary>
    public float LiftoffRotor { get; set; } = 0.85f;
    /// <summary>With riders but no pilot it holds a hover this long, then sinks at autorotate_descent m/s to the ground. With nobody aboard it sinks at once.</summary>
    public float PilotlessHoverTime { get; set; } = 4;
    public float AutorotateDescent { get; set; } = 3;
    /// <summary>Hit points lost per second while the skids are in a water body.</summary>
    public float WaterDamagePerSecond { get; set; } = 60;

    // ---- what collides
    /// <summary>The rotor as the world sees it: a disc of this radius (smaller than the blades, on purpose), this high above the skids and this thick, metres.</summary>
    public float RotorRadius { get; set; } = 4.5f;
    public float RotorHeight { get; set; } = 3.4f;
    public float RotorThickness { get; set; } = 0.3f;

    /// <summary>The pilot's chase camera.</summary>
    public FlightCameraDef Camera { get; set; } = new();

    /// <summary>Null when the numbers make a flyable helicopter, else what is wrong.</summary>
    public string? Validate()
    {
        if (StrafeAcceleration <= 0 || AttitudeResponse <= 0 || InputRise <= 0 || InputFall <= 0 || YawResponse <= 0 || MaxYawRateDeg <= 0 || ClimbRate <= 0 || DescendRate <= 0
            || VerticalResponse <= 0 || VerticalAccel <= 0 || CushionHeight <= 0 || TouchdownSpeed <= 0 || GroundFriction <= 0 || Ceiling <= 0 || CeilingFade <= 0
            || SpoolUpTime <= 0 || SpoolDownTime <= 0 || AutorotateDescent <= 0 || RotorRadius <= 0 || RotorHeight <= 0 || RotorThickness <= 0 || CoordinationSpeed <= 0)
            return "rates, times and sizes must be positive";
        if (MaxPitchDeg is <= 0 or > 60 || MaxPitchBackDeg is <= 0 or > 60 || MaxRollDeg is <= 0 or > 60 || BrakeTiltDeg is < 0 or > 60 || TurnBankMaxDeg is < 0 or > 60) return "tilt limits are 0..60 degrees";
        if (OffAxisDrag < 1) return "off_axis_drag is at least 1";
        if (DragLow < 0 || BrakeGainDeg < 0 || HoverDecel < 0 || TurnBankGain < 0 || TurnGrip < 0 || EdgeMargin < 0 || PilotlessHoverTime < 0 || WaterDamagePerSecond < 0) return "gains must not be negative";
        if (LiftoffRotor is <= 0 or > 1) return "liftoff_rotor is above 0 and at most 1";
        // the attitude and yaw springs are stepped once a tick: past this they would ring instead of settling
        if (AttitudeResponse >= 0.5f * Wire.TickRate || YawResponse >= 0.5f * Wire.TickRate) return $"attitude_response and yaw_response must stay below {0.5f * Wire.TickRate} at {Wire.TickRate} ticks a second";
        return Camera.Validate();
    }
}

/// <summary>The pilot's chase camera (vehicles.json "flight.camera"). The view turns with the mouse at once; the camera's place follows the helicopter on a spring.</summary>
public class FlightCameraDef
{
    /// <summary>Metres behind and above the helicopter.</summary>
    public float Distance { get; set; } = 13;
    public float Height { get; set; } = 3.6f;
    /// <summary>How tightly the camera follows, rad/s (critically damped). Lower: more lag, the helicopter pulls away as it accelerates.</summary>
    public float Follow { get; set; } = 9;
    /// <summary>The furthest the helicopter may pull away from its place in the frame, metres.</summary>
    public float MaxLag { get; set; } = 3.5f;
    /// <summary>How much of the airframe's pitch and roll the camera takes (0 to 1): the horizon tilts by this share.</summary>
    public float Tilt { get; set; } = 0.4f;
    /// <summary>Degrees the field of view widens by at top speed.</summary>
    public float FovBoostDeg { get; set; } = 9;

    public string? Validate() =>
        Distance <= 0 || Follow <= 0 || MaxLag < 0 || Tilt is < 0 or > 1 || FovBoostDeg is < 0 or > 40 ? "camera: distance and follow positive, tilt 0..1, fov_boost_deg 0..40" : null;
}

/// <summary>
/// The handling of a vehicle its driver's client predicts (vehicles.json "drive", ADR 0010 and ADR 0014: the amphibious transport and the car): every number
/// <see cref="DriveModel"/> reads beyond the type's <see cref="VehicleDef.TopSpeed"/>, <see cref="VehicleDef.Acceleration"/>,
/// <see cref="VehicleDef.Brake"/>, <see cref="VehicleDef.TurnRadius"/>, <see cref="VehicleDef.HighSpeedRadiusFactor"/>,
/// <see cref="VehicleDef.WaterTopSpeed"/> and <see cref="VehicleDef.WaterAcceleration"/>. Rates are per second, angles degrees. The summaries say
/// what turning a number up does.
/// </summary>
public class VehicleDriveDef
{
    // ---- the keyboard ramp
    /// <summary>A held throttle key reaches full in 1 / throttle_rise seconds, a released one is gone in 1 / throttle_fall. Higher: taps bite harder.</summary>
    public float ThrottleRise { get; set; } = 4;
    public float ThrottleFall { get; set; } = 7;
    /// <summary>The same for the steering keys at a standstill: full lock in 1 / steer_rise seconds, centred again in 1 / steer_fall.</summary>
    public float SteerRise { get; set; } = 4.5f;
    public float SteerFall { get; set; } = 8;
    /// <summary>How much of steer_rise is left at top speed (0 to 1). Lower: the wheel winds on more slowly when fast, so the steering is calmer there.</summary>
    public float SteerCalm { get; set; } = 0.45f;

    // ---- where the wheels are (the model's)
    /// <summary>
    /// Distance between the axles (the first and the last of a vehicle with more) and between the wheels of an axle, metres: where the ground is sampled, so the body
    /// lies on the slope under it. A track of 0 is a single-track vehicle (a motorcycle): the ground is sampled under its two wheels and it never rolls with the ground.
    /// </summary>
    public float Wheelbase { get; set; } = 2.72f;
    public float Track { get; set; } = 1.96f;
    /// <summary>
    /// Where the point half way between the first and the last axle is along the model, metres (z: positive is behind the origin, 0: under it). The ground is
    /// sampled round this point, so a vehicle whose axles are not centred on its footprint (a combine, a bus with a long nose) still stands on its own wheels.
    /// </summary>
    public float AxleCentre { get; set; }

    public DriveLandDef Land { get; set; } = new();
    public DriveWaterDef Water { get; set; } = new();
    public DriveBodyDef Body { get; set; } = new();
    public DriveCameraDef Camera { get; set; } = new();

    /// <summary>Null when the numbers make a drivable vehicle, else what is wrong.</summary>
    public string? Validate(VehicleDef v)
    {
        if (ThrottleRise <= 0 || ThrottleFall <= 0 || SteerRise <= 0 || SteerFall <= 0 || Wheelbase <= 0 || Track < 0) return "the ramp rates and the wheelbase must be positive, the track not negative (0: a single-track vehicle, a motorcycle)";
        if (SteerCalm is <= 0 or > 1) return "steer_calm is above 0 and at most 1";
        if (MathF.Abs(AxleCentre) + Wheelbase / 2f > v.HalfLength + 0.5f) return "axle_centre and wheelbase put an axle outside the vehicle's length";
        var l = Land;
        if (l.LaunchBoost < 1 || l.LaunchSpeed <= 0 || l.PowerCurve <= 0 || l.CoastDecel < 0 || l.CoastDrag < 0 || l.Grip <= 0 || l.YawResponse <= 0 || l.HandbrakeDecel <= 0
            || l.HandbrakeSteer <= 0 || l.StepHeight <= 0 || l.SnapDown <= 0)
            return "land: launch_boost at least 1; launch_speed, power_curve, grip, yaw_response, handbrake_decel, handbrake_steer, step_height and snap_down positive; coast_decel and coast_drag not negative";
        if (l.BrakeBite is < 0 or > 1 || l.HandbrakeGrip is <= 0 or > 1 || l.ReverseTopFraction is <= 0 or > 1) return "land: brake_bite 0..1, handbrake_grip and reverse_top_fraction above 0 and at most 1";
        if (l.MaxClimbDeg is <= 0 or >= 80) return "land: max_climb_deg is 1..79";
        if (l.PivotBehind is { } pivot && MathF.Abs(pivot) > v.HalfLength + 0.5f) return "land: pivot_behind puts the pivot outside the vehicle's length";
        if (!v.CanFloat) return Body.Validate() ?? Camera.Validate(); // a land type never reads the water numbers, so they are not checked
        var w = Water;
        if (w.Draft < 0 || w.FloatDepth <= 0 || w.ReverseThrust is <= 0 or > 1 || w.CoastDrag < 0 || w.TurnRadius <= 0 || w.PropSteer < 0 || w.YawResponse <= 0 || w.SideDrag <= 0
            || w.SternSwing < 0 || w.BuoyancyResponse <= 0 || w.BuoyancyDamping <= 0 || w.ClimbOutRate <= 0)
            return "water: float_depth, turn_radius, yaw_response, side_drag, buoyancy_response, buoyancy_damping and climb_out_rate positive; reverse_thrust above 0 and at most 1; draft, coast_drag, prop_steer and stern_swing not negative";
        if (w.CoastDrag * v.WaterTopSpeed >= v.WaterAcceleration) return "water: coast_drag times water_top_speed must stay below water_acceleration (the drag alone would hold it below its top speed)";
        // the buoyancy spring is stepped once a tick: past this it would ring instead of settling
        if (w.BuoyancyResponse >= 0.5f * Wire.TickRate) return $"water: buoyancy_response must stay below {0.5f * Wire.TickRate} at {Wire.TickRate} ticks a second";
        return Body.Validate() ?? Camera.Validate();
    }
}

/// <summary>The drive on its wheels (vehicles.json "drive.land").</summary>
public class DriveLandDef
{
    /// <summary>Extra pull from a standstill: the acceleration is multiplied by this at 0 m/s, fading to 1 at launch_speed (m/s). Higher: a harder launch, the same top end.</summary>
    public float LaunchBoost { get; set; } = 1.7f;
    public float LaunchSpeed { get; set; } = 9;
    /// <summary>How the pull fades towards top speed: acceleration times 1 - (speed / top)^power_curve. Higher: it pulls hard for longer and tops out abruptly; 1: it eases off all the way.</summary>
    public float PowerCurve { get; set; } = 2.5f;
    /// <summary>Hands off, it slows by coast_decel (m/s²) plus coast_drag (per second) times its speed. Higher: it stops sooner when you lift; lower: it rolls on, heavier.</summary>
    public float CoastDecel { get; set; } = 1.3f;
    public float CoastDrag { get; set; } = 0.07f;
    /// <summary>The share of the brake that bites the moment the key goes against the travel (0 to 1); the rest comes in with the throttle ramp. Higher: a tap brakes harder.</summary>
    public float BrakeBite { get; set; } = 0.55f;
    /// <summary>Reverse top speed as a fraction of the forward one.</summary>
    public float ReverseTopFraction { get; set; } = 0.3f;
    /// <summary>Sideways acceleration the tyres hold, m/s². It caps the yaw rate at speed (grip / speed), so the circle at top speed is top_speed² / grip. Higher: tighter at speed.</summary>
    public float Grip { get; set; } = 8.5f;
    /// <summary>How fast the yaw rate follows the wheel, per second. Higher: it turns in at once; lower: it is lazier, heavier.</summary>
    public float YawResponse { get; set; } = 9;
    /// <summary>Handbrake (Jump): its deceleration (m/s²), the share of the grip the tyres keep sideways while it is on (lower: the tail slides wider), and how much sharper it steers.</summary>
    public float HandbrakeDecel { get; set; } = 9;
    public float HandbrakeGrip { get; set; } = 0.3f;
    public float HandbrakeSteer { get; set; } = 1.3f;
    /// <summary>A ledge up to this high (m) is driven over as a kerb; a higher one within one tick's travel is a wall. Slopes up to max_climb_deg are climbed.</summary>
    public float StepHeight { get; set; } = 0.4f;
    public float MaxClimbDeg { get; set; } = 48;
    /// <summary>A drop up to this deep (m) keeps the wheels on the ground; a deeper one and it is in the air until it lands.</summary>
    public float SnapDown { get; set; } = 0.3f;
    /// <summary>
    /// Where it turns about on its wheels, metres behind the origin (the model's z): the axle that does not steer, or the mean of several (flak, Scud). The pivot
    /// goes round <c>turn_radius</c>; the origin ahead of it sweeps wider, the nose wider still. Every shipped drive block sets it (<c>EveryVehicleDrivesTests</c>);
    /// unset is 0: it turns about its origin.
    /// </summary>
    public float? PivotBehind { get; set; }
}

/// <summary>The drive afloat (vehicles.json "drive.water").</summary>
public class DriveWaterDef
{
    /// <summary>How deep it sits: the origin (the wheels' underside) rides this far under the surface, metres. The model's waterline.</summary>
    public float Draft { get; set; } = 0.56f;
    /// <summary>It floats where the water over the ground is at least this deep (m); in less it stands on its wheels. (Half of it, to go aground again.)</summary>
    public float FloatDepth { get; set; } = 0.12f;
    /// <summary>Thrust astern as a share of the thrust ahead: there are no brakes afloat, this is how it stops. Higher: it stops shorter and backs faster.</summary>
    public float ReverseThrust { get; set; } = 0.6f;
    /// <summary>A little drag in proportion to the speed, per second, on top of the hull's own (which grows with the square). Higher: it carries less way when you lift off.</summary>
    public float CoastDrag { get; set; } = 0.12f;
    /// <summary>The circle it turns at speed, metres (yaw rate = speed / turn_radius).</summary>
    public float TurnRadius { get; set; } = 16;
    /// <summary>Yaw rate the propeller's wash gives at full throttle whatever the speed, rad/s: it turns from a standstill under power. Astern it turns the other way.</summary>
    public float PropSteer { get; set; } = 0.15f;
    /// <summary>How fast the yaw rate follows the helm, per second. Lower: lazier, more boat.</summary>
    public float YawResponse { get; set; } = 1.6f;
    /// <summary>How fast the water takes the sideways speed away, per second. Lower: it slides wider out of a turn.</summary>
    public float SideDrag { get; set; } = 1.5f;
    /// <summary>How far ahead of the centre it pivots, metres: turning, the stern swings out round that point. 0: it turns about its middle.</summary>
    public float SternSwing { get; set; } = 1.2f;
    /// <summary>The hull's rise and fall to its waterline: how fast (rad/s) and how damped (1: no overshoot; lower: it plunges in, comes up and settles).</summary>
    public float BuoyancyResponse { get; set; } = 5;
    public float BuoyancyDamping { get; set; } = 0.4f;
    /// <summary>Climbing out, the wheels lift it onto the bank at this rate, m/s (the heave). Higher: it pops out; lower: it hauls itself out.</summary>
    public float ClimbOutRate { get; set; } = 1.4f;
}

/// <summary>
/// How the body moves over its wheels and on the water (vehicles.json "drive.body"). Drawn by every client from the vehicle's motion
/// (<see cref="DriveBody"/>); nothing here changes where the vehicle goes or who is hit.
/// </summary>
public class DriveBodyDef
{
    /// <summary>Degrees the tail squats per m/s² of acceleration, the nose dives per m/s² of braking, and the body leans out of a turn per m/s² sideways.</summary>
    public float SquatDeg { get; set; } = 0.55f;
    public float DiveDeg { get; set; } = 0.6f;
    public float LeanDeg { get; set; } = 0.55f;
    /// <summary>The most it pitches and rolls on its springs, degrees.</summary>
    public float MaxPitchDeg { get; set; } = 5;
    public float MaxRollDeg { get; set; } = 6;
    /// <summary>The suspension: how fast it answers (rad/s) and how damped (1: no overshoot; lower: it rocks once before it settles, which reads as weight).</summary>
    public float Response { get; set; } = 7;
    public float Damping { get; set; } = 0.45f;
    /// <summary>How fast the drawn body takes up the slope of the ground under the wheels, per second. Lower: smoother over rough ground.</summary>
    public float TerrainResponse { get; set; } = 12;
    /// <summary>Afloat: degrees the bow lifts at full throttle and top water speed, and degrees it heels out of a turn per m/s² sideways.</summary>
    public float BowRiseDeg { get; set; } = 5;
    public float WaterLeanDeg { get; set; } = 1.2f;
    /// <summary>Afloat: how fast the hull answers (rad/s) and how damped. Slow and loose, so it wallows.</summary>
    public float WaterResponse { get; set; } = 2.5f;
    public float WaterDamping { get; set; } = 0.35f;
    /// <summary>The bob at rest: metres up and down, degrees of pitch and roll, and its period in seconds.</summary>
    public float BobHeight { get; set; } = 0.03f;
    public float BobPitchDeg { get; set; } = 1.0f;
    public float BobRollDeg { get; set; } = 1.5f;
    public float BobPeriod { get; set; } = 2.6f;
    /// <summary>How many times larger the bob is just after it has gone in or been driven hard, and the seconds it takes to settle back.</summary>
    public float BobDisturbed { get; set; } = 3;
    public float SettleTime { get; set; } = 3;
    /// <summary>The front wheels' largest steering angle as drawn, degrees.</summary>
    public float WheelLockDeg { get; set; } = 30;
    /// <summary>The propeller's speed at full throttle, turns a second.</summary>
    public float PropellerRps { get; set; } = 9;

    public string? Validate()
    {
        if (Response <= 0 || Damping <= 0 || TerrainResponse <= 0 || WaterResponse <= 0 || WaterDamping <= 0 || BobPeriod <= 0 || SettleTime <= 0) return "body: responses, dampings, bob_period and settle_time must be positive";
        if (SquatDeg < 0 || DiveDeg < 0 || LeanDeg < 0 || BowRiseDeg < 0 || WaterLeanDeg < 0 || BobHeight < 0 || BobPitchDeg < 0 || BobRollDeg < 0 || BobDisturbed < 1 || PropellerRps < 0)
            return "body: gains and bob sizes must not be negative, bob_disturbed at least 1";
        if (MaxPitchDeg is < 0 or > 30 || MaxRollDeg is < 0 or > 30 || WheelLockDeg is < 0 or > 60) return "body: max_pitch_deg and max_roll_deg are 0..30, wheel_lock_deg 0..60";
        return null;
    }
}

/// <summary>
/// The driver's chase camera of a predicted vehicle (vehicles.json "drive.camera"): the helicopter's spring camera (<see cref="FlightCameraDef"/>),
/// and it swings round with the vehicle, since the keys steer and the mouse only looks.
/// </summary>
public class DriveCameraDef : FlightCameraDef
{
    public DriveCameraDef() { Distance = 7.5f; Height = 2.2f; Follow = 10; MaxLag = 1.6f; Tilt = 0.35f; FovBoostDeg = 7; }

    /// <summary>The share of the vehicle's turning the view takes (0 to 1). 1: the camera stays behind it; 0: the view keeps its compass heading, as on the other cars.</summary>
    public float TurnFollow { get; set; } = 1;
    /// <summary>How fast the view catches up with a turn, per second. Lower: the vehicle swings in the frame first, then the camera comes round.</summary>
    public float TurnResponse { get; set; } = 5;

    public new string? Validate() =>
        base.Validate() ?? (TurnFollow is < 0 or > 1 || TurnResponse <= 0 ? "camera: turn_follow 0..1, turn_response positive" : null);
}
