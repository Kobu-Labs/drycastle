using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// Everything <see cref="FlightModel"/> carries from tick to tick for one flying vehicle. The server owns the true one; the pilot's client
/// steps its own copy from the same inputs (ADR 0006). Angles are radians in Godot's sense, so a node shows it with
/// <c>Rotation = (Pitch, Yaw, Roll)</c>: yaw 0 faces -Z and positive turns left, positive pitch raises the nose, positive roll drops the left side.
/// </summary>
public struct FlightState
{
    /// <summary>The origin: on the ground under the rotor mast when it stands on its skids.</summary>
    public Vector3 Position;
    public Vector3 Velocity;
    public float Yaw, Pitch, Roll;
    public float YawRate, PitchRate, RollRate;
    /// <summary>The keyboard's cyclic after the ramp, -1..1 (x right, y forward).</summary>
    public float InputX, InputY;
    /// <summary>Rotor speed, 0 (stopped) to 1.</summary>
    public float Rotor;
    /// <summary>Standing on its skids: nothing moves until it lifts off.</summary>
    public bool Grounded;
    /// <summary>Seconds in the air (or on the ground) since a pilot last flew it.</summary>
    public float PilotlessTime;

    // ---- what the last step found (not carried into the next one)
    /// <summary>Speed lost against the ground or an obstacle in the last step, m/s: the crash damage rules read it.</summary>
    public float Impact;
    /// <summary>The skids are in a water body.</summary>
    public bool InWater;
    /// <summary>Height over the ground (or water) under it, metres; infinity when there is none within reach.</summary>
    public float Agl;
    /// <summary>The map's edge is holding it back.</summary>
    public bool AtEdge;

    /// <summary>The seat, gun or light at <paramref name="offset"/> (the vehicle's frame: x right, y up, z back) in the world, with the airframe's tilt.</summary>
    public readonly Vector3 ToWorld(float[] offset) => Position + FlightMath.Rotate(new Vector3(offset[0], offset[1], offset[2]), Yaw, Pitch, Roll);
}

/// <summary>One tick of the pilot's controls, as both sides read them from the same <see cref="NetInput"/>.</summary>
public struct FlightInput
{
    /// <summary>Cyclic from the movement keys, -1..1: x right, y forward.</summary>
    public float MoveX, MoveY;
    /// <summary>The heading the nose swings to: the pilot's view yaw.</summary>
    public float Yaw;
    /// <summary>Collective: +1 climb (Jump), -1 descend (Crouch), 0 hold.</summary>
    public int Lift;
    /// <summary>Somebody is in the pilot seat. False: the stabiliser flies (see <see cref="VehicleFlightDef.PilotlessHoverTime"/>).</summary>
    public bool Piloted;
    /// <summary>Somebody is aboard in any seat.</summary>
    public bool Occupied;

    /// <summary>The pilot's input for one tick. <paramref name="handsOff"/>: the pilot is busy elsewhere (steering a killstreak), so the keys are not the helicopter's.</summary>
    public static FlightInput FromNet(in NetInput i, bool handsOff = false)
    {
        var buttons = (MoveButtons)i.Buttons;
        return new FlightInput
        {
            MoveX = handsOff ? 0 : Quant.Axis(i.MoveX),
            MoveY = handsOff ? 0 : Quant.Axis(i.MoveY),
            Yaw = Quant.Yaw(i.Yaw),
            Lift = handsOff ? 0 : ((buttons & MoveButtons.Jump) != 0 ? 1 : 0) - ((buttons & MoveButtons.Crouch) != 0 ? 1 : 0),
            Piloted = true, Occupied = true, HoldHeading = handsOff,
        };
    }

    /// <summary>Keep the nose where it is instead of following <see cref="Yaw"/>.</summary>
    public bool HoldHeading;

    /// <summary>Nobody in the pilot seat.</summary>
    public static FlightInput Pilotless(bool occupied) => new() { Occupied = occupied, HoldHeading = true };

    /// <summary>A pilot whose inputs are not arriving: hands off, heading held.</summary>
    public static FlightInput Idle => new() { Piloted = true, Occupied = true, HoldHeading = true };
}

