using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>
/// Server-side killstreak rules (ticket #118): the Streak counter per life, the three loadout streaks earned at their kill
/// thresholds, activation (one held streak per request, in loadout order), duration and end, and the point-of-view
/// control session. Godot-free and driven by the caller's clock, so the tests run without a server.
///
/// Rules: only kills count, a suicide does not, and a kill by a streak's own weapon does not advance the counter. The
/// counter and every held or used streak reset on death. A streak is earned once per life, and stays held until used.
/// Hot Streak lowers each threshold by its <c>streak_cost_reduction</c> (never below one kill).
/// </summary>
public sealed class KillstreakService
{
    /// <summary>Loadout streak slots per player.</summary>
    public const int Slots = 3;

    sealed class Life
    {
        public readonly string[] Loadout = new string[Slots];
        public float CostReduction;
        public int Kills;
        public readonly HashSet<string> Earned = new();
        public readonly HashSet<string> Used = new();
    }

    readonly KillstreakFile _data;
    readonly Dictionary<string, IKillstreak> _impl;
    readonly HashSet<string> _ownedWeapons;
    readonly Action<ushort, EventKind, byte[]> _send;
    readonly Action<string> _log;
    readonly Dictionary<ushort, Life> _lives = new();
    readonly List<KillstreakUse> _active = new();
    /// <summary>The use that holds the point-of-view control of each owner.</summary>
    readonly Dictionary<ushort, KillstreakUse> _control = new();
    /// <summary>The use whose <see cref="IKillstreak.Activate"/> is running; an End during it is finished by <see cref="Start"/>.</summary>
    KillstreakUse? _activating;

    /// <param name="data">killstreaks.json.</param>
    /// <param name="implementations">Exactly one per streak in <paramref name="data"/>; a mismatch throws.</param>
    /// <param name="send">Sends a reliable event to one player (owner-only events: StreakEarned, StreakUsed, StreakEnded).</param>
    /// <param name="log">Server log sink.</param>
    public KillstreakService(KillstreakFile data, IEnumerable<IKillstreak> implementations, Action<ushort, EventKind, byte[]> send, Action<string> log)
    {
        _data = data;
        _send = send;
        _log = log;
        _impl = new Dictionary<string, IKillstreak>();
        foreach (var i in implementations)
            if (!_impl.TryAdd(i.Id, i)) throw new InvalidOperationException($"killstreak \"{i.Id}\" has two implementations");
        var missing = data.Streaks.Where(s => !_impl.ContainsKey(s.Id)).Select(s => s.Id).ToList();
        if (missing.Count > 0) throw new InvalidOperationException($"no killstreak implementation for: {string.Join(", ", missing)}");
        var unknown = _impl.Keys.Where(id => data.Find(id) == null).ToList();
        if (unknown.Count > 0) throw new InvalidOperationException($"implementations not in killstreaks.json: {string.Join(", ", unknown)}");
        _ownedWeapons = _impl.Values.SelectMany(i => i.OwnedWeapons).ToHashSet();
    }

    /// <summary>Whether a weapon id belongs to a streak (its kills do not advance the Streak counter).</summary>
    public bool IsStreakWeapon(string weaponId) => _ownedWeapons.Contains(weaponId);

    /// <summary>Starts a life: the counter is 0, the loadout's three streak ids (empty = no streak) and the perk's cost reduction apply.</summary>
    public void Arm(ushort owner, IReadOnlyList<string> loadoutStreaks, float costReduction)
    {
        EndAllFor(owner, KillstreakEndReason.OwnerDied);
        var life = new Life { CostReduction = costReduction };
        for (var i = 0; i < Slots; i++) life.Loadout[i] = i < loadoutStreaks.Count ? loadoutStreaks[i] : "";
        _lives[owner] = life;
    }

    /// <summary>A kill made by <paramref name="killer"/>. Suicides and streak-weapon kills do not advance the counter.</summary>
    public void OnKill(ushort killer, ushort victim, bool fromStreak)
    {
        if (killer == victim || fromStreak || !_lives.TryGetValue(killer, out var life)) return;
        life.Kills++;
        foreach (var id in life.Loadout)
        {
            if (id == "" || life.Earned.Contains(id) || _data.Find(id) is not { } def) continue;
            if (life.Kills < Threshold(def, life)) continue;
            life.Earned.Add(id);
            _send(killer, EventKind.StreakEarned, KillstreakEvents.EncodeEarned(id, life.Kills));
        }
    }

    /// <summary>Sets the owner's Hot Streak reduction for this life (a care package perk gained mid-life). Thresholds apply from the next kill.</summary>
    public void SetCostReduction(ushort owner, float costReduction)
    {
        if (_lives.TryGetValue(owner, out var life)) life.CostReduction = costReduction;
    }

    /// <summary>The owner died: the counter, the earned and used streaks reset, and their active instances end.</summary>
    public void OnDeath(ushort owner)
    {
        EndAllFor(owner, KillstreakEndReason.OwnerDied);
        if (!_lives.TryGetValue(owner, out var life)) return;
        life.Kills = 0;
        life.Earned.Clear();
        life.Used.Clear();
    }

    /// <summary>The owner left the server.</summary>
    public void Forget(ushort owner)
    {
        EndAllFor(owner, KillstreakEndReason.OwnerLeft);
        _lives.Remove(owner);
    }

