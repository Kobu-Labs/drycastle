using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>
/// Everything <see cref="DriveModel"/> carries from tick to tick for one driven vehicle (the amphibious transport). The server owns the true one;
/// the driver's client steps its own copy from the same inputs (ADR 0007). Angles are radians in Godot's sense: yaw 0 faces -Z and positive turns
/// left, positive pitch raises the nose, positive roll drops the left side.
/// </summary>
public struct DriveState
{
    /// <summary>The origin: on the ground under the centre, at the wheels' underside. Afloat it rides the draft under the surface.</summary>
    public Vector3 Position;
    public float Yaw, YawRate;
    /// <summary>Speed along the heading, m/s (negative: backwards), and sideways (positive: to the right), the slide.</summary>
    public float Speed, Slip;
    /// <summary>Vertical speed, m/s: falling through the air, or the hull rising and sinking to its waterline.</summary>
    public float VerticalSpeed;
    /// <summary>The slope of the ground under the four wheels; level afloat.</summary>
    public float Pitch, Roll;
    /// <summary>The keys after the ramp, -1..1: forward, and to the right.</summary>
    public float Throttle, Steer;
    /// <summary>Floating in a water body: it is the boat now.</summary>
    public bool Afloat;
    /// <summary>The wheels are off the ground (driven off a ledge): no drive, no steering, until it lands.</summary>
    public bool Airborne;
    /// <summary>Just out of the water: how far the wheels still have to haul it up onto the bank, metres under where it will stand (0 on dry ground).</summary>
    public float Sink;
    /// <summary>Just out of the water and still climbing onto the bank.</summary>
    public readonly bool Emerging => Sink > 0;

    // ---- what the last step found (not carried into the next one)
    /// <summary>Speed lost against an obstacle in the last step, m/s: the crash damage rules read it.</summary>
    public float Impact;
    /// <summary>It went into the water, or came out of it, in the last step.</summary>
    public bool EnteredWater, LeftWater;

    /// <summary>The seat, gun or lamp at <paramref name="offset"/> (the vehicle's frame: x right, y up, z back) in the world, on the slope it stands on.</summary>
    public readonly Vector3 ToWorld(float[] offset) => Position + FlightMath.Rotate(new Vector3(offset[0], offset[1], offset[2]), Yaw, Pitch, Roll);

    /// <summary>The velocity in the world.</summary>
    public readonly Vector3 Velocity
    {
        get
        {
            var (sy, cy) = MathF.SinCos(Yaw);
            return new Vector3(-sy * Speed + cy * Slip, VerticalSpeed, -cy * Speed - sy * Slip);
        }
    }
}

/// <summary>One tick of the driver's controls, as both sides read them from the same <see cref="NetInput"/>. The default is hands off (nobody driving).</summary>
public struct DriveInput
{
    /// <summary>The movement keys, -1..1: forward (back brakes, then reverses), and to the right.</summary>
    public float Throttle, Steer;
    /// <summary>Jump: the handbrake.</summary>
    public bool Handbrake;

    /// <summary>The driver's input for one tick. <paramref name="handsOff"/>: the driver is busy elsewhere (steering a killstreak), so the keys are not the vehicle's.</summary>
    public static DriveInput FromNet(in NetInput i, bool handsOff = false) => handsOff ? default : new DriveInput
    {
        Throttle = Quant.Axis(i.MoveY), Steer = Quant.Axis(i.MoveX), Handbrake = ((MoveButtons)i.Buttons & MoveButtons.Jump) != 0,
    };

    /// <summary>Nobody at the wheel, or a driver whose inputs are not arriving: it coasts.</summary>
    public static DriveInput None => default;
}

/// <summary>Where a move ended, and the surface that stopped it (unit normal, pointing away from the surface).</summary>
public readonly record struct DriveMove(Vector3 Position, bool Hit, Vector3 Normal);

/// <summary>
/// The world as the drive model sees it. The game answers with the physics space (<c>DriveProbe</c>), the same way on the server and on the
/// driver's client; the tests answer with a field, a pond and a wall.
/// </summary>
public interface IDriveWorld
{
    /// <summary>Height of the highest solid under <paramref name="from"/> that a wheel there could stand on (no higher than a step above it), or negative infinity when there is none in reach.</summary>
    float GroundY(Vector3 from);
    /// <summary>Surface height of the water body over (x, z), or negative infinity.</summary>
    float WaterY(float x, float z);
    /// <summary>Moves the hull (lifted clear of the ground it stands on, lying on the slope given) from <paramref name="from"/> by <paramref name="motion"/> until something solid stops it.</summary>
    DriveMove Move(Vector3 from, float yaw, float pitch, float roll, Vector3 motion);
}