/// <summary>The map's flyable rectangle on the ground plane (Godot x and z). <see cref="None"/>: no edge.</summary>
public readonly record struct FlightBounds(float MinX, float MaxX, float MinZ, float MaxZ)
{
    public static readonly FlightBounds None = new(float.NegativeInfinity, float.PositiveInfinity, float.NegativeInfinity, float.PositiveInfinity);
}

/// <summary>Where a move ended, and the surface that stopped it (unit normal, pointing away from the surface).</summary>
public readonly record struct FlightMove(Vector3 Position, bool Hit, Vector3 Normal);

/// <summary>
/// The world as the flight model sees it. The game answers with the physics space (<c>FlightProbe</c>), the same way on the server and on the
/// pilot's client; the tests answer with a flat field and a wall.
/// </summary>
public interface IFlightWorld
{
    /// <summary>Height of the highest solid under <paramref name="position"/> (ground, a roof), or negative infinity when there is none in reach.</summary>
    float GroundY(Vector3 position);
    /// <summary>Surface height of the water body over (x, z), or negative infinity.</summary>
    float WaterY(float x, float z);
    /// <summary>Moves the hull and the rotor disc from <paramref name="from"/> by <paramref name="motion"/> until something solid stops them.</summary>
    FlightMove Move(Vector3 from, float yaw, Vector3 motion);
    FlightBounds Bounds { get; }
}

public static class FlightMath
{
    public const float Deg2Rad = MathF.PI / 180f;

    /// <summary>An angle wrapped into -pi..pi.</summary>
    public static float Wrap(float a)
    {
        a %= Quant.TwoPi;
        if (a > MathF.PI) a -= Quant.TwoPi;
        else if (a < -MathF.PI) a += Quant.TwoPi;
        return a;
    }

    public static float Approach(float v, float target, float step) => v < target ? MathF.Min(target, v + step) : MathF.Max(target, v - step);

    /// <summary>A vector of the vehicle's frame turned into the world by the airframe's attitude (Godot's order: yaw, then pitch, then roll).</summary>
    public static Vector3 Rotate(Vector3 v, float yaw, float pitch, float roll)
    {
        var (sr, cr) = MathF.SinCos(roll);
        var x = v.X * cr - v.Y * sr; var y = v.X * sr + v.Y * cr; var z = v.Z;      // roll about z
        var (sp, cp) = MathF.SinCos(pitch);
        var y2 = y * cp - z * sp; var z2 = y * sp + z * cp;                          // pitch about x
        var (sy, cy) = MathF.SinCos(yaw);
        return new Vector3(x * cy + z2 * sy, y2, -x * sy + z2 * cy);                 // yaw about y
    }

    /// <summary>The inverse of <see cref="Rotate"/>: a world vector in the vehicle's frame.</summary>
    public static Vector3 Unrotate(Vector3 v, float yaw, float pitch, float roll)
    {
        var (sy, cy) = MathF.SinCos(yaw);
        var x = v.X * cy - v.Z * sy; var z = v.X * sy + v.Z * cy; var y = v.Y;
        var (sp, cp) = MathF.SinCos(pitch);
        var y2 = y * cp + z * sp; var z2 = -y * sp + z * cp;
        var (sr, cr) = MathF.SinCos(roll);
        return new Vector3(x * cr + y2 * sr, -x * sr + y2 * cr, z2);
    }
}

