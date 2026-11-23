using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// A fantasy crystal (crystals.md): the pack's crystal rock (<c>SM_Prop_Crystal_Rock_A_01</c>, about 0.73 x 1.15 x 0.88 m) re-dressed with its own material, light, particles, props and sound.
/// Pure client look, nothing on the wire. One subclass per crystal; <see cref="Build"/> makes the nodes once, <see cref="OnApply"/> pushes the tunables of data/crystals.json (called again whenever the file
/// changes in the lab), <see cref="Tick"/> runs every frame. The node's origin is the middle of the base on the ground.
/// </summary>
public abstract partial class CrystalFx : Node3D
{
    public static readonly string[] Ids = { "pheonixite", "thundernite", "smetiskomite", "rosaline", "frostbite", "futbalitrine", "povodnovite", "kulturite", "bloodstone", "magmorite", "venomite", "zombite", "nazium", "holystone", "brothers_stone", "senin", "dubovite", "hovnovite", "micin", "marihuanite" };

    public abstract string Id { get; }

    protected CrystalParams P = CrystalParams.Empty;
    protected MeshInstance3D Body = null!;
    /// <summary>The model's own bounds (what the shaders see as object space).</summary>
    protected Aabb MeshBox;
    /// <summary>The bounds in this node's space, with the base lifted to y = 0.</summary>
    protected Aabb Box;
    /// <summary>Model space to this node's space (the base lifted to y = 0).</summary>
    protected Vector3 Lift;
    protected double Time;
    protected readonly Random Rng = new(20260518);

    LoopVoice? _loop;
    string _loopId = "";
    float _loopDb;

    public static CrystalFx? Make(string id) => id switch
    {
        "pheonixite" => new PheonixiteFx(),
        "thundernite" => new ThundernitFx(),
        "smetiskomite" => new SmetiskomiteFx(),
        "rosaline" => new RosalineFx(),
        "frostbite" => new FrostbiteFx(),
        "futbalitrine" => new FutbalitrineFx(),
        "povodnovite" => new PovodnoviteFx(),
        "kulturite" => new KulturiteFx(),
        "bloodstone" => new BloodstoneFx(),
        "magmorite" => new MagmoriteFx(),
        "venomite" => new VenomiteFx(),
        "zombite" => new ZombiteFx(),
        "nazium" => new NaziumFx(),
        "holystone" => new HolystoneFx(),
        "brothers_stone" => new BrothersStoneFx(),
        "senin" => new SeninFx(),
        "dubovite" => new DuboviteFx(),
        "hovnovite" => new HovnoviteFx(),
        "micin" => new MicinFx(),
        "marihuanite" => new MarihuaniteFx(),
        _ => null,
    };

    public override void _Ready()
    {
        Body = CrystalAssets.MakeBody();
        AddChild(Body);
        MeshBox = Body.GetAabb();
        Lift = new Vector3(0, -MeshBox.Position.Y, 0);
        Body.Position = Lift;
        Box = new Aabb(MeshBox.Position + Lift, MeshBox.Size);
        Build();
        Apply(CrystalData.Get(Id));
    }

    public void Apply(CrystalParams p)
    {
        P = p;
        OnApply();
    }

    protected abstract void Build();
    protected abstract void OnApply();
    protected abstract void Tick(double dt);

    public override void _Process(double delta)
    {
        Time += delta;
        Tick(delta);
        _loop?.Update(GlobalPosition + Box.GetCenter(), 1f, _loopDb);
    }

    public override void _ExitTree()
    {
        _loop?.Release();
        _loop = null;
    }

    protected static CrystalInfo Info => CrystalAssets.Info;
    protected int Crystals => Math.Max(Info.Tips.Count, 1);

    /// <summary>Tip of crystal <paramref name="i"/> (wraps) in this node's space.</summary>
    protected Vector3 Tip(int i) => Info.Tips.Count == 0 ? At(new Vector3(0.5f, 1f, 0.5f)) : Info.Tips[((i % Info.Tips.Count) + Info.Tips.Count) % Info.Tips.Count] + Lift;

