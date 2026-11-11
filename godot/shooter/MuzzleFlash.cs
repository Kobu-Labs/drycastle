using Godot;

namespace Suchohrad.Shooter;

/// <summary>
/// Muzzle flash that lives under a weapon model's Muzzle marker (hidden by default). <see cref="Flash"/> shows it for
/// ~50 ms with a random roll and slight scale variation; no allocations per shot. Mesh and material are shared.
/// </summary>
public sealed partial class MuzzleFlash : Node3D
{
    const ulong ShowMs = 50;
    static SphereMesh? _mesh;
    static StandardMaterial3D? _mat;
    ulong _until;

    public MuzzleFlash()
    {
        Name = "Flash";
        Visible = false;
        SetProcess(false);
        _mat ??= new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(1f, 0.75f, 0.3f),
            EmissionEnabled = true, Emission = new Color(1f, 0.75f, 0.3f), EmissionEnergyMultiplier = 2f,
        };
        _mesh ??= new SphereMesh { Radius = 0.12f, Height = 0.24f, RadialSegments = 12, Rings = 6, Material = _mat };
        // flattened and stretched along the barrel so the random roll is visible
        AddChild(new MeshInstance3D { Mesh = _mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Scale = new Vector3(1.4f, 0.7f, 1.2f) });
        AddChild(new OmniLight3D { LightColor = new Color(1f, 0.7f, 0.3f), LightEnergy = 3f, OmniRange = 7f, ShadowEnabled = false });
    }

    /// <summary>The flash under <paramref name="weapon"/>'s Muzzle marker, or null.</summary>
    public static MuzzleFlash? Of(Node? weapon) =>
        weapon != null && GodotObject.IsInstanceValid(weapon) ? weapon.GetNodeOrNull<Node3D>("Muzzle")?.GetNodeOrNull<MuzzleFlash>("Flash") : null;

    public void Flash(float scale = 1f)
    {
        Rotation = new Vector3(0, 0, GD.Randf() * Mathf.Tau);
        Scale = Vector3.One * scale * (0.85f + GD.Randf() * 0.3f);
        _until = Time.GetTicksMsec() + ShowMs;
        Visible = true;
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        if (Time.GetTicksMsec() < _until) return;
        Visible = false;
        SetProcess(false);
    }
}