/// <summary>
/// The amphibious transport's driving (ADR 0007): a planar arcade model, pure and stepped at the fixed tick by the server and, from the same
/// inputs, by the driver's client. Every number is the type's <see cref="VehicleDriveDef"/> and its basic driving numbers.
///
/// The keys are ramped, so a tap is a small correction. On its wheels it pulls hard from a standstill and eases off towards top speed, the key
/// against the travel brakes, and the steering sets a circle that widens with speed and is held to what the tyres grip; the handbrake lets the
/// tail slide. In a water body it floats and is a boat: thrust against a drag that grows with the square of the speed (so it slows hard coming in
/// fast, and carries its way when the throttle is lifted), a lazy helm that also turns it from a standstill under power, a stern that swings out,
/// and sideways speed the water only slowly takes away. Going in, the hull drops to its waterline and bobs up; coming out, the wheels haul it up
/// the bank. What an obstacle takes from the speed in one step is reported as <see cref="DriveState.Impact"/> for the crash damage rules.
/// </summary>
public static class DriveModel
{
    /// <summary>A surface this upright (normal y) is ground to ride over, not a wall.</summary>
    const float GroundNormalY = 0.6f;
    /// <summary>Afloat, the slope it left settles level at this rate, rad/s.</summary>
    const float SettleRate = 2f;
    /// <summary>The steepest the body lies on the ground, radians.</summary>
    const float MaxTilt = 0.6f;
    /// <summary>A wheel over a drop hangs: it pulls the body down no further than this below the highest wheel, metres.</summary>
    const float WheelDroop = 1.2f;
    /// <summary>Falling, it goes afloat once the wheels are within this of the surface, metres.</summary>
    const float SurfaceTouch = 0.05f;
    /// <summary>Below these the vehicle is taken as exactly still, so nothing creeps.</summary>
    const float StillSpeed = 1e-3f, StillRate = 1e-4f;
    /// <summary>A key (or a ramp) this far off the centre counts as pressed.</summary>
    const float Dead = 0.02f;

    /// <summary>The circle it steers on its wheels at a speed, metres, before the grip limit: the lock at a standstill, widening with speed (as every car's).</summary>
    public static float TurnRadiusAt(VehicleDef def, float speed) => CarModel.TurnRadiusAt(def, speed);

    /// <summary>The hull's drag afloat, per metre: with the linear <see cref="DriveWaterDef.CoastDrag"/> it makes full throttle end at the type's water top speed.</summary>
    public static float WaterDrag(VehicleDef def) =>
        (def.WaterAcceleration - def.Drive!.Water.CoastDrag * def.WaterTopSpeed) / (def.WaterTopSpeed * def.WaterTopSpeed);

