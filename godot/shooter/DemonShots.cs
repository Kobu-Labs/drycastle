using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// <c>--demon-shots DIR</c>: renders the demon's head (rest pose) with the mouth shut, half open and wide, from the front, three quarters and the side,
/// into DIR as PNGs, then exits. For tuning <see cref="DemonMouth"/> by eye without a fight. Needs a display and the private pack.
/// </summary>
public static class DemonShots
{
    public static async Task Run(Node host, string dir)
    {
        if (DisplayServer.GetName() == "headless") { GD.PrintErr("demon-shots needs a display (not --headless)"); host.GetTree().Quit(2); return; }
        Directory.CreateDirectory(dir);
        var vp = new SubViewport { Size = new Vector2I(900, 900), OwnWorld3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        host.AddChild(vp);
        HudShots.Backdrop(vp);
        var demon = SyntyLib.Instance("demon_01", "glb/atlas/horror_mansion_01_A.png");
        var skel = demon?.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>().FirstOrDefault();
        if (demon == null || skel == null) { GD.PrintErr("demon-shots: the demon model is not there (private pack)"); host.GetTree().Quit(2); return; }
        vp.AddChild(demon);
        var mouth = DemonMouth.Install(demon, skel);
        var cam = new Camera3D { Fov = 30, Current = true };
        vp.AddChild(cam);
        var look = new Vector3(0, 1.56f, 0.05f);
        var failed = mouth == null;
        foreach (var (open, on) in new[] { (0f, "shut"), (0.5f, "half"), (1f, "wide") })
        {
            if (mouth != null) mouth.Target = open;
            await HudShots.Frames(host, 45);
            foreach (var (yaw, view) in new[] { (0f, "front"), (40f, "quarter"), (90f, "side"), (20f, "below") })
            {
                var d = new Vector3(MathF.Sin(Mathf.DegToRad(yaw)), view == "below" ? -0.45f : 0.05f, MathF.Cos(Mathf.DegToRad(yaw))).Normalized();
                cam.LookAtFromPosition(look + d * 1.5f, look, Vector3.Up);
                await HudShots.Frames(host, 3);
                var path = Path.Combine(dir, $"demon_{on}_{view}.png");
                var err = vp.GetTexture().GetImage().SavePng(path);
                failed |= err != Error.Ok;
                GD.Print($"demon-shot {on} {view}: {(err == Error.Ok ? path : "FAILED " + err)}");
            }
        }
        GD.Print(failed ? "DEMON_SHOTS_FAIL" : "DEMON_SHOTS_OK");
        host.GetTree().Quit(failed ? 2 : 0);
    }
}
