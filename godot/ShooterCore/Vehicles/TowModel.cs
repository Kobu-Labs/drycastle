using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// The numbers of one tractor and one trailer type, flattened for the step (build once with <see cref="Of"/>). Metres, radians, seconds; offsets in each vehicle's own frame
/// (x right, y up, z back).
/// </summary>
public readonly struct TowGeometry
{
    /// <summary>The hitch in the tractor's frame.</summary>
    public readonly float HitchX, HitchY, HitchZ;
    /// <summary>The drawbar eye in the trailer's frame, and the distance back to the axle the trailer turns about.</summary>
    public readonly float EyeX, EyeY, EyeZ, Drawbar;
    /// <summary>The jackknife limit, radians.</summary>
    public readonly float Limit;
    /// <summary>The trailer's tyre grip (m/s²), how fast the axle slides out past it, and how fast the slide settles.</summary>
    public readonly float Grip, SwayGain, SwaySettle, RollDecel;
    /// <summary>The trailer's footprint, half sizes about its origin.</summary>
    public readonly float HalfWidth, HalfLength;
    /// <summary>The latch rule's numbers (see <see cref="VehicleTowDef"/>); the angle in radians.</summary>
    public readonly float LatchRadius, LatchHeight, LatchAngle, LatchReverseSpeed, Lockout;

    TowGeometry(VehicleTowDef tow, VehicleDef trailer, VehicleTrailerDef tr)
    {
        HitchX = tow.Hitch[0]; HitchY = tow.Hitch[1]; HitchZ = tow.Hitch[2];
        EyeX = tr.Eye[0]; EyeY = tr.Eye[1]; EyeZ = tr.Eye[2]; Drawbar = tr.Drawbar;
        Limit = tow.LimitDeg * FlightMath.Deg2Rad;
        Grip = tr.Grip; SwayGain = tr.SwayGain; SwaySettle = tr.SwaySettle; RollDecel = tr.RollDecel;
        HalfWidth = trailer.HalfWidth; HalfLength = trailer.HalfLength;
        LatchRadius = tow.LatchRadius; LatchHeight = tow.LatchHeight; LatchAngle = tow.LatchAngleDeg * FlightMath.Deg2Rad; LatchReverseSpeed = tow.LatchReverseSpeed; Lockout = tow.RelatchLockout;
    }

    /// <summary>
    /// The geometry of a trailer on its own, with the default tractor numbers: what a trailer that nobody pulls needs (its footprint and how it rolls to a stop,
    /// <see cref="TowModel.StepLoose"/>). The latch numbers and the hitch are a tractor's: use <see cref="Of"/> for those.
    /// </summary>
    public static TowGeometry ForTrailer(VehicleDef trailer) =>
        new(new VehicleTowDef(), trailer, trailer.Trailer ?? throw new ArgumentException($"{trailer.Id} has no trailer block", nameof(trailer)));

    /// <summary>The geometry of <paramref name="tractor"/> pulling <paramref name="trailer"/>. Throws when the first has no <c>tow</c> block or the second no <c>trailer</c> block.</summary>
    public static TowGeometry Of(VehicleDef tractor, VehicleDef trailer) =>
        new(tractor.Tow ?? throw new ArgumentException($"{tractor.Id} has no tow block", nameof(tractor)), trailer,
            trailer.Trailer ?? throw new ArgumentException($"{trailer.Id} has no trailer block", nameof(trailer)));

    /// <summary>Whether <paramref name="tractor"/> can pull <paramref name="trailer"/>: the data says so.</summary>
    public static bool CanTow(VehicleDef tractor, VehicleDef trailer) => tractor.Tow != null && trailer.Trailer != null;
}