    public static void Step(ref DriveState s, VehicleDef def, in DriveInput input, float dt, IDriveWorld world)
    {
        var d = def.Drive!;
        var land = d.Land; var wet = d.Water;
        s.Impact = 0; s.EnteredWater = false; s.LeftWater = false;
        var key = Math.Clamp(input.Throttle, -1f, 1f);
        var wheel = Math.Clamp(input.Steer, -1f, 1f);

        // ---- the keyboard ramp: the steering winds on more slowly the faster it goes
        s.Throttle = Ramp(s.Throttle, key, d.ThrottleRise, d.ThrottleFall, dt);
        var calm = 1f - (1f - d.SteerCalm) * Math.Clamp(MathF.Abs(s.Speed) / def.TopSpeed, 0f, 1f);
        s.Steer = Ramp(s.Steer, wheel, d.SteerRise * calm, d.SteerFall, dt);

        // ---- land or water: it floats inside a water body where the water over the ground is deep enough
        var wasAfloat = s.Afloat;
        var water = def.CanFloat ? world.WaterY(s.Position.X, s.Position.Z) : float.NegativeInfinity;
        if (float.IsNegativeInfinity(water)) s.Afloat = false;
        else
        {
            var bed = world.GroundY(new Vector3(s.Position.X, MathF.Max(s.Position.Y, water), s.Position.Z));
            var depth = float.IsNegativeInfinity(bed) ? float.PositiveInfinity : water - bed;
            s.Afloat = depth >= (wasAfloat ? wet.FloatDepth * 0.5f : wet.FloatDepth) && (wasAfloat || s.Position.Y <= water + SurfaceTouch);
        }
        if (s.Afloat && !wasAfloat) { s.EnteredWater = true; s.Airborne = false; s.Sink = 0; }
        if (!s.Afloat && wasAfloat) { s.LeftWater = true; s.VerticalSpeed = 0; }

        var u = s.Speed; var w = s.Slip;
        float wantYaw, yawResponse;
        if (s.Afloat)
        {
            // ---- the boat: thrust against the hull's drag; astern there is only a weaker thrust, no brake
            var thrust = def.WaterAcceleration * (s.Throttle >= 0 ? s.Throttle : s.Throttle * wet.ReverseThrust);
            u += (thrust - WaterDrag(def) * u * MathF.Abs(u) - wet.CoastDrag * u) * dt;
            if (MathF.Abs(s.Throttle) < Dead && MathF.Abs(u) < StillSpeed) u = 0;
            wantYaw = -s.Steer * (u / wet.TurnRadius + s.Throttle * wet.PropSteer);
            yawResponse = wet.YawResponse;
        }
        else if (s.Airborne)
        {
            wantYaw = 0; yawResponse = 1f; // in the air nothing drives or steers it; the turn it had dies away
        }
        else
        {
            // ---- on its wheels
            var braking = key > Dead && u < -StillSpeed || key < -Dead && u > StillSpeed;
            if (input.Handbrake) u = FlightMath.Approach(u, 0, land.HandbrakeDecel * dt);
            else if (braking)
            {
                // the key against the travel: part of the brake bites at once, the rest comes in with the ramp
                var bite = land.BrakeBite + (1f - land.BrakeBite) * Math.Clamp(-MathF.Sign(u) * s.Throttle, 0f, 1f);
                u = FlightMath.Approach(u, 0, def.Brake * bite * dt);
            }
            else if (MathF.Abs(s.Throttle) > Dead && key * s.Throttle >= 0)
            {
                // driven (a released key's ramp still pushes while it runs out, so lifting off is not a step)
                var limit = s.Throttle > 0 ? def.TopSpeed : def.TopSpeed * land.ReverseTopFraction;
                var along = u * MathF.Sign(s.Throttle); // the speed in the direction it is driven
                var launch = 1f + (land.LaunchBoost - 1f) * MathF.Max(0f, 1f - MathF.Abs(u) / land.LaunchSpeed);
                var fade = 1f - MathF.Pow(Math.Clamp(along / limit, 0f, 1f), land.PowerCurve);
                if (along <= limit) u += def.Acceleration * launch * fade * s.Throttle * dt;
                else u = FlightMath.Approach(u, limit * MathF.Sign(s.Throttle), (land.CoastDecel + land.CoastDrag * MathF.Abs(u)) * dt); // faster than it can drive (out of a drop): it rolls down to it
            }
            else u = FlightMath.Approach(u, 0, (land.CoastDecel + land.CoastDrag * MathF.Abs(u)) * dt);

            // the wheel sets a circle; the tyres hold only so much sideways, which caps the yaw rate at speed
            wantYaw = -s.Steer * u / TurnRadiusAt(def, u);
            var hold = MathF.Abs(u) > 0.5f ? land.Grip / MathF.Abs(u) : float.PositiveInfinity;
            wantYaw = Math.Clamp(wantYaw, -hold, hold) * (input.Handbrake ? land.HandbrakeSteer : 1f);
            yawResponse = land.YawResponse;
        }

        // ---- yaw: the rate follows what the wheel or the helm asks for; the heading turns, the motion keeps its own way
        s.YawRate += (wantYaw - s.YawRate) * (1f - MathF.Exp(-yawResponse * dt));
        if (MathF.Abs(wantYaw) < StillRate && MathF.Abs(s.YawRate) < StillRate) s.YawRate = 0;
        var turn = s.YawRate * dt;
        s.Yaw = FlightMath.Wrap(s.Yaw + turn);
        var (st, ct) = MathF.SinCos(turn);
        (u, w) = (u * ct - w * st, w * ct + u * st);
        // sideways: the tyres stop it (a slide only past their grip, or on the handbrake); the water takes it away slowly, so it slides wide
        if (s.Afloat) w *= MathF.Exp(-wet.SideDrag * dt);
        else if (!s.Airborne) w = FlightMath.Approach(w, 0, land.Grip * (input.Handbrake ? land.HandbrakeGrip : 1f) * dt);
        if (MathF.Abs(w) < StillSpeed) w = 0;

        // ---- move: what a wall takes from the speed is the impact; then slide along it once
        var (sy, cy) = MathF.SinCos(s.Yaw);
        var fwd = new Vector3(-sy, 0, -cy); var right = new Vector3(cy, 0, -sy);
        var swing = s.Afloat ? s.YawRate * wet.SternSwing : 0f; // afloat it pivots ahead of its centre: the stern swings out
        var vel = fwd * u + right * (w + swing);
        // afloat the hull is swept at the surface: a shallow bed is under the boat, not in its way
        var from = s.Afloat ? s.Position + Vector3.UnitY * wet.Draft : s.Position;
        var target = from + vel * dt;
        var move = world.Move(from, s.Yaw, s.Afloat ? 0f : s.Pitch, s.Afloat ? 0f : s.Roll, vel * dt);
        var struck = move.Hit;
        if (move.Hit)
        {
            var rest = Strike(ref vel, move.Normal, target - move.Position, ref s.Impact);
            if (rest.LengthSquared() > 1e-8f)
            {
                var end = move.Position + rest;
                move = world.Move(move.Position, s.Yaw, s.Afloat ? 0f : s.Pitch, s.Afloat ? 0f : s.Roll, rest);
                if (move.Hit) Strike(ref vel, move.Normal, end - move.Position, ref s.Impact);
            }
        }
        var travel = MathF.Sqrt((move.Position.X - s.Position.X) * (move.Position.X - s.Position.X) + (move.Position.Z - s.Position.Z) * (move.Position.Z - s.Position.Z));
        var pos = new Vector3(move.Position.X, s.Position.Y, move.Position.Z);
        if (struck) { u = Vector3.Dot(vel, fwd); w = Vector3.Dot(vel, right) - swing; } // what the wall left of the speed

        // ---- height and the lie of the body
        if (s.Afloat)
        {
            // buoyancy: the hull rises and sinks to its waterline on a spring, so it plunges going in, comes up and settles
            var k = wet.BuoyancyResponse;
            s.VerticalSpeed += (k * k * (water - wet.Draft - pos.Y) - 2f * wet.BuoyancyDamping * k * s.VerticalSpeed) * dt;
            pos.Y += s.VerticalSpeed * dt;
            s.Pitch = FlightMath.Approach(s.Pitch, 0, SettleRate * dt);
            s.Roll = FlightMath.Approach(s.Roll, 0, SettleRate * dt);
        }
        else if (!Fit(world, d, pos, s.Yaw, out var wheels))
        {
            // nothing under it: it falls
            s.Airborne = true; s.Sink = 0;
            s.VerticalSpeed -= MoveConsts.Gravity * dt;
            pos.Y += s.VerticalSpeed * dt;
        }
        else
        {
            var groundY = wheels.Y;
            // out of the water this tick: the wheels have found the bank, and it stands that far under where they will put it
            if (s.LeftWater) s.Sink = MathF.Max(0f, groundY - s.Position.Y);
            var stood = s.Position.Y + s.Sink; // the ground it stood on before this move
            var rise = groundY - stood;
            var climb = travel * MathF.Tan(land.MaxClimbDeg * FlightMath.Deg2Rad);
            if (!s.Airborne && !s.LeftWater && wheels.Step(d, stood, s.Pitch, s.Roll) > land.StepHeight + climb)
            {
                // a ledge too high for a wheel to climb: a wall. It stops where it was
                s.Impact = MathF.Max(s.Impact, MathF.Sqrt(u * u + w * w));
                pos = s.Position; u = 0; w = 0;
            }
            else if (s.Airborne || rise < -(land.SnapDown + climb))
            {
                // off the ground: it falls until the wheels meet it
                s.Airborne = true; s.Sink = 0;
                s.VerticalSpeed -= MoveConsts.Gravity * dt;
                pos.Y = s.Position.Y + s.VerticalSpeed * dt;
                if (pos.Y <= groundY) { pos.Y = groundY; s.Airborne = false; s.VerticalSpeed = 0; s.Pitch = wheels.Pitch; s.Roll = wheels.Roll; }
            }
            else
            {
                // on the ground. Out of the water the wheels haul it up the bank at their own rate (the heave), faster the faster it drives
                s.Sink = MathF.Max(0f, s.Sink - wet.ClimbOutRate * (1f + MathF.Abs(u) / def.WaterTopSpeed) * dt);
                pos.Y = groundY - s.Sink;
                s.VerticalSpeed = 0;
                s.Pitch = wheels.Pitch; s.Roll = wheels.Roll;
            }
        }
        s.Position = pos; s.Speed = u; s.Slip = w;
    }

