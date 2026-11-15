using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>Why an active killstreak ended. The byte is on the wire (StreakEnded).</summary>
public enum KillstreakEndReason : byte
{
    /// <summary>The streak ended itself (<see cref="KillstreakUse.End"/>).</summary>
    Finished = 0,
    /// <summary>Its duration in <see cref="KillstreakDef.DurationSeconds"/> ran out.</summary>
    Expired = 1,
    /// <summary>The owner died.</summary>
    OwnerDied = 2,
    /// <summary>The owner left the server.</summary>
    OwnerLeft = 3,
    /// <summary>The round ended.</summary>
    RoundReset = 4,
    /// <summary>A sandbox owner asked for the same streak again, which replaces the running one (#156).</summary>
    Replaced = 5,
    /// <summary>The streak's aircraft was shot down (ADR 0005).</summary>
    Destroyed = 6,
}

/// <summary>
/// One server-side killstreak. The framework holds one implementation per id in killstreaks.json and refuses to start
/// without it. Each MW3-style streak (UAV, turret, missile, ...) is its own ticket and supplies its own class.
/// </summary>
public interface IKillstreak
{
    /// <summary>The killstreaks.json id this implementation serves.</summary>
    string Id { get; }

    /// <summary>
    /// Weapon ids whose kills are this streak's kills. Those kills pay the streak award and do not advance the Streak
    /// counter (no chaining). Empty for streaks that do not fire a weapon.
    /// </summary>
    IReadOnlySet<string> OwnedWeapons { get; }

    /// <summary>
    /// Called once when the owner uses the streak, at their position. Return false to refuse; the streak then stays held.
    /// Call <see cref="KillstreakUse.End"/> when the instance is over (an instant streak ends at once).
    /// </summary>
    bool Activate(KillstreakUse use);
}

/// <summary>
/// The owner's point-of-view control of a streak entity (the camera is swapped to it and the owner's inputs are routed
/// to <see cref="OnInput"/>). Only one session per owner at a time. Server side; the client learns of it from StreakUsed.
/// </summary>
public sealed class ControlSession
{
    public ushort Owner { get; }
    public string StreakId { get; }
    /// <summary>The world entity (<see cref="WorldEntityManager"/> id) the owner controls.</summary>
    public ushort EntityId { get; }
    public Action<NetInput> OnInput { get; }
    /// <summary>True when the owner's body stands still while the session holds control (the missile); false when the owner keeps moving (a placing turret).</summary>
    public bool FreezesBody { get; }
    /// <summary>True when the owner's body takes no damage while the session holds control (the attack helicopter's pilot).</summary>
    public bool ShieldsBody { get; }

    internal ControlSession(ushort owner, string streakId, ushort entityId, Action<NetInput> onInput, bool freezesBody, bool shieldsBody)
    {
        Owner = owner; StreakId = streakId; EntityId = entityId; OnInput = onInput; FreezesBody = freezesBody; ShieldsBody = shieldsBody;
    }
}

/// <summary>
/// One active instance of a killstreak, handed to <see cref="IKillstreak.Activate"/>. It carries the owner's position and
/// the calls an implementation needs: end the instance, react to its end, take control of an entity, and log.
/// </summary>
public sealed class KillstreakUse
{
    /// <summary>Where one instance is. Activating: inside <see cref="IKillstreak.Activate"/> (an End there is completed once it returns). Open: running. Refused: Activate said no; the service dropped it and nothing ends it (still "open" to <see cref="IsOpen"/>, as before). Ended: over, with an <see cref="EndReason"/>.</summary>
    public enum UseState { Activating, Open, Refused, Ended }