/// <summary>
/// The utility helicopter's flight (ADR 0006): an assisted, attitude-driven arcade model, pure and stepped at the fixed tick by the server and,
/// from the same inputs, by the pilot's client. Every number is the type's <see cref="VehicleFlightDef"/>.
///
/// The keys ask for a tilt, the airframe swings to it on a critically damped spring, and the tilt is what accelerates it; drag sets the top speed.
/// Hands off, the stabiliser tilts against the motion (a flare) until it stands still, and the vertical speed returns to zero, so it holds the hover
/// where it is. The nose follows the pilot's view; in forward flight it banks into the turn and the direction of travel swings round with the nose.
/// Near the ground the descent is slowed to a touchdown that costs nothing; what a surface takes from the speed in one step is reported as
/// <see cref="FlightState.Impact"/> for the crash damage rules.
/// </summary>
public static class FlightModel
{
    /// <summary>A surface this upright (normal y) is ground to stand on.</summary>
    const float GroundNormalY = 0.7f;
    /// <summary>Below this speed on the skids it has come to rest, m/s.</summary>
    const float RestSpeed = 0.3f;
    /// <summary>The skids are "in" water this far above its surface, metres.</summary>
    const float WaterTouch = 0.3f;
    /// <summary>The rate a grounded airframe settles level at, rad/s.</summary>
    const float SettleRate = 1.5f;
    /// <summary>Below these a held hover is taken as exactly still (m/s, and radians or rad/s for the springs), so nothing creeps and no float lingers near zero.</summary>
    const float StillSpeed = 1e-4f, StillAngle = 1e-6f;

    /// <summary>The quadratic drag that, with <see cref="VehicleFlightDef.DragLow"/>, makes full tilt end at the type's top speed.</summary>
    public static float DragHigh(VehicleDef def) => MathF.Max(0f, (def.Acceleration / def.TopSpeed - def.Flight!.DragLow) / def.TopSpeed);