    /// <summary>
    /// A surface stops the motion into it. A wall takes the speed into it (the impact) and the rest of the move slides along it; ground (a bump, a
    /// bank) takes nothing: the move goes on over it. Returns what is left of the move.
    /// </summary>
    static Vector3 Strike(ref Vector3 vel, Vector3 normal, Vector3 rest, ref float impact)
    {
        if (normal.Y > GroundNormalY) return rest - normal * Vector3.Dot(rest, normal);
        var flat = new Vector3(normal.X, 0, normal.Z);
        if (flat.LengthSquared() < 1e-6f) { impact = MathF.Max(impact, vel.Length()); vel = Vector3.Zero; return Vector3.Zero; } // something flat over it: it stops
        flat = Vector3.Normalize(flat);
        var into = Vector3.Dot(vel, flat);
        if (into < 0)
        {
            impact = MathF.Max(impact, -into);
            vel -= flat * into;
        }
        rest.Y = 0;
        return rest - flat * Vector3.Dot(rest, flat);
    }

    /// <summary>The ground under the four wheels: each wheel's height, the height the origin stands at (their mean) and the slope across them.</summary>
    readonly record struct Wheels(float FrontLeft, float FrontRight, float RearLeft, float RearRight, float Y, float Pitch, float Roll)
    {
        /// <summary>
        /// The highest any wheel has to step up from where the body's plane (standing at <paramref name="stood"/>, lying at the given pitch and
        /// roll) had it: on a steady slope nothing, at a kerb or a ledge its height.
        /// </summary>
        public float Step(VehicleDriveDef d, float stood, float pitch, float roll)
        {
            var ahead = MathF.Tan(pitch) * d.Wheelbase / 2f; var side = MathF.Tan(roll) * d.Track / 2f;
            return MathF.Max(MathF.Max(FrontLeft - (stood + ahead - side), FrontRight - (stood + ahead + side)),
                MathF.Max(RearLeft - (stood - ahead - side), RearRight - (stood - ahead + side)));
        }
    }

