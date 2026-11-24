using Drycastle.Shooter.Net.Server.CarPool;

namespace Drycastle.Shooter.Net.Server.Diagnostics;

/// <summary>The verdict that ends a test run: the smoke tests and the loss test end the server when their own finish time or the quit deadline has come.</summary>
internal sealed class SmokeRun
{
    readonly ServerConfig _config;
    readonly ServerContext _ctx;
    readonly SmokeStats _stats;
    readonly Fleet _fleet;
    readonly LossRun _loss;
    readonly Action<bool> _end;

    public SmokeRun(ServerConfig config, ServerContext ctx, SmokeStats stats, Fleet fleet, LossRun loss, Action<bool> end)
    {
        _config = config; _ctx = ctx; _stats = stats; _fleet = fleet; _loss = loss; _end = end;
    }

    /// <summary>A test run ends the server with its verdict, when its deadline or its own finish time has come.</summary>
    public void FinishIfDue()
    {
        var now = _ctx.Now;
        if (_config.CarSmoke && _stats.CarExits >= 1 && _stats.FinishAt == 0) _stats.FinishAt = now + 5.0; // after the bot's check that the empty car stays put
        var quit = _config.QuitAfter > 0 && now >= _config.QuitAfter;
        var due = _stats.FinishAt > 0 && now >= _stats.FinishAt || quit;
        if (_config.RpgSmoke) { if (due) _end(_stats.Rpg()); }
        else if (_config.BlastSmoke) { if (due) _end(_stats.Blast(_fleet.Count)); }
        else if (_config.CarSmoke) { if (due) _end(_stats.Car(_config.Vehicles, _config.SmokeKind, _fleet.Count)); }
        else if (_config.WeaponsSmoke) { if (due) _end(_stats.Weapons()); }
        else if (_config.MatchSmoke) { if (due) _end(_stats.Match(_ctx.Match, _config.MatchConfig)); }
        else if (_loss.On) { if (quit) _loss.End(false); }
        else if (quit) _end(_stats.Net());
    }
}
