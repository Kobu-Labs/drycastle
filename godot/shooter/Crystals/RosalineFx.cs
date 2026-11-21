using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// Rosaline (crystals.md): the stone of the flower garden, overgrown like a villain's greenhouse. Pink rose-quartz crystals with a soft pink glow (<c>shaders/crystal_quartz.gdshader</c>), ivy vines winding up
/// the tallest crystals, roses everywhere: on the rock base, on the crystals, and on the vines; moss on the top of the rock (<c>shaders/crystal_rock_moss.gdshader</c>). Nothing stands off the model. Bees fly from rose to rose, and now and then a leaf or a petal falls
/// and is gone when it reaches the ground. Everything is built in code (placeholders) and placed on the model by seeds. Bees and falling leaves are pure functions of time. Tunables: data/crystals.json "rosaline".
/// </summary>
public sealed partial class RosalineFx : CrystalFx
{
    public override string Id => "rosaline";

    const int MaxRoses = 80, MaxBees = 12, MaxFall = 12, MaxGrass = 30;

    StandardMaterial3D _plant = null!;
    ShaderMaterial _crystal = null!, _rock = null!;
    OmniLight3D _light = null!;
    MeshInstance3D _vines = null!;
    readonly MeshInstance3D[] _roses = new MeshInstance3D[MaxRoses];
    readonly ArrayMesh[] _roseMeshes = new ArrayMesh[3];
    readonly List<Vector3> _rosePos = new();
    readonly Node3D[] _bees = new Node3D[MaxBees];
    readonly Node3D[] _wings = new Node3D[MaxBees];
    readonly MeshInstance3D[] _fall = new MeshInstance3D[MaxFall];
    readonly MeshInstance3D[] _grass = new MeshInstance3D[MaxGrass];

    protected override void Build()
    {
        _rock = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/crystal_rock_moss.gdshader") };
        if (CrystalAssets.Atlas() is { } atlas) { _rock.SetShaderParameter("atlas", atlas); _rock.SetShaderParameter("use_atlas", true); }
        _crystal = Quartz();
        Body.SetSurfaceOverrideMaterial(0, _rock);
        Body.SetSurfaceOverrideMaterial(1, _crystal);
        _light = new OmniLight3D { ShadowEnabled = false };
        AddChild(_light);

        _plant = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.75f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        _roseMeshes[0] = RoseMesh(_plant, new Color(0.42f, 0.01f, 0.05f), new Color(0.9f, 0.08f, 0.16f), 0);
        _roseMeshes[1] = RoseMesh(_plant, new Color(0.7f, 0.12f, 0.32f), new Color(1f, 0.5f, 0.68f), 1);
        _roseMeshes[2] = RoseMesh(_plant, new Color(0.85f, 0.45f, 0.55f), new Color(1f, 0.9f, 0.92f), 2);
        for (var i = 0; i < MaxRoses; i++) _roses[i] = Add(new MeshInstance3D { Name = $"Rose{i}", Mesh = _roseMeshes[i % 3] });
        _vines = Add(new MeshInstance3D { Name = "Vines" });

        var tufts = new[] { SmetiskomiteFx.TuftMesh(_plant, 0, new Color(0.08f, 0.25f, 0.06f), new Color(0.35f, 0.62f, 0.2f)), SmetiskomiteFx.TuftMesh(_plant, 1, new Color(0.08f, 0.25f, 0.06f), new Color(0.3f, 0.58f, 0.18f)) };
        for (var i = 0; i < MaxGrass; i++) _grass[i] = Add(new MeshInstance3D { Name = $"Tuft{i}", Mesh = tufts[i % 2] });

        var leaf = LeafMesh(_plant, new Color(0.1f, 0.3f, 0.08f), new Color(0.3f, 0.6f, 0.2f));
        var petal = LeafMesh(_plant, new Color(0.75f, 0.1f, 0.25f), new Color(1f, 0.55f, 0.7f));
        for (var i = 0; i < MaxFall; i++) _fall[i] = Add(new MeshInstance3D { Name = $"Fall{i}", Mesh = i % 2 == 0 ? leaf : petal, Visible = false });

        BuildBees();
    }

    T Add<T>(T n) where T : GeometryInstance3D
    {
        n.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        AddChild(n);
        return n;
    }

    // ---- meshes ----