    /// <summary>
    /// The ground under the four wheels at <paramref name="pos"/>. A wheel with nothing under it, or over a drop, hangs. False when no wheel has
    /// ground in reach.
    /// </summary>
    static bool Fit(IDriveWorld world, VehicleDriveDef d, Vector3 pos, float yaw, out Wheels wheels)
    {
        var (sy, cy) = MathF.SinCos(yaw);
        var ahead = new Vector3(-sy, 0, -cy) * (d.Wheelbase / 2f); var side = new Vector3(cy, 0, -sy) * (d.Track / 2f);
        var fl = world.GroundY(pos + ahead - side);
        var fr = world.GroundY(pos + ahead + side);
        var rl = world.GroundY(pos - ahead - side);
        var rr = world.GroundY(pos - ahead + side);
        var highest = MathF.Max(MathF.Max(fl, fr), MathF.Max(rl, rr));
        wheels = default;
        if (float.IsNegativeInfinity(highest)) return false;
        fl = MathF.Max(fl, highest - WheelDroop); fr = MathF.Max(fr, highest - WheelDroop);
        rl = MathF.Max(rl, highest - WheelDroop); rr = MathF.Max(rr, highest - WheelDroop);
        wheels = new Wheels(fl, fr, rl, rr, (fl + fr + rl + rr) / 4f,
            Math.Clamp(MathF.Atan2((fl + fr - rl - rr) / 2f, d.Wheelbase), -MaxTilt, MaxTilt),
            Math.Clamp(MathF.Atan2((fr + rr - fl - rl) / 2f, d.Track), -MaxTilt, MaxTilt));
        return true;
    }

    /// <summary>
    /// Stands a vehicle that was just placed (a spawn, a return to its origin) on what is under it: afloat at its waterline in a water body,
    /// otherwise on the ground under its wheels, lying on its slope, at rest.
    /// </summary>
    public static void Settle(ref DriveState s, VehicleDef def, IDriveWorld world)
    {
        var d = def.Drive!;
        s.Speed = 0; s.Slip = 0; s.VerticalSpeed = 0; s.YawRate = 0; s.Throttle = 0; s.Steer = 0; s.Sink = 0; s.Airborne = false; s.Pitch = 0; s.Roll = 0;
        var water = def.CanFloat ? world.WaterY(s.Position.X, s.Position.Z) : float.NegativeInfinity;
        var bed = world.GroundY(new Vector3(s.Position.X, MathF.Max(s.Position.Y, water), s.Position.Z));
        s.Afloat = !float.IsNegativeInfinity(water) && (float.IsNegativeInfinity(bed) || water - bed >= d.Water.FloatDepth);
        if (s.Afloat) { s.Position.Y = water - d.Water.Draft; return; }
        if (!Fit(world, d, s.Position, s.Yaw, out var wheels)) return;
        s.Position.Y = wheels.Y; s.Pitch = wheels.Pitch; s.Roll = wheels.Roll;
    }

    /// <summary>The keyboard ramp: towards a held key at the rise rate, back towards the centre (or the other way) at the fall rate.</summary>
    static float Ramp(float value, float target, float rise, float fall, float dt)
    {
        var away = MathF.Abs(target) > MathF.Abs(value) && value * target >= 0;
        return FlightMath.Approach(value, target, (away ? rise : fall) * dt);
    }
}

/// <summary>
/// How a driven vehicle's motion travels in a snapshot (entity type <see cref="EntityType.Drive"/>), one record per vehicle with a
/// <see cref="VehicleDriveDef"/> next to its <see cref="VehicleRecord"/> (which keeps the position, the yaw and the driver's id). The position
/// fields carry the speeds in the vehicle's own frame (sideways, vertical, forward; m/s in the position's 1/128 units), the yaw field the roll
/// and the pitch field the pitch of the ground it stands on, the health byte the steering and the weapon byte the flags and the throttle. Every
/// client draws the body, the wheels and the propeller from it; the driver's client reconciles its prediction against it.
/// </summary>
public static class DriveRecord
{
    /// <summary>First drive record id: above the vehicles (0x4000 + index), below the flight records (0x6000).</summary>
    public const ushort FirstId = 0x5000;
    public const byte AfloatBit = 1, AirborneBit = 2, EmergingBit = 4;
    /// <summary>The throttle rides in three bits above the flags: 0 is full astern, 7 full ahead.</summary>
    const int ThrottleShift = 3;

