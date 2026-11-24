using Godot;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.ShooterCore.Diagnostics;

namespace Drycastle.Shooter.Net.Server.Diagnostics;

/// <summary>
/// The server's measuring and test hooks, which <see cref="NetServer"/> holds as one object: the perf harness, the always-on tick health and prof lines, the metrics
/// push, and the smoke and loss-test verdicts. The tick calls <see cref="TickBegin"/>, <see cref="Lap"/> after each phase and <see cref="TickEnd"/>; the pieces are
/// public for what else reaches them (the packet reader's counters, the damage hook of the loss test, the save limit).
/// </summary>
internal sealed class ServerDiagnostics
{
    readonly ServerContext _ctx;
    readonly Fleet _fleet;
    readonly Func<int> _rocketCount;
    readonly TickPhases _phases = new();
    readonly ProfLines _prof;
    readonly SmokeRun _smoke;

    public ServerDiagnostics(ServerConfig config, ServerContext ctx, SmokeStats smoke, Fleet fleet, Func<int> rocketCount, ENetConnection host, Action<bool> quit)
    {
        _ctx = ctx; _fleet = fleet; _rocketCount = rocketCount;
        Perf = new PerfHarness(config.PerfOut, ctx, fleet, LiveEntities);
        Metrics = new MetricsOut(ctx);
        _prof = new ProfLines(ctx, _phases, Metrics);
        Health = new ServerHealth(ctx, smoke, _phases, _prof, Metrics, config.Progression, config.QuitAfter, LiveEntities);
        Loss = new LossRun(config, ctx, host, smoke, quit);
        _smoke = new SmokeRun(config, ctx, smoke, fleet, Loss, quit);
    }

    public PerfHarness Perf { get; }
    public ServerHealth Health { get; }
    public MetricsOut Metrics { get; }
    public LossRun Loss { get; }

    /// <summary>Start of a tick: every clock that charges phases starts from here.</summary>
    public void TickBegin()
    {
        Perf.Ticks?.Begin();
        _phases.Begin();
        _prof.TickBegin();
        Prof.TickBegin();
    }

    /// <summary>A phase of the tick ends: the harness CSV and the always-on phase timers both charge it.</summary>
    public void Lap(ServerPerf.Ph p)
    {
        Perf.Ticks?.Lap(p);
        _phases.Lap(p);
    }

    /// <summary>
    /// The last phase and the end of the tick: its length and the report lines, the loss test's window, the harness row, the heartbeat, and the verdict of a test run that is due.
    /// </summary>
    public void TickEnd(ulong startUs, double delta)
    {
        Health.TickEnd(startUs, delta);
        Loss.TickEnd(startUs);
        Lap(ServerPerf.Ph.Telemetry);
        Perf.End();
        Health.Heartbeat();
        _smoke.FinishIfDue();
    }

    /// <summary>What lives in the world: the entities, the joined players, the vehicles and the rockets.</summary>
    int LiveEntities(int joined) => _ctx.Match.Entities.Snapshot().Count + joined + _fleet.Count + _rocketCount();
}
