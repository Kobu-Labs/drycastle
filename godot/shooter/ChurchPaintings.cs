using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The pictures on the church's walls (the <c>painting</c> props of church.json, drawn on the client only; the pipeline no longer bakes canvases into the mesh). Each is a thin
/// frame round a canvas quad at the prop's pose (the middle of the canvas on the wall face, facing into the room), and follows <see cref="ChurchInterior"/>.
/// <para>
/// Calm: a small picture painted in code (soft sky, hills, a cross on a hill or a haloed figure, a muted palette that differs per painting by its index), no asset.
/// Hell: <c>paintings/hell_01.png</c> .. <c>hell_09.png</c> under <see cref="GamePaths.PrivateAssets"/>, each painting taking the image whose aspect fits its canvas best (images are reused when there are more
/// paintings than images, and cropped to the canvas by UV, never stretched); a missing pack falls back to a red and black picture painted in code (an inverted cross, eyes). Both are drawn nearest-filtered.
/// </para>
/// <para>
/// The turn (<see cref="SetDemon"/>): not an instant swap. From the moment the demon appears each canvas burns through to the hell picture over <see cref="BurnS"/> seconds, starting at its own time in the first
/// <see cref="SpreadS"/> seconds (a shader: a noise threshold sweeps over the canvas, the old picture chars in front of it, an ember edge glows on it). Burnt, it pulses a faint red glow of its own so it reads in the
/// dark church. When the demon is gone the canvases return to the calm pictures in <see cref="ReturnS"/> seconds. A joiner who finds the demon mid-fight sees the burnt end state at once.
/// </para>
/// </summary>
public partial class ChurchPaintings : Node3D
{
    /// <summary>The canvases start burning at staggered times, the first after <see cref="FirstS"/> and the last <see cref="SpreadS"/> seconds after the demon appeared; one takes <see cref="BurnS"/> to burn through.</summary>
    public const float FirstS = 0.5f, SpreadS = 11f, BurnS = 4f;
    /// <summary>Seconds to return to the calm picture when the demon is gone.</summary>
    public const float ReturnS = 1.5f;
    /// <summary>Strength (0..1) and speed (rad/s) of the red glow of a burnt canvas.</summary>
    public const float PulseGlow = 0.55f, PulseRate = 1.8f;
    /// <summary>The frame: its width, its depth off the wall; the canvas sits <see cref="CanvasDepthM"/> off the wall, inside the frame.</summary>
    const float FrameW = 0.07f, FrameDepthM = 0.06f, CanvasDepthM = 0.025f;
    /// <summary>Texels per metre of a painted picture (low, so it reads as coarse as the hell pack).</summary>
    const float PxPerM = 56f;
    const int HellImages = 9;
    static readonly Color Gilt = new(0.55f, 0.42f, 0.16f), Charred = new(0.17f, 0.09f, 0.06f);

    public static ChurchPaintings? Current { get; private set; }

    sealed class Canvas
    {
        public ShaderMaterial Mat = null!;
        public StandardMaterial3D Frame = null!;
        public float Delay, Phase, Burn, Applied = -1;
    }

    readonly List<Canvas> _canvases = new();
    bool _demon;
    float _since;
    double _t;

    /// <summary>The paintings of the church.json in <paramref name="mapDir"/>, or null when the map has none (or the file cannot be read: the paintings are decoration).</summary>
    public static ChurchPaintings? Create(string mapDir)
    {
        var path = Path.Combine(mapDir, "church.json");
        if (!File.Exists(path)) return null;
        Church church;
        try { church = Church.Load(path); }
        catch (Exception e) { GD.PushWarning($"ChurchPaintings: church.json not read ({e.Message}); no paintings"); return null; }
        if (church.Paintings.Count == 0) return null;
        var node = new ChurchPaintings { Name = "ChurchPaintings" };
        node.Build(church.Paintings);
        return node;
    }

