using Godot;
using Drycastle.Shooter.Demon;

namespace Drycastle.Shooter.Crystals;

/// <summary>
/// Senin (crystals.md): the hay stone, what is left of a burnt bale. Crystals of packed hay: coarse golden straw strands with burnt patches (<c>shaders/crystal_fur.gdshader</c>), real stalks of straw sticking out of the
/// crystals and the rock, a faint warm glow and light. It smoulders: a few embers and a thin wisp of smoke. Bits of hay lie on the scorched rock. Tunables: data/crystals.json "senin".
/// </summary>
public sealed partial class SeninFx : CrystalFx
{
    public override string Id => "senin";

    StandardMaterial3D _rock = null!;
    ShaderMaterial _crystal = null!;
    OmniLight3D _light = null!;
    MeshInstance3D[] _hay = null!;
    MeshInstance3D _stalks = null!;
    StandardMaterial3D _stalkMat = null!;
    CpuParticles3D _embers = null!, _smoke = null!;

    protected override void Build()
    {
        _rock = CrystalAssets.Rock(Colors.White, 0.95f);
        _crystal = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/crystal_fur.gdshader") };
        _stalkMat = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.85f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        _stalks = new MeshInstance3D { Name = "Stalks", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_stalks);
        Body.SetSurfaceOverrideMaterial(0, _rock);
        Body.SetSurfaceOverrideMaterial(1, _crystal);
        _light = new OmniLight3D { ShadowEnabled = false };
        AddChild(_light);
        _hay = Tufts(30, new Color(0.5f, 0.38f, 0.12f), new Color(0.93f, 0.8f, 0.42f));
        _embers = Emitter(true);
        _smoke = Emitter(false);
        Swell(_smoke, 0.4f, 2.4f);
    }

    protected override void OnApply()
    {
        _rock.AlbedoColor = P.C("rock_tint", "#4a3f2c");
        _crystal.SetShaderParameter("fur", P.C("straw", "#e6c264"));
        _crystal.SetShaderParameter("fur_shadow", P.C("straw_shadow", "#6e4f18"));
        _crystal.SetShaderParameter("strand_scale", P.F("strand_scale", 90f));
        _crystal.SetShaderParameter("strand_length", P.F("strand_length", 0.04f));
        _crystal.SetShaderParameter("strand_depth", P.F("strand_depth", 0.85f));
        _crystal.SetShaderParameter("tuft_scale", P.F("tuft_scale", 12f));
        _crystal.SetShaderParameter("fuzz", P.F("fuzz", 0.15f));
        _crystal.SetShaderParameter("fuzz_power", 2f);
        _crystal.SetShaderParameter("scorch", P.F("scorch", 0.8f));
        _crystal.SetShaderParameter("scorch_scale", P.F("scorch_scale", 6f));
        _crystal.SetShaderParameter("glow", P.F("base_glow", 0.12f));
        BuildStalks();
        _light.LightColor = P.C("light", "#ffb24a");
        _light.OmniRange = P.F("light_range", 2f);
        _light.Position = At(P.V("light_at", new Vector3(0.5f, 0.6f, 0.5f)));
        PlaceTufts(_hay, P.B("dress_hay", true) ? Math.Clamp(P.I("hay_count", 14), 0, _hay.Length) : 0, P.I("hay_seed", 3), P.F("hay_size", 0.06f), P.F("hay_flatten", 0.45f));

        var ember = P.C("ember_color", "#ff9a30");
        Configure(_embers, P.I("ember_amount", 6), P.F("ember_lifetime", 1.8f), new Vector3(Box.GetCenter().X, Box.Size.Y * 0.55f, Box.GetCenter().Z), 0.15f,
            P.F("ember_size", 0.005f), 0.15f, Vector3.Up, 30f, 0.1f, Ramp(new Color(ember, 1f), new Color(ember, 0.8f), new Color(ember, 0f)));
        var smoke = P.C("smoke_color", "#6f6a64");
        Configure(_smoke, P.I("smoke_amount", 7), P.F("smoke_lifetime", 3.5f), Tip(Info.Tips.Count > 0 ? Tallest(1)[0] : 0), 0.02f,
            P.F("smoke_size", 0.03f), 0.12f, Vector3.Up, 12f, 0.02f, Ramp(new Color(smoke, 0f), new Color(smoke, P.F("smoke_alpha", 0.25f)), new Color(smoke, 0f)));
        SetLoop(P.S("crackle_sound", ""), P.F("crackle_db", -24f));
    }