    /// <summary>The round ended or began: every instance ends and every counter resets.</summary>
    public void Reset()
    {
        foreach (var use in _active.ToList()) Finish(use, KillstreakEndReason.RoundReset);
        foreach (var life in _lives.Values)
        {
            life.Kills = 0;
            life.Earned.Clear();
            life.Used.Clear();
        }
    }

    /// <summary>
    /// Uses the first held streak (in loadout order) whose instance limit allows it. The owner must be alive; the caller
    /// checks that. Returns false when nothing is held or every held streak is refused.
    /// </summary>
    public bool Activate(ushort owner, double now, Vector3 position)
    {
        if (!_lives.TryGetValue(owner, out var life)) return false;
        foreach (var id in life.Loadout)
        {
            if (id == "" || !life.Earned.Contains(id) || life.Used.Contains(id)) continue;
            if (_data.Find(id) is not { } def) continue;
            if (ActiveCount(owner, id) >= def.MaxActive) continue;
            if (Start(life, def, owner, now, position)) return true;
        }
        return false;
    }

    /// <summary>Ends the instances whose duration has run out.</summary>
    public void Tick(double now)
    {
        foreach (var use in _active.ToList())
            if (use.IsOpen && use.Def.DurationSeconds > 0 && now - use.StartedAt >= use.Def.DurationSeconds)
                Finish(use, KillstreakEndReason.Expired);
    }

    /// <summary>
    /// Sends the owner's input to their control session. False when the owner controls nothing, so the caller moves the
    /// player as usual. Not called by the input loop yet: the first point-of-view streak wires it in.
    /// </summary>
    public bool RouteInput(ushort owner, NetInput input)
    {
        if (!_control.TryGetValue(owner, out var use) || use.Control is not { } session) return false;
        session.OnInput(input);
        return true;
    }

    /// <summary>The owner's control session, or null.</summary>
    public ControlSession? ControlOf(ushort owner) => _control.TryGetValue(owner, out var use) ? use.Control : null;

    /// <summary>Kills in the owner's current life (the Streak counter).</summary>
    public int KillCount(ushort owner) => _lives.TryGetValue(owner, out var life) ? life.Kills : 0;

    /// <summary>Streaks earned in this life and not yet used, in loadout order.</summary>
    public IReadOnlyList<string> Held(ushort owner)
    {
        if (!_lives.TryGetValue(owner, out var life)) return Array.Empty<string>();
        return life.Loadout.Where(id => id != "" && life.Earned.Contains(id) && !life.Used.Contains(id)).ToList();
    }

    /// <summary>Open instances of one streak for one owner.</summary>
    public int ActiveCount(ushort owner, string streakId) => _active.Count(u => u.Owner == owner && u.StreakId == streakId && u.IsOpen);

    /// <summary>Kills needed for a streak in this life: its threshold less the perk's reduction, never below one.</summary>
    public static int Threshold(KillstreakDef def, float costReduction) =>
        Math.Max(1, def.KillsRequired - (int)MathF.Round(costReduction));

    static int Threshold(KillstreakDef def, Life life) => Threshold(def, life.CostReduction);

    bool Start(Life life, KillstreakDef def, ushort owner, double now, Vector3 position)
    {
        var use = new KillstreakUse(this, owner, def, position, now);
        _active.Add(use);
        var previous = _activating;
        _activating = use;
        bool accepted;
        try { accepted = _impl[def.Id].Activate(use); }
        finally { _activating = previous; }

        if (!accepted)
        {
            _active.Remove(use);
            return false;
        }
        life.Used.Add(def.Id);
        _send(owner, EventKind.StreakUsed, KillstreakEvents.EncodeUsed(def.Id));
        if (!use.IsOpen) Complete(use); // ended during Activate: its end follows its use
        return true;
    }

    /// <summary>Ends one instance. Idempotent. An End during its own activation is completed once Activate returns.</summary>
    internal void Finish(KillstreakUse use, KillstreakEndReason reason)
    {
        if (!use.IsOpen) return;
        use.IsOpen = false;
        use.EndReason = reason;
        if (ReferenceEquals(use, _activating)) return;
        Complete(use);
    }

    void Complete(KillstreakUse use)
    {
        _active.Remove(use);
        if (_control.TryGetValue(use.Owner, out var holder) && ReferenceEquals(holder, use))
        {
            _control.Remove(use.Owner);
            use.Control = null;
        }
        use.RaiseEnded();
        _send(use.Owner, EventKind.StreakEnded, KillstreakEvents.EncodeEnded(use.StreakId, use.EndReason ?? KillstreakEndReason.Finished));
    }

    internal bool TakeControl(KillstreakUse use, ushort entityId, Action<NetInput> onInput, bool freezesBody)
    {
        if (!use.IsOpen) return false;
        if (_control.TryGetValue(use.Owner, out var holder) && !ReferenceEquals(holder, use)) return false;
        use.Control = new ControlSession(use.Owner, use.StreakId, entityId, onInput, freezesBody);
        _control[use.Owner] = use;
        return true;
    }

    internal void Log(string message) => _log(message);

    void EndAllFor(ushort owner, KillstreakEndReason reason)
    {
        foreach (var use in _active.Where(u => u.Owner == owner).ToList()) Finish(use, reason);
    }
}