    /// <summary>A repeatable pick from a list by a seed from crystals.json (change the seed to move the prop to another spot).</summary>
    protected static T Pick<T>(List<T> list, int seed) => list[(int)(unchecked((uint)seed * 2654435761u) % (uint)list.Count)];

    /// <summary>A face of the rock base looking up (position in this node's space, normal), or null if the model has none.</summary>
    protected (Vector3 P, Vector3 N)? RockSpot(int seed) => Info.RockUp.Count == 0 ? null : Pick(Info.RockUp, seed) is var (p, n) ? (p + Lift, n) : null;

    /// <summary>A face of any crystal (position in this node's space, normal), or null.</summary>
    protected (Vector3 P, Vector3 N)? CrystalSpot(int seed) => Info.Facets.Count == 0 ? null : Pick(Info.Facets, seed) is var (p, n, _) ? (p + Lift, n) : null;

    /// <summary>The orientation of something standing on a surface with this normal, turned by <paramref name="yaw"/> about the normal.</summary>
    protected static Basis OnSurface(Vector3 normal, float yaw)
    {
        var n = normal.Normalized();
        var t = Math.Abs(n.Y) > 0.99f ? Vector3.Right : Vector3.Up.Cross(n).Normalized();
        return new Basis(t, n, t.Cross(n)).Rotated(n, yaw);
    }

    /// <summary>A repeatable number in [0..1) from a number (placement and timing that must not change from frame to frame).</summary>
    protected static float Hash01(double n)
    {
        var s = Math.Sin(n * 12.9898 + 0.37) * 43758.5453;
        return (float)(s - Math.Floor(s));
    }

    /// <summary>The flattest up-facing face of the rock among the ones the seed tries (for things that lie or stand on it), or null.</summary>
    protected (Vector3 P, Vector3 N)? FlatRock(int seed)
    {
        (Vector3 P, Vector3 N)? flat = null;
        for (var k = 0; k < 40; k++)
            if (RockSpot(seed + k * 11) is { } s && (flat is null || s.N.Y > flat.Value.N.Y)) flat = s;
        return flat;
    }

