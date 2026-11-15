using Godot;
using Drycastle.ShooterCore;
using NVec2 = System.Numerics.Vector2;

namespace Drycastle.Shooter;

/// <summary>
/// <c>--intel-shots DIR</c>: renders the intel screen's content (<see cref="IntelScreenView"/>) with made-up events into DIR as PNGs
/// and exits: no events, one event under way, a test event, one with no place, and three on the board with the second in front,
/// each at the texture size of the widest wall screen (3.0 m, 960 x 352) and the narrowest (1.6 m, 512 x 352). The screen is a
/// world-space texture, so its sizes are its own, not the HUD's 1280x720 and 3840x2160. There is no map in this run, so a place
/// shows as "map unavailable"; the map tile with a real map is seen in the game. Needs a display (not --headless).
/// </summary>
public static class IntelShots
{
    static readonly int[] Widths = { 960, 512 };
    const int Height = 352;

    public static async Task Run(Node host, string dir)
    {
        if (DisplayServer.GetName() == "headless") { GD.PrintErr("intel-shots needs a display (not --headless)"); host.GetTree().Quit(2); return; }
        Directory.CreateDirectory(dir);
        const double now = 1000;
        IntelEvent Make(string id, string kind, string name, double startsAt, NVec2? where, bool test = false) => new(id, kind, name,
            "A large creature was reported in the dense forest. It is aggressive towards anyone who comes close and throws whatever it can lift.",
            new[] { "Thrown tree trunks", "Ground shockwaves", "Thrown vehicles" }, startsAt, where, test);
        var states = new (string Name, IntelEvent[] Events, int Featured)[]
        {
            ("empty", Array.Empty<IntelEvent>(), 0),
            ("one", new[] { Make("a", "creature", "Bigfoot sighting", now + 95, new NVec2(120, -340)) }, 0),
            ("underway", new[] { Make("a", "generic", "Unknown intruders", now - 30, null) }, 0),
            ("test", new[] { IntelMock.Test(now, new NVec2(0, 0)) }, 0),
            ("several", new[] { Make("a", "creature", "Bigfoot sighting", now + 95, new NVec2(120, -340)), Make("b", "generic", "Suspicious bus", now + 700, null), Make("c", "generic", "Mass in the church", now + 3900, new NVec2(-30, 60)) }, 1),
        };
        var failed = false;
        foreach (var width in Widths)
        {
            var vp = new SubViewport { Size = new Vector2I(width, Height), RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
            host.AddChild(vp);
            var view = new IntelScreenView(width);
            vp.AddChild(view);
            var version = 0;
            foreach (var (name, events, featured) in states)
            {
                view.Show(events, featured, now, null, ++version);
                await HudShots.Frames(host, 4);
                var path = Path.Combine(dir, $"intel_{name}_{width}x{Height}.png");
                var err = vp.GetTexture().GetImage().SavePng(path);
                GD.Print($"intel-shot {name} {width}x{Height}: {(err == Error.Ok ? path : "FAILED " + err)}");
                failed |= err != Error.Ok;
            }
            vp.QueueFree();
        }
        GD.Print(failed ? "INTEL_SHOTS_FAIL" : "INTEL_SHOTS_OK");
        host.GetTree().Quit(failed ? 2 : 0);
    }
}