    public static ushort IdOf(ushort carId) => (ushort)(FirstId + (carId - VehicleRecord.FirstId));
    public static ushort CarOf(ushort recordId) => (ushort)(VehicleRecord.FirstId + (recordId - FirstId));

    public static EntityRecord Make(ushort carId, in DriveState s)
    {
        var throttle = (int)MathF.Round((Math.Clamp(s.Throttle, -1f, 1f) + 1f) * 3.5f);
        var flags = (s.Afloat ? AfloatBit : 0) | (s.Airborne ? AirborneBit : 0) | (s.Emerging ? EmergingBit : 0) | (throttle << ThrottleShift);
        return EntityRecord.Make(IdOf(carId), EntityType.Drive, new Vector3(s.Slip, s.VerticalSpeed, s.Speed), s.Roll, s.Pitch,
            (Math.Clamp(s.Steer, -1f, 1f) + 1f) * 127.5f, EntityState.Alive, (byte)flags);
    }

    public static float Speed(in EntityRecord r) => r.Position.Z;
    public static float Slip(in EntityRecord r) => r.Position.X;
    public static float VerticalSpeed(in EntityRecord r) => r.Position.Y;
    public static float Roll(in EntityRecord r) => FlightMath.Wrap(r.YawRad);
    public static float Pitch(in EntityRecord r) => r.PitchRad;
    public static float Steer(in EntityRecord r) => r.Health / 127.5f - 1f;
    public static float Throttle(in EntityRecord r) => ((r.Weapon >> ThrottleShift) & 7) / 3.5f - 1f;
    public static bool Afloat(in EntityRecord r) => (r.Weapon & AfloatBit) != 0;
    public static bool Airborne(in EntityRecord r) => (r.Weapon & AirborneBit) != 0;
    public static bool Emerging(in EntityRecord r) => (r.Weapon & EmergingBit) != 0;

    /// <summary>The replicated part of a drive state from a vehicle's two records; what is not on the wire (the yaw rate) is left zero, and the ramps are coarse.</summary>
    public static DriveState State(in EntityRecord vehicle, in EntityRecord drive) => new()
    {
        Position = vehicle.Position, Yaw = FlightMath.Wrap(vehicle.YawRad),
        Speed = Speed(drive), Slip = Slip(drive), VerticalSpeed = VerticalSpeed(drive), Pitch = Pitch(drive), Roll = Roll(drive),
        Steer = Steer(drive), Throttle = Throttle(drive), Afloat = Afloat(drive), Airborne = Airborne(drive), Sink = Emerging(drive) ? EmergingSink : 0f,
    };

    /// <summary>The record says only that it is still climbing out of the water, not how far it has to go: a state read from it has this much left, metres.</summary>
    public const float EmergingSink = 0.01f;
}

/// <summary>
/// The driver's client-side prediction (ADR 0007), as the pilot's (<see cref="FlightPrediction"/>): it steps its own <see cref="DriveState"/> with
/// every input it sends, remembers the state after each, and when a snapshot acknowledges an input it compares what it predicted for that input
/// with what the server had. A difference beyond the tolerances puts the server's state in place and replays the inputs the server has not yet seen.
/// </summary>
public sealed class DrivePrediction
{
    /// <summary>Tolerances before a correction: metres, m/s and radians (the snapshot's quantisation is well inside them).</summary>
    public const float PositionEpsilon = 0.05f, SpeedEpsilon = 0.3f, AngleEpsilon = 0.01f;
    const int MaxPending = 120;

    readonly VehicleDef _def;
    readonly IDriveWorld _world;
    readonly List<(ushort Seq, DriveInput Input, DriveState After)> _pending = new();

    public DriveState State;
    /// <summary>Corrections made so far, and the largest position error seen at an acknowledged input.</summary>
    public int Corrections { get; private set; }
    public float MaxError { get; private set; }

    public DrivePrediction(VehicleDef def, IDriveWorld world, DriveState start)
    {
        _def = def; _world = world; State = start;
    }

    public int PendingCount => _pending.Count;

    /// <summary>Steps the prediction with the input just sent under <paramref name="seq"/>.</summary>
    public void Step(ushort seq, in DriveInput input, float dt)
    {
        DriveModel.Step(ref State, _def, input, dt, _world);
        _pending.Add((seq, input, State));
        if (_pending.Count > MaxPending) _pending.RemoveAt(0);
    }

