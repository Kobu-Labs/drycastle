using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// Thundernite (crystals.md): pale storm-glass that glows only faintly until a mini lightning strike zips between its tips; the strike itself is the light (arc ribbons + a short pulse of the OmniLight and of the
/// crystal's emission, a quiet crackle, rarely a big strike up into the sky with a delayed thunder). Now and then a small dark cloud forms just over the crystals and rains on them; while it hangs the strikes come
/// faster. The cloud stays (cloud_permanent) or follows a pure function of time (<see cref="NaturalCloud"/>), not a state machine, so a weather system can later just set <see cref="CloudForce"/> (1 on, 0 off, -1 by itself).
/// Tunables: data/crystals.json "thundernite".
/// </summary>
public sealed partial class ThundernitFx : CrystalFx
{
    public override string Id => "thundernite";

    sealed class Arc
    {
        public MeshInstance3D Outer = null!, Core = null!;
        public ImmediateMesh OuterMesh = null!, CoreMesh = null!;
        public double Life, Refresh;
        public Vector3 A, B;
        public float Jitter;
    }

    const int MaxArcs = 5, MaxPuffs = 14;

    /// <summary>1 = the cloud is up, 0 = it is not, -1 = it comes and goes by itself.</summary>
    public int CloudForce { get; set; } = -1;

    StandardMaterial3D _rock = null!, _outerMat = null!, _coreMat = null!, _cloudMat = null!;
    ShaderMaterial _crystal = null!;
    OmniLight3D _light = null!;
    readonly Arc[] _arcs = new Arc[MaxArcs];
    Node3D _cloudNode = null!;
    readonly MeshInstance3D[] _puffs = new MeshInstance3D[MaxPuffs];
    CpuParticles3D _rain = null!;
    double _next = 1.0;
    float _pulse, _pulseMult = 1f, _cloud;
    bool _lightMoved;

