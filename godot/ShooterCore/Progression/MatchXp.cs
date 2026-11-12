namespace Drycastle.ShooterCore;

/// <summary>Why a player got XP (one per award line, so the post-match summary can list them).</summary>
public enum XpReason { Kill, Headshot, Roadkill, Assist, StreakKill, MatchWin, MatchTop3 }

public readonly record struct XpGrant(ushort Player, int Amount, XpReason Reason);

/// <summary>
/// A kill as the XP rules see it. <paramref name="FromStreak"/> is set when a killstreak's own weapon made the kill
/// (its kills do not advance the Streak counter, but they do pay the streak award).
/// </summary>
public sealed record KillFacts(ushort Killer, ushort Victim, KillCause Cause = KillCause.Standard, bool FromStreak = false);

/// <summary>
/// XP for one match (ADR 0003 keeps the write behind the repositories; this only computes). Godot-free and deterministic:
/// the caller passes the match clock in seconds. Feed it damage (for assists), kills and the final ranking; it returns grants.
/// Suicides pay nothing. Perks never change XP here.
/// </summary>
public sealed class MatchXp
{
    readonly XpAwards _awards;
    // victim -> (attacker -> time of their last hit on the victim)
    readonly Dictionary<ushort, Dictionary<ushort, double>> _damage = new();

    public MatchXp(XpAwards awards) => _awards = awards;

    /// <summary>Forget the assist book (round restart).</summary>
    public void Reset() => _damage.Clear();

    /// <summary>A hit that did damage. Self-damage is not an assist.</summary>
    public void NoteDamage(ushort victim, ushort attacker, double now)
    {
        if (attacker == victim) return;
        if (!_damage.TryGetValue(victim, out var byAttacker)) _damage[victim] = byAttacker = new Dictionary<ushort, double>();
        byAttacker[attacker] = now;
    }

    /// <summary>The kill's XP: the killer's kill (or roadkill) award, plus headshot and streak bonuses, and an assist to each recent attacker.</summary>
    public List<XpGrant> OnKill(KillFacts kill, double now)
    {
        var grants = new List<XpGrant>();
        var hits = _damage.Remove(kill.Victim, out var byAttacker) ? byAttacker : null;
        if (kill.Killer == kill.Victim) return grants;

        var roadkill = kill.Cause == KillCause.Roadkill;
        grants.Add(new XpGrant(kill.Killer, roadkill ? _awards.Roadkill : _awards.Kill, roadkill ? XpReason.Roadkill : XpReason.Kill));
        if (kill.Cause == KillCause.Headshot) grants.Add(new XpGrant(kill.Killer, _awards.Headshot, XpReason.Headshot));
        if (kill.FromStreak) grants.Add(new XpGrant(kill.Killer, _awards.StreakKill, XpReason.StreakKill));

        if (hits != null)
            foreach (var (attacker, at) in hits.OrderBy(h => h.Key))
                if (attacker != kill.Killer && now - at <= _awards.AssistWindowSeconds)
                    grants.Add(new XpGrant(attacker, _awards.Assist, XpReason.Assist));
        return grants;
    }

    /// <summary>
    /// End of match: the winners (all tied players) get the win award; ranking places 2 and 3 that are not winners get the top-3 award.
    /// <paramref name="ranking"/> is the scoreboard order (best first).
    /// </summary>
    public List<XpGrant> OnMatchEnd(IReadOnlyCollection<ushort> winners, IReadOnlyList<ushort> ranking)
    {
        var grants = new List<XpGrant>();
        foreach (var w in winners) grants.Add(new XpGrant(w, _awards.MatchWin, XpReason.MatchWin));
        for (var i = 0; i < Math.Min(3, ranking.Count); i++)
            if (!winners.Contains(ranking[i])) grants.Add(new XpGrant(ranking[i], _awards.MatchTop3, XpReason.MatchTop3));
        return grants;
    }
}
