namespace Drycastle.ShooterCore;

/// <summary>
/// A vehicle that can pull a trailer (vehicles.json "tow"). Only data decides who tows: a type with this block can, a type without cannot. All offsets are in the
/// vehicle's frame, metres: x right, y up, z back (forward is -Z), origin on the ground at the centre (as <see cref="VehicleSeatDef.Offset"/>).
/// </summary>
public class VehicleTowDef
{
    /// <summary>The tow hitch (the ball), [x, y, z]: at the rear, so z is positive and a little behind the footprint's back edge.</summary>
    public float[] Hitch { get; set; } = { 0, 0.5f, 2.4f };
    /// <summary>How far the trailer can fold against the tractor, degrees (the jackknife limit): there the tractor's motion that would fold it further is blocked.</summary>
    public float LimitDeg { get; set; } = 75;
    /// <summary>Top speed while hitched, as a fraction of the type's top speed.</summary>
    public float SpeedFraction { get; set; } = 0.6f;
    /// <summary>Lateral acceleration the tractor holds while hitched, as a fraction of its <see cref="VehicleDef.MaxLateralAccel"/>: it takes bends wider.</summary>
    public float LateralScale { get; set; } = 0.8f;
    /// <summary>Auto-latch: the hitch must be within this of a loose trailer's drawbar eye, metres (and within <see cref="LatchHeight"/> of it vertically).</summary>
    public float LatchRadius { get; set; } = 0.8f;
    public float LatchHeight { get; set; } = 1f;
    /// <summary>Auto-latch: the two headings differ by less than this, degrees.</summary>
    public float LatchAngleDeg { get; set; } = 35;
    /// <summary>Auto-latch: only while reversing faster than this, m/s.</summary>
    public float LatchReverseSpeed { get; set; } = 0.3f;
    /// <summary>After an unhitch the trailer cannot be latched again for this long, seconds.</summary>
    public float RelatchLockout { get; set; } = 2f;

    public string? Validate()
    {
        if (Hitch.Length != 3) return "hitch is [x, y, z]";
        if (Hitch[2] <= 0) return "hitch is at the rear: z must be positive";
        if (LimitDeg is <= 20 or >= 90) return "limit_deg is between 20 and 90";
        if (SpeedFraction is <= 0 or > 1) return "speed_fraction is above 0 and at most 1";
        if (LateralScale is <= 0 or > 1) return "lateral_scale is above 0 and at most 1";
        if (LatchRadius <= 0 || LatchHeight <= 0) return "latch_radius and latch_height must be positive";
        if (LatchAngleDeg is <= 0 or > 90) return "latch_angle_deg is above 0 and at most 90";
        if (LatchReverseSpeed < 0) return "latch_reverse_speed must not be negative";
        if (RelatchLockout < 0) return "relatch_lockout must not be negative";
        return null;
    }
}

/// <summary>
/// A single-axle trailer (vehicles.json "trailer"): a type with this block has no seats and is pulled by a <see cref="VehicleTowDef"/> tractor. Offsets in the trailer's own
/// frame as <see cref="VehicleTowDef"/>'s; the footprint is the type's <see cref="VehicleDef.HalfWidth"/> and <see cref="VehicleDef.HalfLength"/> about the origin.
/// </summary>
public class VehicleTrailerDef
{
    /// <summary>The drawbar eye, [x, y, z]: at the front (z negative), on the centre line (x 0).</summary>
    public float[] Eye { get; set; } = { 0, 0.5f, -1.8f };
    /// <summary>The axle, [x, y, z], on the centre line (x 0). The trailer turns about it: the drawbar is the distance from the eye back to the axle.</summary>
    public float[] Axle { get; set; } = { 0, 0.4f, 0.4f };
    /// <summary>Mass with its load, kg: the tractor's acceleration and braking scale with it.</summary>
    public float Mass { get; set; } = 1500;
    /// <summary>Sideways acceleration the trailer's tyres hold, m/s²: a turn that asks more lets the trailer swing out.</summary>
    public float Grip { get; set; } = 5.5f;
    /// <summary>How fast the axle slides out per m/s² over <see cref="Grip"/> (m/s per m/s², that is seconds), and how fast the slide settles (seconds).</summary>
    public float SwayGain { get; set; } = 1f;
    public float SwaySettle { get; set; } = 0.4f;
    /// <summary>Unhitched at speed it rolls to a stop at this deceleration, m/s².</summary>
    public float RollDecel { get; set; } = 3f;

    /// <summary>The distance from the drawbar eye back to the axle, metres.</summary>
    public float Drawbar => Axle[2] - Eye[2];

    public string? Validate()
    {
        if (Eye.Length != 3 || Axle.Length != 3) return "eye and axle are [x, y, z]";
        if (MathF.Abs(Eye[0]) > 0.01f || MathF.Abs(Axle[0]) > 0.01f) return "eye and axle are on the centre line (x 0)";
        if (Drawbar < 0.5f) return "the axle must be at least 0.5 m behind the drawbar eye (eye z below axle z)";
        if (Mass <= 0) return "mass must be positive";
        if (Grip <= 0 || SwayGain < 0 || SwaySettle <= 0 || RollDecel < 0) return "grip and sway_settle must be positive, sway_gain and roll_decel not negative";
        return null;
    }
}
