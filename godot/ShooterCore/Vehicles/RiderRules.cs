namespace Drycastle.ShooterCore;

/// <summary>
/// Who aboard a vehicle can be shot, and what a rider can do from their seat (ADR 0011, ADR 0012), pure so the server and the tests agree.
/// A rider is covered by the hull unless their seat is exposed; riders of one vehicle never hit each other; and only an armed seat's rider has
/// their weapons.
/// </summary>
public static class RiderRules
{
    /// <summary>
    /// The riders of a vehicle its hull does not cover, picked out of <paramref name="targets"/> (the live bodies a shot can hit), or null when it
    /// has none aboard. <paramref name="seatsOnly"/> leaves out a gunner who is exposed only by the vehicle's <c>exposed_gunner</c> (the flak
    /// truck's, the utility helicopter's): the weapons that could never reach those through their hull still cannot.
    /// </summary>
    public static ExposedRiders? Exposed(VehicleDef def, VehicleSeat seats, IReadOnlyList<HitTarget> targets, bool seatsOnly = false)
    {
        List<HitTarget>? riders = null;
        for (var s = 0; s < seats.Count && s < def.Seats.Count; s++)
        {
            var id = seats.Occupant(s);
            if (id == 0 || !(seatsOnly ? def.Seats[s].Exposed : def.SeatExposed(s))) continue;
            foreach (var t in targets)
                if (t.Id == id) { (riders ??= new List<HitTarget>(3)).Add(t); break; }
        }
        return riders == null ? null : new ExposedRiders(riders);
    }

    /// <summary>The targets of a shot fired from aboard: everyone but the riders of the shooter's own vehicle, who never hit each other.</summary>
    public static List<HitTarget> NotAboard(IReadOnlyList<HitTarget> targets, VehicleSeat seats)
    {
        var list = new List<HitTarget>(targets.Count);
        foreach (var t in targets) if (seats.SeatOf(t.Id) < 0) list.Add(t);
        return list;
    }

    /// <summary>
    /// The stance a rider has to a bullet. In a seat that says it is exposed it is the seat's (seated riders are as tall as a crouch, the gunner in
    /// a ring stands), so the hit volume is the figure every client draws; in every other seat it stays the body's own, as it was.
    /// </summary>
    public static Stance HitStance(VehicleDef def, int seat, Stance body) =>
        seat >= 0 && seat < def.Seats.Count && def.Seats[seat].Exposed ? def.Seats[seat].RiderStance : body;

    /// <summary>
    /// What a seated player's input does to their own weapons: in an armed seat everything it does on foot (fire, reload, the wanted hotbar slot,
    /// next weapon); in any other seat nothing, and null says so (no weapon timers run there).
    /// </summary>
    public static WeaponInput? Weapons(VehicleDef def, int seat, in NetInput input)
    {
        if (seat < 0 || seat >= def.Seats.Count || !def.Seats[seat].Armed) return null;
        var b = (MoveButtons)input.Buttons;
        return new WeaponInput((b & MoveButtons.Fire) != 0, (b & MoveButtons.Reload) != 0, input.Slot, (b & MoveButtons.Switch) != 0);
    }
}
