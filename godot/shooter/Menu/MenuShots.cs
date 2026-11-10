using Godot;

namespace Suchohrad.Shooter;

/// <summary>
/// <c>--menu-shots DIR</c>: renders the menu screens in an off-screen SubViewport at 1280x720 and 3840x2160 and saves one PNG
/// each (main, quit confirm, connect empty and error, the five settings tabs, rebinding capture, pause, loading). Needs a display
/// server with a renderer (not --headless). Timed dialogs and fades are off; nothing is written to the settings file.
/// </summary>
public static class MenuShots
{
    static readonly Vector2I[] Sizes = { new(1280, 720), new(3840, 2160) };

    public static async Task Run(Node host, string dir)
    {
        if (DisplayServer.GetName() == "headless") { GD.PrintErr("menu-shots needs a display (not --headless)"); host.GetTree().Quit(2); return; }
        Directory.CreateDirectory(dir);
        SettingsScreen.Instant = true;
        var failed = false;
        foreach (var size in Sizes)
        {
            var vp = new SubViewport { Size = size, OwnWorld3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
            host.AddChild(vp);
            HudShots.Backdrop(vp);
            var shell = new UiShell { Instant = true };
            vp.AddChild(shell);
            await HudShots.Frames(host, 3);

            async Task Shot(string name, Func<MenuScreen?> make, bool over = false)
            {
                var s = make();
                if (s != null) { if (over) shell.Push(s); else shell.Reset(s); }
                await HudShots.Frames(host, 5);
                var path = Path.Combine(dir, $"menu_{name}_{size.X}x{size.Y}.png");
                var err = vp.GetTexture().GetImage().SavePng(path);
                GD.Print($"menu-shot {name} {size.X}x{size.Y}: {(err == Error.Ok ? path : "FAILED " + err)}");
                failed |= err != Error.Ok;
            }

            await Shot("main", () => new MainMenu(() => { }, () => { }));
            await Shot("quit_confirm", () => new DialogScreen(Strings.T("quit.title"), Strings.T("quit.menu.body"),
                new[] { new DialogScreen.Choice(Strings.T("quit.confirm"), () => { }, "MenuDanger") }), true);
            ConnectScreen? cs = null;
            await Shot("connect_empty", () => cs = new ConnectScreen((_, _, _) => { }));
            await Shot("connect_error", () => { cs!.ShowError(Strings.F("connect.err.timeout", "play.example.cz:27015", 6), "connect.err.help.timeout"); return null; });
            await Shot("loading", () => { var l = new LoadingScreen("play.example.cz:27015"); return l; });
            ((LoadingScreen)shell.Top!).Set("loading.map", 0.4f);
            await HudShots.Frames(host, 3);
            vp.GetTexture().GetImage().SavePng(Path.Combine(dir, $"menu_loading_{size.X}x{size.Y}.png"));
            string[] tabs = { "graphics", "audio", "controls", "gameplay", "access" };
            for (var i = 0; i < tabs.Length; i++)
            {
                var t = i;
                await Shot("settings_" + tabs[i], () => new SettingsScreen(false, t));
            }
            await Shot("pause", () => new PauseScreen(() => { }, () => { }));
            await Shot("pause_settings_over_game", () => new SettingsScreen(true, 2), true);
            vp.QueueFree();
        }
        GD.Print(failed ? "MENU_SHOTS_FAIL" : "MENU_SHOTS_OK");
        host.GetTree().Quit(failed ? 2 : 0);
    }
}