    public static void Step(ref FlightState s, VehicleDef def, in FlightInput input, float dt, IFlightWorld world)
    {
        var f = def.Flight!;
        s.Impact = 0; s.AtEdge = false;
        if (input.Piloted) s.PilotlessTime = 0; else s.PilotlessTime += dt;
        var mx = input.Piloted ? Math.Clamp(input.MoveX, -1f, 1f) : 0f;
        var my = input.Piloted ? Math.Clamp(input.MoveY, -1f, 1f) : 0f;
        var lift = input.Piloted ? Math.Sign(input.Lift) : 0;
        // nobody flying: a hover while riders might still take the seat, then down; an empty one goes down at once
        var sink = !input.Piloted && (!input.Occupied || s.PilotlessTime >= f.PilotlessHoverTime);

        // rotor: runs while there is a pilot or it is in the air, and stops on the ground without one
        var rotorTarget = input.Piloted || !s.Grounded ? 1f : 0f;
        s.Rotor = FlightMath.Approach(s.Rotor, rotorTarget, dt / (rotorTarget > s.Rotor ? f.SpoolUpTime : f.SpoolDownTime));
        var powered = s.Rotor >= f.LiftoffRotor;

        s.InputX = Ramp(s.InputX, mx, f.InputRise, f.InputFall, dt);
        s.InputY = Ramp(s.InputY, my, f.InputRise, f.InputFall, dt);

        var water = world.WaterY(s.Position.X, s.Position.Z);
        var floor = MathF.Max(world.GroundY(s.Position), water);
        s.Agl = float.IsNegativeInfinity(floor) ? float.PositiveInfinity : MathF.Max(0f, s.Position.Y - floor);
        s.InWater = !float.IsNegativeInfinity(water) && s.Position.Y <= water + WaterTouch;

        if (s.Grounded)
        {
            // on the skids: it stands until the rotor is up and the pilot pulls it off the ground
            s.Velocity = Vector3.Zero;
            s.YawRate = s.PitchRate = s.RollRate = 0;
            s.Pitch = FlightMath.Approach(s.Pitch, 0, SettleRate * dt);
            s.Roll = FlightMath.Approach(s.Roll, 0, SettleRate * dt);
            if (!(powered && lift > 0)) return;
            s.Grounded = false;
        }

        // ---- attitude: what the keys ask for, plus the stabiliser's flare on each axis the keys leave alone
        var (sy, cy) = MathF.SinCos(s.Yaw);
        var fwd = new Vector3(-sy, 0, -cy); var right = new Vector3(cy, 0, -sy);
        var vF = Vector3.Dot(s.Velocity, fwd); var vR = Vector3.Dot(s.Velocity, right);
        float maxPitch = f.MaxPitchDeg * FlightMath.Deg2Rad, maxBack = f.MaxPitchBackDeg * FlightMath.Deg2Rad, maxRoll = f.MaxRollDeg * FlightMath.Deg2Rad;
        float brakeGain = f.BrakeGainDeg * FlightMath.Deg2Rad, brakeTilt = f.BrakeTiltDeg * FlightMath.Deg2Rad;
        var pitchTarget = -s.InputY * (s.InputY >= 0 ? maxPitch : maxBack) + (1f - MathF.Abs(s.InputY)) * Math.Clamp(vF * brakeGain, -brakeTilt, brakeTilt);
        var rollTarget = -s.InputX * maxRoll + (1f - MathF.Abs(s.InputX)) * Math.Clamp(vR * brakeGain, -brakeTilt, brakeTilt);
        // lean into a turn in forward flight
        var turnBank = f.TurnBankMaxDeg * FlightMath.Deg2Rad;
        rollTarget += Math.Clamp(MathF.Max(0f, vF) * s.YawRate * f.TurnBankGain * FlightMath.Deg2Rad, -turnBank, turnBank);
        var rollLimit = MathF.Max(maxRoll, turnBank);
        rollTarget = Math.Clamp(rollTarget, -rollLimit, rollLimit);
        Spring(ref s.Pitch, ref s.PitchRate, pitchTarget - s.Pitch, f.AttitudeResponse, dt);
        Spring(ref s.Roll, ref s.RollRate, rollTarget - s.Roll, f.AttitudeResponse, dt);

        // ---- yaw: the nose swings to the view, never faster than the yaw rate
        var maxYawRate = f.MaxYawRateDeg * FlightMath.Deg2Rad;
        var yawError = input.Piloted && !input.HoldHeading ? FlightMath.Wrap(input.Yaw - s.Yaw) : 0f;
        s.YawRate += (f.YawResponse * f.YawResponse * yawError - 2f * f.YawResponse * s.YawRate) * dt;
        s.YawRate = Math.Clamp(s.YawRate, -maxYawRate, maxYawRate);
        if (MathF.Abs(yawError) < StillAngle && MathF.Abs(s.YawRate) < StillAngle) s.YawRate = 0;
        s.Yaw = FlightMath.Wrap(s.Yaw + s.YawRate * dt);
        (sy, cy) = MathF.SinCos(s.Yaw);
        fwd = new Vector3(-sy, 0, -cy); right = new Vector3(cy, 0, -sy);

        // ---- horizontal: the tilt accelerates, drag opposes; full nose-down ends at the type's top speed
        var aF = -def.Acceleration * MathF.Sin(s.Pitch) / MathF.Sin(maxPitch);
        var aR = -f.StrafeAcceleration * MathF.Sin(s.Roll) / MathF.Sin(maxRoll);
        var vh = new Vector3(s.Velocity.X, 0, s.Velocity.Z);
        var speed = vh.Length();
        vF = Vector3.Dot(vh, fwd); vR = Vector3.Dot(vh, right);
        // the hull is slippery nose first and a barn door any other way: sideways and backwards it meets more drag
        var drag = f.DragLow + DragHigh(def) * speed;
        vh += (fwd * (aF - vF * drag * (vF < 0 ? f.OffAxisDrag : 1f)) + right * (aR - vR * drag * f.OffAxisDrag)) * dt;
        // hands off, a little constant deceleration ends the last creep: the hover is exactly still
        var free = (1f - MathF.Abs(s.InputX)) * (1f - MathF.Abs(s.InputY));
        speed = vh.Length();
        if (free > 0) vh = speed > f.HoverDecel * free * dt ? vh * ((speed - f.HoverDecel * free * dt) / speed) : Vector3.Zero;
        // coordination: in forward flight the direction of travel swings round to the nose (the speed is kept)
        vF = Vector3.Dot(vh, fwd); vR = Vector3.Dot(vh, right);
        speed = vh.Length();
        if (vF > 0 && speed > 0.5f && f.TurnGrip > 0)
        {
            var off = MathF.Atan2(vR, vF);
            var k = 1f - MathF.Exp(-f.TurnGrip * dt * MathF.Min(1f, vF / f.CoordinationSpeed) * (1f - MathF.Abs(s.InputX)));
            var (so, co) = MathF.SinCos(off * (1f - k));
            vh = fwd * (speed * co) + right * (speed * so);
        }

        // ---- vertical: the keys set a climb or descent rate; no key, no vertical speed
        float targetVy;
        if (sink) targetVy = -f.AutorotateDescent;
        else if (lift > 0) targetVy = f.ClimbRate * (float.IsPositiveInfinity(s.Agl) ? 0f : Math.Clamp((f.Ceiling - s.Agl) / f.CeilingFade, 0f, 1f));
        else if (lift < 0) targetVy = -f.DescendRate;
        else targetVy = 0;
        // the cushion: the closer the ground, the slower it may come down, whatever asked for the descent
        var allowed = float.IsPositiveInfinity(s.Agl) ? float.PositiveInfinity
            : f.TouchdownSpeed + MathF.Max(0f, f.DescendRate - f.TouchdownSpeed) * Math.Clamp(s.Agl / f.CushionHeight, 0f, 1f);
        targetVy = MathF.Max(targetVy, -allowed);
        var vy = s.Velocity.Y + Math.Clamp((targetVy - s.Velocity.Y) * f.VerticalResponse, -f.VerticalAccel, f.VerticalAccel) * dt;
        vy = MathF.Max(vy, -allowed);
        if (targetVy == 0 && MathF.Abs(vy) < StillSpeed) vy = 0;
        s.Velocity = new Vector3(vh.X, vy, vh.Z);

        // ---- the map's edge: the speed towards it is held down to nothing at the edge
        var b = world.Bounds;
        s.Velocity.X = EdgeLimit(s.Velocity.X, s.Position.X, b.MinX, b.MaxX, def.TopSpeed, f.EdgeMargin, ref s.AtEdge);
        s.Velocity.Z = EdgeLimit(s.Velocity.Z, s.Position.Z, b.MinZ, b.MaxZ, def.TopSpeed, f.EdgeMargin, ref s.AtEdge);

        // ---- move: what a surface takes from the speed is the impact; then slide along it once
        var landed = false;
        var target = s.Position + s.Velocity * dt;
        var move = world.Move(s.Position, s.Yaw, s.Velocity * dt);
        if (move.Hit)
        {
            Strike(ref s, move.Normal, ref landed);
            var rest = target - move.Position;
            rest -= move.Normal * Vector3.Dot(rest, move.Normal);
            if (rest.LengthSquared() > 1e-8f)
            {
                move = world.Move(move.Position, s.Yaw, rest);
                if (move.Hit) Strike(ref s, move.Normal, ref landed);
            }
        }
        s.Position = move.Position;

        // water is not solid to the physics, but the helicopter does not go under it
        if (!float.IsNegativeInfinity(water) && s.Position.Y < water)
        {
            s.Position.Y = water;
            if (s.Velocity.Y < 0) { s.Impact = MathF.Max(s.Impact, -s.Velocity.Y); s.Velocity.Y = 0; }
        }
        s.InWater = !float.IsNegativeInfinity(water) && s.Position.Y <= water + WaterTouch;

        if (landed)
        {
            // the skids slide to a stop; at rest, and not being pulled up, it stands
            vh = new Vector3(s.Velocity.X, 0, s.Velocity.Z);
            speed = vh.Length();
            if (speed > 0) vh *= MathF.Max(0f, speed - f.GroundFriction * dt) / speed;
            s.Velocity = new Vector3(vh.X, s.Velocity.Y, vh.Z);
            if (vh.Length() < RestSpeed && lift <= 0)
            {
                s.Grounded = true;
                s.Velocity = Vector3.Zero;
                s.YawRate = s.PitchRate = s.RollRate = 0;
            }
        }
    }

