using System.Globalization;
using Drycastle.ShooterCore.Jobs;

namespace Drycastle.ShooterCore.Cartel;

/// <summary>
/// One enum's words: a translation key and its English text per value. The server writes the text (chat lines, the refusal event); the client registers every key with its text
/// (<c>CartelStrings</c>) and shows the key, so both word a reason the same. A text may hold format holes (<c>{0}</c>) the caller fills.
/// </summary>
public sealed class WordTable<T> where T : struct, Enum
{
    readonly Dictionary<T, (string Key, string Text)> _by = new();
    readonly (string Key, string Text) _fallback;

    /// <param name="fallback">The words of a value the table has no row for.</param>
    public WordTable((string Key, string Text) fallback, params (T Value, string Key, string Text)[] rows)
    {
        _fallback = fallback;
        foreach (var (v, k, t) in rows) _by.Add(v, (k, t));
    }

    public string Key(T value) => _by.TryGetValue(value, out var w) ? w.Key : _fallback.Key;
    public string Text(T value) => _by.TryGetValue(value, out var w) ? w.Text : _fallback.Text;
    /// <summary>The text with its holes filled (invariant culture).</summary>
    public string Text(T value, params object[] args) => string.Format(CultureInfo.InvariantCulture, Text(value), args);
    /// <summary>Every key with its text, the fallback's too (what the client registers).</summary>
    public IEnumerable<(string Key, string Text)> Entries => _by.Values.Append(_fallback);
}

/// <summary>
/// The Cartel's reasons in words (docs/cartel.md "Failure", "Who runs a job"): why a Job failed, why a start, a join or a quit was refused, why a party member earns no Respect.
/// One table per enum, read by the server and the client alike.
/// </summary>
public static class CartelWords
{
    public static readonly WordTable<JobFailReason> Fail = new(("cartel.fail.other", "It failed"),
        (JobFailReason.Timer, "cartel.fail.timer", "Time ran out"),
        (JobFailReason.RoundReset, "cartel.fail.round", "The round ended"),
        (JobFailReason.LeaderQuit, "cartel.fail.quit", "The leader quit the Cartel"),
        (JobFailReason.Death, "cartel.fail.death", "The leader died"),
        (JobFailReason.Abandoned, "cartel.fail.abandoned", "Nobody is left to run it"),
        (JobFailReason.Objective, "cartel.fail.objective", "The objective was lost"),
        (JobFailReason.Admin, "cartel.fail.admin", "Ended by an admin"));

    /// <summary>Holes: <see cref="JobStartRefusal.UnknownTier"/> {0} the tier count; <see cref="JobStartRefusal.TierLocked"/> {0} the tier, {1} the rank it needs.</summary>
    public static readonly WordTable<JobStartRefusal> Start = new(("cartel.refuse.other", "The job cannot start now."),
        (JobStartRefusal.Bot, "cartel.refuse.bot", "Bots do not run jobs."),
        (JobStartRefusal.NotMember, "cartel.refuse.notmember", "Join the Cartel to take jobs."),
        (JobStartRefusal.NotLeader, "cartel.refuse.notleader", "Your leader starts jobs."),
        (JobStartRefusal.UnknownKind, "cartel.refuse.kind", "The Cartel has no such job."),
        (JobStartRefusal.UnknownTier, "cartel.refuse.tier", "Tiers go from 1 to {0}."),
        (JobStartRefusal.TierLocked, "cartel.refuse.locked", "Tier {0} needs the rank {1}."),
        (JobStartRefusal.NoRound, "cartel.refuse.noround", "Jobs start while a round runs."),
        (JobStartRefusal.Dead, "cartel.refuse.dead", "You cannot start a job while dead."),
        (JobStartRefusal.FarFromContact, "cartel.refuse.far", "Jobs are started at the Contact."),
        (JobStartRefusal.OwnerBusy, "cartel.refuse.running", "Your job is still running."),
        (JobStartRefusal.MateBusy, "cartel.refuse.matebusy", "Someone in your squad is on another job."),
        (JobStartRefusal.NoCartel, "cartel.refuse.nocartel", "This map has no Cartel."),
        (JobStartRefusal.PalletFull, "cartel.refuse.palletfull", "The pallet is full."));

    public static readonly WordTable<CartelJoinRefusal> Join = new(("cartel.joinrefuse.other", "The Contact does not answer now."),
        (CartelJoinRefusal.Bot, "cartel.joinrefuse.bot", "Bots do not work for the Cartel."),
        (CartelJoinRefusal.Guest, "cartel.joinrefuse.guest", "The Cartel only takes players with a saved profile: guests cannot join."),
        (CartelJoinRefusal.AlreadyMember, "cartel.joinrefuse.member", "You already work for the Cartel."),
        (CartelJoinRefusal.NotMember, "cartel.joinrefuse.notmember", "You do not work for the Cartel."),
        (CartelJoinRefusal.NoCartel, "cartel.joinrefuse.nocartel", "This map has no Cartel."),
        (CartelJoinRefusal.FarFromContact, "cartel.joinrefuse.far", "Talk to the Contact for that."));

    public static readonly WordTable<NoRespectReason> NoRespect = new(("cartel.norespect.other", ""),
        (NoRespectReason.Bot, "cartel.norespect.bot", "Bots earn no Respect."),
        (NoRespectReason.Guest, "cartel.norespect.guest", "Guests have no saved profile: they earn no Respect."),
        (NoRespectReason.Sandbox, "cartel.norespect.sandbox", "Sandbox mode earns no Respect."),
        (NoRespectReason.NotMember, "cartel.norespect.notmember", "Join the Cartel at the Contact to earn Respect."));

    /// <summary>Every key of every table with its text, for the client's string table.</summary>
    public static IEnumerable<(string Key, string Text)> Entries => Fail.Entries.Concat(Start.Entries).Concat(Join.Entries).Concat(NoRespect.Entries);
}
