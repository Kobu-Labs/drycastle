using Drycastle.ShooterCore.Jobs;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>Why a player could not join the Cartel.</summary>
public enum CartelJoinRefusal : byte { None, Bot, Guest, AlreadyMember }

/// <summary>Why a Cartel Job was not started, checked in this order (<see cref="CartelRules.CanStart"/>).</summary>
public enum JobStartRefusal : byte
{
    None,
    Bot,
    NotMember,
    /// <summary>In a Squad, only the leader starts a Job.</summary>
    NotLeader,
    UnknownKind,
    UnknownTier,
    TierLocked,
    /// <summary>The round is not running (waiting for players, or its end screen).</summary>
    NoRound,
    Dead,
    FarFromContact,
    /// <summary>The party (the player alone, or the Squad) runs a Job already.</summary>
    OwnerBusy,
    /// <summary>Someone of the party is on another Job.</summary>
    MateBusy,
}

/// <summary>What <see cref="CartelRules.CanStart"/> needs to know about a start request; the server gathers it.</summary>
/// <param name="Rank">The leader's Cartel rank (an index of cartel.json).</param>
/// <param name="IgnoreRank">The admin's <c>/cartel job</c>: any tier, anywhere.</param>
public readonly record struct JobStartFacts(bool Bot, bool Member, bool IsLeader, bool KindKnown, int Tier, int Rank, bool RoundRunning, bool Alive, bool AtContact,
    bool OwnerBusy, bool MateBusy, bool IgnoreRank = false);

/// <summary>The Cartel's rules for joining, starting a Job and paying for one (docs/cartel.md "Membership and rank", "Who runs a job", "Difficulty and reward").</summary>
public static class CartelRules
{
    /// <summary>The giver of the Cartel's Jobs (<see cref="Job.Giver"/>).</summary>
    public const string Giver = "cartel";

    /// <summary>Bots never join; a guest (no stored Profile) cannot, because the rank lives on the Profile.</summary>
    public static CartelJoinRefusal CanJoin(bool bot, bool stored, CartelStanding? standing)
    {
        if (bot) return CartelJoinRefusal.Bot;
        if (!stored || standing == null) return CartelJoinRefusal.Guest;
        return standing.IsMember ? CartelJoinRefusal.AlreadyMember : CartelJoinRefusal.None;
    }

    public static string Words(CartelJoinRefusal r) => r switch
    {
        CartelJoinRefusal.Bot => "Bots do not work for the Cartel.",
        CartelJoinRefusal.Guest => "The Cartel only takes players with a saved profile: guests cannot join.",
        CartelJoinRefusal.AlreadyMember => "You already work for the Cartel.",
        _ => "",
    };

    /// <summary>A party member earns a Job's Respect: a Cartel member with a stored Profile, not a bot, not in sandbox mode. Everyone else shares the objective and earns nothing.</summary>
    public static bool Earns(bool bot, bool stored, bool sandbox, CartelStanding? standing) => !bot && stored && !sandbox && standing is { IsMember: true };

    /// <summary>What a finished Job pays each earning member: the tier's Respect, plus what its objective adds (a heist's duffels).</summary>
    public static int Reward(CartelFile data, Job job) => data.RespectFor(job.Tier) + Math.Max(0, job.Objective?.BonusRespect ?? 0);

    public static JobStartRefusal CanStart(CartelFile data, in JobStartFacts f)
    {
        if (f.Bot) return JobStartRefusal.Bot;
        if (!f.Member) return JobStartRefusal.NotMember;
        if (!f.IsLeader) return JobStartRefusal.NotLeader;
        if (!f.KindKnown) return JobStartRefusal.UnknownKind;
        if (data.Tier(f.Tier) == null) return JobStartRefusal.UnknownTier;
        if (!f.IgnoreRank && !data.TierOpen(f.Tier, f.Rank)) return JobStartRefusal.TierLocked;
        if (!f.RoundRunning) return JobStartRefusal.NoRound;
        if (!f.Alive) return JobStartRefusal.Dead;
        if (!f.IgnoreRank && !f.AtContact) return JobStartRefusal.FarFromContact;
        if (f.OwnerBusy) return JobStartRefusal.OwnerBusy;
        return f.MateBusy ? JobStartRefusal.MateBusy : JobStartRefusal.None;
    }

    public static string Words(JobStartRefusal r, CartelFile data, int tier) => r switch
    {
        JobStartRefusal.Bot => "Bots do not run jobs.",
        JobStartRefusal.NotMember => "Join the Cartel at the Contact first.",
        JobStartRefusal.NotLeader => "Your leader starts jobs.",
        JobStartRefusal.UnknownKind => "The Cartel has no such job.",
        JobStartRefusal.UnknownTier => $"Tiers go from 1 to {data.Tiers.Count}.",
        JobStartRefusal.TierLocked => data.Tier(tier) is { } t ? $"Tier {tier} needs the rank {data.Ranks[data.RankIndex(t.Rank)].Name}." : "That tier is locked.",
        JobStartRefusal.NoRound => "Jobs start while a round runs.",
        JobStartRefusal.Dead => "You cannot start a job while dead.",
        JobStartRefusal.FarFromContact => "Jobs are started at the Contact.",
        JobStartRefusal.OwnerBusy => "Your job is still running.",
        JobStartRefusal.MateBusy => "Someone in your squad is on another job.",
        _ => "",
    };

    /// <summary>The line a failed Job's party reads.</summary>
    public static string Words(JobFailReason r) => r switch
    {
        JobFailReason.Timer => "time ran out",
        JobFailReason.RoundReset => "the round ended",
        JobFailReason.LeaderQuit => "the leader quit the Cartel",
        JobFailReason.Death => "the leader died",
        JobFailReason.Abandoned => "nobody is left to run it",
        JobFailReason.Objective => "the objective was lost",
        _ => "it failed",
    };
}
