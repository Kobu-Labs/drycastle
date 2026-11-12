namespace Drycastle.ShooterCore;

/// <summary>
/// The care package's random reward (killstreaks.json "streak_weights", ticket #151): a roll picks one killstreak with a
/// probability proportional to its weight, so the stronger streaks (smaller weights) are rarer. Entries keep the order of
/// killstreaks.json. Built by <see cref="KillstreakFile"/>, which checks every id against the streak table and refuses
/// care_package (a crate never drops another crate).
/// </summary>
public sealed class StreakRewards
{
    readonly (string Id, int Weight)[] _entries;

    public StreakRewards((string Id, int Weight)[] entries)
    {
        if (entries.Length == 0) throw new InvalidOperationException("care package: no reward streaks");
        _entries = entries;
        Total = entries.Sum(e => e.Weight);
    }

    /// <summary>Sum of all weights: the roll range is [0, Total).</summary>
    public int Total { get; }

    public IReadOnlyList<(string Id, int Weight)> Entries => _entries;

    /// <summary>The streak for a roll in [0, <see cref="Total"/>). Throws outside that range.</summary>
    public string Pick(int roll)
    {
        if (roll < 0 || roll >= Total) throw new ArgumentOutOfRangeException(nameof(roll), roll, $"roll must be in [0, {Total})");
        var acc = 0;
        foreach (var (id, w) in _entries)
        {
            acc += w;
            if (roll < acc) return id;
        }
        throw new InvalidOperationException("unreachable: the weights cover the roll range");
    }

    /// <summary>A random streak from the weights.</summary>
    public string Pick(Random rng) => Pick(rng.Next(Total));
}
