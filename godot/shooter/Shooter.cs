using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// Shooter entry point. Args after "--":
///   --role server|client      (default client; the server is for --headless)
///   --map DIR                 map folder (else SUCHOHRAD_MAP, else ../../out/map relative to the project)
///   --connect HOST[:PORT] --name NAME   join a server (default port 27015); --headless-bot [--bot-seconds N] scripts a walk
///   --port N                  server listen port (default 27015); --quit-after SECONDS ends the server with a verdict
///   --net-sim latency_ms=50,jitter_ms=5,loss=0.03   server and client: delay every outgoing packet, drop that share of the unreliable ones (see NetSim.cs)
///   --loss-test N [--loss-measure S] [--loss-out DIR]   server: loss/latency test round for N bots (use with --match-set min_players=N); --loss-bot walker|shooter on the bots (make shooter-loss-test)
///   --match-set k=v,k=v       server: override match.json values (score_goal, time_limit, respawn_delay, spawn_protection, end_screen_time, min_spawn_distance, min_players)
///   --unlock-all              server: dev mode, every weapon, perk, killstreak and level-gated item unlocked (also SUCHOHRAD_UNLOCK_ALL=1); nothing is saved (issue #149)
///   --targets N               server: plant N stationary target dummies around the spawn area (0-64; default from match.json, 0; make shooter-server uses 24)
///   --allow-sandbox           server: grant sandbox mode (godmode, unlimited ammo, every killstreak on demand, no XP or score) to clients that ask (#156); dev only, make SANDBOX=1
///   --sandbox                 client: ask the server for sandbox mode (also SUCHOHRAD_SANDBOX=1); the server may refuse
///   --dev-ui                  client: show the dev-only UI (the Advanced section of the connect screen); debug builds always show it
///   --debug-cmds              server: honour the client debug kill command; --match-smoke makes the server judge one full round (implies --debug-cmds)
///   --match-bot alice|bob     client: scripted match smoke role (alice kills bob with the debug command, bob fires after each spawn)
///   --blast-smoke             server: flat arena with three cars, judges vehicle damage stages, the blast and the chain reaction; --blast-bot a|b client roles (ticket #103)
///   --car-smoke               server: flat test arena with three cars, judges the vehicle scenario; --car-bot a|b client roles; --car-kind car|motorcycle|bicycle picks the vehicle type of the smoke (default car); --vehicle-set abandon_return_time=3 overrides vehicles.json
///   --weapons-smoke           server: flat test arena with a wall (far from the map), honours debug commands, judges the weapons scenario
///   --bazooka-smoke           server: same flat arena, judges the bazooka scenario
///   --bazooka-bot a|b         client: scripted bazooka smoke role (a shoots, b is the target)
///   --weapons-bot a|b         client: scripted weapons smoke role (a shoots, b stands still)
///   --menu-shots DIR          render main menu, connect (empty, error), settings tabs, pause, loading and a dialog at 1280x720 and 3840x2160 into DIR as PNGs, then exit (needs a display)
///   --offline                 client: skip the menus and walk the map locally (the old dev view)
///   (client with a display and none of the smoke flags starts at the main menu; --connect host:port --name N joins straight away)
///   --model-shots DIR         render the remote character animations and the first-person weapon viewmodels into DIR as PNGs, then exit (needs a display)
///   --hud-shots DIR           render the HUD with fake data at 1280x720 and 3840x2160 into DIR as PNGs, then exit (needs a display)
///   --smoke-move              client only: scripted movement check on the map collision (SHOOTER_SMOKE_MOVE_OK, exit 0; else exit 2)
///   --smoke                   load everything, print SHOOTER_SMOKE_OK and exit 0 (exit 2 on failure)
///   --db PATH                 server: SQLite progression database (default ./shooter.db); created and migrated at start (ADR 0003)
///   --time HH:MM              server: start time of day (overrides environment.json start_time)
///   --day-length MIN          server: real minutes for one 24 hour day (overrides environment.json day_length_minutes)
///   --freeze-time             server: hold the clock at its start time (or --time)
/// No gameplay yet: this only boots the role on the loaded map.
/// </summary>
public partial class Shooter : Node3D
{
    readonly Suchohrad.MapLoader.MapLoader _map = new();
    /// <summary>The map's water (water.json), parsed when the map loads; Water.Empty on a dry map.</summary>
    Suchohrad.ShooterCore.Water _water = Suchohrad.ShooterCore.Water.Empty;
    Suchohrad.ShooterServer.Persistence.IProgressionStore? _progression;
    bool _smoke;
    bool _smokeMove;
    readonly Random _rng = new();
    GameData _data = null!;
    int _frames;

