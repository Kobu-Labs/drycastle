namespace Drycastle.Shooter.Net.Server.Diagnostics;

/// <summary>Round-relative schedule shared by the loss-test server and bots (issue #93): warm-up, measured window, quiet phase, final report.</summary>
public static class LossTimeline
{
    public const double Warm = 3, Stop = 8;
    /// <summary>Server writes its final state this long after the window ends (everything has respawned and settled by then).</summary>
    public const double ServerFinalAfterStop = 7, ServerLateAfterStop = 8.2, BotReportAfterStop = 8.5, ServerEndAfterStop = 11.5;
}
