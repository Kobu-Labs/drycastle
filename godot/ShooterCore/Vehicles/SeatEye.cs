using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// The eye of a seat's first-person (cockpit) view, in the vehicle's hull frame (x right, y up, forward is -Z, origin on the ground at the centre): one
/// rule for every seat of every vehicle, shared by the client (the camera hangs under the hull, so the sway is in the view), the contract test and the
/// seat rules. #339, docs/standards/car-design.md A3 and section 5.
/// <list type="bullet">
/// <item>An <b>exposed</b> seat (<see cref="VehicleDef.SeatExposed"/>): the seat's feet + the figure's own eye, crouch when seated, standing eye when it stands
/// (<see cref="SeatAimRules.EyeHeight"/>): the head of the rider on the motorcycle, the quad, the amphibious transport.</item>
/// <item>A <b>covered</b> seat: the seat's feet + <see cref="VehicleSeatDef.EyeHeight"/>, else the standing eye (1.65 m).</item>
/// <item>A hand-set <see cref="HandSetEyes"/> entry wins for that seat: the eyes of the cockpits built before #339 (their view must not move).</item>
/// </list>
/// </summary>
public static class SeatEye
{
    /// <summary>
    /// Eyes set by hand for a seat (vehicle id, seat index), in the hull frame: the driver's of the seven vehicles that had a hand-built cockpit rig
    /// (<c>CockpitRig</c>), and the Golf's passengers (its seats carry no eye_height). They were measured against the rig and the model, and stay exactly as they were (the Humvee's view must not change). The generic
    /// rule would put most of them within a hand's width of these, but not on them. The numbers sit beside the model scripts' (scripts/make_*_model.py).
    /// </summary>
    public static readonly IReadOnlyDictionary<(string Vehicle, int Seat), Vector3> HandSetEyes = new Dictionary<(string, int), Vector3>
    {
        [("golf_mk2", 0)] = new(-0.4875f, 1.50f, 0.30f),
        // the Golf's other seats (no eye_height in the data: the standing eye would be over its 1.79 m box): the seat + 1.05 m, the crouch's eye
        [("golf_mk2", 1)] = new(0.4875f, 1.50f, 0.2125f),
        [("golf_mk2", 2)] = new(-0.45f, 1.50f, 1.25f),
        [("golf_mk2", 3)] = new(0.45f, 1.50f, 1.25f),
        // the Humvee: seat_driver [-0.62, 0.6, -0.22] + [0, 1.05, 0.09] (docs/research/scud-models-facts.md, scripts/make_humvee_model.py): inside the cab (roof 1.83 inside, windscreen top 1.79), behind the wheel
        [("humvee", 0)] = new(-0.62f, 1.65f, -0.13f),
        [("bus", 0)] = new(-0.70f, 1.60f, -4.75f),
        [("flak", 0)] = new(-0.50f, 2.05f, -1.25f),
        // the harvester (#331): the combine's cab, after SM_Veh_Harvester_01; 1 m over the seat (y 2.0) just behind the wheel, inside the cab
        [("harvester", 0)] = new(-0.79f, 3.0f, -0.55f),
        // the Scud truck: seat_driver [-0.892, 1.15, -4.328] + [0, 1.05, 0.09] (scripts/make_scud_model.py): inside the left half cab (roof inside 2.66, windscreen 1.99-2.42), behind the wheel
        [("scud", 0)] = new(-0.892f, 2.2f, -4.238f),
        [("utility_helicopter", 0)] = new(0.55f, 1.78f, -1.80f),
    };

    /// <summary>The eye of <paramref name="seat"/> of <paramref name="v"/> in the hull frame.</summary>
    public static Vector3 For(VehicleDef v, int seat)
    {
        if (HandSetEyes.TryGetValue((v.Id, seat), out var set)) return set;
        var s = v.Seats[seat];
        var o = s.Offset.Length == 3 ? s.Offset : new float[3];
        var up = v.SeatExposed(seat) ? SeatAimRules.EyeHeight(s) : s.EyeHeight ?? MoveConsts.EyeHeight;
        return new Vector3(o[0], o[1] + up, o[2]);
    }
}