/// <summary>
/// What the tractor did this tick, as the trailer needs it: the origin on the ground, the heading (Godot's: 0 faces -Z, positive turns left), the speed along the heading
/// (negative: reversing), the slide sideways (positive: to the right) and the yaw rate. Take it from the state the tractor's own step just produced.
/// </summary>
public readonly record struct TractorMotion(Vector3 Position, float Yaw, float Speed, float Slip, float YawRate)
{
    public static TractorMotion From(in DriveState s) => new(s.Position, s.Yaw, s.Speed, s.Slip, s.YawRate);
    /// <summary>A tractor stepped by <see cref="CarModel"/>: it never slides; the position is the physics body's.</summary>
    public static TractorMotion From(Vector3 position, in CarState s) => new(position, s.Yaw, s.Speed, 0f, s.YawRate);
}

/// <summary>
/// The trailer's own state: carried from tick to tick, copied freely (the driver's client steps its own copy, ADR 0014). Hitched, <see cref="Position"/> and
/// <see cref="Yaw"/> are recomputed from the tractor every step; <see cref="Phi"/> and <see cref="Slide"/> are what is carried. Loose, <see cref="Position"/>, <see cref="Yaw"/> and
/// <see cref="Speed"/> are all there is.
/// </summary>
public struct TrailerBody
{
    /// <summary>The origin: on the ground at the centre of the footprint.</summary>
    public Vector3 Position;
    public float Yaw;
    /// <summary>Hitched: the trailer's heading less the tractor's, radians, wrapped (positive: the trailer is turned to the left of the tractor). Zero in line.</summary>
    public float Phi;
    /// <summary>Hitched: how fast the axle slides sideways past its grip, m/s (positive: to the left). Zero while the tyres hold.</summary>
    public float Slide;
    /// <summary>The speed of the axle along the trailer's heading, m/s: pulled, or rolling to a stop when loose.</summary>
    public float Speed;

    public static TrailerBody At(Vector3 position, float yaw) => new() { Position = position, Yaw = yaw };
}

/// <summary>
/// The jackknife block for the tractor's next step (<see cref="TowModel.BlockOf"/>): inactive until the trailer stands at its limit; then <see cref="Clamp"/> takes from the
/// tractor's speed and yaw rate whatever would fold the trailer further. A default value blocks nothing.
/// </summary>
public readonly struct TowBlock
{
    readonly bool _active;
    readonly int _side;
    readonly float _phi, _drawbar, _hitchX, _hitchZ;

    internal TowBlock(int side, float phi, float drawbar, float hitchX, float hitchZ)
    {
        _active = true; _side = side; _phi = phi; _drawbar = drawbar; _hitchX = hitchX; _hitchZ = hitchZ;
    }

    /// <summary>The trailer is at its limit.</summary>
    public bool Active => _active;
    /// <summary>The side it is folded to: +1 turned left of the tractor, -1 right, 0 when not active.</summary>
    public int Side => _active ? _side : 0;

    /// <summary>Whether the motion folds the trailer further than it is.</summary>
    public bool Increases(float speed, float slip, float yawRate) => _active && _side * TowModel.PhiRate(_phi, _drawbar, _hitchX, _hitchZ, speed, slip, yawRate, 0f) > 0f;

    /// <summary>
    /// Takes the motion that would fold the trailer further out of the tractor's step: a speed that drives it deeper (reversing, mostly) is stopped, then a yaw rate that still would is
    /// cancelled. True when anything was changed. The same stop <see cref="DriveModel"/> gives a ledge too high to climb.
    /// </summary>
    public bool Clamp(ref float speed, ref float slip, ref float yawRate)
    {
        if (!Increases(speed, slip, yawRate)) return false;
        if (_side * TowModel.PhiRate(_phi, _drawbar, _hitchX, _hitchZ, speed, slip, 0f, 0f) > 0f) { speed = 0; slip = 0; }
        if (Increases(speed, slip, yawRate)) yawRate = 0;
        return true;
    }
}

/// <summary>What the world says about the trailer's box: is the place it would stand in, or the way it moved through, blocked (a wall, a tree, another vehicle)?</summary>
public interface ITrailerWorld
{
    /// <summary>The trailer going from <paramref name="from"/> to <paramref name="to"/> (footprints on the ground plane; <paramref name="y"/> the ground height at the trailer) is stopped by something solid.</summary>
    bool Blocked(in BodyBox from, in BodyBox to, float y);
}

