using Drycastle.Shooter.Net.Server.BigfootEvent;
using Drycastle.Shooter.Net.Server.Careers;
using Drycastle.Shooter.Net.Server.Streaks;
using Drycastle.Shooter.Net.Server.Thieves;

namespace Drycastle.Shooter.Net.Server.RoundFlow;

/// <summary>What the start and the end of a round clear outside the match itself: the killstreaks, the shells in the air, the XP book and the events that do not outlive the round.</summary>
internal sealed class RoundHooks
{
    readonly StreakServer _streaks;
    readonly GunshipServer _gunships;
    readonly FlakServer _flak;
    readonly ProgressionServer _progress;
    readonly BigfootServer _bigfoot;
    readonly ThievesServer _thieves;

    public RoundHooks(StreakServer streaks, GunshipServer gunships, FlakServer flak, ProgressionServer progress, BigfootServer bigfoot, ThievesServer thieves)
    {
        _streaks = streaks; _gunships = gunships; _flak = flak; _progress = progress; _bigfoot = bigfoot; _thieves = thieves;
    }

    /// <summary>A new round: what the round's end left in the killstreaks, the shells, the XP book and the events starts over.</summary>
    public void Started()
    {
        _streaks.RoundReset();
        _gunships.RoundReset(); // a round reset takes the shells in the air with it
        _flak.RoundReset();
        _progress.ResetXp();
        _bigfoot.RoundReset();
        _thieves.RoundReset(); // the Thieves event does not outlive the round either, and what it stole is back
        _progress.ClearSummaries();
    }

    /// <summary>End of round: the killstreaks and shells go, then the awards and each player's XP summary.</summary>
    public void Ended()
    {
        _streaks.RoundReset();
        _gunships.RoundReset();
        _flak.RoundReset();
        _progress.RoundEnded();
    }
}