    /// <summary>A surface stops the motion into it: the speed lost is the impact; an upright surface is ground.</summary>
    static void Strike(ref FlightState s, Vector3 normal, ref bool landed)
    {
        var into = Vector3.Dot(s.Velocity, normal);
        if (into < 0)
        {
            s.Impact = MathF.Max(s.Impact, -into);
            s.Velocity -= normal * into;
        }
        if (normal.Y > GroundNormalY) landed = true;
    }

    /// <summary>The speed along one axis, held to top speed times (distance to that edge / margin) towards the edge it approaches.</summary>
    static float EdgeLimit(float v, float p, float min, float max, float top, float margin, ref bool atEdge)
    {
        if (v > 0 && !float.IsPositiveInfinity(max))
        {
            var limit = margin <= 0 ? (p < max ? top : 0f) : top * Math.Clamp((max - p) / margin, 0f, 1f);
            if (v > limit) { atEdge = true; return limit; }
        }
        else if (v < 0 && !float.IsNegativeInfinity(min))
        {
            var limit = margin <= 0 ? (p > min ? top : 0f) : top * Math.Clamp((p - min) / margin, 0f, 1f);
            if (-v > limit) { atEdge = true; return -limit; }
        }
        return v;
    }

    /// <summary>The keyboard ramp: towards a held key at the rise rate, back towards the centre (or the other way) at the fall rate.</summary>
    static float Ramp(float value, float target, float rise, float fall, float dt)
    {
        var away = MathF.Abs(target) > MathF.Abs(value) && value * target >= 0;
        return FlightMath.Approach(value, target, (away ? rise : fall) * dt);
    }

