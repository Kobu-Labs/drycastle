using System.Numerics;

namespace Drycastle.ShooterCore;

/// <summary>
/// Who a bot may fight (the host's half of <see cref="IBotWorld.Enemies"/>): in team deathmatch the teammates are no enemies,
/// in free-for-all everyone else is. Rebuilt once a tick from the living players; the lists are reused, so a tick allocates nothing.
/// Also the rule turrets and the helicopter's helper use (<see cref="IsEnemy"/>).
/// </summary>
public sealed class BotEnemies
{
    readonly List<BotContact> _vsA = new(), _vsB = new();
    IReadOnlyList<BotContact> _all = Array.Empty<BotContact>();

    /// <summary>True when <paramref name="other"/> is somebody <paramref name="self"/> may shoot at: not itself, not a teammate (never in free-for-all).</summary>
    public static bool IsEnemy(MatchSim match, ushort self, ushort other) => self != other && !match.AreFriends(self, other);

    /// <summary>Call once a tick, after <paramref name="all"/> (every living player) is filled.</summary>
    public void Rebuild(MatchSim match, IReadOnlyList<BotContact> all)
    {
        _all = all;
        _vsA.Clear(); _vsB.Clear();
        if (!match.IsTeamMode) return;
        foreach (var c in all)
        {
            var team = match.Get(c.Id)?.Team ?? Teams.None;
            if (team != Teams.A) _vsA.Add(c);
            if (team != Teams.B) _vsB.Add(c);
        }
    }

    /// <summary>The living players <paramref name="self"/> may fight (the shared list in free-for-all and for a teamless bot, which may hold the bot itself).</summary>
    public IReadOnlyList<BotContact> For(MatchSim match, ushort self)
    {
        if (!match.IsTeamMode) return _all;
        return match.Get(self)?.Team switch { Teams.A => _vsA, Teams.B => _vsB, _ => _all };
    }

    /// <summary>Where the action is for <paramref name="self"/>: the feet of the nearest enemy, or null when there is none (or the bot is not among the living).</summary>
    public Vector3? Goal(MatchSim match, ushort self)
    {
        Vector3? me = null;
        foreach (var c in _all) if (c.Id == self) { me = c.Feet; break; }
        if (me is not { } from) return null;
        Vector3? best = null;
        var bestD = float.PositiveInfinity;
        foreach (var c in For(match, self))
        {
            if (c.Id == self) continue;
            var d = Vector3.DistanceSquared(c.Feet, from);
            if (d < bestD) { bestD = d; best = c.Feet; }
        }
        return best;
    }
}
