namespace Suchohrad.ShooterCore;

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
    readonly KillstreakFile _data;
    readonly string[] _loadout = new string[KillstreakService.Slots];
    readonly HashSet<string> _earned = new(), _used = new(), _active = new();
    float _costReduction;
    int _kills;

    public KillstreakTracker(KillstreakFile data)
    {
        _data = data;
        Array.Fill(_loadout, "");
    }

    /// <summary>Kills this life, as the client counts them (the server's value after each earned event).</summary>
    public int Kills => _kills;

    /// <summary>Starts a life at spawn: the loadout's three streak ids (empty = none) and the perk's cost reduction.</summary>
    public void Arm(IReadOnlyList<string> loadoutStreaks, float costReduction)
    {
        Reset();
        _costReduction = costReduction;
        for (var i = 0; i < KillstreakService.Slots; i++) _loadout[i] = i < loadoutStreaks.Count ? loadoutStreaks[i] ?? "" : "";
    }

    /// <summary>One of the owner's kills (not a suicide). A kill made with a streak's own weapon does not count (ticket #143).</summary>
    public void OnKill(bool fromStreak = false)
    {
        if (!fromStreak) _kills++;
    }

    /// <summary>The server earned a streak; <paramref name="kills"/> is the server's counter at that moment.</summary>
    public void OnEarned(string streakId, int kills)
    {
        _earned.Add(streakId);
        _kills = Math.Max(0, kills);
    }

    /// <summary>The server used a held streak.</summary>
    public void OnUsed(string streakId)
    {
        _earned.Remove(streakId);
        _used.Add(streakId);
        _active.Add(streakId);
    }

    /// <summary>An active streak ended (duration, round reset, death or leaving).</summary>
    public void OnEnded(string streakId) => _active.Remove(streakId);

    /// <summary>The owner died: the counter and every earned, used and active streak reset (the server does the same).</summary>
    public void OnDeath() => Reset();

    /// <summary>Clears the counter and the streak states; the loadout stays until the next <see cref="Arm"/>.</summary>
    public void Reset()
    {
        _kills = 0;
        _earned.Clear();
        _used.Clear();
        _active.Clear();
    }

    /// <summary>The view of loadout slot <paramref name="slot"/> (0-based).</summary>
    public StreakSlotView Slot(int slot)
    {
        var id = slot >= 0 && slot < KillstreakService.Slots ? _loadout[slot] : "";
        if (id == "" || _data.Find(id) is not { } def) return new StreakSlotView(id, "", StreakSlotState.Empty, 0, 0);
        var need = KillstreakService.Threshold(def, _costReduction);
        var state = _active.Contains(id) ? StreakSlotState.Active
            : _used.Contains(id) ? StreakSlotState.Spent
            : _earned.Contains(id) ? StreakSlotState.Ready
            : StreakSlotState.Progress;
        return new StreakSlotView(id, def.Name, state, Math.Min(_kills, need), need);
    }
}
