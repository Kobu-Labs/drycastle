using Godot;
using Drycastle.Shooter.Demon;

namespace Drycastle.Shooter.Crystals;

/// <summary>
/// Dubovite (crystals.md): the stone of the old oaks. Bark-brown crystals with a wood grain (<c>shaders/crystal_veined.gdshader</c>, <c>grain</c>) on a mossy rock, a dappled light that wanders between
/// green and gold. Oak leaves and acorns lie on the rock, and now and then a leaf drifts down from a tip. Tunables: data/crystals.json "dubovite".
/// </summary>
public sealed partial class DuboviteFx : CrystalFx
{
    public override string Id => "dubovite";

    const int MaxLeaves = 24, MaxAcorns = 10;

    ShaderMaterial _rock = null!, _crystal = null!;
    OmniLight3D _light = null!;
    readonly MeshInstance3D[] _leaves = new MeshInstance3D[MaxLeaves];
    readonly Node3D[] _acorns = new Node3D[MaxAcorns];
    FallSet _fall = null!;
    List<Vector3> _from = new();

    protected override void Build()
    {
        _rock = MossRock();
        _crystal = Veined();
        Body.SetSurfaceOverrideMaterial(0, _rock);
        Body.SetSurfaceOverrideMaterial(1, _crystal);
        _light = new OmniLight3D { ShadowEnabled = false };
        AddChild(_light);

        var plant = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.85f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        var meshes = new Mesh[]
        {
            RosalineFx.LeafMesh(plant, new Color(0.35f, 0.18f, 0.05f), new Color(0.75f, 0.45f, 0.12f)),
            RosalineFx.LeafMesh(plant, new Color(0.2f, 0.3f, 0.06f), new Color(0.6f, 0.62f, 0.15f)),
            RosalineFx.LeafMesh(plant, new Color(0.3f, 0.12f, 0.04f), new Color(0.6f, 0.28f, 0.08f)),
        };
        for (var i = 0; i < MaxLeaves; i++)
        {
            _leaves[i] = new MeshInstance3D { Name = $"Leaf{i}", Mesh = meshes[i % meshes.Length], CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(_leaves[i]);
        }
        var nut = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.36f, 0.14f), Roughness = 0.4f };
        var cap = new StandardMaterial3D { AlbedoColor = new Color(0.28f, 0.19f, 0.09f), Roughness = 0.95f };
        for (var i = 0; i < MaxAcorns; i++)
        {
            // an acorn of unit length lying along Z
            var a = new Node3D { Name = $"Acorn{i}" };
            AddChild(a);
            a.AddChild(new MeshInstance3D { Mesh = new CapsuleMesh { Radius = 0.3f, Height = 0.95f, RadialSegments = 8, Rings = 3, Material = nut }, RotationDegrees = new Vector3(90, 0, 0), Position = new Vector3(0, 0.3f, 0) });
            a.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.34f, Height = 0.5f, RadialSegments = 8, Rings = 4, Material = cap }, Position = new Vector3(0, 0.3f, -0.32f) });
            _acorns[i] = a;
        }
        _fall = new FallSet(this, meshes, 8);
    }

    protected override void OnApply()
    {
        ApplyMossRock(_rock, "moss", "#4a3d2e", "#2a5514", "#6b9a2a");
        ApplyVeined(_crystal, new[] { "#5a3d24", "#6b4a2c", "#4a3220" }, "#2e1d10", 0.6f, 0f, 0.85f, 1f);
        _light.OmniRange = P.F("light_range", 2.2f);
        _light.Position = At(P.V("light_at", new Vector3(0.5f, 0.8f, 0.5f)));

        var seed = P.I("seed", 6);
        var ln = P.B("dress_leaves", true) && Info.RockUp.Count > 0 ? Math.Clamp(P.I("leaf_count", 12), 0, MaxLeaves) : 0;
        for (var i = 0; i < MaxLeaves; i++)
        {
            _leaves[i].Visible = i < ln;
            if (i >= ln) continue;
            var s = RockSpot(seed + i * 19)!.Value;
            _leaves[i].Transform = new Transform3D(OnSurface(s.N, i * 2.3f).Scaled(Vector3.One * P.F("leaf_size", 0.055f) * (0.7f + 0.6f * Hash01(i * 1.9))), s.P + s.N * 0.004f);
        }
        var an = P.B("dress_acorns", true) && Info.RockUp.Count > 0 ? Math.Clamp(P.I("acorn_count", 5), 0, MaxAcorns) : 0;
        for (var i = 0; i < MaxAcorns; i++)
        {
            _acorns[i].Visible = i < an;
            if (i >= an) continue;
            var s = RockSpot(seed + 300 + i * 29)!.Value;
            _acorns[i].Transform = new Transform3D(OnSurface(s.N, i * 1.7f).Scaled(Vector3.One * P.F("acorn_size", 0.022f)), s.P);
        }
        _from = DripPoints(false);
        SetLoop(P.S("rustle_sound", ""), P.F("rustle_db", -24f));
    }

    protected override void Tick(double dt)
    {
        var k = 0.5f + 0.5f * Mathf.Sin((float)Time * P.F("dapple_speed", 0.5f));
        _light.LightColor = P.C("light_green", "#9fd45a").Lerp(P.C("light_gold", "#ffd76a"), k);
        _light.LightEnergy = P.F("light_energy", 0.6f) * DemonFx.Flicker(Time, 9.3f, 0.7f, 1.2f);
        _fall.Update(Time, _from, Math.Clamp(P.I("fall_count", 4), 0, 8), P.F("fall_interval", 8f), P.F("fall_speed", 0.18f), P.F("fall_size", 0.05f), Box.GetCenter(), Box.Size.Y);
    }
}
