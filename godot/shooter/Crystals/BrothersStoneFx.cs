using Godot;

namespace Drycastle.Shooter.Crystals;

/// <summary>
/// Brother's stone (crystals.md): a quiet replica of a memorial boulder. Grey granite with moss on its top, dull grey crystals (<c>shaders/crystal_veined.gdshader</c>), a small bronze plaque on its side.
/// No glow, no effects, no sound. TODO (crystals.md): match the real monument from a photo. Tunables: data/crystals.json "brothers_stone".
/// </summary>
public sealed partial class BrothersStoneFx : CrystalFx
{
    public override string Id => "brothers_stone";

    ShaderMaterial _rock = null!, _crystal = null!;
    StandardMaterial3D _bronze = null!;
    MeshInstance3D _plaque = null!;

    protected override void Build()
    {
        _rock = MossRock();
        _crystal = Veined();
        Body.SetSurfaceOverrideMaterial(0, _rock);
        Body.SetSurfaceOverrideMaterial(1, _crystal);
        _bronze = new StandardMaterial3D { Roughness = 0.45f, Metallic = 0.8f };
        _plaque = new MeshInstance3D { Name = "Plaque", Mesh = new BoxMesh { Size = new Vector3(1f, 0.06f, 0.65f), Material = _bronze } };
        AddChild(_plaque);
    }

    protected override void OnApply()
    {
        ApplyMossRock(_rock, "moss", "#7d7d7a", "#2f4a1c", "#5d7a30", 0.7f);
        ApplyVeined(_crystal, new[] { "#77787a", "#6a6b6e", "#85868a" }, "#55565a", 0.4f, 0f, 0.9f);
        _bronze.AlbedoColor = P.C("plaque_color", "#8a6a3a");

        // the plaque on a side face of the rock, the most upright one the seed finds
        var sides = Info.RockFaces.Where(f => Math.Abs(f.N.Y) < 0.35f).ToList();
        _plaque.Visible = P.B("dress_plaque", true) && sides.Count > 0;
        if (!_plaque.Visible) return;
        var best = Pick(sides, P.I("plaque_seed", 2));
        for (var k = 1; k < 12; k++)
        {
            var s = Pick(sides, P.I("plaque_seed", 2) + k * 13);
            if (Math.Abs(s.N.Y) < Math.Abs(best.N.Y)) best = s;
        }
        var size = P.F("plaque_size", 0.12f);
        _plaque.Transform = new Transform3D(OnSurface(best.N, 0f).Scaled(Vector3.One * size), best.P + Lift + best.N * size * 0.02f);
    }

    protected override void Tick(double dt) { }
}