    /// <summary>
    /// The server's state after it processed input <paramref name="ack"/>. Returns true when the prediction had to be corrected. Inputs up to the
    /// acknowledged one are forgotten; an acknowledgement of an input this prediction never stepped (sent before the driver sat down) is ignored.
    /// </summary>
    public bool Reconcile(ushort ack, in DriveState server, float dt)
    {
        var index = _pending.FindIndex(p => p.Seq == ack);
        if (index < 0) return false;
        var predicted = _pending[index].After;
        _pending.RemoveRange(0, index + 1);
        var error = Vector3.Distance(predicted.Position, server.Position);
        MaxError = MathF.Max(MaxError, error);
        if (error <= PositionEpsilon && MathF.Abs(predicted.Speed - server.Speed) <= SpeedEpsilon && MathF.Abs(predicted.Slip - server.Slip) <= SpeedEpsilon
            && MathF.Abs(FlightMath.Wrap(predicted.Yaw - server.Yaw)) <= AngleEpsilon && predicted.Afloat == server.Afloat && predicted.Airborne == server.Airborne)
            return false;
        Corrections++;
        // the server's word for what it sends; our own guess for what it does not, or sends only coarsely (the yaw rate, the key ramps)
        var s = predicted;
        s.Position = server.Position; s.Yaw = server.Yaw;
        s.Speed = server.Speed; s.Slip = server.Slip; s.VerticalSpeed = server.VerticalSpeed;
        s.Pitch = server.Pitch; s.Roll = server.Roll;
        s.Afloat = server.Afloat; s.Airborne = server.Airborne;
        s.Sink = server.Emerging ? MathF.Max(predicted.Sink, server.Sink) : 0f;
        for (var i = 0; i < _pending.Count; i++)
        {
            DriveModel.Step(ref s, _def, _pending[i].Input, dt, _world);
            _pending[i] = (_pending[i].Seq, _pending[i].Input, s);
        }
        State = s;
        return true;
    }
}

/// <summary>What <see cref="DriveBody"/> is told about the vehicle each frame: its motion as drawn (the driver's prediction, or the snapshots).</summary>
public readonly record struct DriveBodyInput(float Speed, float YawRate, float Throttle, bool Afloat, float GroundPitch, float GroundRoll);

/// <summary>
/// How the vehicle is drawn this frame, radians and metres. The whole vehicle lies on the ground's slope and, afloat, rides the water's motion
/// (<see cref="Pitch"/>, <see cref="Roll"/>, <see cref="Lift"/>); the body leans on its springs over the wheels (<see cref="BodyPitch"/>,
/// <see cref="BodyRoll"/>). Signs as <see cref="DriveState"/>'s: positive pitch raises the nose, positive roll drops the left side.
/// </summary>
public readonly record struct DriveBodyPose(float Pitch, float Roll, float Lift, float BodyPitch, float BodyRoll);

/// <summary>What <see cref="DriveBody"/> carries from frame to frame for one vehicle.</summary>
public struct DriveBodyState
{
    public float Pitch, PitchRate, Roll, RollRate;
    public float GroundPitch, GroundRoll;
    /// <summary>The acceleration along the heading as it is felt, m/s² (the speed's change, smoothed).</summary>
    public float Accel;
    /// <summary>0 on its wheels, 1 afloat, eased between them.</summary>
    public float Float;
    /// <summary>The bob's clock, seconds, and how stirred up the water is (0 at rest, 1 just after going in).</summary>
    public float Clock, Disturbed;
    public float LastSpeed, LastThrottle;
    public bool Started, WasAfloat;
}

/// <summary>
/// The weight of the amphibious transport as it is drawn (vehicles.json "drive.body"), pure so the tests see what every client draws. On its
/// wheels the tail squats under throttle, the nose dives under braking and the body leans out of a turn, on a spring that rocks once before it
/// settles. Afloat the bow rises under power, the hull heels out of a turn, wallows on a slower, looser spring, and bobs: gently at rest, harder
/// just after it has gone in or been driven hard, settling again. It changes nothing but the picture.
/// </summary>
public static class DriveBody
{
    /// <summary>The springs are stepped in slices no longer than this, so a slow frame cannot make them ring.</summary>
    const float Slice = 1f / 60f;
    /// <summary>Seconds over which the measured acceleration is smoothed, and over which the body changes between its land and water manners.</summary>
    const float AccelSmooth = 0.08f, MediumBlend = 0.4f;
    const float MaxAccel = 15f;