/// <summary>
/// The kinematic single-axle trailer (towing): the drawbar eye is pinned to the tractor's hitch and the trailer turns about its axle, with no sideways slip at
/// it. Its heading follows from the velocity of the hitch point, which is the speed and the slide of the tractor plus its turn about the hitch's lever arm behind the rear axle:
/// <code>
///   dphi/dt = -( (v + w_z*hx) sin phi + (slip + w_z*hz) cos phi + slide ) / L  -  w_z
/// </code>
/// where phi is the trailer's heading less the tractor's, L the drawbar (eye to axle), (hx, hz) the hitch in the tractor's frame, v the speed, w_z the yaw rate. Pulled straight it
/// converges to in line; in a steady turn to the angle where v sin phi + w_z hz cos phi = -L w_z (the trailer cuts the corner on the inside); reversing the sign of v flips the first term and the angle runs away (the jackknife) until the limit
/// stops it. Pure and deterministic: the same inputs give the same state on the server and the driver's client, at any dt (the step subdivides itself, see <see cref="Step"/>).
/// </summary>
public static class TowModel
{
    /// <summary>A step longer than this is taken as this long (a stalled frame, a snapshot catch-up).</summary>
    public const float MaxDt = 0.2f;
    /// <summary>The angle rate integrates in slices that turn it no more than this (radians) so a fast tractor and a short drawbar stay accurate and stable.</summary>
    const float SliceRad = 0.15f;
    const int MaxSlices = 64;

    /// <summary>The rate the articulation grows at, rad/s, for the tractor's motion (pure; the model's equation above).</summary>
    public static float PhiRate(float phi, float drawbar, float hitchX, float hitchZ, float speed, float slip, float yawRate, float slide)
    {
        var (s, c) = MathF.SinCos(phi);
        var forward = speed + yawRate * hitchX; var right = slip + yawRate * hitchZ;
        return -(forward * s + right * c + slide) / drawbar - yawRate;
    }

    /// <summary>A point of a vehicle's frame (x right, z back) in the world at <paramref name="origin"/> with <paramref name="yaw"/>.</summary>
    static Vector3 ToWorld(Vector3 origin, float yaw, float x, float y, float z)
    {
        var (s, c) = MathF.SinCos(yaw);
        return new Vector3(origin.X + x * c + z * s, origin.Y + y, origin.Z - x * s + z * c);
    }

    /// <summary>The tractor's hitch in the world.</summary>
    public static Vector3 HitchPoint(in TowGeometry g, Vector3 tractorPosition, float tractorYaw) => ToWorld(tractorPosition, tractorYaw, g.HitchX, g.HitchY, g.HitchZ);

    /// <summary>The trailer's drawbar eye in the world.</summary>
    public static Vector3 EyePoint(in TowGeometry g, in TrailerBody b) => ToWorld(b.Position, b.Yaw, g.EyeX, g.EyeY, g.EyeZ);

    /// <summary>The trailer's footprint, for the caller's world query (ADR 0028: a vehicle's Body shape is its box).</summary>
    public static BodyBox Footprint(in TowGeometry g, in TrailerBody b) => new(b.Position.X, b.Position.Z, g.HalfWidth, g.HalfLength, b.Yaw);

    /// <summary>The block the tractor's next step must respect, from the trailer as it stands now.</summary>
    public static TowBlock BlockOf(in TowGeometry g, in TrailerBody b)
    {
        if (MathF.Abs(b.Phi) < g.Limit - 1e-4f) return default;
        return new TowBlock(b.Phi >= 0 ? 1 : -1, b.Phi, g.Drawbar, g.HitchX, g.HitchZ);
    }

    /// <summary>
    /// Couples the trailer to the tractor as it stands: the articulation is what the headings are, the eye goes onto the hitch. (The latch rule that decides when is
    /// <see cref="TowRules.CheckLatch"/>.)
    /// </summary>
    public static void Couple(ref TrailerBody b, in TowGeometry g, in TractorMotion t)
    {
        b.Phi = Math.Clamp(FlightMath.Wrap(b.Yaw - t.Yaw), -g.Limit, g.Limit);
        b.Slide = 0;
        Pin(ref b, g, t);
    }

