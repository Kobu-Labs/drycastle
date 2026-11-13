using System.Collections.Generic;

namespace Drycastle.Shooter;

/// <summary>
/// Rubber-banding telemetry (I-rubber-banding): how often and how far the owned body, a driven or flown vehicle is corrected, and how the
/// remote render clock behaves. Counted always (a few integer adds a frame); the loss test reads it per measured window, the car loop once at its end.
/// </summary>
public sealed class RubberStats
{
    /// <summary>On-foot reconciles (a predicted position that disagreed with the acked server position by more than the epsilon).</summary>
    public int OnFoot;
    /// <summary>On-foot reconciles whose error is over 3, 5, 20 and 50 cm, and over 2 m (a teleport or respawn, not a correction).</summary>
    public readonly int[] OnFootOver = new int[5];
    public double OnFootMax;

    /// <summary>Predicted-vehicle reconciles (drive and flight), their position moved over 5 cm and 20 cm, and their yaw turned over 1 and 3 degrees.</summary>
    public int VehicleCorrections, VehiclePosOver5, VehiclePosOver20, VehicleYawOver1, VehicleYawOver3;
    public double VehicleMaxPos, VehicleMaxYawDeg;

    /// <summary>Remote draws (one per remote per frame), those drawn at the newest snapshot or later (the buffer ran dry: a freeze), and frames where the render tick moved back.</summary>
    public int RemoteDraws, RemoteClamped, RenderBacksteps;
    /// <summary>The largest backward step of the render tick, in ticks.</summary>
    public double MaxBackstep;

    double _prevRt = double.NaN;

    /// <summary>On-foot reconciles with another player within 1 m (horizontally) of the body at the time: the contact cause.</summary>
    public int OnFootNearRemote, OnFootNearRemoteOver20;
    /// <summary>On-foot reconciles whose error is mostly vertical, and of those over 20 cm.</summary>
    public int OnFootVertical, OnFootVerticalOver20;

    public void AddOnFoot(double err, bool nearRemote, bool vertical)
    {
        OnFoot++;
        if (vertical) { OnFootVertical++; if (err > 0.20) OnFootVerticalOver20++; }
        if (nearRemote) { OnFootNearRemote++; if (err > 0.20) OnFootNearRemoteOver20++; }
        OnFootMax = Math.Max(OnFootMax, err);
        if (err > 0.03) OnFootOver[0]++;
        if (err > 0.05) OnFootOver[1]++;
        if (err > 0.20) OnFootOver[2]++;
        if (err > 0.50) OnFootOver[3]++;
        if (err > 2.0) OnFootOver[4]++;
    }

    public void AddVehicle(double posM, double yawRad)
    {
        var yawDeg = Math.Abs(yawRad) * 180 / Math.PI;
        VehicleCorrections++;
        VehicleMaxPos = Math.Max(VehicleMaxPos, posM);
        VehicleMaxYawDeg = Math.Max(VehicleMaxYawDeg, yawDeg);
        if (posM > 0.05) VehiclePosOver5++;
        if (posM > 0.20) VehiclePosOver20++;
        if (yawDeg > 1) VehicleYawOver1++;
        if (yawDeg > 3) VehicleYawOver3++;
    }

    /// <summary>One frame's render tick of the remote entities (<paramref name="rt"/>) against the newest snapshot tick.</summary>
    public void AddRender(double rt, uint newestTick, int draws)
    {
        RemoteDraws += draws;
        if (rt >= newestTick) RemoteClamped += draws;
        if (!double.IsNaN(_prevRt) && rt < _prevRt - 1e-3)
        {
            RenderBacksteps++;
            MaxBackstep = Math.Max(MaxBackstep, _prevRt - rt);
        }
        _prevRt = rt;
    }

    public Dictionary<string, object?> ToDict() => new()
    {
        ["onfoot_reconciles"] = OnFoot, ["onfoot_near_remote"] = OnFootNearRemote, ["onfoot_near_remote_over_20cm"] = OnFootNearRemoteOver20, ["onfoot_vertical"] = OnFootVertical, ["onfoot_vertical_over_20cm"] = OnFootVerticalOver20,
        ["onfoot_over_3cm"] = OnFootOver[0], ["onfoot_over_5cm"] = OnFootOver[1], ["onfoot_over_20cm"] = OnFootOver[2],
        ["onfoot_over_50cm"] = OnFootOver[3], ["onfoot_over_2m"] = OnFootOver[4], ["onfoot_max_m"] = OnFootMax,
        ["vehicle_reconciles"] = VehicleCorrections, ["vehicle_pos_over_5cm"] = VehiclePosOver5, ["vehicle_pos_over_20cm"] = VehiclePosOver20,
        ["vehicle_yaw_over_1deg"] = VehicleYawOver1, ["vehicle_yaw_over_3deg"] = VehicleYawOver3, ["vehicle_max_pos_m"] = VehicleMaxPos, ["vehicle_max_yaw_deg"] = VehicleMaxYawDeg,
        ["remote_draws"] = RemoteDraws, ["remote_draws_clamped"] = RemoteClamped, ["render_backsteps"] = RenderBacksteps, ["render_max_backstep_ticks"] = MaxBackstep,
    };
}

public partial class NetClient
{
    RubberStats _rub = new();
}
