namespace Drycastle.ShooterCore;

/// <summary>
/// What the server knows about one filtered world entity for one client (<see cref="WorldInterest"/>). It decides which record the entity
/// gets this tick; the states that wait for an acknowledgement carry the tick they began in (the client has it once it acknowledges that tick or a later one).
/// </summary>
public enum WorldRelevance : byte
{
    /// <summary>Nothing sent yet (and nothing to send while out of range). Not stored: the encoder's table has no entry.</summary>
    Unseen,
    /// <summary>In interest, the client may not have it yet: a keyframe every tick until a tick at or after the first one is acknowledged.</summary>
    Keying,
    /// <summary>In interest and the client holds the current state: deltas against the shared baseline, as for any world entity.</summary>
    Synced,
    /// <summary>Just left interest: the dormant record every tick until a tick at or after the first one is acknowledged.</summary>
    Leaving,
    /// <summary>Out of interest and the client knows it: nothing is sent.</summary>
    Silent,
}

/// <summary>
/// Distance interest of the world channel (ADR 0024): the world entity types that opt in (<see cref="WorldEntityDef.Interest"/>) go to a client only
/// while they are within <see cref="EnterM"/> of its player; they are shown again from a keyframe and hidden by a dormant record when they leave
/// <see cref="LeaveM"/> (the gap is the hysteresis, so an entity at the edge does not flap). Distance is horizontal.
/// </summary>
public static class WorldInterest
{
    /// <summary>An entity farther than this is out of interest; it enters at this distance.</summary>
    public const float EnterM = 800f;
    /// <summary>An entity in interest stays in until it is farther than this.</summary>
    public const float LeaveM = 850f;

    internal static readonly long EnterSq = SqUnits(EnterM), LeaveSq = SqUnits(LeaveM);

    static long SqUnits(float m)
    {
        long u = (long)MathF.Round(m * Quant.PosScale);
        return u * u;
    }

    public static readonly StateMachineSpec<WorldRelevance> RelevanceSpec = StateMachineSpec<WorldRelevance>
        .Define("WorldInterest.Relevance", "One filtered world entity as one client's encoder sees it: keyframed until the client has it, then delta, then dormant (hidden) until it is acknowledged, then silent. Ticks are acknowledgements, not seconds.", WorldRelevance.Unseen)
        .Allow(WorldRelevance.Unseen, WorldRelevance.Keying, WorldRelevance.Leaving)
        .Allow(WorldRelevance.Keying, WorldRelevance.Synced, WorldRelevance.Leaving)
        .Allow(WorldRelevance.Synced, WorldRelevance.Keying, WorldRelevance.Leaving)
        .Allow(WorldRelevance.Leaving, WorldRelevance.Keying, WorldRelevance.Silent)
        .Allow(WorldRelevance.Silent, WorldRelevance.Keying)
        .Note(WorldRelevance.Unseen, "No entry in the table. In range it goes to Keying. Out of range it stays Unseen, except when the client has no baseline (a full set): then it goes to Leaving so the client hides it.")
        .Note(WorldRelevance.Keying, "A keyframe every tick until the client acknowledges a tick at or after the first one (entered at 800 m). A lost packet is repaired by the next tick's keyframe.")
        .Note(WorldRelevance.Synced, "Deltas against the shared baseline. Goes back to Keying if the baseline does not hold the entity (a safety net).")
        .Note(WorldRelevance.Leaving, "The dormant record every tick until the client acknowledges a tick at or after the first one (left at 850 m). Never a removal: the entity is alive.")
        .Note(WorldRelevance.Silent, "The client has the dormant record. Nothing is sent until the entity enters again.")
        .Build();
}
