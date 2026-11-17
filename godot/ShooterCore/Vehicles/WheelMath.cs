namespace Drycastle.ShooterCore;

/// <summary>
/// The client's generic wheel animation (#295): which nodes are wheels, and the angles they take from the DRAWN motion of the vehicle (its speed
/// along its heading and its yaw rate), so a vehicle that is not predicted rolls and steers as well. Pure: the node work is <c>VehicleWheels</c>
/// in godot/shooter.
/// </summary>
public static class WheelMath
{
    /// <summary>
    /// The wheel layouts a model may use, one row per layout (a new layout is one more row; a name found in two rows is one wheel). Each wheel is the
    /// node of that name, its steering parent (when it steers) is <c>name + SteerSuffix</c>. Two wheels: the bike; four: fl fr rl rr; six: front,
    /// middle, rear (the flak); numbered axles from the front: the Scud's eight, a six-wheel truck's l1..l3.
    /// </summary>
    public static readonly string[][] Layouts =
    {
        new[] { "wheel_f", "wheel_r" },
        new[] { "wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr" },
        new[] { "wheel_fl", "wheel_fr", "wheel_ml", "wheel_mr", "wheel_rl", "wheel_rr" },
        new[] { "wheel_l1", "wheel_r1", "wheel_l2", "wheel_r2", "wheel_l3", "wheel_r3", "wheel_l4", "wheel_r4", "wheel_l5", "wheel_r5", "wheel_l6", "wheel_r6" },
    };

    public const string SteerSuffix = "_steer";
    /// <summary>The steering lock when the model declares none, radians (about 34 degrees).</summary>
    public const float DefaultLock = 0.6f;
    /// <summary>A wheel node whose rest height is not above this has no radius to read: <see cref="FallbackRadius"/> is used, metres.</summary>
    public const float MinRadius = 0.05f, FallbackRadius = 0.4f;
    /// <summary>Beyond this a frame's move is a jump (a return to its place), not driving, metres.</summary>
    public const float JumpM = 5f;

    /// <summary>The distinct wheel names of every layout, in table order.</summary>
    public static List<string> AllNames()
    {
        var all = new List<string>();
        foreach (var row in Layouts) foreach (var n in row) if (!all.Contains(n)) all.Add(n);
        return all;
    }

    /// <summary>The angle a wheel rolls by moving <paramref name="speed"/> m/s for <paramref name="dt"/> s: forward is -Z, which turns a wheel backwards about X.</summary>
    public static float RollDelta(float speed, float radius, float dt) => -speed * dt / (radius > MinRadius ? radius : FallbackRadius);

    /// <summary>
    /// 1 / turn radius of the path, positive to the left: the yaw rate over the speed, eased to zero as the speed does (a vehicle turning on the spot
    /// has no wheel angle to read from it).
    /// </summary>
    public static float Curvature(float yawRate, float speed) => yawRate * speed / (speed * speed + 0.25f);

    /// <summary>The angle of an axle <paramref name="arm"/> metres ahead of the rear axle on a path of this curvature (positive turns left), capped by the lock.</summary>
    public static float SteerAngle(float arm, float curvature, float lockRad = DefaultLock) => Math.Clamp(MathF.Atan(arm * curvature), -lockRad, lockRad);

    /// <summary>The drawn motion of one frame: the speed along the heading (m/s, negative astern) and the yaw rate (rad/s, positive to the left). A jump reads as standing still.</summary>
    public static (float Speed, float YawRate) Motion(float dx, float dz, float yaw, float dyaw, float dt)
    {
        if (dt < 1e-4f || dx * dx + dz * dz > JumpM * JumpM) return (0f, 0f);
        // forward is (-sin yaw, -cos yaw) in x, z
        var along = -(dx * MathF.Sin(yaw) + dz * MathF.Cos(yaw));
        var d = dyaw;
        while (d > MathF.PI) d -= MathF.Tau;
        while (d < -MathF.PI) d += MathF.Tau;
        return (along / dt, d / dt);
    }

    /// <summary>Eases <paramref name="current"/> to <paramref name="target"/> at <paramref name="rate"/> per second (frame-rate independent).</summary>
    public static float Ease(float current, float target, float rate, float dt) => current + (target - current) * (1f - MathF.Exp(-rate * dt));

    /// <summary>
    /// The pivot of the vehicle's turn: the mean z of its axles that do not steer (the rear axle, or the rear pair or trio), else the rearmost axle (+Z is back).
    /// <paramref name="steered"/> says which of the axle z values steer.
    /// </summary>
    public static float PivotZ(ReadOnlySpan<float> axleZ, ReadOnlySpan<bool> steered)
    {
        float sum = 0, rear = float.NegativeInfinity;
        var n = 0;
        for (var i = 0; i < axleZ.Length; i++)
        {
            rear = MathF.Max(rear, axleZ[i]);
            if (!steered[i]) { sum += axleZ[i]; n++; }
        }
        return n > 0 ? sum / n : rear;
    }
}
