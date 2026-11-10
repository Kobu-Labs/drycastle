using Godot;

/// <summary>
/// Netcode spike entry point. Args after "--":
///   --role server|client|probe-server|probe-client
///   --mode builtin|custom        replication approach (A | B)
///   --n N                        server: clients to wait for
///   --seconds S --warmup W       measurement window / warm-up (server)
///   --interest R                 interest radius in metres (0 = off)
///   --sync-mode always|onchange  approach A synchronizer mode (default always; onchange is broken on 4.7.2, see report)
///   --port P --host H --out DIR --map DIR --index I --seed S
/// </summary>
public partial class Spike : Node
{
    IRole? _role;

    public override void _Ready()
    {
        var cfg = Config.Parse(OS.GetCmdlineUserArgs());
        var role = cfg.Str("role", "server");
        var mode = cfg.Str("mode", "builtin");
        Engine.PhysicsTicksPerSecond = Net.Hz;
        Engine.MaxFps = role == "server" ? Net.Hz : 60;
        try
        {
            switch (role)
            {
                case "server":
                {
                    var core = new ServerCore(this, cfg);
                    core.Load();
                    _role = mode == "custom" ? new CustomServer(this, core) : new BuiltinServer(this, core);
                    GD.Print($"NETSPIKE_READY {mode} port={cfg.Int("port", 27015)}");
                    break;
                }
                case "client":
                {
                    var core = new ClientCore(this, cfg);
                    _role = mode == "custom" ? new CustomClient(this, core) : new BuiltinClient(this, core);
                    break;
                }
                case "probe-server": _role = new ProbeRole(this, cfg, true); break;
                case "probe-client": _role = new ProbeRole(this, cfg, false); break;
                default: throw new InvalidOperationException($"unknown role {role}");
            }
        }
        catch (Exception e)
        {
            GD.PrintErr($"startup failed: {e}");
            GetTree().Quit(2);
        }
    }

    public override void _PhysicsProcess(double delta) => _role?.Physics(delta);
    public override void _Process(double delta) => _role?.Frame(delta);
}
