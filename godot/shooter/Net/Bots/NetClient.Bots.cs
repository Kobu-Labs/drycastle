using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The headless bot roles of this client (set by the host from the command line) and what the scripted bots (<see cref="ClientBot"/>) may see of it: <see cref="IBotHost"/>, implemented
/// here and nowhere else. The bots themselves live beside this file.
/// </summary>
public partial class NetClient : IBotHost
{
    /// <summary>Loss test role ("walker", "shooter", ...); empty = off. See <see cref="LossTestBot"/>.</summary>
    public string LossBot = "";
    public double LossMeasure = 30;
    /// <summary>Directory for the per-bot snapshot csv; empty = no log.</summary>
    public string LossOut = "";
    /// <summary>Perf-harness role "streaker": the killstreaks and vehicles it keeps asking for, and how often.</summary>
    public string PerfStreaks = "", PerfVehicles = "";
    public double PerfStreakPeriod = 2;
    /// <summary>Match smoke role: "alice" (kills bob with the debug command) or "bob" (fires after each spawn); empty = normal bot/player.</summary>
    public string MatchBot = "";
    /// <summary>Weapons smoke role: "a" (shoots) or "b" (stands still and counts hits); empty = none.</summary>
    public string WeaponsBot = "";
    /// <summary>Rpg smoke role ("a"/"b"); WeaponsBot carries the same letter so the shared bot plumbing runs.</summary>
    public string RpgBot = "";
    /// <summary>Vehicle blast smoke role ("a"/"b"); WeaponsBot carries the same letter.</summary>
    public string BlastBot = "";
    /// <summary>Car smoke role: "a" drives, "b" stands in the way.</summary>
    public string CarBot = "";
    /// <summary>--car-loop: a headless driver on the normal map for <see cref="CarLoopSeconds"/> (perf load).</summary>
    public bool CarLoop;
    public double CarLoopSeconds = 120;
    /// <summary>--bot-chat script: chat lines at their second, and the rozhlas feed check (see <see cref="RozhlasTestBot"/>).</summary>
    public string BotChat = "";
    public double BotSeconds = 6;

    ClientBot? _bot;
    RozhlasTestBot? _rozhlasTest;
    readonly EventLogs _logs = new();

    /// <summary>The event logs are read only by the scripted bots (the weapons, rpg, blast and car checks), so a normal match keeps none of them: they grew for the whole match (#173, T3-028).</summary>
    bool KeepEventLogs => WeaponsBot != "" || RpgBot != "" || BlastBot != "" || CarBot != "";

    /// <summary>The bot this headless client plays as: the first role that is set, else the plain walker (null for a player with a window).</summary>
    ClientBot? MakeBot()
    {
        if (!Bot) return null;
        _rozhlasTest = BotChat != "" ? new RozhlasTestBot(this, BotChat) : null;
        var chat = _rozhlasTest;
        if (MatchBot != "") return new MatchSmokeBot(this, chat, MatchBot);
        if (LossBot != "") return new LossTestBot(this, chat, LossBot, LossMeasure, LossOut, PerfStreaks, PerfVehicles, PerfStreakPeriod);
        if (CarBot != "") return new CarSmokeBot(this, chat, CarBot);
        if (CarLoop) return new CarLoopBot(this, chat, CarLoopSeconds);
        if (BlastBot != "") return new BlastSmokeBot(this, chat, WeaponsBot);
        if (RpgBot != "") return new RpgSmokeBot(this, chat, WeaponsBot);
        if (WeaponsBot != "") return new WeaponsSmokeBot(this, chat, WeaponsBot);
        return new WalkBot(this, chat);
    }

    ClientContext IBotHost.Ctx => Ctx;
    bool IBotHost.HoldsRozhlasSet => _rozhlas.HoldsSet;
    double IBotHost.Elapsed => _elapsed;
    double IBotHost.Seconds => BotSeconds;
    bool IBotHost.LinkEnded => LinkEnded;
    bool IBotHost.OwnSpawned => OwnSpawned;
    EntityState IBotHost.MyState => _myState;
    bool IBotHost.Driving => _driving;
    int IBotHost.WantSlot { set => _wantSlot = value; }
    IReadOnlyDictionary<ushort, EntityRecord> IBotHost.States => _dec.States;
    EventLogs IBotHost.Logs => _logs;
    IEnumerable<BotCar> IBotHost.Cars => _cars.Values.Select(AsBotCar);
    BotCar? IBotHost.Car(ushort id) => _cars.TryGetValue(id, out var v) ? AsBotCar(v) : null;
    IEnumerable<BotRemote> IBotHost.Remotes => _remotes.Values.Select(r => new BotRemote(r.State, r.LastPos));
    int IBotHost.RocketCount => _rockets.Count;
    VehiclesFile? IBotHost.Vehicles => Vehicles;
    int IBotHost.Snaps => _snaps;
    int IBotHost.Recon => _recon;
    float IBotHost.MaxErr => _maxErr;
    uint IBotHost.LastTick => _lastTick;
    double IBotHost.LastArrival => _lastArrival;
    ushort IBotHost.Seq => _seq;
    float IBotHost.OwnTravel => new Vector2(_meMax.X - _meMin.X, _meMax.Z - _meMin.Z).Length();
    float IBotHost.BestRemoteTravel => _remotes.Values.Select(r => new Vector2(r.Max.X - r.Min.X, r.Max.Z - r.Min.Z).Length()).DefaultIfEmpty(0f).Max();
    RubberStats IBotHost.Rubber { get => _rub; set => _rub = value; }
    NetSim? IBotHost.Sim => Sim;
    int IBotHost.DriveCorrections => _driveCorrections;

    (double Received, double Sent) IBotHost.PopTraffic() =>
        (_conn.PopStatistic(ENetConnection.HostStatistic.ReceivedData), _conn.PopStatistic(ENetConnection.HostStatistic.SentData));

    void IBotHost.Leave() => _link.Enter(LinkPhase.Left);
    void IBotHost.Quit(bool ok) => GetTree().Quit(ok ? 0 : 2);

    void IBotHost.SendMic(byte[] packet) => SendMic(packet);

    void SendMic(byte[] packet)
    {
        if (_peer != null) Tx(_peer, Wire.ChanRozhlasFeed, packet, 0);
    }

    BotCar AsBotCar(CarView v) => new(v.Id, v.Kind, v.Driver, v.Pos, v.Kmh, v.Stage, v.Destroyed, v.Exploded, v.Node, DriveDef(v) != null);
}