    public override void _Ready()
    {
        var cfg = Config.Parse(OS.GetCmdlineUserArgs());
        var role = cfg.Str("role", "client");
        _smoke = cfg.Flag("smoke");
        _smokeMove = cfg.Flag("smoke-move");
        _cfg = cfg;
        DevUi.Enabled = OS.IsDebugBuild() || cfg.Flag("dev-ui");
        GameSettings.Apply(GameSettings.Current, false);
        if (cfg.Str("model-shots") != "") { _ = ModelShots.Run(this, cfg.Str("model-shots")); return; }
        if (cfg.Str("hud-shots") != "") { _ = HudShots.Run(this, cfg.Str("hud-shots")); return; }
        if (cfg.Str("menu-shots") != "") { _ = MenuShots.Run(this, cfg.Str("menu-shots")); return; }
        if (role == "client")
        {
            try { GD.Print($"sounds: {Sfx.Init(this).SoundCount} events, table and files ok"); }
            catch (Exception e)
            {
                GD.PrintErr($"startup failed: {e.Message}");
                GetTree().Quit(2);
                _smoke = false;
                _frames = int.MinValue;
                return;
            }
        }
        if (role == "client" && !_smoke && !_smokeMove && !cfg.Flag("offline") && !cfg.Flag("headless-bot") && DisplayServer.GetName() != "headless")
        {
            GameSettings.RestoreWindowSize();
            StartMenus(cfg);
            return;
        }
        try
        {
            if (role is not ("server" or "client")) throw new InvalidOperationException($"unknown role {role} (server|client)");
            var data = _data = LoadData();
            GD.Print($"data: {data.Weapons.Weapons.Count} weapons, score goal {data.Match.ScoreGoal}, {data.Vehicles.Vehicles.Count} vehicle types");
            LoadMap(cfg);
            if (role == "server" && !_smoke) BuildServer(cfg);
            if (role == "client" && cfg.Str("connect") != "") BuildNetClient(cfg);
            else if (role == "client") BuildClientView();
            if (role == "client" && _smokeMove) _smoke = false;
            GD.Print($"SHOOTER_READY role={role}");
        }
        catch (Exception e)
        {
            GD.PrintErr($"startup failed: {e.Message}");
            GetTree().Quit(2);
            _smoke = false;
            _frames = int.MinValue;
        }
    }

    /// <summary>Server shutdown: closes the progression store, which writes out the queued saves before it returns.</summary>
    public override void _ExitTree()
    {
        _progression?.Dispose();
        _progression = null;
    }

    public override void _Process(double delta)
    {
        // let a few frames run so physics and the scene tree settle before a smoke exit
        if (_smoke && ++_frames >= 3)
        {
            GD.Print("SHOOTER_SMOKE_OK");
            GetTree().Quit(0);
        }
    }

    // ---- menu flow: Boot -> Main menu -> Play/connect -> Loading -> Game <-> Pause ----

    Config _cfg = null!;
    UiShell _shell = null!;
    NetClient? _client;
    bool _mapLoaded, _worldLit;

    void StartMenus(Config cfg)
    {
        _shell = new UiShell
        {
            PauseFactory = () => new PauseScreen(() => { }, () => LeaveMatch(null), () => _shell.Push(NewLoadouts()), () => _client?.Profile is { } p && DevUnlocks.IsMarked(p.Unlocks)),
            WantsFreeMouse = () => _client is { Dead: true }, // the respawn screen releases the mouse so its buttons can be clicked
        };
        AddChild(_shell);
        GD.Print("SHOOTER_MENU role=client");
        var target = cfg.Str("connect");
        if (target == "") { ShowMain(); return; }
        var (host, p) = ConnectForm.Split(target);
        _ = BeginGame(host, int.TryParse(p, out var port) ? port : ConnectForm.DefaultPort, cfg.Str("name", "player"));
    }

