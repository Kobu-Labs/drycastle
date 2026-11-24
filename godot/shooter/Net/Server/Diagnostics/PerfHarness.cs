using Godot;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Server.Diagnostics;

/// <summary>
/// What the perf harness writes when the server runs with <c>--perf-out DIR</c>: the per-tick phase CSV, the network audit and the timestamps of the Shot / Hit / Kill events
/// (docs/perf-investigation/measurement-plan.md). Without the flag <see cref="Ticks"/> and <see cref="Audit"/> are null and the rest does nothing.
/// </summary>
internal sealed class PerfHarness
{
    readonly string _dir;
    readonly ServerContext _ctx;
    readonly Fleet _fleet;
    readonly Func<int, int> _liveEntities;
    readonly List<System.Numerics.Vector3> _auditViewers = new();
    StreamWriter? _events;

    public PerfHarness(string dir, ServerContext ctx, Fleet fleet, Func<int, int> liveEntities)
    {
        _dir = dir; _ctx = ctx; _fleet = fleet; _liveEntities = liveEntities;
        if (dir.Length == 0) return;
        Ticks = new ServerPerf(dir);
        Audit = new PerfAudit(dir);
        GD.Print($"server: perf phases on, writing {dir}/server-ticks.csv (--perf-out)");
    }

    /// <summary>The per-tick phase CSV.</summary>
    public ServerPerf? Ticks { get; }
    /// <summary>The network audit.</summary>
    public PerfAudit? Audit { get; }

    /// <summary>End of a tick: one CSV row.</summary>
    public void End()
    {
        if (Ticks == null) return;
        var joined = _ctx.Connections.Count(c => c.Joined);
        var moving = _fleet.Cars.Count(c => c.Velocity.LengthSquared() > 0.01f || c.Seat.Occupied);
        var asleep = 0; var parkedAwake = 0; // asleep covers every type (ground, flying, driven); parked-awake = driverless and not destroyed, yet still stepped
        foreach (var c in _fleet.Cars)
        {
            if (c.Asleep) asleep++;
            else if (!c.Destroyed && !c.Seat.Occupied) parkedAwake++;
        }
        Ticks.End(_ctx.Tick, joined, _ctx.Players.Count - joined, moving, _fleet.Count, _liveEntities(joined), _ctx.Now, asleep, parkedAwake);
    }

    /// <summary>The world entities sent this tick, and where every player stands, for the audit's interest-radius counts.</summary>
    public void AuditTick(IReadOnlyList<WorldEntry> world)
    {
        if (Audit == null) return;
        _auditViewers.Clear();
        foreach (var c in _ctx.Roster) _auditViewers.Add(c.Body.GlobalPosition.ToNumerics());
        Audit.Tick(world, _auditViewers, _ctx.Connections.Count(c => c.Joined), _ctx.Now);
    }

    /// <summary>Reliable event timestamps (measurement plan W2): the wall-clock time every Shot/Hit/Kill event was queued, for delivery latency under real loss.</summary>
    public void EventSent(EventKind kind, byte[] payload)
    {
        if (_dir.Length == 0 || kind is not (EventKind.Shot or EventKind.Hit or EventKind.Kill)) return;
        _events ??= new StreamWriter(Path.Combine(_dir, "events-server.csv")) { AutoFlush = true };
        _events.WriteLine((DateTime.UtcNow - DateTime.UnixEpoch).Ticks / 10 + "," + (int)kind + "," + Convert.ToHexString(payload));
    }

    public void Flush() => Ticks?.Flush();
}
