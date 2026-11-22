using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// A Companion as a small live 3D view (#488): the model of its look alone in its own SubViewport, on a transparent ground, seen from the front and a little to the side, playing
/// its idle clip (it stands still with reduce motion). A cell of the Equipment screen's Companion picker draws <see cref="Picture"/>. It renders only while
/// <see cref="Live"/> (the screen is open): closed, it costs nothing. The view is fitted to the look's body box of animals.json, so a small dog and a big one both fill the cell.
/// </summary>
public sealed partial class CompanionPortrait : SubViewport
{
    /// <summary>How far round from straight ahead the camera stands, and how far above level, degrees.</summary>
    const float SideDeg = 35f, AboveDeg = 12f;
    const float FovDeg = 30f;
    /// <summary>Room round the body box: a walker's box is its body, a flyer's wings reach well beyond it.</summary>
    const float WalkerMargin = 1.15f, FlyerMargin = 1.7f;

    CompanionPortrait() { }

    /// <summary>What a cell draws: the view's texture (transparent where the model is not).</summary>
    public Texture2D Picture => GetTexture();

    /// <summary>Renders every frame while true; not at all while false (the last picture stays).</summary>
    public bool Live
    {
        set => RenderTargetUpdateMode = value ? UpdateMode.Always : UpdateMode.Disabled;
    }

    /// <summary>The view of one look of a species, or null when its model cannot be loaded (headless, or the asset folder is absent): the cell then shows its ghost.</summary>
    public static CompanionPortrait? Of(AnimalDef def, int variant)
    {
        var box = def.HitBoxOf(variant);
        if (box.Count < 3) return null;
        var root = new Node3D();
        var (model, anim, clips) = NetClient.AddAnimalModel(root, def, variant, 1f, box);
        if (model == null) { root.Free(); return null; }

        var view = new CompanionPortrait
        {
            Size = new Vector2I(ShooterTheme.PortraitPx, ShooterTheme.PortraitPx), OwnWorld3D = true, TransparentBg = true, Msaa3D = Msaa.Msaa4X,
            RenderTargetUpdateMode = UpdateMode.Disabled,
        };
        var env = new Godot.Environment
        {
            AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.55f, 0.58f, 0.65f), AmbientLightEnergy = 0.9f,
        };
        view.AddChild(new WorldEnvironment { Environment = env });
        view.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-40, 30, 0), LightEnergy = 1.1f });
        view.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-15, -120, 0), LightEnergy = 0.35f });
        view.AddChild(root);

        // the body box is [width, height, length]: a walker's stands on its origin, a flyer's is round it
        var (w, h, l) = (box[0], box[1], box[2]);
        var centre = new Vector3(0, def.Flies ? 0 : h / 2f, 0);
        var radius = 0.5f * MathF.Sqrt(w * w + h * h + l * l) * (def.Flies ? FlyerMargin : WalkerMargin);
        var distance = radius / MathF.Sin(Mathf.DegToRad(FovDeg) / 2f);
        var (side, above) = (Mathf.DegToRad(SideDeg), Mathf.DegToRad(AboveDeg));
        // the model faces -Z: the camera stands ahead of it, to its left, a little above
        var dir = new Vector3(MathF.Sin(side) * MathF.Cos(above), MathF.Sin(above), -MathF.Cos(side) * MathF.Cos(above));
        var camera = new Camera3D { Fov = FovDeg, Current = true, Near = Mathf.Max(0.01f, distance - radius * 2f), Far = distance + radius * 4f };
        view.AddChild(camera);
        camera.Position = centre + dir * distance;
        camera.Basis = Basis.LookingAt(-dir, Vector3.Up);

        view._anim = anim;
        view._idle = clips.Length > (int)AnimalClip.Idle ? clips[(int)AnimalClip.Idle] : null;
        return view;
    }

    AnimationPlayer? _anim;
    string? _idle;

    /// <summary>In the tree: the idle clip starts (the player needs the tree to pose the model).</summary>
    public override void _Ready()
    {
        if (_anim == null || _idle == null) return;
        _anim.Play(_idle);
        if (GameSettings.Current.ReduceMotion) { _anim.Seek(0, true); _anim.Pause(); } // the standing pose, still
    }
}
