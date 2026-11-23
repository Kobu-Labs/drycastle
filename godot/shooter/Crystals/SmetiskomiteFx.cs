using Godot;

namespace Drycastle.Shooter.Crystals;

/// <summary>
/// Smetiskomite (crystals.md): the stone of the illegal dump (smetisko). Quartz-like, a little translucent and spoiled: every crystal of the rock has its own tint (rust, sickly green, mustard) with grimy
/// inclusions (<c>shaders/crystal_quartz.gdshader</c>), NO glow and NO light. Everything it is dressed with sits ON the model, on the rock base or on the crystals (placeholders built from boxes, each placed
/// on a face picked by a seed in data/crystals.json so a prop moves by changing its seed): oil drips running down the crystals (drop falls, puddle on the rock base) and grass tufts (blades fanned from one root) on the rock, a fish skeleton on a rock ledge, a banana peel on a crystal, a spider web
/// strung between the two tallest crystals. Flies circle a tip and a dim green vapour seeps from another. Tunables: data/crystals.json "smetiskomite".
/// </summary>
public sealed partial class SmetiskomiteFx : CrystalFx
{
    public override string Id => "smetiskomite";

    const int MaxGrass = 40, MaxOil = 6, MaxFlies = 16;

    StandardMaterial3D _rock = null!;
    ShaderMaterial _crystal = null!;
    Node3D _banana = null!, _fish = null!, _grass = null!, _oil = null!;
    MeshInstance3D _web = null!;
    readonly MeshInstance3D[] _flies = new MeshInstance3D[MaxFlies];
    readonly float[] _flyPhase = new float[MaxFlies], _flySpeed = new float[MaxFlies], _flyRadius = new float[MaxFlies];
    CpuParticles3D _vapor = null!;