    void ShowMain()
    {
        Sfx.MenuMusic(true);
        // Loadouts and camouflage live on the server, so they are not offered before joining: they are in the pause menu and on the respawn screen.
        _shell.Reset(new MainMenu(() => _shell.Push(new ConnectScreen((h, p, n) => _ = BeginGame(h, p, n))), () => _shell.Push(new SettingsScreen(false))));
    }

    /// <summary>The loadout editor, from pause or the respawn screen.</summary>
    LoadoutScreen NewLoadouts() => new(() => _client, () => _data ??= LoadData()) { CustomizeFactory = slot => NewCustomization(slot) };

    /// <summary>The camouflage customiser for one loadout slot (-1: the active one), from the main menu or the loadout editor (#141).</summary>
    CustomizationScreen NewCustomization(int slot) => new(() => _client, () => _data ??= LoadData(), slot);

    async Task Step(LoadingScreen ld, string key, float f)
    {
        ld.Set(key, f);
        for (var i = 0; i < 2; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    async Task BeginGame(string host, int port, string name)
    {
        var ld = new LoadingScreen($"{host}:{port}");
        _shell.SetGameActive(false);
        _shell.Reset(ld);
        string? failure = null;
        try
        {
            await Step(ld, "loading.data", 0.05f);
            _data ??= LoadData();
            await Step(ld, "loading.map", 0.15f);
            if (!_mapLoaded)
            {
                // One step per chunk, a frame drawn between steps: the spinner and the bar keep moving through the load.
                foreach (var f in LoadMapSteps(_cfg)) await Step(ld, "loading.map", 0.15f + 0.60f * f);
                _mapLoaded = true;
            }
            await Step(ld, "loading.world", 0.75f);
            if (!_worldLit) { AddLightAndSky(); _worldLit = true; }
            await Step(ld, "loading.connect", 0.85f);
            var done = new TaskCompletionSource<string?>();
            var killY = _map.SpawnPoints.Count > 0 ? _map.SpawnPoints.Min(p => p.Position.Y) - 100f : -1000f;
            var client = _client = new NetClient
            {
                World = this, Host = host, Port = port, PlayerName = name, Token = GameSettings.PlayerToken(), SandboxRequested = SandboxAsked(_cfg), KillY = killY, Water = _water, Weapons = _data.Weapons, PerkData = _data.Perks, CamoData = _data.Camo, Killstreaks = _data.Killstreaks, XpCurve = _data.Levels.Curve, Map = _map, Vehicles = _data.Vehicles, FxDemo = _cfg.Str("fx-demo"), PoseDemo = _cfg.Flag("pose-demo"), Sim = NetSim.Parse(_cfg.Str("net-sim")),
                OnWelcomed = () => done.TrySetResult(null),
            };
            client.OnFailed = (why, joined) =>
            {
                if (!joined) done.TrySetResult(Strings.F("connect.err.join", $"{host}:{port}", why));
                else if (_client == client) LeaveMatch(Strings.F("connect.err.lost", $"{host}:{port}", why));
            };
            client.OpenLoadouts = () => _shell.Push(NewLoadouts());
            client.MenuOpen = () => _shell.Top != null;
            client.LevelUpReceived += lu => _shell.Toast(Strings.F("progress.levelup", lu.Level));
            client.SandboxAnswered += a => _shell.Toast(a.Reason, a.Granted ? ToastKind.Info : ToastKind.Warn);
            client.SandboxNoticed += text => _shell.Toast(text, ToastKind.Warn);
            client.OpenSandbox = () => _shell.Push(new SandboxScreen(client, () => _data ??= LoadData()));
            AddChild(client);
            failure = await done.Task;
        }
        catch (Exception e)
        {
            failure = e.Message;
            GD.PrintErr($"start failed: {e.Message}");
        }
        if (failure != null)
        {
            DropClient();
            ShowMain();
            _shell.Push(new ConnectScreen((h, p, n) => _ = BeginGame(h, p, n), failure));
            return;
        }
        ld.Set("loading.ready", 1f);
        Sfx.MenuMusic(false);
        _shell.SetGameActive(true);
        _shell.Reset();
        GD.Print("SHOOTER_GAME_STARTED");
    }

    void DropClient()
    {
        if (_client == null) return;
        _client.Teardown();
        _client.QueueFree();
        _client = null;
    }

    /// <summary>Back to the main menu; with a message when the server dropped us (what happened and what to do).</summary>
    void LeaveMatch(string? error)
    {
        DropClient();
        ShowMain();
        _shell.Toast(error == null ? Strings.T("disconnect.done") : error + " " + Strings.T("connect.err.help.lost"), error == null ? ToastKind.Info : ToastKind.Error);
    }

    static GameData LoadData()
    {
        static string Read(string f) =>
            Godot.FileAccess.Open($"res://data/{f}", Godot.FileAccess.ModeFlags.Read)?.GetAsText()
            ?? throw new DataFileException($"{f}: cannot open ({Godot.FileAccess.GetOpenError()})");
        return GameData.Parse(Read("weapons.json"), Read("match.json"), Read("vehicles.json"), Read("perks.json"), Read("levels.json"), Read("killstreaks.json"), Read("turret.json"), Read("environment.json"), Read("camo.json"));
    }

    void LoadMap(Config cfg) { foreach (var _ in LoadMapSteps(cfg)) { } }

    /// <summary>Loads the map, yielding the fraction done after each chunk so the loading screen can draw a frame between steps.</summary>
    IEnumerable<float> LoadMapSteps(Config cfg)
    {
        var dir = GamePaths.Map(cfg.Str("map", ""));
        if (!Directory.Exists(dir)) throw new InvalidOperationException($"map folder not found: {dir} (pass --map DIR or set SUCHOHRAD_MAP)");
        AddChild(_map.Root);
        foreach (var f in _map.LoadSteps(Path.GetFullPath(dir))) yield return f * 0.95f;
        _water = string.IsNullOrEmpty(_map.WaterJson) ? Suchohrad.ShooterCore.Water.Empty : Suchohrad.ShooterCore.Water.Parse(_map.WaterJson);
        WaterSurfaces.Attach(this, _water); // client-side meshes; null on a dry map or headless
        var s = _map.Stats;
        // Lamps are solid, so the server gets the same pole colliders as the client (map 0.5.0+; none on older maps).
        if (_map.Lamps.Count > 0) AddChild(new StreetLamps { Bases = _map.Lamps.Select(l => l.Position).ToList() });
        GD.Print($"map {_map.MapVersion}: chunks={s.ChunksLoaded} colliders={s.CollidersBuilt} spawns={_map.SpawnPoints.Count} lamps={_map.Lamps.Count} load={s.LoadSeconds:F2}s");
    }

    Vector3 RandomSpawn() =>
        _map.SpawnPoints.Count > 0 ? _map.SpawnPoints[_rng.Next(_map.SpawnPoints.Count)].Position : Vector3.Zero;

    void BuildServer(Config cfg)
    {
        // Opened here so a bad path or failed migration stops the server at start, like a bad --match-set.
        // Write failures are logged by the store, never thrown into the match. Disposed in _ExitTree (flushes queued writes).
        // --db is a SQLite file path or a PostgreSQL URL/connection string; SUCHOHRAD_DB (else ./shooter.db) when absent, so a password stays out of the command line.
        var envDb = OS.GetEnvironment("SUCHOHRAD_DB");
        var dbPath = cfg.Str("db", string.IsNullOrEmpty(envDb) ? "./shooter.db" : envDb);
        _progression = Suchohrad.ShooterServer.Persistence.ProgressionStores.Open(dbPath, s => GD.PrintErr(s), _data.Levels.Curve);
        GD.Print($"server: progression db {Suchohrad.ShooterServer.Persistence.ProgressionStores.Describe(dbPath)}");
        var killY = _map.SpawnPoints.Count > 0 ? _map.SpawnPoints.Min(p => p.Position.Y) - 100f : -1000f;
        var server = new NetServer
        {
            World = this, KillY = killY, Water = _water, Map = _map, Progression = _progression, Perks = _data.Perks, Levels = _data.Levels, Killstreaks = _data.Killstreaks, CamoData = _data.Camo, Turret = _data.Turret,
            Spawns = _map.SpawnPoints.Select(sp => new System.Numerics.Vector3(sp.Position.X, sp.Position.Y, sp.Position.Z)).ToList(),
            MatchConfig = WithTargets(ApplyOverrides(_data.Match, cfg.Str("match-set")), cfg.Str("targets")),
            Vehicles = ApplyVehicleOverrides(_data.Vehicles, cfg.Str("vehicle-set")),
            RoadSpawns = _map.SpawnPoints.Where(sp => sp.Kind == "road").OrderBy(sp => sp.Highway is "path" or "footway" or "steps" or "track" ? 1 : 0)
                .Select(sp => new System.Numerics.Vector3(sp.Position.X, sp.Position.Y, sp.Position.Z)).ToList(),
            CarSmoke = cfg.Flag("car-smoke"), BlastSmoke = cfg.Flag("blast-smoke"), SmokeKind = cfg.Str("car-kind", "car"),
            Weapons = _data.Weapons, WeaponsSmoke = cfg.Flag("weapons-smoke"), BazookaSmoke = cfg.Flag("bazooka-smoke"),
            MatchSmoke = cfg.Flag("match-smoke"), DebugCommands = cfg.Flag("debug-cmds") || cfg.Flag("match-smoke"),
            Clock = ServerClock(cfg),
            Port = int.TryParse(cfg.Str("port", "27015"), out var port) ? port : 27015,
            QuitAfter = double.TryParse(cfg.Str("quit-after", "0"), out var q) ? q : 0,
            Sim = NetSim.Parse(cfg.Str("net-sim")),
            LossTest = int.TryParse(cfg.Str("loss-test", "0"), out var lt) ? lt : 0,
            LossMeasure = double.TryParse(cfg.Str("loss-measure", "30"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lm) ? lm : 30,
            LossOut = cfg.Str("loss-out"),
            UnlockAll = DevUnlocks.Enabled(cfg.Flag("unlock-all"), System.Environment.GetEnvironmentVariable(DevUnlocks.EnvVar)),
            AllowSandbox = cfg.Flag("allow-sandbox"),
        };
        if (server.AllowSandbox) GD.Print("server: sandbox mode allowed (--allow-sandbox, #156)");
        if (server.UnlockAll) GD.Print("server: dev mode, every unlock on (not saved to the database)");
        if (server.Sim != null) GD.Print($"server: net-sim {server.Sim}");
        if (server.LossTest > 0 || server.Sim != null) Engine.MaxFps = 200; // delayed packets are released at frame rate
        AddChild(server);
    }

    /// <summary>The server's day/night clock: environment.json, then --time, --day-length and --freeze-time.</summary>
    DayClock ServerClock(Config cfg)
    {
        var time = cfg.Str("time");
        var dayLength = cfg.Str("day-length");
        double? minutes = null;
        if (dayLength != "")
        {
            if (!double.TryParse(dayLength, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m)) throw new InvalidOperationException($"bad --day-length {dayLength}");
            minutes = m;
        }
        return _data.Environment.ToClock(time == "" ? null : time, minutes, cfg.Flag("freeze-time"));
    }

    static VehiclesFile ApplyVehicleOverrides(VehiclesFile v, string spec)
    {
        foreach (var kv in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = kv.Split('=');
            if (p.Length != 2 || !float.TryParse(p[1], System.Globalization.CultureInfo.InvariantCulture, out var x)) throw new InvalidOperationException($"bad --vehicle-set entry {kv}");
            switch (p[0])
            {
                case "abandon_return_time": v.AbandonReturnTime = x; break;
                case "destroyed_return_time": v.DestroyedReturnTime = x; break;
                case "run_over_min_speed": v.RunOverMinSpeed = x; break;
                default: throw new InvalidOperationException($"unknown vehicle key {p[0]}");
            }
        }
        return v;
    }

    /// <summary>--targets N: how many stationary target dummies the server plants (0-64, #148). Absent keeps match.json's value.</summary>
    static MatchConfig WithTargets(MatchConfig m, string spec)
    {
        if (spec == "") return m;
        if (!int.TryParse(spec, out var n) || n < 0 || n > 64) throw new InvalidOperationException($"bad --targets {spec} (0-64)");
        m.Targets = n;
        return m;
    }

    static MatchConfig ApplyOverrides(MatchConfig m, string spec)
    {
        foreach (var kv in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = kv.Split('=');
            if (p.Length != 2 || !float.TryParse(p[1], System.Globalization.CultureInfo.InvariantCulture, out var v)) throw new InvalidOperationException($"bad --match-set entry {kv}");
            switch (p[0])
            {
                case "score_goal": m.ScoreGoal = (int)v; break;
                case "time_limit": m.TimeLimit = v; break;
                case "respawn_delay": m.RespawnDelay = v; break;
                case "spawn_protection": m.SpawnProtection = v; break;
                case "end_screen_time": m.EndScreenTime = v; break;
                case "min_spawn_distance": m.MinSpawnDistance = v; break;
                case "min_players": m.MinPlayers = (int)v; break;
                default: throw new InvalidOperationException($"unknown match key {p[0]}");
            }
        }
        return m;
    }

    /// <summary>Networked client: light/sky, then prediction and interpolation via <see cref="NetClient"/>.</summary>
    /// <summary>--sandbox or SUCHOHRAD_SANDBOX=1: the client asks for sandbox mode (#156).</summary>
    static bool SandboxAsked(Config cfg) =>
        cfg.Flag("sandbox") || System.Environment.GetEnvironmentVariable("SUCHOHRAD_SANDBOX") is "1" or "true"
        || (DevUi.Enabled && GameSettings.LoadConnect("sandbox", "0") == "1"); // the connect screen's checkbox (dev builds)

    void BuildNetClient(Config cfg)
    {
        var target = cfg.Str("connect");
        var host = target; var port = 27015;
        var colon = target.LastIndexOf(':');
        if (colon > 0 && int.TryParse(target[(colon + 1)..], out var pp)) { host = target[..colon]; port = pp; }
        var bot = cfg.Flag("headless-bot");
        if (!bot) AddLightAndSky();
        var killY = _map.SpawnPoints.Count > 0 ? _map.SpawnPoints.Min(p => p.Position.Y) - 100f : -1000f;
        _smoke = false;
        AddChild(new NetClient
        {
            World = this, Host = host, Port = port, PlayerName = cfg.Str("name", "player"), Bot = bot, KillY = killY, SandboxRequested = SandboxAsked(cfg),
            BotSeconds = double.TryParse(cfg.Str("bot-seconds", "6"), out var bs) ? bs : 6,
            Sim = NetSim.Parse(cfg.Str("net-sim")), LossBot = cfg.Str("loss-bot"), LossOut = cfg.Str("loss-out"),
            LossMeasure = double.TryParse(cfg.Str("loss-measure", "30"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lm) ? lm : 30,
            FxDemo = cfg.Str("fx-demo"), MatchBot = cfg.Str("match-bot"), CarBot = cfg.Str("car-bot"), BazookaBot = cfg.Str("bazooka-bot"), BlastBot = cfg.Str("blast-bot"), WeaponsBot = cfg.Str("bazooka-bot") != "" ? cfg.Str("bazooka-bot") : cfg.Str("blast-bot") != "" ? cfg.Str("blast-bot") : cfg.Str("weapons-bot"), Weapons = _data.Weapons, PerkData = _data.Perks, CamoData = _data.Camo, Vehicles = _data.Vehicles, Killstreaks = _data.Killstreaks, Map = _map,
        });
    }

    /// <summary>Sun, moon, sky and fog for the world (the day/night cycle; the server clock drives it when networked).</summary>
    void AddLightAndSky()
    {
        AddChild(new DayNightCycle { LocalClock = _data.Environment.ToClock(), MinAmbient = _data.Environment.MinAmbient });
        AddChild(new UnderwaterView { Water = _water });
    }

    /// <summary>Client view: light, sky and the first-person player at a random spawn point.</summary>
    void BuildClientView()
    {
        AddLightAndSky();
        var killY = _map.SpawnPoints.Count > 0 ? _map.SpawnPoints.Min(p => p.Position.Y) - 100f : -1000f;
        var player = new Player { Name = "Player", KillY = killY, Water = _water, PickSpawn = RandomSpawn, CaptureMouse = !_smokeMove };
        AddChild(player);
        var spawn = RandomSpawn();
        player.Teleport(spawn + Vector3.Up * 0.1f);
        if (_smokeMove) AddChild(new SmokeMove { Player = player, Spawn = spawn });
    }
}

/// <summary>Minimal "--key value" / "--flag" parser for the user args after "--".</summary>
public class Config
{
    readonly Dictionary<string, string> _o = new();

    public static Config Parse(string[] a)
    {
        var c = new Config();
        for (var i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--")) continue;
            var k = a[i][2..];
            if (i + 1 < a.Length && !a[i + 1].StartsWith("--")) c._o[k] = a[++i];
            else c._o[k] = "true";
        }
        return c;
    }

    public string Str(string k, string d = "") => _o.TryGetValue(k, out var v) ? v : d;
    public bool Flag(string k) => _o.ContainsKey(k);
}
