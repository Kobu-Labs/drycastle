using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// <c>--hud-shots DIR</c>: renders the HUD over a tiny placeholder scene with fake data in an off-screen SubViewport at
/// 1280x720 and 3840x2160 and saves one PNG per state (play, protected, scoreboard, respawn, roundend). Needs a display
/// server with a renderer (not --headless). The Hud clock is frozen so transient elements stay visible.
/// </summary>
public static class HudShots
{
    static readonly Vector2I[] Sizes = { new(1280, 720), new(3840, 2160) };

    public static async Task Run(Node host, string dir)
    {
        if (DisplayServer.GetName() == "headless") { GD.PrintErr("hud-shots needs a display (not --headless)"); host.GetTree().Quit(2); return; }
        Directory.CreateDirectory(dir);
        Hud.FrozenNow = 1000;
        var failed = false;
        foreach (var size in Sizes)
        {
            var vp = new SubViewport { Size = size, OwnWorld3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
            host.AddChild(vp);
            Backdrop(vp);
            var hud = new Hud { MyId = 1, MouseModeOverride = Input.MouseModeEnum.Captured, Spread = () => 2.5f };
            vp.AddChild(hud);
            await Frames(host, 3);
            foreach (var state in new[] { "play", "protected", "scoreboard", "respawn", "roundend" })
            {
                Fake(hud, state);
                await Frames(host, 4);
                var path = Path.Combine(dir, $"hud_{state}_{size.X}x{size.Y}.png");
                var err = vp.GetTexture().GetImage().SavePng(path);
                GD.Print($"hud-shot {state} {size.X}x{size.Y}: {(err == Error.Ok ? path : "FAILED " + err)}");
                failed |= err != Error.Ok;
            }
            vp.QueueFree();
        }
        GD.Print(failed ? "HUD_SHOTS_FAIL" : "HUD_SHOTS_OK");
        host.GetTree().Quit(failed ? 2 : 0);
    }

    static async Task Frames(Node host, int n)
    {
        for (var i = 0; i < n; i++) await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
    }

    static void Backdrop(SubViewport vp)
    {
        vp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55, 30, 0) });
        vp.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky, Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial() },
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            },
        });
        vp.AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(200, 200) } });
        for (var i = 0; i < 6; i++)
            vp.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(4, 3 + i, 4) }, Position = new Vector3(-20 + i * 8, (3 + i) / 2f, -25 - i * 3) });
        vp.AddChild(new Camera3D { Position = new Vector3(0, 1.7f, 0), Fov = 90, Current = true });
    }

    static readonly string[] Names = { "Kuba", "Alexandra", "Bob", "Cyril", "Dagmar" };

    static void Fake(Hud hud, string state)
    {
        var rows = new List<ScoreRow>();
        for (var i = 0; i < Names.Length; i++) rows.Add(new ScoreRow((ushort)(i + 1), Names[i], new[] { 5, 9, 3, 7, 1 }[i], new[] { 2, 4, 6, 3, 8 }[i], (ushort)(12 + i * 9)));
        var weapons = new[] { "Pistol", "AK-47", "Bazooka", "Knife" };
        hud.SetWeapons(weapons);
        hud.SetHealth(state == "play" || state == "protected" ? 64 : 100);
        var all = new List<(int, int, bool)> { (15, 60, false), (23, 90, false), (1, 4, false), (0, 0, true) };
        hud.SetAmmo(2, 23, 90, all, state == "play" ? 1.2f : 0f, 2.5f);
        hud.SetMatch(state == "roundend" ? MatchPhase.Ending : MatchPhase.Running, 214, rows, state == "roundend" ? new ushort[] { 2 } : Array.Empty<ushort>(), id => Names[id - 1]);
        hud.ClearKills(); hud.ClearTransients();
        hud.AddKill(new KillMsg(3, 4, 0), "Bob", "Cyril");
        hud.AddKill(new KillMsg(2, 1, 2), "Alexandra", "Kuba");
        hud.AddKill(new KillMsg(1, 2, 1), "Kuba", "Alexandra");
        hud.AddKill(new KillMsg(5, 5, 255), "Dagmar", "Dagmar");
        hud.ForceScoreboard = state is "scoreboard" ? true : null;
        hud.SetDead(state == "respawn", "Alexandra", 2.4f);
        hud.SetProtection(state == "protected" ? 2.4f : 0);
        if (state is "play" or "protected")
        {
            hud.Damage(0.6f); hud.Damage(-2.6f);
            hud.Hit(true, false);
        }
    }
}