    /// <summary>A leaf from <paramref name="o"/> along <paramref name="dir"/>, facing <paramref name="up"/>: four triangles folded a little along the midrib.</summary>
    internal static void AddLeaf(SurfaceTool st, Vector3 o, Vector3 dir, Vector3 up, float size, Color dark, Color light)
    {
        dir = dir.Normalized();
        var side = dir.Cross(up);
        if (side.LengthSquared() < 1e-4f) side = dir.Cross(Vector3.Right);
        side = side.Normalized();
        up = side.Cross(dir).Normalized();
        Vector3 m = o + dir * size * 0.5f, tip = o + dir * size;
        Vector3 l = o + dir * size * 0.4f + side * size * 0.3f + up * size * 0.07f, r = o + dir * size * 0.4f - side * size * 0.3f + up * size * 0.07f;
        void V(Vector3 p, Color c) { st.SetColor(c); st.AddVertex(p); }
        var mid = dark.Lerp(light, 0.4f);
        V(o, dark); V(l, light); V(m, mid);
        V(l, light); V(tip, light); V(m, mid);
        V(o, dark); V(m, mid); V(r, light);
        V(m, mid); V(tip, light); V(r, light);
    }

    static ArrayMesh Commit(SurfaceTool st, Material mat)
    {
        st.GenerateNormals();
        var mesh = st.Commit();
        mesh.SurfaceSetMaterial(0, mat);
        return mesh;
    }

