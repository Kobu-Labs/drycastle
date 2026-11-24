namespace Drycastle.Shooter.Net.Server.RoundFlow;

/// <summary>
/// What a round leaves behind on the server, cleared when the next one starts and when the match drops back to waiting: the Holy Mass, the crowd, the animals, the
/// vehicles (riders out of their seats, everything back at its origin, repaired), burning, killstreaks, shells in the air and the round's XP. The steps are given in the order they run
/// (<c>NetServer.BuildFeatures</c> lists them: the order is the rule, a step may rely on the ones before it).
/// </summary>
internal sealed class RoundReset
{
    readonly Action[] _steps;

    public RoundReset(params Action[] steps) => _steps = steps;

    public void Run()
    {
        foreach (var step in _steps) step();
    }
}
