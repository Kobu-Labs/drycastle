using Godot;
using Drycastle.Shooter.Net.Bots;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Chat;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Navigation;

/// <summary>
/// The navigation mesh the bots, the citizens and the Gang members walk on (<see cref="BotNav"/>, baked from the map colliders at start) and the route worker behind it. The mesh is built once, when
/// first needed (the server pauses for the bake); the worker's finished routes are applied on the tick at one point only (<see cref="Drain"/>), and the cost of the routes is counted for the bots' report.
/// </summary>
internal sealed class NavService
{
    readonly ServerContext _ctx;
    readonly Node _world;
    readonly string _mapDir;
    readonly string _cacheDir;
    readonly bool _arena;
    readonly Action _flush;
    readonly Func<long, NVec3[], bool> _other;
    BotNav? _nav;
    bool _tried;
    long _ticket;
    int _routes, _dropped;
    double _workMs, _workMaxMs, _waitMs, _waitMaxMs;

    /// <param name="world">The scene the mesh is baked from.</param>
    /// <param name="mapDir">The map folder (its manifest lists the chunks); empty = no map, no mesh.</param>
    /// <param name="cacheDir">Where baked tiles are kept.</param>
    /// <param name="arena">A smoke arena, far from the map: no mesh.</param>
    /// <param name="flush">Sends what is queued, so a notice goes out before the pause.</param>
    /// <param name="other">Takes a route no bot asked for (the Gang members'): by its ticket and points, true when somebody wanted it.</param>
    public NavService(ServerContext ctx, Node world, string mapDir, string cacheDir, bool arena, Action flush, Func<long, NVec3[], bool> other)
    {
        _ctx = ctx; _world = world; _mapDir = mapDir; _cacheDir = cacheDir; _arena = arena; _flush = flush; _other = other;
    }

    /// <summary>The mesh, or null when it is not built (no map, or the build failed).</summary>
    public BotNav? Mesh => _nav;

    /// <summary>Whether a build could still start: not tried yet, on a real map.</summary>
    public bool CanBuild => !_tried && !_arena && _mapDir != "";

    /// <summary>A ticket for a route request: the microsecond clock, kept strictly rising, so it is unique and the answer's age is read off it.</summary>
    public long NextTicket() => _ticket = Math.Max(_ticket + 1, (long)Time.GetTicksUsec());

    /// <summary>
    /// Once the colliders are in, at start and before connections are accepted: the mesh the bots and the citizens walk on. Not in the
    /// smoke arenas (far from the map). Tried once; it blocks for the bake (once per map version, then it is read from the cache).
    /// </summary>
    public void Build()
    {
        if (!CanBuild) return;
        _tried = true;
        try
        {
            _nav = BotNav.Build(_world, _mapDir, _cacheDir, Math.Max(1, System.Environment.ProcessorCount / 2));
            GD.Print($"server: bot navigation from {_nav.Source}: {_nav.Polygons} polygons in {_nav.Tiles} tiles, {_nav.BuildSeconds:F1} s");
        }
        catch (Exception e)
        {
            GD.PrintErr($"server: bot navigation failed, the bots will stand where they spawn: {e.Message}");
            _nav = null;
        }
    }

    /// <summary>The navigation mesh, built when first needed (the server pauses for a few seconds); false when it is not there (no map, or the build failed).</summary>
    public bool Ensure(ServerClient c)
    {
        if (_nav == null && !_tried)
        {
            _ctx.ChatTo(c, ChatLineKind.Info, "Building the navigation for the first time. The server pauses for a few seconds.");
            BuildAfterNotice();
        }
        return _nav != null;
    }

    /// <summary>Builds the mesh after the notice the caller just queued has gone out (the bake pauses the server).</summary>
    public void BuildAfterNotice()
    {
        _flush();
        Build();
    }

    /// <summary>
    /// The route worker's finished answers, at one fixed point of the tick (before the entities tick): each goes to the bot that asked, else to the other asker (the Gang members), or is dropped
    /// when none wants it any more.
    /// </summary>
    public void Drain()
    {
        if (_nav == null) return;
        while (_nav.TryTake(out var a))
        {
            var route = ToNumerics(a.Path);
            var applied = false;
            foreach (var b in _ctx.Players.Values)
            {
                if (!b.IsBot || b.Brain!.PathTicket != a.Ticket) continue;
                applied = b.BotWasAlive && b.Brain.AcceptPath(a.Ticket, route, b.Body.GlobalPosition.ToNumerics(), _ctx.Now);
                break;
            }
            if (!applied) applied = _other(a.Ticket, route);
            if (!applied) { _dropped++; continue; }
            var wait = ((long)Time.GetTicksUsec() - a.Ticket) / 1000.0;
            _routes++; _workMs += a.WorkMs; _workMaxMs = Math.Max(_workMaxMs, a.WorkMs);
            _waitMs += wait; _waitMaxMs = Math.Max(_waitMaxMs, wait);
        }
    }

    /// <summary>A route of the worker as the pure side wants it; fewer than two points is no route.</summary>
    static NVec3[] ToNumerics(Vector3[] route)
    {
        if (route.Length < 2) return Array.Empty<NVec3>();
        var r = new NVec3[route.Length];
        for (var i = 0; i < r.Length; i++) r[i] = route[i].ToNumerics();
        return r;
    }

    /// <summary>The cost of the routes since the last call as one log line (null when there were none), and the count starts again.</summary>
    public string? TakeSummary(int bots)
    {
        var line = _routes > 0 || _dropped > 0
            ? $"server: bots {bots}, routes {_routes} in 10 s (dropped {_dropped}), worker mean {_workMs / Math.Max(1, _routes):F1} ms max {_workMaxMs:F1} ms, wait to applied mean {_waitMs / Math.Max(1, _routes):F1} ms max {_waitMaxMs:F1} ms"
            : null;
        _routes = _dropped = 0; _workMs = _workMaxMs = _waitMs = _waitMaxMs = 0;
        return line;
    }

    /// <summary>Gives the worker's tiles back (the server is closing).</summary>
    public void Free() => _nav?.Free();
}