    /// <summary>A unit leaf lying flat, pointing along +Z (the falling leaves and petals).</summary>
    internal static ArrayMesh LeafMesh(Material mat, Color dark, Color light)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        AddLeaf(st, new Vector3(0, 0, -0.5f), Vector3.Back, Vector3.Up, 1f, dark, light);
        return Commit(st, mat);
    }

    /// <summary>
    /// A rose of about unit width standing on y = 0: four rings of cupped petals (upright in the middle, opening outwards), dark at the base and light at the lip, green sepals under it, a stem going down
    /// (into whatever it stands on) with a leaf.
    /// </summary>
    static ArrayMesh RoseMesh(Material mat, Color dark, Color light, int variant)
    {
        var rng = new Random(5 + variant * 17);
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        int[] counts = { 3, 4, 5, 6 };
        for (var l = 0; l < 4; l++)
        {
            var rl = 0.04f + l * 0.1f;
            var tilt = Mathf.DegToRad(4f + l * 21f);
            var h = 0.44f - l * 0.04f;
            var w = 0.15f + l * 0.075f;
            var a0 = l * 0.9f + (float)rng.NextDouble();
            for (var p = 0; p < counts[l]; p++)
            {
                var a = a0 + p / (float)counts[l] * Mathf.Tau;
                var o = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                var c = new Vector3(-o.Z, 0, o.X);
                var upDir = Vector3.Up * Mathf.Cos(tilt) + o * Mathf.Sin(tilt);
                var layer = l;
                Vector3 Pt(float u, float v)
                {
                    var ww = w * Mathf.Sin(Mathf.Pi * (0.2f + 0.6f * v));
                    return o * rl + c * (u * ww) + upDir * (v * h) - o * (u * u * ww * 0.6f) + o * (v * v * 0.07f * layer) + Vector3.Up * 0.08f;
                }
                Color Col(float v) => dark.Lerp(light, Mathf.Clamp(v * 0.8f + layer * 0.08f, 0f, 1f));
                const int nu = 4, nv = 4;
                for (var iu = 0; iu < nu; iu++)
                    for (var iv = 0; iv < nv; iv++)
                    {
                        float u0 = -1f + 2f * iu / nu, u1 = -1f + 2f * (iu + 1) / nu, v0 = iv / (float)nv, v1 = (iv + 1) / (float)nv;
                        st.SetColor(Col(v0)); st.AddVertex(Pt(u0, v0)); st.SetColor(Col(v0)); st.AddVertex(Pt(u1, v0)); st.SetColor(Col(v1)); st.AddVertex(Pt(u0, v1));
                        st.SetColor(Col(v0)); st.AddVertex(Pt(u1, v0)); st.SetColor(Col(v1)); st.AddVertex(Pt(u1, v1)); st.SetColor(Col(v1)); st.AddVertex(Pt(u0, v1));
                    }
            }
        }
        Color g0 = new(0.07f, 0.22f, 0.05f), g1 = new(0.25f, 0.5f, 0.15f);
        for (var s = 0; s < 5; s++)
        {
            var a = s / 5f * Mathf.Tau + 0.3f;
            var o = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
            AddLeaf(st, new Vector3(0, 0.07f, 0), o + Vector3.Up * 0.25f, Vector3.Up, 0.42f, g0, g1);
        }
        // stem: two crossed strips down from the flower, and a leaf on it
        foreach (var o in new[] { Vector3.Right * 0.025f, Vector3.Back * 0.025f })
        {
            Vector3 t = new(0, 0.09f, 0), b = new(0, -1.6f, 0);
            st.SetColor(g0);
            st.AddVertex(t - o); st.AddVertex(t + o); st.AddVertex(b + o);
            st.AddVertex(t - o); st.AddVertex(b + o); st.AddVertex(b - o);
        }
        AddLeaf(st, new Vector3(0, -0.35f, 0), new Vector3(1f, 0.35f, 0.3f), Vector3.Up, 0.5f, g0, g1);
        AddLeaf(st, new Vector3(0, -0.7f, 0), new Vector3(-0.8f, 0.3f, -0.6f), Vector3.Up, 0.45f, g0, g1);
        return Commit(st, mat);
    }

    void BuildBees()
    {
        var yellow = new StandardMaterial3D { AlbedoColor = new Color(0.95f, 0.72f, 0.08f), Roughness = 0.6f };
        var black = new StandardMaterial3D { AlbedoColor = new Color(0.05f, 0.04f, 0.03f), Roughness = 0.6f };
        var wing = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.9f, 0.95f), Roughness = 0.3f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        var ball = new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 8, Rings = 4 };
        MeshInstance3D Part(Node parent, Mesh mesh, Material mat, Vector3 pos, Vector3 scale)
        {
            var m = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Position = pos, Scale = scale, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            parent.AddChild(m);
            return m;
        }
        for (var i = 0; i < MaxBees; i++)
        {
            // a bee of unit length looking along -Z
            var bee = new Node3D { Name = $"Bee{i}" };
            AddChild(bee);
            Part(bee, ball, yellow, Vector3.Zero, new Vector3(0.55f, 0.5f, 1f));
            Part(bee, ball, black, new Vector3(0, 0, 0.05f), new Vector3(0.57f, 0.52f, 0.2f));
            Part(bee, ball, black, new Vector3(0, 0, 0.3f), new Vector3(0.45f, 0.42f, 0.18f));
            Part(bee, ball, black, new Vector3(0, 0, -0.5f), new Vector3(0.4f, 0.38f, 0.38f));
            var wings = new Node3D { Position = new Vector3(0, 0.22f, -0.1f) };
            bee.AddChild(wings);
            var quad = new BoxMesh { Size = new Vector3(0.6f, 0.01f, 0.3f) };
            Part(wings, quad, wing, new Vector3(0.32f, 0, 0), Vector3.One);
            Part(wings, quad, wing, new Vector3(-0.32f, 0, 0), Vector3.One);
            _bees[i] = bee;
            _wings[i] = wings;
        }
    }

    static float H(double n)
    {
        var s = Math.Sin(n * 12.9898 + 4.1) * 43758.5453;
        return (float)(s - Math.Floor(s));
    }

    // ---- placement ----

    int _roseN;

    void PutRose(Vector3 pos, Vector3 up, float size, int k)
    {
        if (_roseN >= MaxRoses) return;
        var r = _roses[_roseN++];
        up = up.Normalized();
        r.Mesh = _roseMeshes[(int)(H(k * 3.7) * 3f) % 3];
        r.Transform = new Transform3D(OnSurface(up, k * 2.1f).Scaled(Vector3.One * size), pos);
        r.Visible = true;
        _rosePos.Add(pos + up * size * 0.35f);
    }

    protected override void OnApply()
    {
        _rock.SetShaderParameter("tint", P.C("rock_tint", "#5d6b4a"));
        _rock.SetShaderParameter("rough", P.F("rock_roughness", 0.9f));
        _rock.SetShaderParameter("moss", P.C("moss_color", "#2a6114"));
        _rock.SetShaderParameter("moss_light", P.C("moss_light", "#6b9e29"));
        _rock.SetShaderParameter("moss_amount", P.F("moss_amount", 1f));
        _rock.SetShaderParameter("moss_slope", P.F("moss_slope", 0.35f));
        _rock.SetShaderParameter("moss_scale", P.F("moss_scale", 14f));
        ApplyQuartz(_crystal, new[] { "#ff8fb4", "#f7b6cd", "#ff6f9c" }, "#fff0f5");
        _crystal.SetShaderParameter("glow", P.F("base_glow", 0.8f));
        _crystal.SetShaderParameter("glow_opacity", P.F("glow_opacity", 0.15f));
        _light.LightColor = P.C("light", "#ff8fb8");
        _light.OmniRange = P.F("light_range", 2.2f);
        _light.Position = At(P.V("light_at", new Vector3(0.5f, 0.7f, 0.5f)));

        // roses: on the rock, on the crystals, on the vines (never off the model)
        _roseN = 0;
        _rosePos.Clear();
        foreach (var r in _roses) r.Visible = false;
        var size = P.F("rose_size", 0.07f);
        var seed = P.I("rose_seed", 7);
        float Size(int k) => size * (0.65f + 0.7f * H(k * 1.9 + seed));

        BuildVines(seed, Size);

        if (P.B("dress_roses", true))
        {
            for (var i = 0; i < P.I("rose_rock", 16); i++)
                if (RockSpot(seed + i * 13) is { } s) PutRose(s.P, s.N + Vector3.Up * 0.5f, Size(i), i);
            var put = 0;
            var want = P.I("rose_crystal", 8);
            for (var i = 0; i < 200 && put < want; i++)
                if (CrystalSpot(seed + 500 + i * 31) is { } s && s.N.Y > 0.05f) { PutRose(s.P, s.N + Vector3.Up * 0.3f, Size(100 + i) * 0.85f, 100 + i); put++; }
        }

        var grassN = P.B("dress_grass", true) && Info.RockUp.Count > 0 ? Math.Clamp(P.I("grass_count", 10), 0, MaxGrass) : 0;
        for (var i = 0; i < MaxGrass; i++)
        {
            _grass[i].Visible = i < grassN;
            if (i >= grassN) continue;
            var s = RockSpot(seed + 900 + i * 17)!.Value;
            _grass[i].Transform = new Transform3D(OnSurface(s.N, i * 2.4f).Scaled(Vector3.One * P.F("grass_height", 0.06f) * (0.7f + 0.6f * H(i))), s.P);
        }

        var bn = _rosePos.Count > 1 ? Math.Clamp(P.I("bee_count", 6), 0, MaxBees) : 0;
        for (var i = 0; i < MaxBees; i++)
        {
            _bees[i].Visible = i < bn;
            _bees[i].Scale = Vector3.One * P.F("bee_size", 0.014f);
        }
        SetLoop(P.S("bee_sound", ""), P.F("bee_db", -22f));
    }

    /// <summary>
    /// Ivy up the tallest crystals: a helix about the crystal's own axis (middle of its foot to its tip) at the crystal's width at that height, drawn as thin crossed strips with a leaf at every step and a
    /// rose here and there.
    /// </summary>
    void BuildVines(int seed, Func<int, float> roseSize)
    {
        var n = P.B("dress_vines", true) ? Math.Clamp(P.I("vine_count", 4), 0, Info.Tips.Count) : 0;
        _vines.Visible = n > 0;
        if (n == 0) return;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        Color g0 = new(0.08f, 0.32f, 0.06f), g1 = new(0.4f, 0.8f, 0.18f), g2 = new(0.62f, 0.88f, 0.25f), stem = new(0.12f, 0.3f, 0.08f);
        var w = P.F("vine_thickness", 0.004f);
        var leaf = P.F("leaf_size", 0.075f);
        var turns = P.F("vine_turns", 2.5f);
        var top = P.F("vine_top", 0.85f);
        var roses = P.I("rose_vine", 2);
        var order = Enumerable.Range(0, Info.Tips.Count).OrderByDescending(i => Info.Tips[i].Y).Take(n).ToArray();
        foreach (var isl in order)
        {
            if (Helix(isl, turns, w * 1.5f) is not { } hx) continue;
            var (Pt, Out) = hx;
            var segs = Math.Max(12, (int)(turns * 18));
            for (var k = 0; k < segs; k++)
            {
                float t0 = top * k / segs, t1 = top * (k + 1) / segs;
                Vector3 p0 = Pt(t0), p1 = Pt(t1);
                var d = (p1 - p0).Normalized();
                var o = Out(t0);
                var u = d.Cross(o).Normalized() * w;
                foreach (var off in new[] { u, o * w })
                {
                    st.SetColor(stem);
                    st.AddVertex(p0 - off); st.AddVertex(p0 + off); st.AddVertex(p1 + off);
                    st.AddVertex(p0 - off); st.AddVertex(p1 + off); st.AddVertex(p1 - off);
                }
                var sideways = d.Cross(o).Normalized() * (k % 2 == 0 ? 1f : -1f);
                var lift = (H(k * 1.3 + isl) - 0.4f) * 0.8f;
                AddLeaf(st, p0, sideways + o * 0.6f + Vector3.Up * lift, o, leaf * (0.7f + 0.6f * H(k + isl * 9)), g0, H(k * 2.9 + isl) > 0.6f ? g2 : g1);
                if (k % 3 == 0) AddLeaf(st, p0, -sideways * 0.6f + o + Vector3.Up * 0.3f, o, leaf * 0.6f, g0, g2);
            }
            for (var r = 0; r < roses; r++)
            {
                var t = top * (0.25f + 0.6f * (r + H(isl * 3 + r)) / Math.Max(roses, 1));
                PutRose(Pt(t), Out(t) + Vector3.Up * 0.6f, roseSize(300 + isl * 5 + r) * 0.8f, 300 + isl * 5 + r);
            }
        }
        _vines.Mesh = Commit(st, _plant);
    }

    // ---- motion: pure functions of time ----

    /// <summary>Bee <paramref name="i"/> at a time: hovers about a rose, then flies in an arc to the next one (both picked from the bee number and the round, so nothing is remembered).</summary>
    Vector3 BeeAt(int i, double time)
    {
        var dwell = P.F("bee_dwell", 2.2f) * (0.6f + 0.8f * H(i * 3.1));
        var travel = P.F("bee_travel", 0.9f);
        var x = (time + H(i) * 20.0) / (dwell + travel);
        var round = Math.Floor(x);
        var t = (float)(x - round) * (dwell + travel);
        Vector3 Rose(double r) => _rosePos[(int)(H(i * 17.3 + r * 7.1) * _rosePos.Count) % _rosePos.Count];
        var s = Mathf.SmoothStep(0f, 1f, Mathf.Clamp((t - dwell) / travel, 0f, 1f));
        var a = (float)time * P.F("bee_speed", 6f) + i * 2f;
        var hover = P.F("bee_hover", 0.03f);
        return Rose(round).Lerp(Rose(round + 1), s) + Vector3.Up * (hover + Mathf.Sin(Mathf.Pi * s) * 0.08f)
            + new Vector3(Mathf.Cos(a) * hover, Mathf.Sin(a * 2.3f) * hover * 0.4f, Mathf.Sin(a) * hover);
    }

    protected override void Tick(double dt)
    {
        _light.LightEnergy = P.F("light_energy", 0.6f) * (1f + P.F("light_breathe", 0.15f) * Mathf.Sin((float)Time * 0.8f));

        if (_rosePos.Count == 0) { foreach (var f in _fall) f.Visible = false; return; }
        for (var i = 0; i < MaxBees; i++)
        {
            if (!_bees[i].Visible) continue;
            var p = BeeAt(i, Time);
            var v = p - BeeAt(i, Time - 0.03);
            v.Y *= 0.3f;
            var s = _bees[i].Scale.X;
            _bees[i].Position = p;
            if (v.LengthSquared() > 1e-10f) _bees[i].Basis = Basis.LookingAt(v.Normalized(), Vector3.Up).Scaled(Vector3.One * s);
            _wings[i].Rotation = new Vector3(0, 0, Mathf.Sin((float)Time * 90f + i) * 0.5f);
        }

        // falling leaves and petals: each one waits, falls from a rose swaying and turning, and shrinks away as it reaches the ground (nothing is left lying off the model)
        var fn = Math.Clamp(P.I("fall_count", 5), 0, MaxFall);
        var speed = Math.Max(P.F("fall_speed", 0.18f), 0.02f);
        var size = P.F("fall_size", 0.05f);
        var maxFall = (Box.Size.Y + 0.1f) / speed;
        for (var j = 0; j < MaxFall; j++)
        {
            var f = _fall[j];
            if (j >= fn) { f.Visible = false; continue; }
            var period = P.F("fall_interval", 9f) * (0.6f + 0.8f * H(j * 2.3)) + maxFall;
            var x = (Time + H(j * 9.1) * 100.0) / period;
            var round = Math.Floor(x);
            var t = (float)((x - round) * period);
            var from = _rosePos[(int)(H(j * 5.9 + round * 3.3) * _rosePos.Count) % _rosePos.Count];
            var dur = from.Y / speed;
            f.Visible = t < dur;
            if (!f.Visible) continue;
            var ft = Math.Min(t, dur);
            var away = new Vector3(from.X - Box.GetCenter().X, 0, from.Z - Box.GetCenter().Z).Normalized();
            var pos = from + away * (0.03f + ft * 0.04f) + new Vector3(Mathf.Sin(ft * 2.1f + j), 0, Mathf.Cos(ft * 1.7f + j * 2f)) * 0.035f * Math.Min(ft, 1f);
            pos.Y = Math.Max(from.Y - speed * ft, 0.004f);
            var basis = Basis.FromEuler(new Vector3(Mathf.Sin(ft * 3.1f + j) * 0.9f, ft * 1.4f + j, Mathf.Sin(ft * 2.3f) * 0.7f));
            f.Transform = new Transform3D(basis.Scaled(Vector3.One * size * Mathf.Clamp((dur - ft) / 0.4f, 0f, 1f)), pos);
        }
    }
}
