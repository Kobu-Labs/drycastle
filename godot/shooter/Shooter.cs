using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// Shooter entry point. Args after "--":
///   --role server|client      (default client; the server is for --headless)
///   --map DIR                 map folder (else SUCHOHRAD_MAP, else ../../out/map relative to the project)
///   --connect HOST[:PORT] --name NAME   join a server (default port 27015); --headless-bot [--bot-seconds N] scripts a walk
///   --port N                  server listen port (default 27015); --quit-after SECONDS ends the server with a verdict
///   --match-set k=v,k=v       server: override match.json values (score_goal, time_limit, respawn_delay, spawn_protection, end_screen_time, min_spawn_distance, min_players)
///   --debug-cmds              server: honour the client debug kill command; --match-smoke makes the server judge one full round (implies --debug-cmds)
///   --match-bot alice|bob     client: scripted match smoke role (alice kills bob with the debug command, bob fires after each spawn)
///   --weapons-smoke           server: flat test arena with a wall (far from the map), honours debug commands, judges the weapons scenario
///   --bazooka-smoke           server: same flat arena, judges the bazooka scenario
///   --bazooka-bot a|b         client: scripted bazooka smoke role (a shoots, b is the target)
///   --weapons-bot a|b         client: scripted weapons smoke role (a shoots, b stands still)
///   --hud-shots DIR           render the HUD with fake data at 1280x720 and 3840x2160 into DIR as PNGs, then exit (needs a display)
///   --smoke-move              client only: scripted movement check on the map collision (SHOOTER_SMOKE_MOVE_OK, exit 0; else exit 2)
///   --smoke                   load everything, print SHOOTER_SMOKE_OK and exit 0 (exit 2 on failure)
/// No gameplay yet: this only boots the role on the loaded map.
/// </summary>
public partial class Shooter : Node3D
{
    readonly Suchohrad.MapLoader.MapLoader _map = new();
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
        if (cfg.Str("hud-shots") != "") { _ = HudShots.Run(this, cfg.Str("hud-shots")); return; }
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

    public override void _Process(double delta)
    {
        // let a few frames run so physics and the scene tree settle before a smoke exit
        if (_smoke && ++_frames >= 3)
        {
            GD.Print("SHOOTER_SMOKE_OK");
            GetTree().Quit(0);
        }
    }

    static GameData LoadData()
    {
        static string Read(string f) =>
            Godot.FileAccess.Open($"res://data/{f}", Godot.FileAccess.ModeFlags.Read)?.GetAsText()
            ?? throw new DataFileException($"{f}: cannot open ({Godot.FileAccess.GetOpenError()})");
        return GameData.Parse(Read("weapons.json"), Read("match.json"), Read("vehicles.json"));
    }

    void LoadMap(Config cfg)
    {
        var dir = cfg.Str("map", OS.GetEnvironment("SUCHOHRAD_MAP"));
        if (string.IsNullOrEmpty(dir)) dir = ProjectSettings.GlobalizePath("res://") + "../../out/map";
        if (!Directory.Exists(dir)) throw new InvalidOperationException($"map folder not found: {dir} (pass --map DIR or set SUCHOHRAD_MAP)");
        AddChild(_map.Root);
        _map.Load(Path.GetFullPath(dir));
        var s = _map.Stats;
        GD.Print($"map {_map.MapVersion}: chunks={s.ChunksLoaded} colliders={s.CollidersBuilt} spawns={_map.SpawnPoints.Count} load={s.LoadSeconds:F2}s");
    }

    Vector3 RandomSpawn() =>
        _map.SpawnPoints.Count > 0 ? _map.SpawnPoints[_rng.Next(_map.SpawnPoints.Count)].Position : Vector3.Zero;

    void BuildServer(Config cfg)
    {
        var killY = _map.SpawnPoints.Count > 0 ? _map.SpawnPoints.Min(p => p.Position.Y) - 100f : -1000f;
        var server = new NetServer
        {
            World = this, KillY = killY,
            Spawns = _map.SpawnPoints.Select(sp => new System.Numerics.Vector3(sp.Position.X, sp.Position.Y, sp.Position.Z)).ToList(),
            MatchConfig = ApplyOverrides(_data.Match, cfg.Str("match-set")),
            Weapons = _data.Weapons, WeaponsSmoke = cfg.Flag("weapons-smoke"), BazookaSmoke = cfg.Flag("bazooka-smoke"),
            MatchSmoke = cfg.Flag("match-smoke"), DebugCommands = cfg.Flag("debug-cmds") || cfg.Flag("match-smoke"),
            Port = int.TryParse(cfg.Str("port", "27015"), out var port) ? port : 27015,
            QuitAfter = double.TryParse(cfg.Str("quit-after", "0"), out var q) ? q : 0,
        };
        AddChild(server);
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
            World = this, Host = host, Port = port, PlayerName = cfg.Str("name", "player"), Bot = bot, KillY = killY,
            BotSeconds = double.TryParse(cfg.Str("bot-seconds", "6"), out var bs) ? bs : 6,
            MatchBot = cfg.Str("match-bot"), BazookaBot = cfg.Str("bazooka-bot"), WeaponsBot = cfg.Str("bazooka-bot") != "" ? cfg.Str("bazooka-bot") : cfg.Str("weapons-bot"), Weapons = _data.Weapons,
        });
    }

    void AddLightAndSky()
    {
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55, 30, 0), ShadowEnabled = true });
        var sky = new ProceduralSkyMaterial();
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = sky },
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            },
        });
    }

    /// <summary>Client view: light, sky and the first-person player at a random spawn point.</summary>
    void BuildClientView()
    {
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55, 30, 0), ShadowEnabled = true });
        var sky = new ProceduralSkyMaterial();
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = sky },
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            },
        });
        var killY = _map.SpawnPoints.Count > 0 ? _map.SpawnPoints.Min(p => p.Position.Y) - 100f : -1000f;
        var player = new Player { Name = "Player", KillY = killY, PickSpawn = RandomSpawn, CaptureMouse = !_smokeMove };
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
