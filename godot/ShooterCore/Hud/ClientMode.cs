namespace Drycastle.ShooterCore.Hud;

/// <summary>
/// What the local player is doing, as far as the camera, the body and the viewmodel are concerned. The client keeps these as separate flags that other
/// systems own (the car, the turret, the missile, the Mass, the demon, the Bigfoot); <see cref="ClientModeInputs.Resolve"/> names the winner when one name
/// is wanted (diagnostics, docs). The behaviour lives in the named predicates of <see cref="ClientModeInputs"/>, not in this enum: the old sites tested
/// different subsets of the flags and the predicates keep each subset exactly.
/// </summary>
public enum ClientMode
{
    /// <summary>On the respawn screen.</summary>
    Dead,
    /// <summary>Steering the attack missile: its camera owns the screen, the body stands still.</summary>
    Missile,
    /// <summary>At a gunship, AC-130 or attack helicopter turret: its overlay owns the screen.</summary>
    Turret,
    /// <summary>In a vehicle seat (driver, pilot, passenger, gunner).</summary>
    Seated,
    /// <summary>In the Bigfoot's hand: the body is where the server puts it.</summary>
    Held,
    /// <summary>Seated at the Holy Mass: the server holds the body.</summary>
    MassSeated,
    /// <summary>Pinned by the demon's pounce.</summary>
    Pinned,
    /// <summary>Getting up from a pin: the body walks again but the viewmodel is still down.</summary>
    Rising,
    /// <summary>Walking: the default, own body, own camera.</summary>
    OnFoot,
}

/// <summary>
/// The flags the client mode is derived from, read once per question (build it from the live fields, ask, drop it). Every predicate is the exact boolean
/// one old call site computed, so a replaced site cannot change behaviour; <c>ClientModeTests</c> compares each against the original expression for every
/// combination. Where two predicates look alike and differ (one omits <see cref="Dead"/>, one counts <see cref="Rising"/>) that is the old behaviour kept,
/// not a slip: see the hazards in the test file.
/// </summary>
public readonly record struct ClientModeInputs(
    bool Dead,
    bool Driving,
    bool Turret,
    bool Controlling,
    bool MassSeated,
    bool Pinned,
    bool HeldByBigfoot,
    bool Rising,
    bool StateInVehicle,
    bool InArmedSeat)
{
    /// <summary>The primary mode, by priority: Dead, Missile, Turret, Seated, Held, MassSeated, Pinned, Rising, else OnFoot.</summary>
    public ClientMode Resolve() =>
        Dead ? ClientMode.Dead
        : Controlling ? ClientMode.Missile
        : Turret ? ClientMode.Turret
        : Driving || StateInVehicle ? ClientMode.Seated
        : HeldByBigfoot ? ClientMode.Held
        : MassSeated ? ClientMode.MassSeated
        : Pinned ? ClientMode.Pinned
        : Rising ? ClientMode.Rising
        : ClientMode.OnFoot;

    /// <summary>None of the flags: the player walks on their own.</summary>
    public bool OnFoot => !Dead && !Driving && !Turret && !Controlling && !MassSeated && !Pinned && !HeldByBigfoot && !Rising && !StateInVehicle;

    /// <summary>A turret or the missile owns the screen: the HUD's control mode, and the driver or pilot's hands are off (the vehicle input is zero).</summary>
    public bool RemoteViewOwnsScreen => Turret || Controlling;

    /// <summary>The camera is the body's or the vehicle's own, not a turret's or the missile's (view hints, the armed seat, the cockpit view).</summary>
    public bool ViewIsBody => !Turret && !Controlling;

    /// <summary>Server snapshots are not reconciled against the prediction: the body is a vehicle's, a turret's or the missile's, so the pending inputs are dropped.</summary>
    public bool SkipsReconcile => Driving || Controlling || Turret;

    /// <summary>The Bigfoot holds the body on foot: the snapshot is taken as is.</summary>
    public bool HeldOnFoot => HeldByBigfoot && !Driving;

    /// <summary>Seated at the Mass or pinned by the demon: the server holds the body, the snapshot is taken as is.</summary>
    public bool ServerHoldsBody => MassSeated || Pinned;

    /// <summary>The own body walks by prediction this tick (a dead body is not stepped; the caller adds that). A turret's body does not move either.</summary>
    public bool BodyIsPredicted => !Driving && !Controlling && !Turret && !HeldByBigfoot && !MassSeated && !Pinned;

    /// <summary>In a seat that is not an armed one (also trusts the snapshot's state while the driving flag lags): the viewmodel's "seated".</summary>
    public bool Seated => (Driving || StateInVehicle) && !InArmedSeat;

    /// <summary>The held weapon is drawn: not dead, not seated (an armed seat's rider holds the weapon), not at the Mass, pinned or rising, not steering the missile or at a turret.</summary>
    public bool ShowsViewmodel => !Dead && !Seated && !MassSeated && !Pinned && !Rising && !Controlling && !Turret;

    /// <summary>The held weapon's scope and iron-sight zoom stay off: any seat, or steering the missile (#241).</summary>
    public bool ScopeSuppressed => Driving || Controlling || StateInVehicle;

    /// <summary>The scope zoom may be used for prediction (a vehicle or the missile has no scoped aim).</summary>
    public bool CanScope => !Driving && !Controlling;

    /// <summary>The local fire prediction may run (the caller guards the turret itself).</summary>
    public bool CanPredictFire => !(Dead || HeldByBigfoot || MassSeated || Pinned || Driving && !InArmedSeat || Controlling);

    /// <summary>In a vehicle and alive: the vehicle's own prediction and the seat lookups run.</summary>
    public bool DrivingAlive => Driving && !Dead;

    /// <summary>The seat number keys, the armed seat and the door gun are live: in a seat, alive, not at a turret or the missile.</summary>
    public bool SeatKeysLive => Driving && !Dead && !Turret && !Controlling;

    /// <summary>The vehicle-view key (cockpit or chase) works.</summary>
    public bool CanToggleCockpitView => !Dead && !Turret && !Controlling;

    /// <summary>The number keys pick weapons (on a seat they are the seats').</summary>
    public bool HotbarKeysLive => !Dead && !Driving;
}