    protected override void Build()
    {
        _rock = CrystalAssets.Rock(Colors.White, 0.95f);
        _crystal = Quartz();
        Body.SetSurfaceOverrideMaterial(0, _rock);
        Body.SetSurfaceOverrideMaterial(1, _crystal);

        BuildBanana();
        BuildFish();
        BuildGrass();
        BuildOil();
        _web = new MeshInstance3D
        {
            Name = "Web", Mesh = new ImmediateMesh(), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.82f, 0.82f, 0.8f), CullMode = BaseMaterial3D.CullModeEnum.Disabled },
        };
        AddChild(_web);

        var fly = Flat(new Color(0.03f, 0.03f, 0.03f), 0.4f);
        var flyMesh = new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 6, Rings = 3, Material = fly };
        for (var i = 0; i < MaxFlies; i++)
        {
            _flies[i] = new MeshInstance3D { Name = $"Fly{i}", Mesh = flyMesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(_flies[i]);
            _flyPhase[i] = Rand(0f, Mathf.Tau);
            _flySpeed[i] = Rand(0.7f, 1.4f) * (Rng.Next(2) == 0 ? 1 : -1);
            _flyRadius[i] = Rand(0.5f, 1f);
        }

        _vapor = Emitter(false);
        _vapor.ScaleAmountCurve = new Curve();
        _vapor.ScaleAmountCurve.AddPoint(new Vector2(0f, 0.4f));
        _vapor.ScaleAmountCurve.AddPoint(new Vector2(1f, 2.4f));
    }

    static StandardMaterial3D Flat(Color c, float rough, float metal = 0f) => new() { AlbedoColor = c, Roughness = rough, Metallic = metal };

    static MeshInstance3D Part(Node parent, Mesh mesh, Vector3 pos, Vector3 rotDeg)
    {
        var m = new MeshInstance3D { Mesh = mesh, Position = pos, RotationDegrees = rotDeg, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        parent.AddChild(m);
        return m;
    }

    void BuildBanana()
    {
        _banana = new Node3D { Name = "BananaPeel" };
        AddChild(_banana);
        var skin = Flat(new Color(0.78f, 0.66f, 0.12f), 0.6f);
        var spot = Flat(new Color(0.25f, 0.16f, 0.05f), 0.9f);
        for (var i = 0; i < 3; i++)
        {
            var arm = new Node3D { RotationDegrees = new Vector3(0, i * 120f + 15f, 0) };
            _banana.AddChild(arm);
            Part(arm, new BoxMesh { Size = new Vector3(0.026f, 0.004f, 0.1f), Material = skin }, new Vector3(0, 0.004f, 0.05f), new Vector3(-8f, 0, 0));
            Part(arm, new BoxMesh { Size = new Vector3(0.022f, 0.005f, 0.02f), Material = spot }, new Vector3(0, 0.002f, 0.1f), new Vector3(-8f, 0, 0));
        }
        Part(_banana, new BoxMesh { Size = new Vector3(0.012f, 0.03f, 0.012f), Material = spot }, new Vector3(0, 0.014f, 0), new Vector3(0, 0, 10f));
    }

    void BuildFish()
    {
        _fish = new Node3D { Name = "FishSkeleton" };
        AddChild(_fish);
        var bone = Flat(new Color(0.85f, 0.82f, 0.72f), 0.8f);
        Part(_fish, new BoxMesh { Size = new Vector3(0.012f, 0.004f, 0.17f), Material = bone }, new Vector3(0, 0.004f, 0), Vector3.Zero);
        for (var i = 0; i < 7; i++)
        {
            var z = -0.06f + i * 0.02f;
            var len = 0.05f - Math.Abs(i - 3) * 0.006f;
            Part(_fish, new BoxMesh { Size = new Vector3(len, 0.003f, 0.003f), Material = bone }, new Vector3(len * 0.45f, 0.004f, z), new Vector3(0, 28f, 0));
            Part(_fish, new BoxMesh { Size = new Vector3(len, 0.003f, 0.003f), Material = bone }, new Vector3(-len * 0.45f, 0.004f, z), new Vector3(0, -28f, 0));
        }
        Part(_fish, new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 8, Rings = 4, Material = bone }, new Vector3(0, 0.008f, 0.1f), Vector3.Zero).Scale = new Vector3(0.03f, 0.022f, 0.045f);
        Part(_fish, new PrismMesh { Size = new Vector3(0.045f, 0.04f, 0.003f), Material = bone }, new Vector3(0, 0.004f, -0.1f), new Vector3(90f, 0, 0));
    }

    readonly ArrayMesh[] _tufts = new ArrayMesh[3];

    void BuildGrass()
    {
        _grass = new Node3D { Name = "Grass" };
        AddChild(_grass);
        var mat = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.9f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        for (var v = 0; v < _tufts.Length; v++) _tufts[v] = TuftMesh(mat, v);
        for (var i = 0; i < MaxGrass; i++)
            _grass.AddChild(new MeshInstance3D { Name = $"Tuft{i}", Mesh = _tufts[i % _tufts.Length], CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }

    /// <summary>One tuft of unit height: 6-9 tapered blades fanned out of one point, each leaning outwards and bending over at the tip, dark at the root and yellow-green at the tip.</summary>
    internal static ArrayMesh TuftMesh(Material mat, int variant, Color? rootColor = null, Color? tipColor = null)
    {
        var rng = new Random(77 + variant * 31);
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        var blades = 6 + variant * 2;
        var root = rootColor ?? new Color(0.12f, 0.2f, 0.05f);
        var tip = tipColor ?? new Color(0.45f, 0.52f, 0.14f);
        for (var b = 0; b < blades; b++)
        {
            var yaw = (b + (float)rng.NextDouble() * 0.5f) / blades * Mathf.Tau;
            var lean = Mathf.DegToRad(12f + (float)rng.NextDouble() * 28f);
            var height = 0.55f + (float)rng.NextDouble() * 0.45f;
            var width = 0.05f + (float)rng.NextDouble() * 0.03f;
            var dir = new Vector3(Mathf.Cos(yaw), 0, Mathf.Sin(yaw));
            var side = new Vector3(-dir.Z, 0, dir.X);
            const int seg = 4;
            Vector3 Centre(float t) => dir * (Mathf.Sin(lean) * t * t * height * 0.9f + Mathf.Sin(lean) * t * height * 0.3f) + Vector3.Up * (t * height * Mathf.Cos(lean * 0.5f));
            for (var k = 0; k < seg; k++)
            {
                float t0 = k / (float)seg, t1 = (k + 1) / (float)seg;
                var w0 = width * (1f - t0);
                var w1 = width * (1f - t1) * (k == seg - 1 ? 0f : 1f);
                Vector3 a = Centre(t0) - side * w0, bb = Centre(t0) + side * w0, c = Centre(t1) - side * w1, d = Centre(t1) + side * w1;
                Color c0 = root.Lerp(tip, t0), c1 = root.Lerp(tip, t1);
                st.SetColor(c0); st.AddVertex(a); st.SetColor(c0); st.AddVertex(bb); st.SetColor(c1); st.AddVertex(c);
                st.SetColor(c0); st.AddVertex(bb); st.SetColor(c1); st.AddVertex(d); st.SetColor(c1); st.AddVertex(c);
            }
        }
        st.GenerateNormals();
        var mesh = st.Commit();
        mesh.SurfaceSetMaterial(0, mat);
        return mesh;
    }

    /// <summary>One oil drip: a glossy streak down a crystal facet, a bulb at its end, a drop that falls from it and (on the rock base) a puddle.</summary>
    sealed class Drip
    {
        public MeshInstance3D Streak = null!, Bulb = null!, Drop = null!, Puddle = null!;
        public Vector3 End, Land;
        public bool OnRock;
        public float Phase, Fall;
    }

    readonly Drip[] _drips = new Drip[MaxOil];
    StandardMaterial3D _oilMat = null!;

    void BuildOil()
    {
        _oil = new Node3D { Name = "OilDrips" };
        AddChild(_oil);
        _oilMat = Flat(new Color(0.07f, 0.045f, 0.02f), 0.06f, 0.35f);
        _oilMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        var ball = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 8, Rings = 4, Material = _oilMat };
        var disc = new CylinderMesh { TopRadius = 1f, BottomRadius = 1f, Height = 0.002f, RadialSegments = 16, Material = _oilMat };
        MeshInstance3D Mk(string n, Mesh? m) { var mi = new MeshInstance3D { Name = n, Mesh = m, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off }; _oil.AddChild(mi); return mi; }
        for (var i = 0; i < MaxOil; i++)
            _drips[i] = new Drip { Streak = Mk($"Streak{i}", null), Bulb = Mk($"Bulb{i}", ball), Drop = Mk($"Drop{i}", ball), Puddle = Mk($"Puddle{i}", disc) };
    }

    /// <summary>Lays drip <paramref name="i"/>: starts on a side facet of a crystal (picked by the seed), runs down the facet, ends in a bulb; the drop lands on a rock face below it (puddle) or on the ground.</summary>
    void SetDrip(int i, int seed, float length, float width, float puddle)
    {
        var d = _drips[i];
        (Vector3 P, Vector3 N)? pick = null;
        for (var k = 0; k < 24 && pick is null; k++)
            if (CrystalSpot(seed + i * 29 + k * 7) is { } c && c.N.Y > -0.2f && c.N.Y < 0.85f) pick = c;
        d.Streak.Visible = d.Drop.Visible = d.Puddle.Visible = pick is not null;
        if (pick is null) return;
        var (p0, n) = pick.Value;
        var down = (Vector3.Down - n * n.Dot(Vector3.Down)).Normalized();
        var side = n.Cross(down).Normalized();
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetNormal(n);
        const int seg = 22;
        // a wet smear: fat where it starts, a thin neck, a rounded teardrop at the end, wavy edges; a shorter second rivulet beside it
        float W(float t, float w)
        {
            var baseW = w * (0.5f + 1.7f * Mathf.Exp(-6f * t)) * (1f - Mathf.SmoothStep(0.93f, 1f, t));
            var u = (t - 0.93f) / 0.07f;
            var bulge = t > 0.86f ? w * 1.5f * Mathf.Sqrt(Math.Max(0f, 1f - u * u)) : 0f;
            return Math.Max(baseW, bulge) * (1f + 0.18f * Mathf.Sin(t * 23f + i * 3f));
        }
        void Rivulet(float len, float off, float w, float ph)
        {
            Vector3 C(float t) => p0 + n * 0.003f + side * off + down * (t * len) + side * (Mathf.Sin(t * 4f + ph) * w * 0.5f);
            for (var k = 0; k < seg; k++)
            {
                float t0 = k / (float)seg, t1 = (k + 1) / (float)seg;
                Vector3 a = C(t0) - side * W(t0, w), b = C(t0) + side * W(t0, w), c = C(t1) - side * W(t1, w), e = C(t1) + side * W(t1, w);
                st.AddVertex(a); st.AddVertex(b); st.AddVertex(c);
                st.AddVertex(b); st.AddVertex(e); st.AddVertex(c);
            }
        }
        Rivulet(length * 0.55f, width * 2.6f, width * 0.7f, i * 1.3f + 1f);
        Rivulet(length, 0f, width, i * 2f);
        Vector3 At(float t) => p0 + n * 0.003f + down * (t * length) + side * (Mathf.Sin(t * 4f + i * 2f) * width * 0.5f);
        d.Streak.Mesh = st.Commit();
        d.Streak.Mesh.SurfaceSetMaterial(0, _oilMat);
        d.End = At(1f) + n * 0.004f;
        d.Bulb.Position = d.End;
        d.Bulb.Visible = false;

        // what the drop hits: a rock face just below the end, else the ground
        var best = float.MaxValue;
        d.OnRock = false;
        d.Land = new Vector3(d.End.X, 0f, d.End.Z);
        var rockN = Vector3.Up;
        foreach (var (rp, rn) in Info.RockUp)
        {
            var q = rp + Lift;
            var dist = new Vector2(q.X - d.End.X, q.Z - d.End.Z).Length();
            if (q.Y > d.End.Y || dist > 0.07f || dist >= best) continue;
            best = dist;
            d.OnRock = true;
            d.Land = new Vector3(d.End.X, q.Y, d.End.Z);
            rockN = rn;
        }
        d.Fall = Mathf.Sqrt(2f * Math.Max(d.End.Y - d.Land.Y, 0.01f) / 9.8f);
        d.Phase = (float)Hash(i + 3) * 10f;
        d.Bulb.Scale = Vector3.One * width * 1.4f;
        d.Drop.Scale = Vector3.One * width * 0.9f;
        d.Puddle.Visible = d.OnRock;
        if (d.OnRock)
        {
            var r = puddle * (0.7f + 0.5f * (float)Hash(i + 9));
            d.Puddle.Transform = new Transform3D(OnSurface(rockN, i * 1.7f) * Basis.FromScale(new Vector3(r, 1f, r * 0.75f)), d.Land + rockN * 0.002f);
        }
    }

    void TickOil()
    {
        if (!_oil.Visible) return;
        var period = Math.Max(P.F("oil_period", 2.4f), 0.5f);
        var width = P.F("oil_width", 0.012f);
        foreach (var d in _drips)
        {
            if (!d.Streak.Visible) continue;
            var t = (float)((Time + d.Phase) % period);
            var swell = Mathf.Clamp(t / (period - d.Fall), 0f, 1f);
            
            var fall = t - (period - d.Fall);
            d.Drop.Visible = fall > 0f;
            if (fall > 0f)
            {
                var y = d.End.Y - 0.5f * 9.8f * fall * fall;
                d.Drop.Position = new Vector3(d.End.X, Math.Max(y, d.Land.Y), d.End.Z);
                d.Drop.Scale = new Vector3(width * 0.7f, width * 1.1f, width * 0.7f);
            }
        }
    }

    /// <summary>A prop's transform on a face: standing on it, turned by <paramref name="yaw"/>, scaled, lifted a hair off the surface.</summary>
    static Transform3D On((Vector3 P, Vector3 N) spot, float yaw, float scale, float lift = 0.003f) =>
        new(OnSurface(spot.N, yaw).Scaled(Vector3.One * scale), spot.P + spot.N * lift);

    protected override void OnApply()
    {
        _rock.AlbedoColor = P.C("rock_tint", "#4a4338");
        _rock.Roughness = P.F("rock_roughness", 0.95f);
        ApplyQuartz(_crystal, new[] { "#a8501c", "#6f8f2a", "#c4a22e" }, "#1c130a");
        _crystal.SetShaderParameter("glow", 0f); // this stone does not glow

        _banana.Visible = P.B("dress_banana", true) && CrystalSpot(P.I("banana_seed", 3)) is not null;
        if (_banana.Visible) _banana.Transform = On(CrystalSpot(P.I("banana_seed", 3))!.Value, P.F("banana_yaw", 0.6f), P.F("banana_scale", 0.8f));
        _fish.Visible = P.B("dress_fish", true) && RockSpot(P.I("fish_seed", 5)) is not null;
        if (_fish.Visible) _fish.Transform = On(RockSpot(P.I("fish_seed", 5))!.Value, P.F("fish_yaw", 0.9f), P.F("fish_scale", 0.7f));

        _grass.Visible = P.B("dress_grass", true) && Info.RockUp.Count > 0;
        if (_grass.Visible)
        {
            var gn = Math.Clamp(P.I("grass_count", 6), 0, MaxGrass);
            var gh = P.F("grass_height", 0.08f);
            var seed = P.I("grass_seed", 11);
            for (var i = 0; i < MaxGrass; i++)
            {
                var t = (MeshInstance3D)_grass.GetChild(i);
                t.Visible = i < gn;
                if (i >= gn) continue;
                var spot = RockSpot(seed + i * 13)!.Value;
                var height = gh * (0.7f + 0.6f * (float)Hash(i));
                t.Transform = new Transform3D(OnSurface(spot.N, i * 2.4f).Scaled(Vector3.One * height), spot.P);
            }
        }

        _oil.Visible = P.B("dress_oil", true) && Info.Facets.Count > 0;
        if (_oil.Visible)
        {
            var on = Math.Clamp(P.I("oil_count", 3), 0, MaxOil);
            for (var i = 0; i < MaxOil; i++)
            {
                if (i < on) SetDrip(i, P.I("oil_seed", 17), P.F("oil_length", 0.14f), P.F("oil_width", 0.012f), P.F("oil_puddle", 0.05f));
                else { var d = _drips[i]; d.Streak.Visible = d.Bulb.Visible = d.Drop.Visible = d.Puddle.Visible = false; }
            }
        }

        BuildWebMesh();

        var fs = P.F("fly_size", 0.007f);
        var fn = Math.Clamp(P.I("flies_count", 7), 0, MaxFlies);
        for (var i = 0; i < MaxFlies; i++)
        {
            _flies[i].Visible = i < fn;
            _flies[i].Scale = Vector3.One * fs * 2f;
        }

        var green = P.C("vapor_color", "#6f8f2a");
        var alpha = P.F("vapor_alpha", 0.22f);
        Configure(_vapor, P.I("vapor_amount", 9), P.F("vapor_lifetime", 2.8f), Tip(P.I("vapor_island", 0)) + new Vector3(0, 0.01f, 0), 0.015f,
            P.F("vapor_size", 0.05f), P.F("vapor_speed", 0.1f), Vector3.Up, 25f, 0.01f,
            Ramp(new Color(green, 0f), new Color(green, alpha), new Color(green, 0f)));

        SetLoop(P.S("fly_sound", ""), P.F("fly_db", -22f));
    }

    static double Hash(double n)
    {
        var s = Math.Sin(n * 12.9898) * 43758.5453;
        return s - Math.Floor(s);
    }

    /// <summary>
    /// A web strung between the two tallest crystals (web_a / web_b override): from both tips down to the facets lower on the crystals that face each other, spokes and sagging rings of thin crossed strips.
    /// Opaque on purpose: a see-through line mesh sorted behind the translucent crystals and vanished from some sides.
    /// </summary>
    void BuildWebMesh()
    {
        _web.Visible = P.B("dress_web", true) && Info.Tips.Count >= 2;
        var m = (ImmediateMesh)_web.Mesh;
        m.ClearSurfaces();
        if (!_web.Visible) return;
        int a = P.I("web_a", -1), b = P.I("web_b", -1);
        if (a < 0 || b < 0)
        {
            var order = Enumerable.Range(0, Info.Tips.Count).OrderByDescending(i => Info.Tips[i].Y).ToArray();
            a = order[0];
            b = order[1];
        }
        var tipA = Tip(a);
        var tipB = Tip(b);
        var lowA = Lower(a, tipB);
        var lowB = Lower(b, tipA);
        var ring = new[] { tipA, (tipA + tipB) * 0.5f, tipB, (tipB + lowB) * 0.5f, lowB, (lowB + lowA) * 0.5f, lowA, (lowA + tipA) * 0.5f };
        var g = ring.Aggregate(Vector3.Zero, (acc, p) => acc + p) / ring.Length;
        var w = P.F("web_thickness", 0.0012f);
        var segs = new List<(Vector3, Vector3)>();
        foreach (var p in ring) segs.Add((g, p));
        foreach (var k in new[] { 1f, 0.7f, 0.4f })
            for (var i = 0; i < ring.Length; i++)
            {
                var sag = new Vector3(0, -0.01f * k, 0);
                segs.Add((g + (ring[i] - g) * k + sag, g + (ring[(i + 1) % ring.Length] - g) * k + sag));
            }
        m.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        foreach (var (p0, p1) in segs)
        {
            var dir = (p1 - p0).Normalized();
            var u = dir.Cross(Vector3.Up);
            if (u.LengthSquared() < 1e-4f) u = dir.Cross(Vector3.Right);
            u = u.Normalized() * w;
            var v = dir.Cross(u).Normalized() * w;
            foreach (var o in new[] { u, v })
            {
                m.SurfaceAddVertex(p0 - o); m.SurfaceAddVertex(p0 + o); m.SurfaceAddVertex(p1 + o);
                m.SurfaceAddVertex(p0 - o); m.SurfaceAddVertex(p1 + o); m.SurfaceAddVertex(p1 - o);
            }
        }
        m.SurfaceEnd();
    }

    /// <summary>The point on crystal <paramref name="island"/>, well below its tip, that is nearest to <paramref name="toward"/> (the other crystal's tip).</summary>
    Vector3 Lower(int island, Vector3 toward)
    {
        var tip = Info.Tips[island];
        var h = Info.Boxes[island].Size.Y;
        var best = Tip(island) + new Vector3(0, -h * 0.4f, 0);
        var bestD = float.MaxValue;
        foreach (var (p, n, isl) in Info.Facets)
        {
            if (isl != island || p.Y > tip.Y - h * 0.3f || p.Y < tip.Y - h * 0.7f) continue;
            var d = (p + Lift).DistanceTo(toward);
            if (d < bestD) { bestD = d; best = p + Lift + n * 0.004f; }
        }
        return best;
    }

    protected override void Tick(double dt)
    {
        TickOil();
        var c = Tip(P.I("flies_island", 3)) + new Vector3(0, 0.05f, 0);
        var radius = P.F("flies_radius", 0.12f);
        var speed = P.F("flies_speed", 3.2f);
        for (var i = 0; i < MaxFlies; i++)
        {
            if (!_flies[i].Visible) continue;
            var a = (float)Time * speed * _flySpeed[i] + _flyPhase[i];
            var r = radius * _flyRadius[i] * (0.8f + 0.2f * Mathf.Sin(a * 0.37f + i));
            _flies[i].Position = c + new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a * 1.7f + _flyPhase[i]) * radius * 0.25f, Mathf.Sin(a) * r * 0.8f)
                + new Vector3(Mathf.Sin(a * 9.1f), Mathf.Sin(a * 7.3f), Mathf.Sin(a * 11.7f)) * 0.004f;
        }
    }
}
