namespace Drycastle.ShooterCore;

/// <summary>
/// What a hitched trailer takes from its tractor, as scales the drive step applies (<see cref="DriveModel.Step"/> and <see cref="CarModel.Step"/> take it as an optional last
/// argument). A default value is "not hitched": every scale reads 1 and nothing changes, so a vehicle without a trailer steps exactly as it did.
/// </summary>
public readonly struct TowLoad
{
    readonly bool _hitched;
    readonly float _accel, _brake, _top, _lateral;

    TowLoad(float accel, float brake, float top, float lateral) { _hitched = true; _accel = accel; _brake = brake; _top = top; _lateral = lateral; }

    /// <summary>A trailer is on the hook.</summary>
    public bool Hitched => _hitched;
    /// <summary>Acceleration: the tractor's mass over the pair's, m/(m+mt).</summary>
    public float AccelScale => _hitched ? _accel : 1f;
    /// <summary>Braking deceleration: the same ratio (the trailer's mass pushes on, it has no brake of its own), so it takes longer to stop.</summary>
    public float BrakeScale => _hitched ? _brake : 1f;
    /// <summary>Top speed, as a fraction of the type's (<see cref="VehicleTowDef.SpeedFraction"/>).</summary>
    public float TopSpeedScale => _hitched ? _top : 1f;
    /// <summary>The lateral acceleration it holds, as a fraction of <see cref="VehicleDef.MaxLateralAccel"/> (<see cref="VehicleTowDef.LateralScale"/>).</summary>
    public float LateralScale => _hitched ? _lateral : 1f;

    public static TowLoad None => default;

    /// <summary>The load of <paramref name="trailer"/> on <paramref name="tractor"/>; <paramref name="loaded"/> false: the trailer's empty mass (a rocket cart without its missile).</summary>
    public static TowLoad Of(VehicleDef tractor, VehicleDef trailer, bool loaded = true)
    {
        var tow = tractor.Tow ?? throw new ArgumentException($"{tractor.Id} has no tow block", nameof(tractor));
        var tr = trailer.Trailer ?? throw new ArgumentException($"{trailer.Id} has no trailer block", nameof(trailer));
        var ratio = tractor.Mass / (tractor.Mass + tr.MassFor(loaded));
        return new TowLoad(ratio, ratio, tow.SpeedFraction, tow.LateralScale);
    }
}

/// <summary>
/// Everything a drive step is told about the trailer: the load, and the jackknife block. Pass it as the last argument of <see cref="DriveModel.Step"/> or
/// <see cref="CarModel.Step"/>. The default is no trailer.
/// </summary>
public readonly struct TowEffect
{
    public TowEffect(TowLoad load, TowBlock block) { Load = load; Block = block; }
    public TowLoad Load { get; }
    public TowBlock Block { get; }
    public bool Hitched => Load.Hitched;
    public static TowEffect None => default;
}

/// <summary>Why a loose trailer is not latched by a tractor (<see cref="TowRules.CheckLatch"/>).</summary>
public enum LatchCheck : byte { Ok, AlreadyHitched, LockedOut, NotReversing, TooFar, AngleTooBig }

/// <summary>The rules of coupling, pure.</summary>
public static class TowRules
{
    /// <summary>
    /// Whether the tractor latches the trailer now (GTA-style auto-latch): the trailer is loose (not hitched, not locked out after an unhitch), the tractor reverses faster than
    /// <see cref="VehicleTowDef.LatchReverseSpeed"/>, its hitch is within <see cref="VehicleTowDef.LatchRadius"/> (flat) and <see cref="VehicleTowDef.LatchHeight"/> (up) of the
    /// drawbar eye and the headings differ by less than <see cref="VehicleTowDef.LatchAngleDeg"/>. The first failing check is the answer, in that order.
    /// </summary>
    public static LatchCheck CheckLatch(in TowGeometry g, in TractorMotion t, in TrailerBody b, TrailerState state)
    {
        if (state == TrailerState.Hitched) return LatchCheck.AlreadyHitched;
        if (state == TrailerState.Released) return LatchCheck.LockedOut;
        if (!(t.Speed < -g.LatchReverseSpeed)) return LatchCheck.NotReversing;
        var hitch = TowModel.HitchPoint(g, t.Position, t.Yaw);
        var eye = TowModel.EyePoint(g, b);
        var dx = hitch.X - eye.X; var dz = hitch.Z - eye.Z;
        if (dx * dx + dz * dz > g.LatchRadius * g.LatchRadius || MathF.Abs(hitch.Y - eye.Y) > g.LatchHeight) return LatchCheck.TooFar;
        if (MathF.Abs(FlightMath.Wrap(b.Yaw - t.Yaw)) >= g.LatchAngle) return LatchCheck.AngleTooBig;
        return LatchCheck.Ok;
    }
}