    /// <summary>Activating -> Open when accepted, or Refused when not; any of them ends (an End during Activate included).</summary>
    public static readonly StateMachineSpec<UseState> Spec = StateMachineSpec<UseState>.Define(
            "Killstreak.Use", "One activation of a killstreak: inside Activate, running, ended; or refused and dropped.", UseState.Activating)
        .Allow(UseState.Activating, UseState.Open, UseState.Refused, UseState.Ended)
        .Allow(UseState.Open, UseState.Ended)
        .Allow(UseState.Refused, UseState.Ended)
        .Note(UseState.Activating, "The service called the implementation's Activate and has not heard back. A use that ends here is completed (event, callbacks) after Activate returns, once StreakUsed went out.")
        .Note(UseState.Open, "Accepted and running until its duration, its own End, its owner's death or leaving, a round reset, or its aircraft's destruction.")
        .Note(UseState.Refused, "Activate returned false: dropped by the service, the streak stays held. Never ended by the framework.")
        .Note(UseState.Ended, "Over: EndReason is set, StreakEnded sent, callbacks run.")
        .Build();

    readonly StateMachine<UseState> _state = new(Spec);
    readonly KillstreakService _service;
    readonly List<Action<KillstreakEndReason>> _onEnded = new();

    internal KillstreakUse(KillstreakService service, ushort owner, KillstreakDef def, Vector3 position, double startedAt)
    {
        _service = service; Owner = owner; Def = def; Position = position; StartedAt = startedAt;
    }

    public ushort Owner { get; }
    public KillstreakDef Def { get; }
    public string StreakId => Def.Id;
    /// <summary>Where the owner stood when they used the streak.</summary>
    public Vector3 Position { get; }
    /// <summary>The match clock (seconds) when the streak was used.</summary>
    public double StartedAt { get; }
    public UseState State => _state.Current;
    /// <summary>False once the instance has ended.</summary>
    public bool IsOpen => !_state.Is(UseState.Ended);
    /// <summary>The end reason, once <see cref="IsOpen"/> is false.</summary>
    public KillstreakEndReason? EndReason { get; private set; }
    /// <summary>The owner's control session when this instance has taken control, else null.</summary>
    public ControlSession? Control { get; internal set; }
    /// <summary>The world entity this instance flies or places, when it told the service (<see cref="BindEntity"/>) or took control of it. Used to end the instance when the entity is destroyed.</summary>
    public ushort? Entity { get; internal set; }

    /// <summary>Tells the service which world entity this instance owns (an aircraft with no control session, the helicopter helper's), so its destruction ends the instance.</summary>
    public void BindEntity(ushort entityId) => Entity = entityId;

    /// <summary>Ends this instance (idempotent). Ending during <see cref="IKillstreak.Activate"/> is allowed.</summary>
    public void End(KillstreakEndReason reason = KillstreakEndReason.Finished) => _service.Finish(this, reason);

    /// <summary>Runs <paramref name="callback"/> when this instance ends (for example to remove a placed entity).</summary>
    public void OnEnded(Action<KillstreakEndReason> callback) => _onEnded.Add(callback);

    /// <summary>Gives the owner control of an entity. False when the owner already controls another streak or this one has ended.</summary>
    public bool TakeControl(ushort entityId, Action<NetInput> onInput, bool freezesBody = false, bool shieldsBody = false) =>
        _service.TakeControl(this, entityId, onInput, freezesBody, shieldsBody);

    /// <summary>Writes a line to the server log, tagged with the streak and owner.</summary>
    public void Log(string message) => _service.Log($"killstreak {StreakId} (player {Owner}): {message}");

    internal void MarkOpen() { if (_state.Is(UseState.Activating)) _state.Enter(UseState.Open); }
    internal void MarkRefused() { if (_state.Is(UseState.Activating)) _state.Enter(UseState.Refused); }
    internal void Close(KillstreakEndReason reason) { EndReason = reason; _state.Enter(UseState.Ended); }

    internal void RaiseEnded() { foreach (var cb in _onEnded) cb(EndReason ?? KillstreakEndReason.Finished); }
}

/// <summary>Placeholder implementation: logs the activation and holds the instance open (until its duration, its end or its owner's death).</summary>
public sealed class LoggingKillstreak : IKillstreak
{
    public LoggingKillstreak(string id) => Id = id;

    public string Id { get; }
    public IReadOnlySet<string> OwnedWeapons { get; } = new HashSet<string>();

    public bool Activate(KillstreakUse use)
    {
        use.Log("activated (stub: no effect yet)");
        return true;
    }
}
