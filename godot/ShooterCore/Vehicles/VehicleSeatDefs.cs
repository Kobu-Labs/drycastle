using Drycastle.ShooterCore.Movement;

namespace Drycastle.ShooterCore.Vehicles;

/// <summary>What protects a seat's rider from a shot (ADR 0035). Glass is the default of a seat that says nothing.</summary>
public enum SeatCover
{
    /// <summary>Behind panes: a bullet reaches the rider only through a pane or an opening of the hull, as that pane's kind lets it.</summary>
    Glass,
    /// <summary>Hidden behind armour: no bullet, pellet or mounted-gun round reaches the rider; explosive damage and the vehicle blowing up do.</summary>
    Armoured,
    /// <summary>In the open (ADR 0012): a round that reaches the rider's body hits them, whatever the hull is.</summary>
    Exposed,
}

/// <summary>One seat of a vehicle (vehicles.json "seats"). Seat 0 is always the driver. Offset is in the vehicle's frame, metres: x right, y up, z back (forward is -Z), origin on the ground at the car's centre.</summary>
public class VehicleSeatDef
{
    /// <summary>"driver" (seat 0 only, drives), "gunner" (fires the vehicle's gun) or "passenger".</summary>
    public string Role { get; set; } = "passenger";
    public float[] Offset { get; set; } = { 0, 0, 0 };
    /// <summary>
    /// An armed seat (ADR 0011): its rider keeps their own loadout and fights with it from the seat, in first person, aiming with the view inside
    /// <see cref="Aim"/>. A passenger seat only; false keeps the rider's hands empty, as on the bus.
    /// </summary>
    public bool Armed { get; set; }
    /// <summary>
    /// What protects this seat's rider (vehicles.json seat <c>cover</c>, ADR 0035): <see cref="SeatCover.Armoured"/>, <see cref="SeatCover.Glass"/> or
    /// <see cref="SeatCover.Exposed"/> (ADR 0012). Null: not declared (the contract test wants every seat to declare it; <see cref="VehicleDef.CoverOf"/> reads Glass then).
    /// </summary>
    public SeatCover? Cover { get; set; }
    /// <summary>
    /// The hull does not cover this seat's rider (ADR 0012): a shot that reaches their body hits them and not the hull (<see cref="GunnerCover"/>).
    /// Its own spelling in vehicles.json is <c>"cover": "exposed"</c>; <c>"exposed": true</c> is read as the same (the old spelling). (The flak truck and the
    /// utility helicopter say it for their gunner with the vehicle's <see cref="VehicleDef.ExposedGunner"/>.)
    /// </summary>
    public bool Exposed { get => Cover == SeatCover.Exposed; set { if (value) Cover = SeatCover.Exposed; else if (Cover == SeatCover.Exposed) Cover = SeatCover.Glass; } }
    /// <summary>The rider of an exposed seat stands (the gunner in a ring) instead of sitting: that is how they are drawn, and how tall they are to a bullet.</summary>
    public bool Standing { get; set; }
    /// <summary>How far round and up and down an armed seat's rider can aim. Null: all the way round.</summary>
    public VehicleSeatAimDef? Aim { get; set; }
    /// <summary>
    /// Metres from the seat's feet to the eye of the rider's own first-person view from a seat the hull covers (the client rides the camera on the seat). Null: the
    /// body's own standing eye, which is right in a tall cabin (the bus) and above the roof of a low one (the Humvee's seats: 0.6 + 1.65 is over its 1.86 m roof, hence 1.05). An exposed seat has its own (<see cref="RiderStance"/>).
    /// </summary>
    public float? EyeHeight { get; set; }

    /// <summary>The stance an exposed rider has to a bullet and to the eye: seated riders have the crouch's height.</summary>
    public Stance RiderStance => Standing ? Stance.Stand : Stance.Crouch;
}

/// <summary>
/// The arc an armed seat's rider aims in (vehicles.json seat "aim"), in the vehicle's own frame so it turns and tilts with it: what the seat, the
/// rider beside it and the rest of the vehicle leave free. The view is held inside it, and so are the shots (<see cref="SeatAimRules"/>).
/// </summary>
public class VehicleSeatAimDef
{
    /// <summary>The middle of the arc as a yaw in the vehicle's frame, degrees (0 is the nose, -90 out of the right side, 90 out of the left).</summary>
    public float YawDeg { get; set; }
    /// <summary>How far the aim swings either side of that, degrees (180: all the way round).</summary>
    public float ArcDeg { get; set; } = 180;
    /// <summary>Lowest and highest aim, degrees.</summary>
    public float MinPitchDeg { get; set; } = -60;
    public float MaxPitchDeg { get; set; } = 80;
    /// <summary>
    /// The vehicle's speed (m/s) at which the rider's shots carry the weapon's whole movement spread, as their own sprint does on foot; below it in
    /// proportion, none at a standstill. Higher: steadier at speed.
    /// </summary>
    public float SpreadFullSpeed { get; set; } = 16;
}
