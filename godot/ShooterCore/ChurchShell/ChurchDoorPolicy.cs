namespace Drycastle.ShooterCore;

/// <summary>
/// Why the church doors are asked to move. Each one is a named decider; <see cref="ChurchDoorPolicy"/> is the only place that knows what each does.
/// </summary>
public enum DoorReason
{
    /// <summary>The Mass starts gathering: open for the event. Cancels a pending close-when-empty.</summary>
    MassGathering,
    /// <summary>The pastor turned (the Mass is void): slammed shut, the demon fight locks everyone in.</summary>
    MassVoid,
    /// <summary>The event is over (phase Idle): the doors shut once nobody is left inside (<see cref="ChurchDoorPolicy.CloseWhenEmpty"/>).</summary>
    MassOver,
    /// <summary>The demon's stage machine started (the pastor's turn went through): no pending close-when-empty while it fights.</summary>
    DemonStarting,
    /// <summary>The demon died: open for everyone locked in, shut again once the church is empty.</summary>
    DemonKilled,
    /// <summary>The demon fight was cleared (the event ended, the demon vanished): nobody stays locked in.</summary>
    DemonCleared,
    /// <summary>The demon could not start (no data, no anchors, no entity): nobody stays locked in, and the event can be ended.</summary>
    DemonFailed,
    /// <summary>The church is empty after an event: the deferred close fires.</summary>
    ChurchEmpty,
}

/// <summary>
/// The single owner of "should the church doors be open" (ADR 0019). Before, five code paths (the Mass phase reaction, the demon's clear, its kill, its
/// per-tick door keeper, the admin command) each set the doors and two of them kept a loose <c>_closeWhenEmpty</c> flag. Here every decision is one
/// <see cref="Apply"/> of a named <see cref="DoorReason"/> (or the fight's own <see cref="TickFight"/>), the deferred close is the explicit
/// <see cref="CloseWhenEmpty"/> state, and the caller applies the result (the box, the push out of the doorway, the broadcast) in one place.
/// <para>
/// Not a priority queue: the latest decision wins, as it always did. The only memory is <see cref="CloseWhenEmpty"/> (set by <see cref="DoorReason.MassOver"/>
/// and <see cref="DoorReason.DemonKilled"/>, cleared by <see cref="DoorReason.MassGathering"/>, <see cref="DoorReason.DemonStarting"/> and when it fires) and
/// the fight's door-grace clock. Pure and Godot-free.
/// </para>
/// </summary>
public sealed class ChurchDoorPolicy
{
    float _graceT;

    /// <summary>Whether the doors should be open now. Closed by default.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>The doors shut as soon as <see cref="ChurchEmptied"/> says the church is empty (after an event, so nobody is locked out of leaving).</summary>
    public bool CloseWhenEmpty { get; private set; }

    /// <summary>Applies a decision. True when <see cref="IsOpen"/> changed (the caller then moves the doors).</summary>
    public bool Apply(DoorReason reason)
    {
        switch (reason)
        {
            case DoorReason.MassGathering: CloseWhenEmpty = false; return Set(true);
            case DoorReason.MassVoid: return Set(false);
            case DoorReason.MassOver: CloseWhenEmpty = true; return false;
            case DoorReason.DemonStarting: CloseWhenEmpty = false; return false;
            case DoorReason.DemonKilled: { var changed = Set(true); CloseWhenEmpty = true; return changed; }
            case DoorReason.DemonCleared or DoorReason.DemonFailed: return Set(true);
            case DoorReason.ChurchEmpty: CloseWhenEmpty = false; return Set(false);
            default: return false;
        }
    }

    /// <summary>An admin's <c>/church open|close</c>: sets the doors once and changes nothing else (a pending <see cref="CloseWhenEmpty"/> stays pending). True when <see cref="IsOpen"/> changed.</summary>
    public bool SetByAdmin(bool open) => Set(open);

    /// <summary>
    /// Once a tick: the deferred close. True when it fired (the church is empty and <see cref="CloseWhenEmpty"/> was set), so the doors shut. Pass the
    /// head count lazily: it is only asked while the close is pending.
    /// </summary>
    public bool TickDeferredClose(Func<int> playersInside) =>
        CloseWhenEmpty && playersInside() == 0 && Apply(DoorReason.ChurchEmpty);

    /// <summary>The demon fight starts (its turning): the door-grace clock starts over.</summary>
    public void FightBegins() => _graceT = 0;

    /// <summary>
    /// The fight's door keeper, once a tick while the demon fights: locked while someone is inside (they shut <paramref name="graceS"/> after the last time the
    /// doors stood open with a player inside), open and waiting when nobody is. True when <see cref="IsOpen"/> changed.
    /// </summary>
    public bool TickFight(float dt, int inside, float graceS)
    {
        if (inside > 0)
        {
            if (!IsOpen) { _graceT = 0; return false; }
            _graceT += dt;
            return _graceT >= graceS && Set(false);
        }
        _graceT = 0;
        return !IsOpen && Set(true);
    }

    bool Set(bool open)
    {
        if (open == IsOpen) return false;
        IsOpen = open;
        return true;
    }
}