    protected override void Build()
    {
        _rock = CrystalAssets.Rock(Colors.White, 0.75f);
        _crystal = Quartz();
        Body.SetSurfaceOverrideMaterial(0, _rock);
        Body.SetSurfaceOverrideMaterial(1, _crystal);

        _light = new OmniLight3D { Name = "StormLight", ShadowEnabled = false };
        AddChild(_light);

        _outerMat = ArcMaterial();
        _coreMat = ArcMaterial();
        for (var i = 0; i < MaxArcs; i++)
        {
            var a = new Arc { OuterMesh = new ImmediateMesh(), CoreMesh = new ImmediateMesh() };
            a.Outer = new MeshInstance3D { Name = $"ArcOuter{i}", Mesh = a.OuterMesh, MaterialOverride = _outerMat, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            a.Core = new MeshInstance3D { Name = $"ArcCore{i}", Mesh = a.CoreMesh, MaterialOverride = _coreMat, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(a.Outer);
            AddChild(a.Core);
            _arcs[i] = a;
        }

        _cloudNode = new Node3D { Name = "Cloud", Visible = false };
        AddChild(_cloudNode);
        _cloudMat = new StandardMaterial3D { Roughness = 1f, EmissionEnabled = true, Emission = new Color(1f, 0.85f, 0.35f), EmissionEnergyMultiplier = 0f };
        for (var i = 0; i < MaxPuffs; i++)
        {
            _puffs[i] = new MeshInstance3D { Name = $"Puff{i}", Mesh = new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 10, Rings = 5, Material = _cloudMat }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            _cloudNode.AddChild(_puffs[i]);
        }
        _rain = new CpuParticles3D
        {
            Name = "Rain", Visible = false, Emitting = false, LocalCoords = false, Randomness = 0.6f,
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Box,
            Mesh = new BoxMesh { Size = new Vector3(0.002f, 0.04f, 0.002f), Material = ParticleMaterial(false) },
            Direction = Vector3.Down, Spread = 2f, Gravity = Vector3.Zero,
        };
        _cloudNode.AddChild(_rain);
    }

    static StandardMaterial3D ArcMaterial() => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        BlendMode = BaseMaterial3D.BlendModeEnum.Add, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    protected override void OnApply()
    {
        _rock.AlbedoColor = P.C("rock_tint", "#5a6070");
        _rock.Roughness = P.F("rock_roughness", 0.75f);
        ApplyQuartz(_crystal, new[] { "#ffe14a" }, "#fff2b0");
        _crystal.SetShaderParameter("glow_opacity", P.F("glow_opacity", 0.15f));

        var e = P.F("arc_energy", 4f);
        var arc = P.C("arc_color", "#bfd8ff");
        var core = P.C("arc_core_color", "#ffffff");
        _outerMat.AlbedoColor = new Color(arc.R * e, arc.G * e, arc.B * e, 0.55f);
        _coreMat.AlbedoColor = new Color(core.R * e, core.G * e, core.B * e, 1f);

        _light.LightColor = P.C("light", "#9cc0ff");
        _light.OmniRange = P.F("light_range", 2.5f);
        if (!_lightMoved) _light.Position = At(P.V("light_at", new Vector3(0.5f, 0.8f, 0.5f)));

        _cloudMat.AlbedoColor = P.C("cloud_color", "#3a3d44");
        var rain = P.C("rain_color", "#9fb4cc");
        if (_rain.Mesh is BoxMesh bm) bm.Size = new Vector3(0.002f, P.F("rain_length", 0.04f), 0.002f);
        var speed = P.F("rain_speed", 1.6f);
        var fall = P.F("cloud_height", 0.4f) + Box.Size.Y * 0.6f;
        _rain.Lifetime = Math.Max(fall / Math.Max(speed, 0.1f), 0.1f);
        _rain.InitialVelocityMin = speed * 0.9f;
        _rain.InitialVelocityMax = speed * 1.1f;
        var r = P.F("cloud_radius", 0.3f) * 0.8f;
        _rain.EmissionBoxExtents = new Vector3(r, 0.01f, r);
        _rain.ColorRamp = Ramp(new Color(rain, 0.7f), new Color(rain, 0.7f), new Color(rain, 0f));
        ParticleFx.SetAmount(_rain, Math.Max(P.I("rain_amount", 60), 1));
    }

    protected override void Tick(double dt)
    {
        var d = (float)dt;
        _pulse = Math.Max(0f, _pulse - d / Math.Max(P.F("light_pulse_time", 0.09f), 0.01f));
        if (_pulse <= 0f && _lightMoved)
        {
            _lightMoved = false;
            _light.Position = At(P.V("light_at", new Vector3(0.5f, 0.8f, 0.5f)));
        }

        UpdateCloud(d);

        // strikes: faster while the cloud hangs
        var rate = 1f + (P.F("cloud_arc_mult", 2f) - 1f) * _cloud;
        _next -= dt * rate;
        if (_next <= 0)
        {
            Strike(Rng.NextDouble() < P.F("big_chance", 0.12f));
            _next = Rand(P.F("arc_interval_min", 0.5f), Math.Max(P.F("arc_interval_max", 2f), P.F("arc_interval_min", 0.5f)));
        }

        foreach (var a in _arcs)
        {
            if (a.Life <= 0) continue;
            a.Life -= dt;
            if (a.Life <= 0) { a.Outer.Visible = false; a.Core.Visible = false; continue; }
            a.Refresh -= dt;
            if (a.Refresh <= 0) { Rebuild(a); a.Refresh = P.F("arc_refresh", 0.04f); }
        }

        _light.LightEnergy = P.F("light_base", 1f) * DemonFx.Flicker(Time, 7.7f, P.F("flicker_low", 0.75f), P.F("flicker_high", 1.15f)) + _pulse * P.F("light_pulse", 1.6f) * _pulseMult;
        _crystal.SetShaderParameter("glow", P.F("base_glow", 2f) + _pulse * P.F("emission_pulse", 6f));
        _cloudMat.EmissionEnergyMultiplier = _pulse * 3f;
    }

    // ---- cloud ----

    static double Hash(double n)
    {
        var s = Math.Sin(n * 12.9898) * 43758.5453;
        return s - Math.Floor(s);
    }

    /// <summary>1 while the cloud is meant to hang at time <paramref name="t"/>, else 0: each cycle (gap + life) has one cloud, its start and length randomised per cycle.</summary>
    float NaturalCloud(double t)
    {
        var gap = P.F("cloud_gap", 45f);
        var life = P.F("cloud_life", 28f);
        var period = Math.Max(gap + life, 1f);
        var k = Math.Floor(t / period);
        var u = t - k * period;
        var len = life * (0.7 + 0.6 * Hash(k));
        var start = Math.Max(period - len, 0) * Hash(k + 7.3);
        return u >= start && u < start + len ? 1f : 0f;
    }

    void UpdateCloud(float d)
    {
        var enabled = P.B("cloud_enabled", true);
        var target = !enabled ? 0f : CloudForce switch { 1 => 1f, 0 => 0f, _ => P.B("cloud_permanent", true) ? 1f : NaturalCloud(Time) };
        _cloud = Mathf.MoveToward(_cloud, target, d / Math.Max(P.F("cloud_fade", 3f), 0.1f));
        _cloudNode.Visible = _cloud > 0.001f;
        if (!_cloudNode.Visible) { ParticleFx.Run(_rain, false); SetLoop("", 0f); return; }

        var height = P.F("cloud_height", 0.4f);
        var radius = P.F("cloud_radius", 0.3f);
        _cloudNode.Position = At(new Vector3(0.5f, 1f, 0.5f)) + new Vector3(0, height, 0);
        var n = Math.Clamp(P.I("cloud_puffs", 8), 1, MaxPuffs);
        for (var i = 0; i < MaxPuffs; i++)
        {
            var puff = _puffs[i];
            puff.Visible = i < n;
            if (i >= n) continue;
            var ang = i * 2.39996f;
            var r = Mathf.Sqrt((i + 0.5f) / n) * radius * 0.75f;
            var size = radius * (0.55f + 0.35f * (float)Hash(i + 1)) * _cloud;
            puff.Position = new Vector3(Mathf.Cos(ang) * r, Mathf.Sin((float)Time * 0.7f + i) * 0.012f + (i % 3) * 0.02f, Mathf.Sin(ang) * r);
            puff.Scale = new Vector3(size, size * 0.65f, size);
        }
        var raining = _cloud > 0.35f;
        ParticleFx.Run(_rain, raining);
        SetLoop(raining ? P.S("rain_sound", "") : "", P.F("rain_db", -20f));
    }

    // ---- lightning ----

    /// <summary>Forces a strike now (the lab's T key).</summary>
    public void Zap(bool big) => Strike(big);

    void Strike(bool big)
    {
        var count = big ? 1 : Rng.Next(1, Math.Max(P.I("arc_max_simultaneous", 3), 1) + 1);
        var minLen = P.F("arc_min_length", 0.18f);
        Vector3 mid = default;
        var any = false;
        for (var n = 0; n < count; n++)
        {
            var a = _arcs.FirstOrDefault(x => x.Life <= 0);
            if (a == null) break;
            Vector3 p, q;
            var tries = 0;
            do
            {
                // from the tip of one crystal to the tip of another, or down to the rock
                p = Tip(Rng.Next(Crystals));
                if (big) q = p + new Vector3(Rand(-0.1f, 0.1f), P.F("big_length", 0.7f), Rand(-0.1f, 0.1f));
                else if (Rng.NextDouble() < 0.25 && RockSpot(Rng.Next(1000)) is { } rock) q = rock.P + rock.N * 0.01f;
                else q = Tip(Rng.Next(Crystals));
            } while (p.DistanceTo(q) < minLen && ++tries < 8);
            a.A = p;
            a.B = q;
            a.Jitter = P.F("arc_jitter", 0.05f) * (big ? 2.2f : 1f);
            a.Life = P.F("arc_life", 0.16f) * (big ? 1.6f : Rand(0.6f, 1f));
            a.Refresh = 0;
            Rebuild(a);
            a.Outer.Visible = true;
            a.Core.Visible = true;
            mid += (p + q) * 0.5f;
            any = true;
        }
        if (!any) return;
        mid /= count;
        _pulse = 1f;
        _pulseMult = big ? P.F("big_light_mult", 3f) : 1f;
        _light.Position = mid;
        _lightMoved = true;
        Sound(P.S("crackle_sound", ""), mid, P.F("crackle_db", -14f));
        if (big && P.S("thunder_sound", "") is { Length: > 0 } thunder && IsInsideTree())
        {
            var db = P.F("thunder_db", -20f);
            GetTree().CreateTimer(Rand(0.25f, 0.6f)).Timeout += () => { if (IsInsideTree()) Sound(thunder, mid, db); };
        }
    }

    void Rebuild(Arc a)
    {
        var segs = Math.Max(P.I("arc_segments", 9), 2);
        var pts = new Vector3[segs + 1];
        for (var i = 0; i <= segs; i++)
        {
            var t = i / (float)segs;
            var taper = Mathf.Sin(t * Mathf.Pi);
            pts[i] = a.A.Lerp(a.B, t) + new Vector3(Rand(-1f, 1f), Rand(-1f, 1f), Rand(-1f, 1f)) * a.Jitter * taper;
        }
        var w = P.F("arc_width", 0.006f);
        Ribbon(a.OuterMesh, pts, w * 3f);
        Ribbon(a.CoreMesh, pts, w);
    }

    /// <summary>A ribbon of two crossed quads along the points, so it shows from any side without billboarding.</summary>
    static void Ribbon(ImmediateMesh m, Vector3[] pts, float w)
    {
        m.ClearSurfaces();
        m.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        for (var i = 0; i < pts.Length - 1; i++)
        {
            var dir = (pts[i + 1] - pts[i]).Normalized();
            var u = dir.Cross(Vector3.Up);
            if (u.LengthSquared() < 1e-4f) u = dir.Cross(Vector3.Right);
            u = u.Normalized() * w;
            var v = dir.Cross(u).Normalized() * w;
            Quad(m, pts[i], pts[i + 1], u);
            Quad(m, pts[i], pts[i + 1], v);
        }
        m.SurfaceEnd();
    }

    static void Quad(ImmediateMesh m, Vector3 a, Vector3 b, Vector3 o)
    {
        m.SurfaceAddVertex(a - o); m.SurfaceAddVertex(a + o); m.SurfaceAddVertex(b + o);
        m.SurfaceAddVertex(a - o); m.SurfaceAddVertex(b + o); m.SurfaceAddVertex(b - o);
    }
}