    public override void _EnterTree()
    {
        Current = this;
        Visible = ChurchInterior.Visible;
        ChurchInterior.Changed += OnInterior;
    }

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
        ChurchInterior.Changed -= OnInterior;
    }

    void OnInterior(bool visible) => Visible = visible;

    // ---- build ----

    static Shader? _shader;

    void Build(IReadOnlyList<ChurchPainting> paintings)
    {
        var hell = LoadHell();
        var uses = new int[hell.Count];
        var order = Enumerable.Range(0, paintings.Count).OrderBy(i => new Random(4242 + i * 31).Next()).ToList(); // who burns when: a fixed shuffle
        for (var i = 0; i < paintings.Count; i++)
        {
            var p = paintings[i];
            var aspect = p.W / p.H;
            // the image with the nearest aspect, a little less keen on one already used
            var best = -1;
            var bestCost = float.MaxValue;
            for (var k = 0; k < hell.Count; k++)
            {
                var cost = MathF.Abs(MathF.Log(aspect / hell[k].Aspect)) + 0.2f * uses[k];
                if (cost < bestCost) { bestCost = cost; best = k; }
            }
            Texture2D hellTex;
            var imgAspect = aspect;
            if (best >= 0) { uses[best]++; hellTex = hell[best].Tex; imgAspect = hell[best].Aspect; }
            else hellTex = HellTexture(i, p.W, p.H);

            var scale = Vector2.One;
            var offset = Vector2.Zero;
            if (imgAspect > aspect) { scale.X = aspect / imgAspect; offset.X = (1f - scale.X) / 2f; } // a wider picture loses its sides
            else { scale.Y = imgAspect / aspect; offset.Y = (1f - scale.Y) / 2f; }

            var c = new Canvas
            {
                Delay = FirstS + (SpreadS - FirstS) * (paintings.Count > 1 ? order.IndexOf(i) / (float)(paintings.Count - 1) : 0f),
                Phase = i * 1.9f,
                Mat = new ShaderMaterial { Shader = _shader ??= new Shader { Code = ShaderCode } },
                Frame = new StandardMaterial3D { AlbedoColor = Gilt, Roughness = 0.6f, Metallic = 0.3f },
            };
            c.Mat.SetShaderParameter("calm_tex", CalmTexture(i, p.W, p.H));
            c.Mat.SetShaderParameter("hell_tex", hellTex);
            c.Mat.SetShaderParameter("hell_scale", scale);
            c.Mat.SetShaderParameter("hell_offset", offset);
            c.Mat.SetShaderParameter("seed", i * 3.7f);
            _canvases.Add(c);

            // local +Z is the way it faces (a quad's front); Godot yaw 0 faces -Z
            var pivot = new Node3D { Name = $"Painting{i}", Position = new Godot.Vector3(p.Pos.X, p.Pos.Y, p.Pos.Z), Rotation = new Godot.Vector3(0, p.Yaw + Mathf.Pi, 0) };
            AddChild(pivot);
            pivot.AddChild(new MeshInstance3D
            {
                Mesh = new QuadMesh { Size = new Vector2(p.W, p.H), Material = c.Mat }, Position = new Godot.Vector3(0, 0, CanvasDepthM),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
            foreach (var (size, at) in new[]
            {
                (new Godot.Vector3(p.W + 2 * FrameW, FrameW, FrameDepthM), new Godot.Vector3(0, (p.H + FrameW) / 2, FrameDepthM / 2)),
                (new Godot.Vector3(p.W + 2 * FrameW, FrameW, FrameDepthM), new Godot.Vector3(0, -(p.H + FrameW) / 2, FrameDepthM / 2)),
                (new Godot.Vector3(FrameW, p.H, FrameDepthM), new Godot.Vector3((p.W + FrameW) / 2, 0, FrameDepthM / 2)),
                (new Godot.Vector3(FrameW, p.H, FrameDepthM), new Godot.Vector3(-(p.W + FrameW) / 2, 0, FrameDepthM / 2)),
            })
                pivot.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size, Material = c.Frame }, Position = at, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        }
    }

    /// <summary>The hell pictures that exist in the private pack (any subset; none: empty).</summary>
    static List<(ImageTexture Tex, float Aspect)> LoadHell()
    {
        var list = new List<(ImageTexture, float)>();
        for (var n = 1; n <= HellImages; n++)
        {
            var path = Path.Combine(GamePaths.PrivateAssets(), "paintings", $"hell_{n:00}.png");
            if (!File.Exists(path)) continue;
            var img = Image.LoadFromFile(path);
            if (img == null || img.GetWidth() < 2 || img.GetHeight() < 2) continue;
            list.Add((ImageTexture.CreateFromImage(img), img.GetWidth() / (float)img.GetHeight()));
        }
        if (list.Count == 0) GD.Print("ChurchPaintings: paintings/hell_*.png not found in the private assets; hellish pictures are painted in code");
        return list;
    }

    // ---- the turn ----

    /// <summary>The demon is in the church (its entity exists) or is gone. <paramref name="snap"/>: a joiner who finds it mid-fight sees every canvas burnt at once.</summary>
    public void SetDemon(bool active, bool snap = false)
    {
        if (active && !_demon) _since = 0;
        if (!active) _since = 0;
        _demon = active;
        if (active && snap) { _since = 1000f; foreach (var c in _canvases) c.Burn = 1; }
    }

    public override void _Process(double delta)
    {
        var dt = (float)delta;
        _t += delta;
        if (_demon) _since += dt;
        foreach (var c in _canvases)
        {
            c.Burn = _demon ? MathF.Max(c.Burn, Mathf.Clamp((_since - c.Delay) / BurnS, 0f, 1f)) : Mathf.MoveToward(c.Burn, 0f, dt / ReturnS);
            if (!Visible || (c.Burn == 0f && c.Applied == 0f)) continue;
            c.Applied = c.Burn;
            var pulse = Mathf.SmoothStep(0.75f, 1f, c.Burn) * PulseGlow * (0.55f + 0.45f * MathF.Sin((float)_t * PulseRate + c.Phase));
            c.Mat.SetShaderParameter("burn", c.Burn);
            c.Mat.SetShaderParameter("pulse", pulse);
            c.Frame.AlbedoColor = Gilt.Lerp(Charred, c.Burn);
        }
    }

    /// <summary>
    /// calm_tex and hell_tex are mixed by a noise threshold that sweeps with <c>burn</c> (0 calm, 1 hell): where the noise is below the threshold the canvas is burnt (hell), just before it
    /// the calm picture is charred, and on the front itself an ember glows. A burnt canvas glows red by <c>pulse</c>, scaled by the picture so its shapes show in the dark.
    /// </summary>
    const string ShaderCode = @"
shader_type spatial;
render_mode specular_disabled;

uniform sampler2D calm_tex : source_color, filter_nearest;
uniform sampler2D hell_tex : source_color, filter_nearest;
uniform vec2 hell_scale = vec2(1.0, 1.0);
uniform vec2 hell_offset = vec2(0.0, 0.0);
uniform float burn = 0.0;
uniform float pulse = 0.0;
uniform float seed = 0.0;

float hash21(vec2 p) {
	p = fract(p * vec2(123.34, 456.21) + seed);
	p += dot(p, p + 45.32);
	return fract(p.x * p.y);
}

float vnoise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float a = hash21(i);
	float b = hash21(i + vec2(1.0, 0.0));
	float c = hash21(i + vec2(0.0, 1.0));
	float d = hash21(i + vec2(1.0, 1.0));
	return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

void fragment() {
	vec3 calm = texture(calm_tex, UV).rgb;
	vec3 hell = texture(hell_tex, UV * hell_scale + hell_offset).rgb;
	float n = 0.6 * vnoise(UV * 7.0) + 0.4 * vnoise(UV * 19.0 + vec2(3.7, 1.3));
	float t = mix(-0.2, 1.2, burn);
	float d = t - n;
	float burnt = smoothstep(0.0, 0.03, d);
	float charred = smoothstep(-0.3, 0.0, d);
	vec3 base = mix(calm * (1.0 - 0.8 * charred), hell, burnt);
	float q = (d - 0.02) / 0.05;
	float ember = exp(-q * q);
	ALBEDO = base;
	ROUGHNESS = 0.95;
	EMISSION = vec3(1.0, 0.35, 0.05) * ember * 2.5 + hell * vec3(1.0, 0.15, 0.1) * pulse * burnt;
}
";

    // ---- pictures painted in code ----

    readonly record struct Palette(Color SkyTop, Color SkyLow, Color Glow, Color Far, Color Ground, Color Dark, Color Robe);

    static readonly Palette[] Palettes =
    {
        new(new Color(0.36f, 0.46f, 0.62f), new Color(0.88f, 0.78f, 0.58f), new Color(1f, 0.9f, 0.6f), new Color(0.45f, 0.52f, 0.5f), new Color(0.55f, 0.5f, 0.3f), new Color(0.26f, 0.2f, 0.12f), new Color(0.2f, 0.3f, 0.55f)),
        new(new Color(0.72f, 0.58f, 0.32f), new Color(0.95f, 0.85f, 0.6f), new Color(1f, 0.95f, 0.7f), new Color(0.6f, 0.5f, 0.35f), new Color(0.42f, 0.36f, 0.22f), new Color(0.22f, 0.15f, 0.1f), new Color(0.62f, 0.2f, 0.18f)),
        new(new Color(0.16f, 0.22f, 0.4f), new Color(0.45f, 0.55f, 0.7f), new Color(0.95f, 0.95f, 0.8f), new Color(0.2f, 0.3f, 0.4f), new Color(0.18f, 0.3f, 0.28f), new Color(0.08f, 0.1f, 0.14f), new Color(0.85f, 0.82f, 0.7f)),
        new(new Color(0.5f, 0.62f, 0.66f), new Color(0.9f, 0.88f, 0.72f), new Color(1f, 0.92f, 0.65f), new Color(0.5f, 0.6f, 0.5f), new Color(0.38f, 0.48f, 0.3f), new Color(0.2f, 0.18f, 0.1f), new Color(0.7f, 0.55f, 0.25f)),
    };

    static float Hash(int x, int y, int seed)
    {
        var h = (uint)(x * 374761393 + y * 668265263 + seed * 2147483647);
        h = (h ^ (h >> 13)) * 1274126177u;
        return ((h ^ (h >> 16)) & 0xffff) / 65535f;
    }

    /// <summary>Coverage of a shape from its signed distance (negative inside) at <paramref name="eps"/> (one texel in picture units): a soft one-texel edge.</summary>
    static float Cov(float d, float eps) => Mathf.Clamp(0.5f - d / eps, 0f, 1f);

    /// <summary>A canvas-sized picture of <paramref name="w"/> x <paramref name="h"/> metres: a function of (x, y in picture units, y down 0..1, x 0..aspect) to a colour.</summary>
    static ImageTexture Paint(float w, float h, int seed, Func<float, float, float, Color> shade)
    {
        var nx = Math.Max(16, (int)MathF.Round(w * PxPerM));
        var ny = Math.Max(16, (int)MathF.Round(h * PxPerM));
        var aspect = w / h;
        var eps = 1f / ny;
        var px = new byte[nx * ny * 4];
        for (var y = 0; y < ny; y++)
            for (var x = 0; x < nx; x++)
            {
                var u = (x + 0.5f) / nx * aspect;
                var v = (y + 0.5f) / ny;
                var col = shade(u, v, eps);
                var edge = MathF.Min(MathF.Min(u, aspect - u), MathF.Min(v, 1f - v)); // an aged, darker rim
                var k = (1f - 0.4f * Mathf.SmoothStep(0.16f, 0f, edge)) * (1f + (Hash(x, y, seed) - 0.5f) * 0.07f);
                var o = (y * nx + x) * 4;
                px[o] = (byte)(Mathf.Clamp(col.R * k, 0f, 1f) * 255f);
                px[o + 1] = (byte)(Mathf.Clamp(col.G * k, 0f, 1f) * 255f);
                px[o + 2] = (byte)(Mathf.Clamp(col.B * k, 0f, 1f) * 255f);
                px[o + 3] = 255;
            }
        using var img = Image.CreateFromData(nx, ny, false, Image.Format.Rgba8, px);
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>The calm picture of painting <paramref name="index"/>: sky and hills with a cross on a hill (every third), a haloed figure, or a figure in rays of light; palette, horizon, place of the subject and of the sun come from the index.</summary>
    static ImageTexture CalmTexture(int index, float w, float h)
    {
        var rng = new Random(9001 + index * 37);
        var pal = Palettes[(index + rng.Next(Palettes.Length)) % Palettes.Length];
        var kind = index % 3;
        var aspect = w / h;
        var horizon = 0.58f + 0.12f * rng.NextSingle();
        var phase = rng.NextSingle() * 6.28f;
        var cx = aspect * (0.38f + 0.24f * rng.NextSingle());
        var sunX = aspect * (0.15f + 0.7f * rng.NextSingle());
        var sunY = 0.16f + 0.14f * rng.NextSingle();
        float Ground(float u) => horizon + 0.035f * MathF.Sin(u * 3f + phase) + 0.02f * MathF.Sin(u * 7f + phase * 2f);
        var feet = Ground(cx) + 0.015f;
        var gold = new Color(0.86f, 0.7f, 0.28f);
        var skin = new Color(0.85f, 0.7f, 0.55f);

        return Paint(w, h, index, (u, v, eps) =>
        {
            var col = pal.SkyTop.Lerp(pal.SkyLow, Mathf.Clamp(v / horizon, 0f, 1f));
            var sd = MathF.Sqrt((u - sunX) * (u - sunX) + (v - sunY) * (v - sunY));
            col = col.Lerp(pal.Glow, 0.5f * MathF.Exp(-sd * sd * 14f));
            col = col.Lerp(pal.Glow, Cov(sd - 0.045f, eps));
            var far = horizon - 0.05f + 0.04f * MathF.Sin(u * 2.1f + phase * 1.3f);
            if (v > far) col = pal.Far;
            var g = Ground(u);
            if (v > g) col = pal.Ground.Lerp(pal.Dark, Mathf.Clamp((v - g) * 1.6f, 0f, 1f) * 0.7f);
            if (kind == 0)
            {
                var ch = 0.42f;
                var armY = feet - ch * 0.72f;
                var hr = MathF.Sqrt((u - cx) * (u - cx) + (v - armY) * (v - armY));
                col = col.Lerp(pal.Glow, 0.4f * MathF.Exp(-hr * hr * 36f));
                var bar = MathF.Max(MathF.Abs(u - cx) - 0.014f, MathF.Abs(v - (feet - ch / 2)) - ch / 2);
                var arm = MathF.Max(MathF.Abs(u - cx) - 0.07f, MathF.Abs(v - armY) - 0.013f);
                col = col.Lerp(pal.Dark, Cov(MathF.Min(bar, arm), eps));
                return col;
            }
            var headY = feet - 0.4f;
            var hd = MathF.Sqrt((u - cx) * (u - cx) + (v - headY) * (v - headY));
            if (kind == 2) // rays of light from the head
            {
                var ang = MathF.Atan2(v - headY, u - cx);
                col = col.Lerp(pal.Glow, 0.22f * (0.5f + 0.5f * MathF.Sin(ang * 14f)) * MathF.Exp(-hd * 2.5f));
            }
            col = col.Lerp(pal.Glow, 0.4f * Cov(hd - 0.085f, eps));
            var tt = Mathf.Clamp((v - (headY + 0.035f)) / (feet - headY - 0.035f), 0f, 1f);
            var robe = MathF.Max(MathF.Abs(u - cx) - Mathf.Lerp(0.045f, 0.13f, tt), MathF.Max(headY + 0.035f - v, v - feet));
            col = col.Lerp(pal.Robe * (0.9f + 0.1f * MathF.Sin((u - cx) * 60f)), Cov(robe, eps));
            col = col.Lerp(skin, Cov(hd - 0.045f, eps));
            col = col.Lerp(gold, Cov(MathF.Abs(hd - 0.085f) - 0.011f, eps));
            return col;
        });
    }

    /// <summary>Fallback hell picture of painting <paramref name="index"/>, at the canvas's aspect: red haze to black, flames at the foot, an inverted cross, a few eyes.</summary>
    static ImageTexture HellTexture(int index, float w, float h)
    {
        var rng = new Random(666 + index * 53);
        var aspect = w / h;
        var cx = aspect / 2f;
        var phase = rng.NextSingle() * 6.28f;
        var eyes = new List<(float X, float Y, float R)>();
        for (var i = 0; i < 3 + rng.Next(3); i++) eyes.Add((aspect * (0.1f + 0.8f * rng.NextSingle()), 0.1f + 0.7f * rng.NextSingle(), 0.025f + 0.025f * rng.NextSingle()));
        var bone = new Color(0.85f, 0.75f, 0.6f);
        return Paint(w, h, index + 100, (u, v, eps) =>
        {
            var r2 = (u - cx) * (u - cx) + (v - 0.45f) * (v - 0.45f);
            var col = new Color(0.03f, 0f, 0f).Lerp(new Color(0.55f, 0.04f, 0.02f), MathF.Exp(-r2 * 4f));
            var flame = 0.5f + 0.5f * MathF.Sin(u * 25f + MathF.Sin(v * 7f + phase) * 2f);
            col += new Color(1f, 0.3f, 0f) * (MathF.Max(0f, v - 0.62f) * flame * 1.6f);
            foreach (var (ex, ey, er) in eyes) // glow, a yellow almond, a dark slit
            {
                var dx = (u - ex) / er;
                var dy = (v - ey) / (er * 0.45f);
                col = col.Lerp(new Color(0.9f, 0.3f, 0f), 0.35f * MathF.Exp(-(dx * dx + dy * dy) * 0.25f));
                col = col.Lerp(new Color(1f, 0.75f, 0.1f), Cov((MathF.Sqrt(dx * dx + dy * dy) - 1f) * er, eps));
                col = col.Lerp(new Color(0f, 0f, 0f), Cov(MathF.Max(MathF.Abs(u - ex) - 0.004f, MathF.Abs(v - ey) - er * 0.45f), eps));
            }
            var bar = MathF.Max(MathF.Abs(u - cx) - 0.03f, MathF.Abs(v - 0.5f) - 0.3f);
            var arm = MathF.Max(MathF.Abs(u - cx) - 0.13f, MathF.Abs(v - 0.6f) - 0.028f); // upside down: the arm near the foot
            return col.Lerp(bone, Cov(MathF.Min(bar, arm), eps));
        });
    }
}