    /// <summary>A thin disc of radius 1 (puddles, stains): scale and place it with <see cref="PlaceDisc"/>.</summary>
    protected MeshInstance3D Disc(string name, Material mat)
    {
        var m = new MeshInstance3D { Name = name, Mesh = new CylinderMesh { TopRadius = 1f, BottomRadius = 1f, Height = 0.002f, RadialSegments = 16, Material = mat }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(m);
        return m;
    }

    protected static void PlaceDisc(MeshInstance3D m, (Vector3 P, Vector3 N) spot, float rx, float rz, float yaw) =>
        m.Transform = new Transform3D(OnSurface(spot.N, yaw) * Basis.FromScale(new Vector3(rx, 1f, rz)), spot.P + spot.N * 0.002f);

    /// <summary>Where drops hang: the tips of the crystals and the faces that look down (this node's space).</summary>
    protected List<Vector3> DripPoints(bool undersides = true)
    {
        var from = new List<Vector3>();
        if (undersides) foreach (var (p, n, _) in Info.Facets) if (n.Y < -0.2f) from.Add(p + Lift);
        for (var i = 0; i < Info.Tips.Count; i++) from.Add(Tip(i));
        return from;
    }

    /// <summary>The opaque veined crystal material (shaders/crystal_veined.gdshader).</summary>
    protected static ShaderMaterial Veined() => new() { Shader = GD.Load<Shader>("res://shaders/crystal_veined.gdshader") };

    /// <summary>Pushes the veined tunables of crystals.json (tint0..3, tint_count, vein_*, mottle, grain, speck*, crystal_roughness, base_glow) over the defaults given.</summary>
    protected void ApplyVeined(ShaderMaterial m, string[] tints, string vein, float veinAmount, float veinGlow, float rough, float grain = 0f, float speck = 0f, string speckColor = "#ffffff")
    {
        m.SetShaderParameter("tint_count", Math.Clamp(P.I("tint_count", tints.Length), 1, 4));
        for (var i = 0; i < 4; i++) m.SetShaderParameter($"tint{i}", P.C($"tint{i}", tints[i % tints.Length]));
        m.SetShaderParameter("vein_color", P.C("vein_color", vein));
        m.SetShaderParameter("vein_amount", P.F("vein_amount", veinAmount));
        m.SetShaderParameter("vein_scale", P.F("vein_scale", 6f));
        m.SetShaderParameter("vein_width", P.F("vein_width", 0.05f));
        m.SetShaderParameter("vein_glow", P.F("vein_glow", veinGlow));
        m.SetShaderParameter("vein_scroll", P.F("vein_scroll", 0f));
        m.SetShaderParameter("mottle", P.F("mottle", 0.25f));
        m.SetShaderParameter("grain", P.F("grain", grain));
        m.SetShaderParameter("grain_freq", P.F("grain_freq", 60f));
        m.SetShaderParameter("speck", P.F("speck", speck));
        m.SetShaderParameter("speck_color", P.C("speck_color", speckColor));
        m.SetShaderParameter("speck_glow", P.F("speck_glow", 0f));
        m.SetShaderParameter("rough", P.F("crystal_roughness", rough));
        m.SetShaderParameter("glow", P.F("base_glow", 0f));
    }

    /// <summary>The rock base with a growth on the faces that look up: moss, snow, turf (shaders/crystal_rock_moss.gdshader).</summary>
    protected static ShaderMaterial MossRock()
    {
        var m = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/crystal_rock_moss.gdshader") };
        if (CrystalAssets.Atlas() is { } atlas) { m.SetShaderParameter("atlas", atlas); m.SetShaderParameter("use_atlas", true); }
        return m;
    }

    /// <summary>Pushes the tunables of <see cref="MossRock"/>: rock_tint, rock_roughness and {key}_color, {key}_light, {key}_amount, {key}_slope, {key}_scale.</summary>
    protected void ApplyMossRock(ShaderMaterial m, string key, string rock, string dark, string light, float amount = 1f, float scale = 14f)
    {
        m.SetShaderParameter("tint", P.C("rock_tint", rock));
        m.SetShaderParameter("rough", P.F("rock_roughness", 0.9f));
        m.SetShaderParameter("moss", P.C(key + "_color", dark));
        m.SetShaderParameter("moss_light", P.C(key + "_light", light));
        m.SetShaderParameter("moss_amount", P.F(key + "_amount", amount));
        m.SetShaderParameter("moss_slope", P.F(key + "_slope", 0.35f));
        m.SetShaderParameter("moss_scale", P.F(key + "_scale", scale));
    }

    /// <summary>The <paramref name="n"/> tallest crystals of the rock, tallest first.</summary>
    protected int[] Tallest(int n) => Enumerable.Range(0, Info.Tips.Count).OrderByDescending(i => Info.Tips[i].Y).Take(Math.Max(n, 0)).ToArray();

    /// <summary>
    /// A helix wound about crystal <paramref name="isl"/>: about the crystal's own axis (middle of its foot to its tip), at the crystal's width at that height plus <paramref name="pad"/>.
    /// Returns the point at t in [0..1] (foot to tip, this node's space) and the outward direction there; null for a crystal too small to measure.
    /// </summary>
    protected (Func<float, Vector3> Pt, Func<float, Vector3> Out)? Helix(int isl, float turns, float pad)
    {
        var pts = Info.Facets.Where(f => f.Island == isl).Select(f => f.P).ToList();
        if (pts.Count < 4) return null;
        var tip = Info.Tips[isl];
        var minY = pts.Min(p => p.Y);
        var low = pts.Where(p => p.Y <= minY + (tip.Y - minY) * 0.2f).ToList();
        var foot = low.Aggregate(Vector3.Zero, (a, p) => a + p) / low.Count;
        var axis = tip - foot;
        var len2 = axis.LengthSquared();
        if (len2 < 1e-4f) return null;
        const int bins = 8;
        var rad = new float[bins];
        foreach (var p in pts)
        {
            var t = Mathf.Clamp((p - foot).Dot(axis) / len2, 0f, 1f);
            var b = Math.Min(bins - 1, (int)(t * bins));
            rad[b] = Math.Max(rad[b], (p - (foot + axis * t)).Length());
        }
        for (var b = 1; b < bins; b++) if (rad[b] == 0f) rad[b] = rad[b - 1] * 0.8f;
        float R(float t)
        {
            var x = Mathf.Clamp(t * bins - 0.5f, 0f, bins - 1.001f);
            var b = (int)x;
            return Mathf.Lerp(rad[b], rad[b + 1], x - b) * 1.2f + pad;
        }
        var dir = axis.Normalized();
        var e1 = dir.Cross(Vector3.Right).LengthSquared() > 0.01f ? dir.Cross(Vector3.Right).Normalized() : dir.Cross(Vector3.Back).Normalized();
        var e2 = dir.Cross(e1);
        var ph = isl * 1.7f;
        var lift = Lift;
        Vector3 Out(float t) => e1 * Mathf.Cos(t * turns * Mathf.Tau + ph) + e2 * Mathf.Sin(t * turns * Mathf.Tau + ph);
        Vector3 Pt(float t) => foot + axis * t + Out(t) * R(t) + lift;
        return (Pt, Out);
    }

    /// <summary>The translucent per-crystal quartz material (shaders/crystal_quartz.gdshader).</summary>
    protected static ShaderMaterial Quartz() => new() { Shader = GD.Load<Shader>("res://shaders/crystal_quartz.gdshader") };

    /// <summary>Pushes the quartz tunables of crystals.json: tint0..tint7 (cycled over the crystals, tint_count of them), alpha, rim, inclusions, roughness, glow.</summary>
    protected void ApplyQuartz(ShaderMaterial m, string[] tints, string inclusion)
    {
        m.SetShaderParameter("tint_count", Math.Clamp(P.I("tint_count", tints.Length), 1, 8));
        for (var i = 0; i < 8; i++) m.SetShaderParameter($"tint{i}", P.C($"tint{i}", tints[i % tints.Length]));
        m.SetShaderParameter("inclusion_color", P.C("inclusion_color", inclusion));
        m.SetShaderParameter("inclusion", P.F("inclusion", 0.4f));
        m.SetShaderParameter("inclusion_scale", P.F("inclusion_scale", 9f));
        m.SetShaderParameter("alpha", P.F("crystal_alpha", 0.55f));
        m.SetShaderParameter("rim_alpha", P.F("rim_alpha", 0.4f));
        m.SetShaderParameter("rim_power", P.F("rim_power", 2.5f));
        m.SetShaderParameter("rough", P.F("crystal_roughness", 0.08f));
    }

    /// <summary>A point in the crystal's box by fractions [0..1] of its size (x, y up, z).</summary>
    protected Vector3 At(Vector3 frac) => Box.Position + frac * Box.Size;

    protected float Rand(float lo, float hi) => lo + (float)Rng.NextDouble() * (hi - lo);

    /// <summary>The looping sound of the crystal (an id of sounds.json, "" = none); <paramref name="db"/> is the offset from the sound's own level.</summary>
    protected void SetLoop(string id, float db)
    {
        _loopDb = db;
        if (id == _loopId) return;
        _loop?.Release();
        _loop = null;
        _loopId = id;
        if (id != "" && Sfx.Instance != null) _loop = Sfx.Loop(id, GlobalPosition);
    }

    /// <summary>A quiet one-shot at a point of this crystal (an id of sounds.json, "" = none).</summary>
    protected void Sound(string id, Vector3 localAt, float db)
    {
        if (id != "") Sfx.Play3D(id, GlobalPosition + localAt, db);
    }

    // ---- particles: hidden until they emit (ParticleFx), unshaded, colour from the gradient ----

    protected static StandardMaterial3D ParticleMaterial(bool additive) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        BlendMode = additive ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    /// <summary>Makes the particles of an emitter grow (or shrink) over their life: smoke, steam, mist.</summary>
    protected static void Swell(CpuParticles3D p, float from, float to)
    {
        p.ScaleAmountCurve = new Curve();
        p.ScaleAmountCurve.AddPoint(new Vector2(0f, from));
        p.ScaleAmountCurve.AddPoint(new Vector2(1f, to));
    }

    /// <summary>Grass, straw or hay tufts on the up-facing faces of the rock: <paramref name="count"/> of the pool shown, placed by the seed.</summary>
    protected void PlaceTufts(MeshInstance3D[] pool, int count, int seed, float height, float flatten = 1f)
    {
        if (Info.RockUp.Count == 0) count = 0;
        for (var i = 0; i < pool.Length; i++)
        {
            pool[i].Visible = i < count;
            if (i >= count) continue;
            var s = RockSpot(seed + i * 17)!.Value;
            var h = height * (0.7f + 0.6f * Hash01(i * 1.3 + seed));
            pool[i].Transform = new Transform3D(OnSurface(s.N, i * 2.4f) * Basis.FromScale(new Vector3(h, h * flatten, h)), s.P);
        }
    }

    /// <summary>A pool of tufts in two shades (root colour to tip colour), hidden until <see cref="PlaceTufts"/>.</summary>
    protected MeshInstance3D[] Tufts(int max, Color root, Color tip)
    {
        var mat = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.9f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        var meshes = new[] { SmetiskomiteFx.TuftMesh(mat, 0, root, tip), SmetiskomiteFx.TuftMesh(mat, 1, root, tip.Lightened(0.12f)), SmetiskomiteFx.TuftMesh(mat, 2, root.Darkened(0.2f), tip) };
        var pool = new MeshInstance3D[max];
        for (var i = 0; i < max; i++)
        {
            pool[i] = new MeshInstance3D { Name = $"Tuft{i}", Mesh = meshes[i % meshes.Length], Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(pool[i]);
        }
        return pool;
    }

    protected static Gradient Ramp(Color a, Color b, Color c) => new() { Offsets = new[] { 0f, 0.5f, 1f }, Colors = new[] { a, b, c } };

    /// <summary>A world-space emitter of small spheres as a child of this crystal; configure it with <see cref="Configure"/>.</summary>
    protected CpuParticles3D Emitter(bool additive)
    {
        var p = new CpuParticles3D
        {
            Visible = false, Emitting = false, LocalCoords = false, Randomness = 1f,
            Mesh = new SphereMesh { RadialSegments = 6, Rings = 3, Material = ParticleMaterial(additive) },
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere,
        };
        AddChild(p);
        return p;
    }

    protected static void Configure(CpuParticles3D p, int amount, float life, Vector3 at, float radius, float size, float speed, Vector3 dir, float spread, float gravityY, Gradient ramp)
    {
        if (p.Mesh is SphereMesh s) { s.Radius = size; s.Height = size * 2f; }
        p.Lifetime = life;
        p.Position = at;
        p.EmissionSphereRadius = Math.Max(radius, 0.001f);
        p.Direction = dir;
        p.Spread = spread;
        p.InitialVelocityMin = speed * 0.5f;
        p.InitialVelocityMax = speed;
        p.Gravity = new Vector3(0, gravityY, 0);
        p.ColorRamp = ramp;
        p.ScaleAmountMin = 0.6f;
        p.ScaleAmountMax = 1.2f;
        ParticleFx.SetAmount(p, Math.Max(amount, 1));
        ParticleFx.Run(p, amount > 0);
    }
}
