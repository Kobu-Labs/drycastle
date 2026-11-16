namespace Drycastle.ShooterCore;

/// <summary>How a rider's figure is posed on a fixed seat, relative to the hull (radians).</summary>
/// <param name="BodyYaw">The whole figure turned on its seat: only a rider who works a gun, with the aim.</param>
/// <param name="TorsoYaw">The upper body twisted over the hips: what a look beyond the neck's reach turns.</param>
/// <param name="HeadYaw">The head turned on the neck.</param>
/// <param name="Pitch">The head's (a gunner's: the weapon's) pitch against the horizon.</param>
public readonly record struct RiderFigure(float BodyYaw, float TorsoYaw, float HeadYaw, float Pitch);

/// <summary>
/// The rules of a rider on a fixed seat (ADR 0026), pure so the server, every client and the tests do the same arithmetic. Every seat of every vehicle
/// fixes its rider to the hull: the figure sits where the seat is and turns with the vehicle. What a rider moves is the head (and, past the neck's reach,
/// the upper body) when they look around in a first-person view; a rider who works a gun (a gunner seat, an <see cref="VehicleSeatDef.Armed"/> seat) also
/// turns the whole figure on the seat to face the aim. What travels is the view (a yaw and a pitch in the world, the snapshot's own fields), so nobody needs
/// a new field: the pose is the view taken against the hull.
/// </summary>
public static class RiderPose
{
    const float Deg2Rad = MathF.PI / 180f;
    /// <summary>The head turns this far either side on the neck; further round the upper body twists (<see cref="TorsoLimit"/>), so the whole look reaches straight behind.</summary>
    public const float HeadYawLimit = 70f * Deg2Rad;
    /// <summary>The upper body twists this far over the hips, on top of the head's turn.</summary>
    public const float TorsoLimit = MathF.PI - HeadYawLimit;
    /// <summary>How far down and up a look goes (the cockpit view's own limits).</summary>
    public const float PitchDown = -0.9f, PitchUp = 0.9f;

    /// <summary>A rider who works a gun from the seat: the figure faces the aim (a gunner seat, an armed seat). Everyone else keeps facing the way the vehicle does.</summary>
    public static bool FollowsAim(VehicleSeatDef seat) => seat.Armed || seat.Role == "gunner";

    /// <summary>The turn of <paramref name="yaw"/> against <paramref name="hullYaw"/>, in -pi..pi.</summary>
    public static float Offset(float hullYaw, float yaw) => FlightMath.Wrap(yaw - hullYaw);

    public static float ClampPitch(float pitch) => Math.Clamp(pitch, PitchDown, PitchUp);

    /// <summary>
    /// The turn a passive rider's look has against the hull, and its pitch, from an input (the server's side): with <paramref name="headLook"/> clear
    /// the head faces ahead; a helicopter's pilot never turns their yaw (it is the heading they steer to, so the head faces where the nose goes), only
    /// the pitch of the look.
    /// </summary>
    public static (float Offset, float Pitch) Passive(bool flightPilot, bool headLook, float hullYaw, float yaw, float pitch) =>
        !headLook ? (0f, 0f) : (flightPilot ? 0f : Offset(hullYaw, yaw), ClampPitch(pitch));

    /// <summary>
    /// The figure a seat's rider is drawn as, from the view the snapshot has for them and the hull's yaw at the same snapshot. A gun's rider turns the
    /// whole figure to the aim; any other rider's body stays on the seat and the look is shared out: the head up to <see cref="HeadYawLimit"/>, the
    /// upper body the rest.
    /// </summary>
    public static RiderFigure Figure(VehicleSeatDef seat, float hullYaw, float viewYaw, float viewPitch)
    {
        var off = Offset(hullYaw, viewYaw);
        if (FollowsAim(seat)) return new RiderFigure(off, 0f, 0f, viewPitch);
        var head = Math.Clamp(off, -HeadYawLimit, HeadYawLimit);
        return new RiderFigure(0f, off - head, head, ClampPitch(viewPitch));
    }
}

/// <summary>
/// Who sits in the seats of a vehicle, as it travels in a snapshot (entity type <see cref="EntityType.Seats"/>), one record per vehicle with more than one
/// seat. The driver is in the vehicle's own record (<see cref="VehicleRecord.Driver"/>); this carries seats 1..3 so every client knows who sits where by
/// id, and draws each rider on their seat instead of guessing it from where their body is. The three ids are the record's yaw, pitch and the health and
/// weapon bytes; the position stays at the origin so the record is only sent when somebody changes seat. The record id is the vehicle's index in the id
/// range <see cref="FirstId"/>.
/// </summary>
public static class SeatsRecord
{
    /// <summary>First seats record id: below the vehicles (0x4000) and above every player.</summary>
    public const ushort FirstId = 0x3000;

    public static ushort IdOf(ushort carId) => (ushort)(FirstId + (carId - VehicleRecord.FirstId));
    public static ushort CarOf(ushort recordId) => (ushort)(VehicleRecord.FirstId + (recordId - FirstId));

    /// <summary>The record for the occupants of seats 1..3 (<paramref name="occupants"/>[0] is the driver's and is left out).</summary>
    public static EntityRecord Make(ushort carId, IReadOnlyList<ushort> occupants)
    {
        ushort At(int seat) => seat < occupants.Count ? occupants[seat] : (ushort)0;
        var any = At(1) != 0 || At(2) != 0 || At(3) != 0;
        var r = EntityRecord.Make(IdOf(carId), EntityType.Seats, System.Numerics.Vector3.Zero, 0, 0, 0, any ? EntityState.InVehicle : EntityState.Alive, 0);
        r.Yaw = At(1);
        r.Pitch = unchecked((short)At(2));
        var third = At(3);
        r.Health = (byte)(third & 0xFF);
        r.Weapon = (byte)(third >> 8);
        return r;
    }

    /// <summary>The occupant of <paramref name="seat"/> (1..3) in a record, 0 when empty.</summary>
    public static ushort Occupant(in EntityRecord r, int seat) => seat switch
    {
        1 => r.Yaw,
        2 => unchecked((ushort)r.Pitch),
        3 => (ushort)(r.Health | (r.Weapon << 8)),
        _ => 0,
    };
}
