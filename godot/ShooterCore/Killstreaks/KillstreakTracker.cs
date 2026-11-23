using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Killstreaks;

/// <summary>What one loadout streak slot shows on the HUD tracker (ticket #119).</summary>
public enum StreakSlotState
{
    /// <summary>The loadout has no streak in this slot.</summary>
    Empty,
    /// <summary>Not earned yet: kills still to go.</summary>
    Progress,
    /// <summary>Earned and held: the Killstreak key uses it.</summary>
    Ready,
    /// <summary>Used and running (for example the UAV for its duration).</summary>
    Active,
    /// <summary>Used this life; it comes back only after a death.</summary>
    Spent,
}

/// <summary>One slot of the tracker. <see cref="KillsRequired"/> is 0 for an empty slot.</summary>
public readonly record struct StreakSlotView(string Id, string Name, StreakSlotState State, int KillsDone, int KillsRequired);

/// <summary>
/// The owner's client copy of the killstreak counter for the HUD tracker (ticket #119). It mirrors the rules of
/// <see cref="KillstreakService"/>: the counter counts the owner's kills (not suicides), resets on death, and a streak is
/// earned at its threshold (lowered by Hot Streak). The server's StreakEarned, StreakUsed and StreakEnded events are
/// authoritative: earned sets the counter to the server's value, the other two set the streak's state.
/// Godot-free and unit tested.
/// </summary>
public sealed class KillstreakTracker
{
    /// <summary>
    /// One loadout slot as the HUD shows it, driven by the server's events. Empty is a slot with no (known) streak. A streak is earned, used once,
    /// and runs until its end; a death (or a round reset) brings it back to Progress, but a Ready one stays Ready through a death (#414). The sandbox can use a streak that was never earned (Progress -> Active) and use
    /// it again (Spent -> Active), so those edges are declared too.
    /// </summary>
    public static readonly StateMachineSpec<StreakSlotState> Spec = StateMachineSpec<StreakSlotState>.Define(
            "Killstreak.Slot", "A loadout streak slot on the HUD tracker: kills to go, ready to use (kept through a death), running, spent.", StreakSlotState.Empty)
        .Allow(StreakSlotState.Empty, StreakSlotState.Progress)
        .Allow(StreakSlotState.Progress, StreakSlotState.Ready, StreakSlotState.Active)
        .Allow(StreakSlotState.Ready, StreakSlotState.Active, StreakSlotState.Progress)
        .Allow(StreakSlotState.Active, StreakSlotState.Spent, StreakSlotState.Progress)
        .Allow(StreakSlotState.Spent, StreakSlotState.Active, StreakSlotState.Progress)
        .Note(StreakSlotState.Empty, "No streak (or an unknown id) in the slot; set by Arm, never left except by the next Arm.")
        .Note(StreakSlotState.Progress, "Not earned: kills still to go. The state of every streak at the start of a life.")
        .Note(StreakSlotState.Ready, "Earned (StreakEarned) and held: the Killstreak key uses it. Survives a death, not a round reset.")
        .Note(StreakSlotState.Active, "Used (StreakUsed) and running until StreakEnded.")
        .Note(StreakSlotState.Spent, "Used and ended this life; it comes back only after a death or a round reset.")
        .Build();

    readonly KillstreakFile _data;
    readonly string[] _loadout = new string[KillstreakService.Slots];
    readonly StateMachine<StreakSlotState>[] _slots = Enumerable.Range(0, KillstreakService.Slots).Select(_ => new StateMachine<StreakSlotState>(Spec)).ToArray();
    float _costReduction;
    int _kills;

    public KillstreakTracker(KillstreakFile data)
    {
        _data = data;
        Array.Fill(_loadout, "");
    }

    /// <summary>Kills this life, as the client counts them (the server's value after each earned event).</summary>
    public int Kills => _kills;

    /// <summary>Starts a life at spawn: the loadout's three streak ids (empty = none) and the perk's cost reduction. A streak held Ready stays Ready if the new loadout still has it (#414).</summary>
    public void Arm(IReadOnlyList<string> loadoutStreaks, float costReduction)
    {
        var ready = new HashSet<string>();
        for (var i = 0; i < _slots.Length; i++) if (_slots[i].Is(StreakSlotState.Ready)) ready.Add(_loadout[i]);
        Reset();
        _costReduction = costReduction;
        for (var i = 0; i < KillstreakService.Slots; i++)
        {
            _loadout[i] = i < loadoutStreaks.Count ? loadoutStreaks[i] ?? "" : "";
            _slots[i].Reset(StreakSlotState.Empty);
            if (_loadout[i] != "" && _data.Find(_loadout[i]) != null)
            {
                _slots[i].Enter(StreakSlotState.Progress);
                if (ready.Contains(_loadout[i])) _slots[i].Enter(StreakSlotState.Ready);
            }
        }
    }

    /// <summary>One of the owner's kills (not a suicide). A kill made with a streak's own weapon does not count (ticket #143).</summary>
    public void OnKill(bool fromStreak = false)
    {
        if (!fromStreak) _kills++;
    }

    /// <summary>The server earned a streak; <paramref name="kills"/> is the server's counter at that moment.</summary>
    public void OnEarned(string streakId, int kills)
    {
        // a streak already used this life stays Active or Spent (earned only moves a streak that is still in Progress)
        EachSlotOf(streakId, m => { if (m.Is(StreakSlotState.Progress)) m.Enter(StreakSlotState.Ready); });
        _kills = Math.Max(0, kills);
    }

    /// <summary>The server used a held streak.</summary>
    public void OnUsed(string streakId)
    {
        EachSlotOf(streakId, m => m.Enter(StreakSlotState.Active));
    }

    /// <summary>An active streak ended (duration, round reset, death or leaving).</summary>
    public void OnEnded(string streakId) => EachSlotOf(streakId, m => { if (m.Is(StreakSlotState.Active)) m.Enter(StreakSlotState.Spent); });

    /// <summary>The owner died: the counter and the used and active streaks reset; a Ready streak stays Ready (the server does the same, #414).</summary>
    public void OnDeath()
    {
        _kills = 0;
        foreach (var m in _slots)
            if (!m.Is(StreakSlotState.Empty) && !m.Is(StreakSlotState.Ready)) m.Enter(StreakSlotState.Progress);
    }

    /// <summary>Clears the counter and the streak states; the loadout stays until the next <see cref="Arm"/>.</summary>
    public void Reset()
    {
        _kills = 0;
        foreach (var m in _slots)
            if (!m.Is(StreakSlotState.Empty)) m.Enter(StreakSlotState.Progress);
    }

    /// <summary>Runs <paramref name="act"/> on the machine of every filled slot that holds <paramref name="streakId"/> (an id the loadout does not hold changes nothing on the HUD).</summary>
    void EachSlotOf(string streakId, Action<StateMachine<StreakSlotState>> act)
    {
        for (var i = 0; i < _slots.Length; i++)
            if (_loadout[i] == streakId && !_slots[i].Is(StreakSlotState.Empty)) act(_slots[i]);
    }

    /// <summary>The view of loadout slot <paramref name="slot"/> (0-based).</summary>
    public StreakSlotView Slot(int slot)
    {
        var id = slot >= 0 && slot < KillstreakService.Slots ? _loadout[slot] : "";
        if (id == "" || _data.Find(id) is not { } def) return new StreakSlotView(id, "", StreakSlotState.Empty, 0, 0);
        var need = KillstreakService.Threshold(def, _costReduction);
        return new StreakSlotView(id, def.Name, _slots[slot].Current, Math.Min(_kills, need), need);
    }
}
