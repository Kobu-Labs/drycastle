using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// Shooter entry point. Args after "--":
///   --role server|client      (default client; the server is for --headless)
///   --map DIR                 map folder (else SUCHOHRAD_MAP, else ../../out/map relative to the project)
///   --smoke                   load everything, print SHOOTER_SMOKE_OK and exit 0 (exit 2 on failure)
/// No gameplay yet: this only boots the role on the loaded map.
/// </summary>
public partial class Shooter : Node3D
{
    readonly Suchohrad.MapLoader.MapLoader _map = new();
    bool _smoke;
    int _frames;

    public override void _Ready()
    {
        var cfg = Config.Parse(OS.GetCmdlineUserArgs());
        var role = cfg.Str("role", "client");
        _smoke = cfg.Flag("smoke");
        try
        {
            if (role is not ("server" or "client")) throw new InvalidOperationException($"unknown role {role} (server|client)");
            var data = LoadData();
            GD.Print($"data: {data.Weapons.Weapons.Count} weapons, score goal {data.Match.ScoreGoal}, {data.Vehicles.Vehicles.Count} vehicle types");
            LoadMap(cfg);
            if (role == "client") BuildClientView();
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

    /// <summary>Placeholder view so the client shows the map: light, sky and a camera over the first spawn.</summary>
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
        var at = _map.SpawnPoints.Count > 0 ? _map.SpawnPoints[0].Position : Vector3.Zero;
        var cam = new Camera3D { Current = true, Far = 2000 };
        AddChild(cam);
        cam.Position = at + new Vector3(0, 12, 12);
        cam.LookAt(at + Vector3.Up * 1.5f);
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
