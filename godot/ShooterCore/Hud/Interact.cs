using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Plantation;

namespace Drycastle.ShooterCore.Hud;

/// <summary>Who has the interact key (E) on foot when several things are in reach.</summary>
public enum InteractOwner
{
    None,
    /// <summary>A landed care package: hold to open (ticket #143).</summary>
    Crate,
    /// <summary>A ground item nearer than any corpse: tap to take (#267).</summary>
    GroundItem,
    /// <summary>A corpse: hold to take the weapon, tap for the chooser.</summary>
    Corpse,
    /// <summary>Inside a marijuana plantation, on foot: hold to get high. The LOWEST priority of the key and nothing is shown for it; the server counts it (<see cref="PlantationRules"/>).</summary>
    Plantation,
}

/// <summary>
/// Arbitration of the interact key between the things on foot that listen to it, in one place: the care package first, then a ground item (when it is nearer
/// than the corpse and the chooser is not up), then the corpse, then, last of all, the plantation. Vehicles, the door gun and the Mass seat have their own state (a seat, a mode) and
/// are not candidates here: the prompt chain in NetClient.Cars.cs puts them before these (turret, vehicle, Mass, then crate, ground item, corpse).
/// </summary>
public static class InteractArbiter
{
    public static InteractOwner Owner(bool crateInReach, bool groundItemInReach, bool groundItemNearest, bool corpseInReach, bool chooserOpen, bool insidePlantation = false)
    {
        if (crateInReach) return InteractOwner.Crate;
        if (groundItemInReach && groundItemNearest && !chooserOpen) return InteractOwner.GroundItem;
        if (corpseInReach) return InteractOwner.Corpse;
        if (insidePlantation) return InteractOwner.Plantation;
        return InteractOwner.None;
    }

    /// <summary>
    /// The corpse prompt shows when the corpse owns the key and the chooser is not up. The old chain also showed it while a crate was in reach and its hold ran
    /// (the crate's own prompt steps aside then), saying "hold to take the rifle" while the key was opening the crate.
    /// </summary>
    public static bool CorpseShowsPrompt(bool crateInReach, bool groundItemInReach, bool groundItemNearest, bool corpseInReach, bool chooserOpen) =>
        !chooserOpen && Owner(crateInReach, groundItemInReach, groundItemNearest, corpseInReach, chooserOpen) == InteractOwner.Corpse;
}

/// <summary>
/// The press and the release of the interact key from one reading per update. <c>Pressed</c> is <c>use &amp;&amp; !wasUse</c>, <c>Released</c> is
/// <c>!use &amp;&amp; wasUse</c>, both against the same previous reading (an <see cref="Edge"/> alone would give the second one the already updated state).
/// </summary>
public struct InteractKey
{
    Edge _edge;

    public readonly bool Held => _edge.Held;

    public (bool Pressed, bool Released) Step(bool use)
    {
        var was = _edge.Held;
        var pressed = _edge.Pressed(use);
        return (pressed, !use && was);
    }
}

/// <summary>Where one hold of the interact key is: nothing held, counting up toward the send, or sent and waiting for the release.</summary>
public enum InteractHoldState : byte { Idle, Charging, Sent }

/// <summary>
/// Hold the interact key at something in reach (a landed crate, the rozhlas set) for <c>holdSeconds</c> to use it (it fires once per hold). Releasing resets; leaving reach
/// resets the time but a sent hold stays sent until the key is released (so the crate that is opening does not open twice). The exact logic of the old inline code.
/// </summary>
public struct InteractHold
{
    public static readonly StateMachineSpec<InteractHoldState> Spec = StateMachineSpec<InteractHoldState>
        .Define("Interact.Hold", "One hold of the interact key at a crate or a rozhlas set: idle, charging toward the send, sent once and waiting for the key to be released.", InteractHoldState.Idle)
        .Allow(InteractHoldState.Idle, InteractHoldState.Charging)
        .Allow(InteractHoldState.Charging, InteractHoldState.Sent, InteractHoldState.Idle)
        .Allow(InteractHoldState.Sent, InteractHoldState.Idle)
        .Note(InteractHoldState.Idle, "Key up, or held with nothing in reach (Held is 0).")
        .Note(InteractHoldState.Charging, "Key held at something in reach and the open request is not sent yet: Held counts up.")
        .Note(InteractHoldState.Sent, "The open request is sent: nothing more until the key is released. Leaving reach zeroes Held but stays Sent; a despawn (Reset) ends it too.")
        .Build();

    InteractHoldState _state;

    /// <summary>Seconds held at a crate in reach; 0 when not holding. Stays at its value after the send until release or leaving reach.</summary>
    public double Held;

    /// <summary>The state of the hold (a struct cannot own a <see cref="StateMachine{TState}"/> and stay a plain value, so it checks its own edges).</summary>
    public readonly InteractHoldState State => _state;

    /// <summary>The open request for this hold has been sent; nothing more until the key is released.</summary>
    public readonly bool Sent => _state == InteractHoldState.Sent;

    /// <summary>Despawn of the crate in reach: the hold starts over.</summary>
    public void Reset() { Held = 0; Go(InteractHoldState.Idle); }

    /// <summary>One input tick. True on the tick the hold completes (send once).</summary>
    public bool Tick(bool use, bool inReach, double dt, double holdSeconds)
    {
        if (!use) { Held = 0; Go(InteractHoldState.Idle); }
        else if (!inReach)
        {
            Held = 0;
            if (_state == InteractHoldState.Charging) Go(InteractHoldState.Idle); // a sent hold stays sent
        }
        else if (_state != InteractHoldState.Sent)
        {
            Go(InteractHoldState.Charging);
            Held += dt;
            if (Held >= holdSeconds) { Go(InteractHoldState.Sent); return true; }
        }
        return false;
    }

    void Go(InteractHoldState next)
    {
        if (_state == next) return;
        if (!Spec.Allows(_state, next)) StateMachineDiagnostics.Illegal(Spec.Name, _state.ToString(), next.ToString());
        _state = next;
    }
}
