using Godot;
using Drycastle.Shooter.Assets;

namespace Drycastle.Shooter.Crystals;

/// <summary>
/// Marihuanite (crystals.md): the weed stone. Deep green, somewhat translucent crystals (<c>shaders/crystal_quartz.gdshader</c>) with a low green glow and a soft green light, and the plantation's own
/// marijuana plants (the gang pack's <c>SM_Prop_Plant_01 / 02</c>, the ones placed on the map's plantation) growing small on the rock between the crystals. Nothing else, nothing moves. Without the pack
/// there are no plants. Tunables: data/crystals.json "marihuanite".
/// </summary>
public sealed partial class MarihuaniteFx : CrystalFx
{
    public override string Id => "marihuanite";

    const int MaxPlants = 16;
    const float PlantHeight = 1.24f; // the pack's plant as imported
    const string TexDir = "synty/glb/gang-warfare/tex/";
    static readonly string[] Models = { "gang-warfare/SM_Prop_Plant_01", "gang-warfare/SM_Prop_Plant_02" };

    StandardMaterial3D _rock = null!;
    ShaderMaterial _crystal = null!;
    OmniLight3D _light = null!;
    readonly Node3D?[] _plants = new Node3D?[MaxPlants];

    protected override void Build()
    {
        _rock = CrystalAssets.Rock(Colors.White, 0.9f);
        _crystal = Quartz();
        Body.SetSurfaceOverrideMaterial(0, _rock);
        Body.SetSurfaceOverrideMaterial(1, _crystal);
        _light = new OmniLight3D { ShadowEnabled = false };
        AddChild(_light);

        // the plant's two surfaces: 0 the leaves (a cut-out texture), 1 the stem (the pack's atlas)
        var leaves = new StandardMaterial3D
        {
            AlbedoTexture = Texture(TexDir + "Leaves_01_Diffuse.png"), Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor, AlphaScissorThreshold = 0.5f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, Roughness = 0.8f,
        };
        var stem = new StandardMaterial3D { AlbedoTexture = Texture(TexDir + "PolygonGangWarfare_01_A.png"), Roughness = 0.9f };
        for (var i = 0; i < MaxPlants; i++)
        {
            if (SyntyLib.Model(Models[i % Models.Length]) is not { } plant) continue;
            plant.Name = $"Plant{i}";
            foreach (var m in plant.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
            {
                m.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                for (var s = 0; s < (m.Mesh?.GetSurfaceCount() ?? 0); s++) m.SetSurfaceOverrideMaterial(s, s == 0 ? leaves : stem);
            }
            AddChild(plant);
            _plants[i] = plant;
        }
    }

    static Texture2D? Texture(string rel)
    {
        var path = Path.Combine(GamePaths.PrivateAssets(), rel);
        if (!File.Exists(path) || Image.LoadFromFile(path) is not { } img) return null;
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    protected override void OnApply()
    {
        _rock.AlbedoColor = P.C("rock_tint", "#4a4a3a");
        ApplyQuartz(_crystal, new[] { "#0f5a22", "#17702c", "#0b4a1c" }, "#063312");
        _crystal.SetShaderParameter("glow", P.F("base_glow", 0.35f));
        _crystal.SetShaderParameter("glow_opacity", P.F("glow_opacity", 0.15f));
        _light.LightColor = P.C("light", "#5fd06a");
        _light.LightEnergy = P.F("light_energy", 0.5f);
        _light.OmniRange = P.F("light_range", 2.2f);
        _light.Position = At(P.V("light_at", new Vector3(0.5f, 0.7f, 0.5f)));

        var n = P.B("dress_plants", true) && Info.RockUp.Count > 0 ? Math.Clamp(P.I("plant_count", 8), 0, MaxPlants) : 0;
        var seed = P.I("plant_seed", 5);
        for (var i = 0; i < MaxPlants; i++)
        {
            if (_plants[i] is not { } plant) continue;
            plant.Visible = i < n;
            if (i >= n) continue;
            var s = RockSpot(seed + i * 17)!.Value;
            var h = P.F("plant_height", 0.22f) * (0.6f + 0.8f * Hash01(i * 1.7 + seed));
            // upright (a plant grows up whatever the slope), sunk a little so the stem starts in the rock
            plant.Transform = new Transform3D(Basis.FromEuler(new Vector3(0, i * 2.3f, 0)).Scaled(Vector3.One * h / PlantHeight), s.P + Vector3.Down * h * 0.03f);
        }
    }

    protected override void Tick(double dt) { }
}