    public static DriveBodyPose Step(ref DriveBodyState b, VehicleDef def, in DriveBodyInput input, float dt)
    {
        var d = def.Drive!.Body;
        if (!b.Started) { b.Started = true; b.LastSpeed = input.Speed; b.LastThrottle = input.Throttle; b.WasAfloat = input.Afloat; b.Float = input.Afloat ? 1f : 0f; b.GroundPitch = input.GroundPitch; b.GroundRoll = input.GroundRoll; }
        dt = Math.Clamp(dt, 0f, 0.25f);
        if (dt <= 0f) return Pose(b, d);

        // what it feels: the change of speed along the heading, and the turn's pull sideways (positive: to the right)
        // (smoothed before it is limited: a speed that only changes on a tick is a spike on one frame and nothing on the next few)
        b.Accel += ((input.Speed - b.LastSpeed) / dt - b.Accel) * (1f - MathF.Exp(-dt / AccelSmooth));
        b.Accel = Math.Clamp(b.Accel, -MaxAccel, MaxAccel);
        b.LastSpeed = input.Speed;
        var sideways = Math.Clamp(-input.Speed * input.YawRate, -MaxAccel, MaxAccel);

        // going in stirs the water up, and so does throwing the throttle about; it settles again
        if (input.Afloat && !b.WasAfloat) b.Disturbed = 1f;
        if (input.Afloat) b.Disturbed = MathF.Min(1f, b.Disturbed + MathF.Abs(input.Throttle - b.LastThrottle) * 0.5f);
        b.Disturbed = MathF.Max(0f, b.Disturbed - dt / d.SettleTime);
        b.WasAfloat = input.Afloat; b.LastThrottle = input.Throttle;
        b.Float = FlightMath.Approach(b.Float, input.Afloat ? 1f : 0f, dt / MediumBlend);
        b.Clock += dt;

        var deg = FlightMath.Deg2Rad;
        float maxPitch = d.MaxPitchDeg * deg, maxRoll = d.MaxRollDeg * deg;
        // on its wheels: squat, dive and lean
        var landPitch = Math.Clamp(b.Accel * (b.Accel > 0 ? d.SquatDeg : d.DiveDeg) * deg, -maxPitch, maxPitch);
        var landRoll = Math.Clamp(sideways * d.LeanDeg * deg, -maxRoll, maxRoll);
        // afloat: the bow rises with the power and the speed, dips when the water checks it, and the hull heels out of a turn
        var top = MathF.Max(0.1f, def.WaterTopSpeed);
        var power = MathF.Max(0f, input.Throttle) * (0.35f + 0.65f * Math.Clamp(input.Speed / top, 0f, 1f));
        var wetPitch = Math.Clamp(d.BowRiseDeg * deg * power + MathF.Min(0f, b.Accel) * d.DiveDeg * deg, -maxPitch, MathF.Max(maxPitch, d.BowRiseDeg * deg));
        var wetRoll = Math.Clamp(sideways * d.WaterLeanDeg * deg, -maxRoll, maxRoll);
        var pitchTarget = landPitch + (wetPitch - landPitch) * b.Float;
        var rollTarget = landRoll + (wetRoll - landRoll) * b.Float;
        var k = d.Response + (d.WaterResponse - d.Response) * b.Float;
        var zeta = d.Damping + (d.WaterDamping - d.Damping) * b.Float;
        for (var left = dt; left > 1e-6f; left -= Slice)
        {
            var h = MathF.Min(left, Slice);
            b.PitchRate += (k * k * (pitchTarget - b.Pitch) - 2f * zeta * k * b.PitchRate) * h;
            b.Pitch += b.PitchRate * h;
            b.RollRate += (k * k * (rollTarget - b.Roll) - 2f * zeta * k * b.RollRate) * h;
            b.Roll += b.RollRate * h;
        }
        // the slope under the wheels is taken up smoothly, so rough ground does not shake the picture
        var take = 1f - MathF.Exp(-d.TerrainResponse * dt);
        b.GroundPitch += (input.GroundPitch - b.GroundPitch) * take;
        b.GroundRoll += (input.GroundRoll - b.GroundRoll) * take;
        return Pose(b, d);
    }

    static DriveBodyPose Pose(in DriveBodyState b, DriveBodyDef d)
    {
        var deg = FlightMath.Deg2Rad;
        // the bob: three slow waves out of step, so it never repeats visibly
        var size = (1f + (d.BobDisturbed - 1f) * b.Disturbed) * b.Float;
        var t = b.Clock * (2f * MathF.PI / d.BobPeriod);
        var lift = d.BobHeight * size * MathF.Sin(t);
        var bobPitch = d.BobPitchDeg * deg * size * MathF.Sin(t / 1.31f + 1.1f);
        var bobRoll = d.BobRollDeg * deg * size * MathF.Sin(t / 0.83f + 0.4f);
        // afloat the whole craft moves; on land the body moves over its wheels
        return new DriveBodyPose(b.GroundPitch + (b.Pitch * b.Float + bobPitch), b.GroundRoll + (b.Roll * b.Float + bobRoll), lift,
            b.Pitch * (1f - b.Float), b.Roll * (1f - b.Float));
    }
}