    /// <summary>
    /// One step of a critically damped spring pulling <paramref name="value"/> by <paramref name="error"/> (target minus value). The rate is
    /// updated first and then applied, which for response x dt below 0.5 never rings (<see cref="VehicleFlightDef.Validate"/> holds that).
    /// </summary>
    static void Spring(ref float value, ref float rate, float error, float response, float dt)
    {
        rate += (response * response * error - 2f * response * rate) * dt;
        value += rate * dt;
        if (MathF.Abs(error) < StillAngle && MathF.Abs(rate) < StillAngle) { value += error - rate * dt; rate = 0; }
    }
}

/// <summary>
/// How a flying vehicle's attitude and velocity travel in a snapshot (entity type <see cref="EntityType.Flight"/>), one record per flying
/// vehicle next to its <see cref="VehicleRecord"/> (which keeps the position, the yaw and the pilot's id). The position fields carry the
/// velocity (m/s, in the position's 1/128 units), the yaw field the roll, the pitch field the pitch, the health byte the rotor speed and the
/// weapon byte the flags. Every client tilts the model and turns the rotor from it; the pilot's client reconciles its prediction against it.
/// </summary>
public static class FlightRecord
{
    /// <summary>First flight record id: above the vehicles (0x4000 + index), below the turret aims (0x7000).</summary>
    public const ushort FirstId = 0x6000;
    public const byte GroundedBit = 1, InWaterBit = 2, AtEdgeBit = 4;

    public static ushort IdOf(ushort carId) => (ushort)(FirstId + (carId - VehicleRecord.FirstId));
    public static ushort CarOf(ushort recordId) => (ushort)(VehicleRecord.FirstId + (recordId - FirstId));

    public static EntityRecord Make(ushort carId, in FlightState s)
    {
        var flags = (s.Grounded ? GroundedBit : 0) | (s.InWater ? InWaterBit : 0) | (s.AtEdge ? AtEdgeBit : 0);
        return EntityRecord.Make(IdOf(carId), EntityType.Flight, s.Velocity, s.Roll, s.Pitch, s.Rotor * 255f, EntityState.Alive, (byte)flags);
    }

    public static Vector3 Velocity(in EntityRecord r) => r.Position;
    public static float Roll(in EntityRecord r) => FlightMath.Wrap(r.YawRad);
    public static float Pitch(in EntityRecord r) => r.PitchRad;
    public static float Rotor(in EntityRecord r) => r.Health / 255f;
    public static bool Grounded(in EntityRecord r) => (r.Weapon & GroundedBit) != 0;
    public static bool InWater(in EntityRecord r) => (r.Weapon & InWaterBit) != 0;
    public static bool AtEdge(in EntityRecord r) => (r.Weapon & AtEdgeBit) != 0;

