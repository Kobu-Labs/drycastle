using Drycastle.ShooterCore.Jobs;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>Why the Contact refused a join or a quit. The refusal event (<see cref="Net.EventKind.CartelRefused"/>) carries the byte: do not renumber.</summary>
public enum CartelJoinRefusal : byte
{
    None,
    Bot,
    Guest,
    AlreadyMember,
    /// <summary>A quit from a player who does not work for the Cartel.</summary>
    NotMember,
    /// <summary>The map has no Contact.</summary>
    NoCartel,
    /// <summary>Out of the Contact's reach.</summary>
    FarFromContact,
}

/// <summary>Why a party member earns no Respect from a Job (<see cref="CartelRules.NoRespect"/>); None: they earn.</summary>
public enum NoRespectReason : byte { None, Bot, Guest, Sandbox, NotMember }

/// <summary>
/// Why a Cartel Job was not started, checked in this order (<see cref="CartelRules.CanStart"/>; <see cref="NoCartel"/> before all, <see cref="CannotBegin"/> after all). The refusal event
/// (<see cref="Net.EventKind.CartelRefused"/>) carries the byte: do not renumber.
/// </summary>
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
    /// <summary>The map has no Contact.</summary>
    NoCartel,
    /// <summary>The kind could not begin (no drop point free, no house free...): its own words go with it.</summary>
    CannotBegin,
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

    public static string Words(CartelJoinRefusal r) => CartelWords.Join.Text(r);

    /// <summary>A party member earns a Job's Respect: a Cartel member with a stored Profile, not a bot, not in sandbox mode. Everyone else shares the objective and earns nothing.</summary>
    public static bool Earns(bool bot, bool stored, bool sandbox, CartelStanding? standing) => NoRespect(bot, stored, sandbox, standing) == NoRespectReason.None;

    /// <summary>Why the member earns no Respect (in this order: a bot, a guest, sandbox mode, not in the Cartel); None when they earn.</summary>
    public static NoRespectReason NoRespect(bool bot, bool stored, bool sandbox, CartelStanding? standing) =>
        bot ? NoRespectReason.Bot : !stored ? NoRespectReason.Guest : sandbox ? NoRespectReason.Sandbox : standing is { IsMember: true } ? NoRespectReason.None : NoRespectReason.NotMember;

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
        JobStartRefusal.UnknownTier => CartelWords.Start.Text(r, data.Tiers.Count),
        JobStartRefusal.TierLocked => data.Tier(tier) is { } t ? CartelWords.Start.Text(r, tier, data.Ranks[data.RankIndex(t.Rank)].Name) : CartelWords.Start.Text(JobStartRefusal.CannotBegin),
        _ => CartelWords.Start.Text(r),
    };

    /// <summary>The line a failed Job's party reads.</summary>
    public static string Words(JobFailReason r) => CartelWords.Fail.Text(r);
}
