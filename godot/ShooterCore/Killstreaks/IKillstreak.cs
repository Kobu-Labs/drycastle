using System.Numerics;

namespace Suchohrad.ShooterCore;

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

    internal ControlSession(ushort owner, string streakId, ushort entityId, Action<NetInput> onInput)
    {
        Owner = owner; StreakId = streakId; EntityId = entityId; OnInput = onInput;
    }
}

/// <summary>
/// One active instance of a killstreak, handed to <see cref="IKillstreak.Activate"/>. It carries the owner's position and
/// the calls an implementation needs: end the instance, react to its end, take control of an entity, and log.
/// </summary>
public sealed class KillstreakUse
{
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
    /// <summary>False once the instance has ended.</summary>
    public bool IsOpen { get; internal set; } = true;
    /// <summary>The end reason, once <see cref="IsOpen"/> is false.</summary>
    public KillstreakEndReason? EndReason { get; internal set; }
    /// <summary>The owner's control session when this instance has taken control, else null.</summary>
    public ControlSession? Control { get; internal set; }

    /// <summary>Ends this instance (idempotent). Ending during <see cref="IKillstreak.Activate"/> is allowed.</summary>
    public void End(KillstreakEndReason reason = KillstreakEndReason.Finished) => _service.Finish(this, reason);

    /// <summary>Runs <paramref name="callback"/> when this instance ends (for example to remove a placed entity).</summary>
    public void OnEnded(Action<KillstreakEndReason> callback) => _onEnded.Add(callback);

    /// <summary>Gives the owner control of an entity. False when the owner already controls another streak or this one has ended.</summary>
    public bool TakeControl(ushort entityId, Action<NetInput> onInput) => _service.TakeControl(this, entityId, onInput);

    /// <summary>Writes a line to the server log, tagged with the streak and owner.</summary>
    public void Log(string message) => _service.Log($"killstreak {StreakId} (player {Owner}): {message}");

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
