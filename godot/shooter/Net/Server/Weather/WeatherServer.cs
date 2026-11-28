using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Scenery;

namespace Drycastle.Shooter.Net.Server.Weather;

/// <summary>
/// The weather on the server (Weather, #230, GLOSSARY "Thunderstorm"): a <see cref="WeatherMachine"/> ticked every game tick. A change of phase goes to everyone
/// (<see cref="EventKind.WeatherState"/>) and to each joiner; a lightning strike goes to everyone (<see cref="EventKind.Lightning"/>). Thundernite reads <see cref="Storm"/>.
/// <c>/weather</c> (admin) tells the phase; <c>/weather storm</c> and <c>/weather clear</c> force it.
/// </summary>
internal sealed class WeatherServer
{
    readonly ServerContext _ctx;
    readonly WeatherMachine _weather;

    public WeatherServer(ServerContext ctx, StormLook cfg, Random rng)
    {
        _ctx = ctx;
        _weather = new WeatherMachine(cfg, rng);
        _weather.Changed += phase => _ctx.Broadcast(EventKind.WeatherState, WeatherCodec.EncodeState(phase));
    }

    /// <summary>A thunderstorm is on (Thundernite can be taken).</summary>
    public bool Storm => _weather.Storm;

    public void Tick(double dt)
    {
        if (_weather.Tick((float)dt) is { } strike) _ctx.Broadcast(EventKind.Lightning, WeatherCodec.EncodeStrike(strike));
    }

    /// <summary>Join: the phase (always, so a client that kept an old storm in its sky clears it).</summary>
    public void SendOnJoin(ServerClient c) => _ctx.SendEvent(c, EventKind.WeatherState, WeatherCodec.EncodeState(_weather.Phase));

    /// <summary>"/weather": tells the phase and the time left; "storm" or "clear" forces it for everyone.</summary>
    public void Chat(ServerClient c, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            _ctx.ChatTo(c, ChatLineKind.Info, $"The weather is {(_weather.Storm ? "a thunderstorm" : "clear")} for {_weather.SecondsLeft / 60:0.#} more minutes.");
            return;
        }
        var storm = args[0] == "storm";
        var changed = storm ? _weather.ForceStorm() : _weather.ForceClear();
        _ctx.ChatTo(c, ChatLineKind.Info, $"{c.Name} {(changed ? "made" : "kept")} the weather {(storm ? "a thunderstorm" : "clear")}.");
    }
}
