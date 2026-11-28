using Drycastle.ShooterCore.Cartel;

namespace Drycastle.Shooter.Cartel;

/// <summary>
/// Strings of the cartel's player side (#494): the Contact's prompt and panel, the Job tracker and its end card, the Job marker and the rank-up toast. Kept in their own file so
/// parallel work on <see cref="Menu.Strings"/> stays small; <see cref="Menu.Strings.Register"/> adds this table too. Keys are prefixed cartel. The reasons (a Job's failure, a refused
/// start, join or quit, why no Respect) are ShooterCore's <see cref="CartelWords"/>, which the server words its lines with: the table ends with them.
/// </summary>
public static class CartelStrings
{
    public static readonly (string Key, string Text)[] Table = Own().Concat(CartelWords.Entries).ToArray();

    static (string Key, string Text)[] Own() => new[]
    {
        ("cartel.handover", "Hold {0}: hand the duffel over"),
        ("cartel.ring.handover", "Handing over..."),
        ("cartel.ring.fill", "Filling..."),
        ("cartel.fill", "Hold {0}: fill a duffel"),
        ("cartel.fill.carrying", "You're carrying a duffel: {0} throws it"),
        ("cartel.fill.full", "All {0} duffels are filled"),
        ("cartel.fill.van", "Bring the Cartel van to the plantation first"),
        ("cartel.prompt", "{0}  Talk to the Contact"),
        ("cartel.title", "The Contact"),
        ("cartel.close", "Close"),
        ("cartel.close.tip", "Esc"),
        ("cartel.loading", "Talking to the Contact..."),
        ("cartel.rank.caption", "Cartel rank"),
        ("cartel.respect", "{0} Respect"),
        ("cartel.respect.next", "{0} Respect to {1}"),
        ("cartel.respect.top", "Top rank"),
        ("cartel.sandbox", "Sandbox mode: jobs pay you no Respect."),
        ("cartel.jobs.caption", "Jobs"),
        ("cartel.jobs.none", "The Contact has no jobs on this server."),
        ("cartel.tier", "Tier {0}"),
        ("cartel.tier.pays", "{0} Respect"),
        ("cartel.tier.needs", "Needs {0}"),
        ("cartel.tier.locked.tip", "Tier {0} opens at the rank {1}."),
        ("cartel.start", "Start {0}, tier {1}"),
        ("cartel.start.pick", "Pick a job"),
        ("cartel.quit", "Quit the Cartel"),
        ("cartel.quit.ask", "Quit the Cartel? Your rank and Respect wait for your return."),
        ("cartel.quit.fails", "The job you lead fails."),
        ("cartel.quit.confirm", "Quit"),
        ("cartel.quit.cancel", "Stay"),
        ("cartel.about", "The Cartel hands out jobs here at the plantation: deliveries, hits and heists. A finished job pays Respect, and Respect raises your Cartel rank."),
        ("cartel.about.squad", "In a squad, your leader starts the jobs and the whole squad runs them."),
        ("cartel.quit.was", "You quit as a {0} with {1} Respect. Rejoin to carry on from there."),
        ("cartel.join", "Join the Cartel"),
        ("cartel.rejoin", "Rejoin the Cartel"),
        ("cartel.track.tier", "Tier {0}"),
        ("cartel.track.left", "{0} left"),
        ("cartel.track.lead", "You lead this job"),
        ("cartel.track.led", "Led by {0}"),
        ("cartel.end.done", "Job done"),
        ("cartel.end.failed", "Job failed"),
        ("cartel.end.kind", "{0}, tier {1}"),
        ("cartel.end.paid", "+{0} Respect"),
        ("cartel.end.unpaid", "You earn no Respect from it"),
        ("cartel.marker", "Job - {0}"),
        ("cartel.rankup.you", "You are now a {0} of the Cartel"),
        ("cartel.rankup.other", "{0} is now a {1} of the Cartel"),
    };
}
