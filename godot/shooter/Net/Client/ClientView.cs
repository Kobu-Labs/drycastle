using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// Who has the local player's view and body this frame: on foot, in a vehicle, at a turret, steering the missile, held (Mass, demon pin, Bigfoot). It is not a machine of its own: each
/// feature that can take the view says so through the flags and sources below, and <see cref="Mode"/> composes them into the one value (<see cref="ClientModeInputs"/>, with
/// <see cref="ClientMode"/> as its primary reading) the rest asks named questions of. Build <see cref="Mode"/> where it is used: it is a few bools.
/// </summary>
public sealed class ClientView
{
    readonly ClientContext _ctx;

    public ClientView(ClientContext ctx) { _ctx = ctx; }

    /// <summary>The entity state of the own record in the newest snapshot (Reconcile).</summary>
    public EntityState MyState;
    /// <summary>The Bigfoot holds this client's player: no prediction, the body is where the server puts it.</summary>
    public bool HeldByBigfoot;
    /// <summary>A gunship or turret view is up.</summary>
    public Func<bool> OnTurret = () => false;
    /// <summary>The Scud screen is open.</summary>
    public Func<bool> ScudScreen = () => false;
    /// <summary>The player steers a killstreak missile.</summary>
    public Func<bool> Controlling = () => false;
    /// <summary>Seated at the Holy Mass.</summary>
    public Func<bool> MassSeated = () => false;
    /// <summary>Pinned by the demon.</summary>
    public Func<bool> Pinned = () => false;
    /// <summary>Getting up from a pin.</summary>
    public Func<bool> Rising = () => false;

    public bool ScudScreenOn => ScudScreen();

    /// <summary>The player is in a vehicle's seat or driving (the driver's place or any seat).</summary>
    public bool InVehicle => _ctx.Driving || MyState == EntityState.InVehicle;

    /// <summary>
    /// The local player sits in an armed seat (ADR 0011): they keep their own weapons there, so the weapon in the view, its fire and reload
    /// prediction, the crosshair and the aim are on as they are on foot. The number keys stay the seats'; the mouse wheel changes the weapon.
    /// </summary>
    public bool InArmedSeat => !OnTurret() && !Controlling() && _ctx.Cars.MySeat() is { Seat.Armed: true };

    /// <summary>
    /// The local player sits in a driver's or an unarmed passenger's seat (#373): nothing can be fired from it, so the crosshair, the ammo readout and the Lock-on
    /// launcher's sight and seeker beeps stay off. A gunner's seat is not meant: the turret and the door gun have their own sight and readout.
    /// </summary>
    public bool InUnarmedSeat => !OnTurret() && !Controlling() && _ctx.Cars.MySeat() is { Seat: { Role: "driver" or "passenger" } s } && !LockRules.SeatAllows(true, s.Armed);

    /// <summary>The mode flags (dead, in a vehicle, at a turret, steering the missile, held by the Mass, the demon or the Bigfoot) as one value; <c>InArmedSeat</c> is only ever true while driving.</summary>
    public ClientModeInputs Mode => new(_ctx.Dead, _ctx.Driving, OnTurret() || ScudScreenOn, Controlling(), MassSeated(), Pinned(), HeldByBigfoot, Rising(), MyState == EntityState.InVehicle, _ctx.Driving && InArmedSeat);
}
