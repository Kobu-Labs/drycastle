using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// A Companion as a small live 3D view (#488): the model of its look alone in its own SubViewport, on a transparent ground, seen from the front and a little to the side, playing
/// its idle clip (it stands still with reduce motion). A cell of the Equipment screen's Companion picker draws <see cref="Picture"/>. It renders only while
/// <see cref="Live"/> (the screen is open): closed, it costs nothing. The view is fitted to the model's own meshes as they stand (the corners of their boxes, seen from the camera), so a small dog, a big one and a bird with its wings
/// out all fill the cell.
/// </summary>
public sealed partial class CompanionPortrait : SubViewport
{
    /// <summary>How far round from straight ahead the camera stands, and how far above level, degrees.</summary>
    const float SideDeg = 35f, AboveDeg = 12f;
    const float FovDeg = 30f;
    /// <summary>Room round the model, times its size on the picture.</summary>
    const float Margin = 1.08f;

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

        view._camera = new Camera3D { Fov = FovDeg, Current = true };
        view.AddChild(view._camera);
        view._model = root;
        // the body box is [width, height, length]: a walker's stands on its origin, a flyer's is round it. Only for a model with no mesh to measure
        view._box = new Aabb(new Vector3(-box[0] / 2f, def.Flies ? -box[1] / 2f : 0, -box[2] / 2f), new Vector3(box[0], box[1], box[2]));
        view._anim = anim;
        view._idle = clips.Length > (int)AnimalClip.Idle ? clips[(int)AnimalClip.Idle] : null;
        return view;
    }

    AnimationPlayer? _anim;
    string? _idle;
    Camera3D _camera = null!;
    Node3D _model = null!;
    Aabb _box;

    /// <summary>In the tree: the camera is fitted to the model and the idle clip starts (both need the tree: global transforms, and the player to pose the model).</summary>
    public override void _Ready()
    {
        Fit();
        if (_anim == null || _idle == null) return;
        _anim.Play(_idle);
        if (GameSettings.Current.ReduceMotion) { _anim.Seek(0, true); _anim.Pause(); } // the standing pose, still
    }

    /// <summary>
    /// Puts the camera where the whole model just fills the picture: ahead of it, to its side and a little above, looking at the middle of its meshes' boxes, as near as
    /// the farthest-out corner of them allows.
    /// </summary>
    void Fit()
    {
        Aabb? found = null;
        foreach (var mesh in _model.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
        {
            if (mesh.Mesh == null || !mesh.Visible) continue;
            var box = mesh.GlobalTransform * mesh.GetAabb();
            found = found is { } all ? all.Merge(box) : box;
        }
        var bounds = found is { } b && b.Size.Length() > 0.01f ? b : _box;
        var centre = bounds.GetCenter();
        var (side, above) = (Mathf.DegToRad(SideDeg), Mathf.DegToRad(AboveDeg));
        // the model faces -Z: the camera stands ahead of it, to its right, a little above, so it looks to the picture's left (away from the tick in the cell's top right corner)
        var dir = new Vector3(-MathF.Sin(side) * MathF.Cos(above), MathF.Sin(above), -MathF.Cos(side) * MathF.Cos(above));
        var basis = Basis.LookingAt(-dir, Vector3.Up);
        var tan = MathF.Tan(Mathf.DegToRad(FovDeg) / 2f);
        var distance = 0.01f;
        var depth = 0f;
        for (var i = 0; i < 8; i++)
        {
            var p = (bounds.GetEndpoint(i) - centre) * basis; // the corner as the camera sees it: x right, y up, z towards the camera
            distance = MathF.Max(distance, MathF.Max(MathF.Abs(p.X), MathF.Abs(p.Y)) * Margin / tan + p.Z);
            depth = MathF.Max(depth, MathF.Abs(p.Z));
        }
        _centre = centre; _dir = dir; _distance = distance; _depth = depth;
        _camera.Basis = basis;
        Place();
    }

    Vector3 _centre, _dir;
    float _distance, _depth;
    int _frames; // rendered frames since it went live, until the view is tightened
    bool _tight;

    void Place()
    {
        _camera.Position = _centre + _dir * _distance;
        _camera.Near = MathF.Max(0.01f, (_distance - _depth) * 0.5f);
        _camera.Far = _distance + _depth * 2f + 1f;
    }

    /// <summary>
    /// Once, two frames after it first renders: the boxes of a skinned model are those of its rest pose and can be far larger than the animal as it stands (a bird's
    /// wings), so the view is tightened to what the picture really shows (the bounds of its opaque pixels), centred, with the margin round it.
    /// </summary>
    public override void _Process(double delta)
    {
        if (_tight || RenderTargetUpdateMode != UpdateMode.Always) return;
        if (++_frames < 3) return;
        _tight = true;
        SetProcess(false);
        var img = GetTexture().GetImage();
        if (img == null || img.IsEmpty()) return;
        var used = img.GetUsedRect();
        if (used.Size.X <= 0 || used.Size.Y <= 0) return;
        var n = (float)img.GetWidth();
        var half = _distance * MathF.Tan(Mathf.DegToRad(FovDeg) / 2f); // half the picture's side, metres, at the model
        var cx = ((used.Position.X + used.Size.X / 2f) / n - 0.5f) * 2f;
        var cy = ((used.Position.Y + used.Size.Y / 2f) / n - 0.5f) * 2f;
        _centre += _camera.Basis.X * (cx * half) - _camera.Basis.Y * (cy * half);
        _distance *= MathF.Max(used.Size.X, used.Size.Y) / n * Margin;
        Place();
    }
}