    /// <summary>
    /// Where the trailer's origin stands when its drawbar eye is at <paramref name="eye"/> (a point in the world) and it faces <paramref name="yaw"/>: the height is the eye's less the
    /// eye's height on the trailer. The client draws a trailer from the hitch as it is drawn (the tractor leans, the trailer follows) with this.
    /// </summary>
    public static Vector3 OriginAt(in TowGeometry g, Vector3 eye, float yaw)
    {
        var (s, c) = MathF.SinCos(yaw);
        // eye = origin + (EyeX*c + EyeZ*s, ., -EyeX*s + EyeZ*c)
        return new Vector3(eye.X - (g.EyeX * c + g.EyeZ * s), eye.Y - g.EyeY, eye.Z - (-g.EyeX * s + g.EyeZ * c));
    }

    /// <summary>
    /// The trailer's body when hitched to a tractor at <paramref name="tractorPosition"/> facing <paramref name="tractorYaw"/>, turned <paramref name="phi"/> against it (<see cref="TrailerBody.Phi"/>):
    /// the pose a client draws from the tractor as it is drawn and the articulation it is told (speed and slide left zero).
    /// </summary>
    public static TrailerBody Pose(in TowGeometry g, Vector3 tractorPosition, float tractorYaw, float phi)
    {
        var b = new TrailerBody { Phi = Math.Clamp(FlightMath.Wrap(phi), -g.Limit, g.Limit) };
        Pin(ref b, g, new TractorMotion(tractorPosition, tractorYaw, 0f, 0f, 0f));
        return b;
    }

    /// <summary>
    /// How far the trailer is pitched (radians, positive: the drawbar end up) when its wheels stand on the ground and the drawbar eye is <paramref name="eyeAboveAxleGround"/> metres above
    /// the ground under the axle: the hitch's height when pulled; loose, the eye's height on the trailer plus the ground's rise from the axle to the eye. Level ground and a level hitch give 0.
    /// The turn is about the axle's contact point; held to ±<paramref name="maxPitch"/>.
    /// </summary>
    public static float PitchFor(in TowGeometry g, float eyeAboveAxleGround, float maxPitch = 0.45f)
    {
        // eye above the axle's ground = EyeY cos(p) + Drawbar sin(p) (the trailer turning about the axle's contact point), solved for p
        var r = MathF.Sqrt(g.Drawbar * g.Drawbar + g.EyeY * g.EyeY);
        var p = MathF.Asin(Math.Clamp(eyeAboveAxleGround / r, -1f, 1f)) - MathF.Atan2(g.EyeY, g.Drawbar);
        return Math.Clamp(p, -maxPitch, maxPitch);
    }

    /// <summary>Puts the trailer where the hitch and the articulation say: heading, then the origin so that the eye is on the hitch; the axle speed.</summary>
    static void Pin(ref TrailerBody b, in TowGeometry g, in TractorMotion t)
    {
        b.Yaw = FlightMath.Wrap(t.Yaw + b.Phi);
        var hitch = HitchPoint(g, t.Position, t.Yaw);
        b.Position = OriginAt(g, hitch, b.Yaw);
        var (sp, cp) = MathF.SinCos(b.Phi);
        b.Speed = (t.Speed + t.YawRate * g.HitchX) * cp - (t.Slip + t.YawRate * g.HitchZ) * sp;
    }