    /// <summary>The replicated part of a flight state from a vehicle's two records; what is not on the wire (rates, the key ramp) is left zero.</summary>
    public static FlightState State(in EntityRecord vehicle, in EntityRecord flight) => new()
    {
        Position = vehicle.Position, Yaw = FlightMath.Wrap(vehicle.YawRad),
        Velocity = Velocity(flight), Pitch = Pitch(flight), Roll = Roll(flight),
        Rotor = Rotor(flight), Grounded = Grounded(flight), InWater = InWater(flight), AtEdge = AtEdge(flight),
    };
}

/// <summary>
/// The pilot's client-side prediction (ADR 0006): it steps its own <see cref="FlightState"/> with every input it sends, remembers the state after
/// each, and when a snapshot acknowledges an input it compares what it predicted for that input with what the server had. A difference beyond
/// the tolerances puts the server's state in place and replays the inputs the server has not yet seen. The player on foot is predicted the same
/// way; this is the first vehicle that is.
/// </summary>
public sealed class FlightPrediction
{
    /// <summary>Tolerances before a correction: metres, m/s and radians (the snapshot's quantisation is well inside them).</summary>
    public const float PositionEpsilon = 0.05f, VelocityEpsilon = 0.3f, AngleEpsilon = 0.01f;
    const int MaxPending = 120;

    readonly VehicleDef _def;
    readonly IFlightWorld _world;
    readonly List<(ushort Seq, FlightInput Input, FlightState After)> _pending = new();

    public FlightState State;
    /// <summary>Corrections made so far, and the largest position error seen at an acknowledged input.</summary>
    public int Corrections { get; private set; }
    public float MaxError { get; private set; }

    public FlightPrediction(VehicleDef def, IFlightWorld world, FlightState start)
    {
        _def = def; _world = world; State = start;
    }

    public int PendingCount => _pending.Count;

    /// <summary>Steps the prediction with the input just sent under <paramref name="seq"/>.</summary>
    public void Step(ushort seq, in FlightInput input, float dt)
    {
        FlightModel.Step(ref State, _def, input, dt, _world);
        _pending.Add((seq, input, State));
        if (_pending.Count > MaxPending) _pending.RemoveAt(0);
    }

    /// <summary>
    /// The server's state after it processed input <paramref name="ack"/>. Returns true when the prediction had to be corrected. Inputs up to the
    /// acknowledged one are forgotten; an acknowledgement of an input this prediction never stepped (sent before the pilot sat down) is ignored.
    /// </summary>
    public bool Reconcile(ushort ack, in FlightState server, float dt)
    {
        var index = _pending.FindIndex(p => p.Seq == ack);
        if (index < 0) return false;
        var predicted = _pending[index].After;
        _pending.RemoveRange(0, index + 1);
        var error = Vector3.Distance(predicted.Position, server.Position);
        MaxError = MathF.Max(MaxError, error);
        if (error <= PositionEpsilon && Vector3.Distance(predicted.Velocity, server.Velocity) <= VelocityEpsilon
            && MathF.Abs(FlightMath.Wrap(predicted.Yaw - server.Yaw)) <= AngleEpsilon && MathF.Abs(predicted.Pitch - server.Pitch) <= AngleEpsilon
            && MathF.Abs(predicted.Roll - server.Roll) <= AngleEpsilon && predicted.Grounded == server.Grounded)
            return false;
        Corrections++;
        // the server's word for what it sends; our own guess for what it does not (rates, the key ramp)
        var s = predicted;
        s.Position = server.Position; s.Velocity = server.Velocity;
        s.Yaw = server.Yaw; s.Pitch = server.Pitch; s.Roll = server.Roll;
        s.Rotor = server.Rotor; s.Grounded = server.Grounded;
        for (var i = 0; i < _pending.Count; i++)
        {
            FlightModel.Step(ref s, _def, _pending[i].Input, dt, _world);
            _pending[i] = (_pending[i].Seq, _pending[i].Input, s);
        }
        State = s;
        return true;
    }
}
