using Godot;

namespace Drycastle.Shooter.Crystals;

/// <summary>
/// Nazium (crystals.md): the bunker stone. Olive-drab and concrete-grey crystals (<c>shaders/crystal_veined.gdshader</c>) on a concrete base; the tallest crystal wears a red band with a plain white disc
/// round it (no emblem, no real-world symbols). A searchlight on its tip sweeps a pale beam from side to side. Rusty steel plates lie on the rock. Tunables: data/crystals.json "nazium".
/// </summary>
public sealed partial class NaziumFx : CrystalFx
{
    public override string Id => "nazium";

    const int MaxPlates = 5;

    StandardMaterial3D _rock = null!, _rust = null!, _beamMat = null!;
    ShaderMaterial _crystal = null!;
    Node3D _pivot = null!;
    SpotLight3D _spot = null!;
    MeshInstance3D _beam = null!, _lamp = null!;
    readonly MeshInstance3D[] _plates = new MeshInstance3D[MaxPlates];

    protected override void Build()
    {
        _rock = CrystalAssets.Rock(Colors.White, 0.95f);
        _crystal = Veined();
        Body.SetSurfaceOverrideMaterial(0, _rock);
        Body.SetSurfaceOverrideMaterial(1, _crystal);
        _rust = new StandardMaterial3D { Roughness = 0.75f, Metallic = 0.5f };
        for (var i = 0; i < MaxPlates; i++)
        {
            _plates[i] = new MeshInstance3D { Name = $"Plate{i}", Mesh = new BoxMesh { Size = new Vector3(1f, 0.08f, 0.7f), Material = _rust } };
            AddChild(_plates[i]);
        }
        _pivot = new Node3D { Name = "Searchlight" };
        AddChild(_pivot);
        _lamp = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.018f, BottomRadius = 0.014f, Height = 0.03f, RadialSegments = 8, Material = new StandardMaterial3D { AlbedoColor = new Color(0.15f, 0.16f, 0.14f), Roughness = 0.6f, Metallic = 0.6f } }, RotationDegrees = new Vector3(90, 0, 0) };
        _pivot.AddChild(_lamp);
        _spot = new SpotLight3D { ShadowEnabled = false };
        _pivot.AddChild(_spot);
        _beamMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _beam = new MeshInstance3D { Name = "Beam", Mesh = new CylinderMesh { TopRadius = 0.02f, BottomRadius = 1f, Height = 1f, RadialSegments = 14, Rings = 1, CapTop = false, CapBottom = false, Material = _beamMat }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _pivot.AddChild(_beam);
    }

    protected override void OnApply()
    {
        _rock.AlbedoColor = P.C("rock_tint", "#7c7c76");
        ApplyVeined(_crystal, new[] { "#4b5320", "#8a8a84", "#5a6130" }, "#2a2e14", 0.5f, 0f, 0.8f);
        _crystal.SetShaderParameter("mottle", P.F("mottle", 0.4f));

        var tall = Info.Tips.Count > 0 ? Tallest(1)[0] : -1;
        _crystal.SetShaderParameter("band_island", P.B("dress_band", true) ? tall : -1);
        if (tall >= 0)
        {
            var box = Info.Boxes[tall];
            _crystal.SetShaderParameter("band_y", box.Position.Y + box.Size.Y * P.F("band_at", 0.55f));
            _crystal.SetShaderParameter("band_width", P.F("band_width", 0.07f));
            _crystal.SetShaderParameter("band_center", box.GetCenter());
            _crystal.SetShaderParameter("band_color", P.C("band_color", "#c0100f"));
            _crystal.SetShaderParameter("disc_color", P.C("disc_color", "#f4f4f0"));
            _crystal.SetShaderParameter("disc_radius", P.F("disc_radius", 0.026f));
            _crystal.SetShaderParameter("disc_azimuth", P.F("disc_azimuth", 1.57f));
        }

        _rust.AlbedoColor = P.C("plate_color", "#6b3a1e");
        var pn = P.B("dress_plates", true) && Info.RockUp.Count > 0 ? Math.Clamp(P.I("plate_count", 3), 0, MaxPlates) : 0;
        for (var i = 0; i < MaxPlates; i++)
        {
            _plates[i].Visible = i < pn;
            if (i >= pn) continue;
            var s = RockSpot(P.I("plate_seed", 5) + i * 23)!.Value;
            var size = P.F("plate_size", 0.11f) * (0.7f + 0.5f * Hash01(i * 3.1));
            _plates[i].Transform = new Transform3D(OnSurface(s.N, i * 1.9f).Scaled(Vector3.One * size), s.P + s.N * size * 0.04f);
        }

        var on = P.B("searchlight", true);
        _pivot.Visible = on;
        _pivot.Position = Tip(Math.Max(tall, 0)) + new Vector3(0, 0.015f, 0);
        var c = P.C("beam_color", "#ffe4e0");
        var len = P.F("beam_length", 1.4f);
        var half = Mathf.DegToRad(P.F("beam_angle", 9f));
        _spot.LightColor = c;
        _spot.LightEnergy = P.F("beam_light", 4f);
        _spot.SpotRange = len * 2f;
        _spot.SpotAngle = Mathf.RadToDeg(half) * 1.3f;
        _beamMat.AlbedoColor = new Color(c, P.F("beam_alpha", 0.1f));
        // the cone's narrow end at the lamp, opening along -Z (where a light looks)
        _beam.Transform = new Transform3D(Basis.FromEuler(new Vector3(Mathf.Pi * 0.5f, 0, 0)) * Basis.FromScale(new Vector3(len * Mathf.Tan(half), len, len * Mathf.Tan(half))), new Vector3(0, 0, -len * 0.5f));
        SetLoop(P.S("static_sound", ""), P.F("static_db", -24f));
    }

    protected override void Tick(double dt)
    {
        var yaw = P.F("sweep_center", 0f) + Mathf.Sin((float)Time * P.F("sweep_speed", 0.6f)) * Mathf.DegToRad(P.F("sweep_degrees", 80f));
        _pivot.Rotation = new Vector3(Mathf.DegToRad(-P.F("beam_pitch", 12f)), yaw, 0);
    }
}