    /// <summary>
    /// One tick hitched: <paramref name="t"/> is the tractor after its own step. Updates the axle's slide (a turn asking more than the trailer's grip pushes the axle out of the
    /// bend, settling exponentially: stable at any dt) and the articulation (midpoint method in slices of at most 0.15 rad of change, so it is accurate and stable for any speed, any
    /// drawbar and any dt; the step is taken as at most <see cref="MaxDt"/> long), holds it inside the jackknife limit, then pins the trailer to the hitch.
    /// </summary>
    public static void Step(ref TrailerBody b, in TowGeometry g, in TractorMotion t, float dt)
    {
        dt = Math.Clamp(dt, 0f, MaxDt);
        // sway: the bend asks v*w of sideways acceleration (positive: to the left); past the trailer's grip the axle slides out of it
        var lateral = t.Speed * t.YawRate;
        var excess = MathF.Max(0f, MathF.Abs(lateral) - g.Grip);
        var slideTarget = -MathF.Sign(lateral) * excess * g.SwayGain;
        b.Slide += (slideTarget - b.Slide) * (1f - MathF.Exp(-dt / g.SwaySettle));

        var reach = (MathF.Abs(t.Speed) + MathF.Abs(t.Slip) + MathF.Abs(t.YawRate) * (MathF.Abs(g.HitchX) + MathF.Abs(g.HitchZ)) + MathF.Abs(b.Slide)) / g.Drawbar;
        var slices = Math.Clamp((int)MathF.Ceiling(dt * reach / SliceRad), 1, MaxSlices);
        var h = dt / slices;
        var phi = b.Phi;
        for (var i = 0; i < slices; i++)
        {
            var k1 = PhiRate(phi, g.Drawbar, g.HitchX, g.HitchZ, t.Speed, t.Slip, t.YawRate, b.Slide);
            var k2 = PhiRate(phi + 0.5f * h * k1, g.Drawbar, g.HitchX, g.HitchZ, t.Speed, t.Slip, t.YawRate, b.Slide);
            phi += h * k2;
        }
        b.Phi = Math.Clamp(FlightMath.Wrap(phi), -g.Limit, g.Limit);
        Pin(ref b, g, t);
    }

    /// <summary>
    /// <see cref="Step"/> against the world: when the box the trailer would stand in is blocked, the body stays as it was (speed 0) and the result is false. The caller then
    /// holds the tractor where it was (<see cref="Hold(ref DriveState, in DriveState)"/>): a trailer that is stopped stops the tractor.
    /// </summary>
    public static bool StepChecked(ref TrailerBody b, in TowGeometry g, in TractorMotion t, float dt, ITrailerWorld world)
    {
        var next = b;
        Step(ref next, g, t, dt);
        if (world.Blocked(Footprint(g, b), Footprint(g, next), next.Position.Y)) { b.Speed = 0; return false; }
        b = next;
        return true;
    }

    /// <summary>The tractor stopped where it was before the step (its pose and heading restored, speeds zero): the answer to a blocked trailer, as a ledge too high is for the drive model.</summary>
    public static void Hold(ref DriveState s, in DriveState before)
    {
        s.Impact = MathF.Max(s.Impact, MathF.Sqrt(s.Speed * s.Speed + s.Slip * s.Slip));
        s.Position = before.Position; s.Yaw = before.Yaw;
        s.Speed = 0; s.Slip = 0; s.YawRate = 0;
    }

    /// <summary>
    /// A loose trailer for one tick: it rolls on along its heading and comes to a stop (<see cref="TowGeometry.RollDecel"/>), stopping dead at something solid when a world is given.
    /// Standing still it does nothing.
    /// </summary>
    public static void StepLoose(ref TrailerBody b, in TowGeometry g, float dt, ITrailerWorld? world = null)
    {
        dt = Math.Clamp(dt, 0f, MaxDt);
        b.Phi = 0; b.Slide = 0;
        if (b.Speed == 0) return;
        b.Speed = FlightMath.Approach(b.Speed, 0f, g.RollDecel * dt);
        if (MathF.Abs(b.Speed) < 1e-3f) b.Speed = 0;
        var (s, c) = MathF.SinCos(b.Yaw);
        var next = b;
        next.Position += new Vector3(-s, 0, -c) * (b.Speed * dt);
        if (world != null && world.Blocked(Footprint(g, b), Footprint(g, next), next.Position.Y)) { b.Speed = 0; return; }
        b = next;
    }
}