    /// <summary>Stalks of straw sticking out of the faces of the crystals and of the rock: thin crossed strips, a little bent, pale to golden, some burnt at the end.</summary>
    void BuildStalks()
    {
        var onCrystal = P.B("dress_stalks", true) ? P.I("stalk_count", 260) : 0;
        var onRock = P.B("dress_stalks", true) ? P.I("stalk_rock_count", 90) : 0;
        _stalks.Visible = onCrystal + onRock > 0 && Info.Facets.Count > 0;
        if (!_stalks.Visible) return;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        var len = P.F("stalk_length", 0.07f);
        var w = P.F("stalk_width", 0.0016f);
        var seed = P.I("stalk_seed", 9);
        Color pale = P.C("stalk_pale", "#f2dc8c"), gold = P.C("stalk_gold", "#c79a3a"), burnt = new(0.08f, 0.06f, 0.04f);
        for (var i = 0; i < onCrystal + onRock; i++)
        {
            Vector3 p, n;
            if (i < onCrystal) { var f = Info.Facets[(int)(Hash01(i * 3.7 + seed) * Info.Facets.Count) % Info.Facets.Count]; p = f.P + Lift; n = f.N; }
            else if (Info.RockFaces.Count > 0) { var f = Info.RockFaces[(int)(Hash01(i * 5.3 + seed) * Info.RockFaces.Count) % Info.RockFaces.Count]; p = f.P + Lift; n = f.N; if (n.Y < -0.3f) continue; }
            else break;
            var t1 = Math.Abs(n.Y) > 0.9f ? Vector3.Right : Vector3.Up.Cross(n).Normalized();
            var t2 = n.Cross(t1);
            // the stalk leaves the face at a slant, starts a little way off the face's middle, and bends over
            var a = Hash01(i * 7.1) * Mathf.Tau;
            var lean = t1 * Mathf.Cos(a) + t2 * Mathf.Sin(a);
            p += (t1 * (Hash01(i * 2.3) - 0.5f) + t2 * (Hash01(i * 4.9) - 0.5f)) * 0.05f - n * 0.004f;
            var dir = (n * (0.35f + 0.9f * Hash01(i * 9.7)) + lean).Normalized();
            var l = len * (0.4f + 1.1f * Hash01(i * 1.3 + seed));
            var mid = p + dir * l * 0.55f;
            var end = mid + (dir + lean * 0.5f + Vector3.Down * 0.35f).Normalized() * l * 0.45f;
            var c0 = pale.Lerp(gold, Hash01(i * 6.1));
            var c1 = Hash01(i * 8.9) < P.F("stalk_burnt", 0.2f) ? burnt : c0.Lightened(0.1f);
            var u = dir.Cross(n).LengthSquared() > 1e-4f ? dir.Cross(n).Normalized() * w : t1 * w;
            var v = dir.Cross(u).Normalized() * w;
            foreach (var o in new[] { u, v })
            {
                st.SetColor(c0); st.AddVertex(p - o); st.SetColor(c0); st.AddVertex(p + o); st.SetColor(c0); st.AddVertex(mid + o);
                st.SetColor(c0); st.AddVertex(p - o); st.SetColor(c0); st.AddVertex(mid + o); st.SetColor(c0); st.AddVertex(mid - o);
                st.SetColor(c0); st.AddVertex(mid - o); st.SetColor(c0); st.AddVertex(mid + o); st.SetColor(c1); st.AddVertex(end);
            }
        }
        st.GenerateNormals();
        var mesh = st.Commit();
        if (mesh.GetSurfaceCount() > 0) mesh.SurfaceSetMaterial(0, _stalkMat);
        _stalks.Mesh = mesh;
    }

    protected override void Tick(double dt) =>
        _light.LightEnergy = P.F("light_energy", 0.5f) * DemonFx.Flicker(Time, 5.1f, 0.8f, 1.15f);
}
