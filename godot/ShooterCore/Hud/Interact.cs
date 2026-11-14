namespace Drycastle.ShooterCore;

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
}

/// <summary>
/// Arbitration of the interact key between the things on foot that listen to it, in one place: the care package first, then a ground item (when it is nearer
/// than the corpse and the chooser is not up), then the corpse. Vehicles, the door gun and the Mass seat have their own state (a seat, a mode) and
/// are not candidates here: the prompt chain in NetClient.Cars.cs puts them before these (turret, vehicle, Mass, then crate, ground item, corpse).
/// </summary>
public static class InteractArbiter
{
    public static InteractOwner Owner(bool crateInReach, bool groundItemInReach, bool groundItemNearest, bool corpseInReach, bool chooserOpen)
    {
        if (crateInReach) return InteractOwner.Crate;
        if (groundItemInReach && groundItemNearest && !chooserOpen) return InteractOwner.GroundItem;
        if (corpseInReach) return InteractOwner.Corpse;
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

/// <summary>
/// Hold the interact key at something in reach (a landed crate, the rozhlas set) for <c>holdSeconds</c> to use it (it fires once per hold). Releasing resets; leaving reach
/// resets the time but a sent hold stays sent until the key is released (so the crate that is opening does not open twice). The exact logic of the old inline code.
/// </summary>
public struct InteractHold
{
    /// <summary>Seconds held at a crate in reach; 0 when not holding. Stays at its value after the send until release or leaving reach.</summary>
    public double Held;
    /// <summary>The open request for this hold has been sent; nothing more until the key is released.</summary>
    public bool Sent;

    /// <summary>Despawn of the crate in reach: the hold starts over.</summary>
    public void Reset() { Held = 0; Sent = false; }

    /// <summary>One input tick. True on the tick the hold completes (send once).</summary>
    public bool Tick(bool use, bool inReach, double dt, double holdSeconds)
    {
        if (!use) { Held = 0; Sent = false; }
        else if (!inReach) Held = 0;
        else if (!Sent)
        {
            Held += dt;
            if (Held >= holdSeconds) { Sent = true; return true; }
        }
        return false;
    }
}
