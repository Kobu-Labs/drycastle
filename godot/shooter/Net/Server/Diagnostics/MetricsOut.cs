using Godot;
using Drycastle.Shooter.Net.Server.Rewind;
using Drycastle.ShooterCore.Diagnostics;

namespace Drycastle.Shooter.Net.Server.Diagnostics;

/// <summary>
/// The numbers of the "server: stats" and "server: prof" lines, sent every report period to a metrics store when <c>DRYCASTLE_METRICS_URL</c> is set
/// (VictoriaMetrics' Influx write address, <c>http://metrics:8428/write</c> in deploy/docker-compose.yml; docs/monitoring.md). Off without it. The
/// lines are built on the game thread from text the log already has, once per period; the HTTP request runs on a pool thread, and a store that is
/// down costs the game nothing (<see cref="MetricsSender"/>). Series: <c>drycastle_&lt;measurement&gt;_&lt;field&gt;</c>, labelled by phase, scope or thread; none per player.
/// </summary>
internal sealed class MetricsOut
{
    readonly ServerContext _ctx;
    readonly MetricsBatch _batch = new();
    MetricsSender? _sender;
    bool _tried;

    public MetricsOut(ServerContext ctx) => _ctx = ctx;

    /// <summary>The batch of this period, or null when no store is configured.</summary>
    public MetricsBatch? Batch
    {
        get
        {
            if (!_tried)
            {
                _tried = true;
                var url = System.Environment.GetEnvironmentVariable("DRYCASTLE_METRICS_URL");
                if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out _))
                {
                    _sender = new MetricsSender(url, s => GD.PrintErr($"server: {s}"));
                    GD.Print($"server: metrics go to {url} every {ServerHealth.ReportSeconds:F0} s");
                }
                else if (!string.IsNullOrWhiteSpace(url)) GD.PrintErr($"server: DRYCASTLE_METRICS_URL is not an address: {url}");
            }
            return _sender != null ? _batch : null;
        }
    }

    /// <summary>
    /// A client's performance numbers of its last period (<see cref="ClientReport"/>): they join this period's batch as <c>drycastle_client_*</c>, labelled with
    /// the player and their graphics adapter, and <c>drycastle_client_scope_ms_per_s</c> per client scope. One per client per half period; a report that does
    /// not decode is dropped. Nothing in the game reads them.
    /// </summary>
    public void OnClientReport(ServerClient c, byte[] payload)
    {
        if (_ctx.Now - c.ClientReportAt < ClientReport.PeriodSeconds / 2) return;
        if (!ClientReport.TryDecode(payload, out var values, out var scopes, out var adapter, out _)) return;
        c.ClientReportAt = _ctx.Now;
        if (Batch is not { } m) return;
        var gpu = ClientReport.AdapterLabel(adapter);
        m.Begin("drycastle_client", "player", c.Name, "gpu", gpu);
        for (var i = 0; i < values.Length && i < ClientReport.Fields.Length; i++) m.Field(ClientReport.Fields[i], values[i]);
        m.Field("server_rtt_ms", PoseRewind.RttOf(c)).End();
        for (var i = 0; i < scopes.Length && i < ClientProf.ScopeCount; i++)
            m.Begin("drycastle_client_scope", "player", c.Name, "scope", ClientProf.Name(i)).Field("ms_per_s", scopes[i]).End();
    }

    /// <summary>End of the report period: what was gathered goes to the store.</summary>
    public void Flush(int reports)
    {
        if (_sender == null) return;
        _batch.Begin("drycastle_up").Field("reports", reports).Field("uptime_s", _ctx.Now).End(); // a heartbeat: no data means the server is down or hung
        _sender.Send(_batch.Take());
    }
}
