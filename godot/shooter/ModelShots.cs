using Godot;

namespace Suchohrad.Shooter;

/// <summary>
/// <c>--model-shots DIR</c>: renders the remote character in each animation (with each weapon in hand) and the first-person
/// viewmodel per weapon over a plain floor, saves PNGs and prints the clip names. Needs a display (not --headless).
/// </summary>
public static class ModelShots
{
    public static async Task Run(Node host, string dir)
    {
        if (!ShotFx.Enabled) { GD.PrintErr("model-shots needs a display (not --headless)"); host.GetTree().Quit(2); return; }
        Directory.CreateDirectory(dir);
        var vp = new SubViewport { Size = new Vector2I(1280, 720), OwnWorld3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        host.AddChild(vp);
        HudShots.Backdrop(vp);
        var cam = new Camera3D { Fov = 60, Current = true };
        vp.AddChild(cam);
        var ok = true;
        // remote characters: five side by side, facing the camera (the game's forward is -Z, so rotate 180 about Y)
        var states = new (string W, bool Fire, bool Dead, float Speed)[] { ("pistol", false, false, 0), ("ak47", false, false, 3), ("bazooka", false, false, 7), ("knife", true, false, 0), ("ak47", true, false, 0), ("pistol", false, true, 0) };
        var views = new List<CharacterView>();
        for (var i = 0; i < states.Length; i++)
        {
            var v = CharacterView.Make();
            v.Position = new Vector3((i - (states.Length - 1) / 2f) * 1.6f, 0, 0);
            v.RotationDegrees = new Vector3(0, 180, 0);
            vp.AddChild(v);
            v.Apply(states[i].Speed, states[i].Fire, states[i].Dead, states[i].W);
            views.Add(v);
            GD.Print($"model-shots: character {i} model={v.HasModel} anim={v.CurrentAnim} weapon={states[i].W}");
            ok &= v.HasModel;
        }
        cam.Position = new Vector3(0, 1.0f, 8f);
        await HudShots.Frames(host, 20);
        for (var f = 0; f < 3; f++)
        {
            await HudShots.Frames(host, 12);
            ok &= Save(vp, Path.Combine(dir, $"characters_{f}.png"));
        }
        foreach (var v in views) v.QueueFree();
        // viewmodel per weapon, from a camera at the origin looking down -Z, with a ground and a post as reference
        vp.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.3f, 3, 0.3f) }, Position = new Vector3(0, 1, -6) });
        cam.Position = new Vector3(0, 1.6f, 0); cam.Fov = 90;
        var vm = new WeaponViewmodel();
        cam.AddChild(vm);
        foreach (var w in new[] { "pistol", "ak47", "bazooka", "knife" })
        {
            vm.Show(w);
            await HudShots.Frames(host, 6);
            ok &= Save(vp, Path.Combine(dir, $"viewmodel_{w}.png"));
        }
        GD.Print(ok ? "MODEL_SHOTS_OK" : "MODEL_SHOTS_FAIL");
        host.GetTree().Quit(ok ? 0 : 2);
    }

    static bool Save(SubViewport vp, string path)
    {
        var err = vp.GetTexture().GetImage().SavePng(path);
        GD.Print($"model-shot: {(err == Error.Ok ? path : "FAILED " + err)}");
        return err == Error.Ok;
    }
}
