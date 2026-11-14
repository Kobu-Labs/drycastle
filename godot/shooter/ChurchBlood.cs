using System.Text.Json;
using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// The church bleeds while the demon lives (client only, built from church.json + windows.json). Under every window, on both faces of the wall, a thin quad lies
/// <see cref="OffsetM"/> off the wall face; its shader draws blood welling over the sill and running down as several uneven streaks: each has its own start
/// delay, width, wobble and speed, and advances in spurts. With time the streaks widen and merge into a stain, drying darker, with a glossy fresh tip. A streak that
/// reaches the foot of the wall spreads there; inside, a puddle grows on the floor. The stains start staggered over <see cref="StartSpreadS"/> seconds after the demon
/// appears, grow while it lives (<see cref="Freeze"/> stops them when it is gone, they stay) and vanish at <see cref="Reset"/> (the next Mass). A joiner who finds the
/// demon mid-fight starts at <see cref="JoinAgeS"/>. Inside faces follow <see cref="ChurchInterior"/>; <see cref="ChurchDecor"/> owns this node (as a sibling: the decor hides with the interior).
/// <para>
/// Where a streak ends: outside, on the ground by the door (<c>exit</c>), or on the roof of a lower room the wall rises out of (the aisle roofs under the clerestory), or at the lintel
/// of the doorway for the window over the door; inside, on the floor (the pastor's spot), or at the arch tops (<see cref="ArchTopM"/>) for the clerestory above the arcades.
/// </para>
/// </summary>
public partial class ChurchBlood : Node3D
{
    /// <summary>The stains of the windows start at random moments inside this many seconds after the demon appears.</summary>
    public const float StartSpreadS = 20f;
    /// <summary>A joiner finds the demon already fighting: the stains start this old.</summary>
    public const float JoinAgeS = 75f;
    /// <summary>The quad lies this far off the wall face (the shader pushes it a little further with distance, against z-fighting); the pane is in the middle of the 0.4 m wall.</summary>
    const float OffsetM = 0.02f, WallHalfM = 0.2f;
    /// <summary>The quad is this much wider than the window: room for the stain to widen.</summary>
    const float MarginM = 0.7f;
    /// <summary>The arcades below the clerestory are open under this height above the floor: an inside streak there stops at it.</summary>
    const float ArchTopM = 3.7f;
    /// <summary>A pool on the floor under an inside stain: its size, how far off the wall it lies, when it begins to grow (the streaks fall at about this speed) and how long it takes to grow.</summary>
    const float PoolSizeM = 1.8f, PoolSetM = 0.3f, StreakMps = 0.25f, PoolGrowS = 60f;

    sealed class Stain
    {
        public MeshInstance3D Wall = null!;
        public ShaderMaterial Mat = null!;
        public MeshInstance3D? Pool;
        public ShaderMaterial? PoolMat;
        public float Start, PoolStart;
    }

    readonly List<Stain> _stains = new();
    Node3D _outside = null!, _inside = null!;
    double _age = -1;
    bool _running, _dirty;

    static Shader? _wallShader, _poolShader;

    const string WallCode = @"
shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_disabled;

uniform float age = 0.0;
uniform float salt = 0.0;
uniform vec2 quad_size = vec2(1.6, 2.0);
uniform float win_w = 0.9;

float h11(float p) {
	return fract(sin(p * 127.1 + salt * 311.7) * 43758.5453);
}

void vertex() {
	float dist = min(length((MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz), 120.0);
	VERTEX.z += 0.0004 * dist;
}

void fragment() {
	float x = (UV.x - 0.5) * quad_size.x;
	float y = UV.y * quad_size.y;
	float cover = 0.0;
	float core = 0.0;
	float fresh = 0.0;

	// the well: blood standing over the sill, thicker and wider with time
	float wl = (0.03 + 0.09 * smoothstep(0.0, 30.0, age)) * (0.8 + 0.4 * sin(x * 31.0 + salt));
	float wx = win_w * 0.5 + 0.02 + 0.13 * smoothstep(0.0, 60.0, age);
	float well = (1.0 - smoothstep(wl * 0.6, wl, y)) * (1.0 - smoothstep(wx - 0.04, wx, abs(x + 0.03 * sin(y * 40.0 + salt))));
	cover = well;
	core = well;
	fresh = well * 0.7;

	for (int i = 0; i < 9; i++) {
		float fi = float(i);
		float r0 = h11(fi * 7.31 + 1.0);
		float r1 = h11(fi * 3.17 + 2.0);
		float r2 = h11(fi * 5.53 + 3.0);
		float r3 = h11(fi * 9.71 + 4.0);
		float cx = (r0 - 0.5) * win_w * 0.95;
		float a = max(age - r1 * 8.0, 0.0);
		float s = a * (0.4 + r2 * 0.6);
		float head = 0.0;
		if (a > 0.0) {
			// advances in spurts: a step of the streak, a pause, the next step
			head = (floor(s) + smoothstep(0.0, 0.45, fract(s))) * (0.14 + r3 * 0.3) + 0.04;
		}
		head = min(head, quad_size.y);
		bool reached = head >= quad_size.y - 0.01;
		float toTip = reached ? 10.0 : head - y;
		if (toTip > 0.0) {
			float w = (0.012 + r3 * 0.02) * (1.0 + 2.2 * smoothstep(0.0, 70.0, a));
			w *= 0.8 + 0.3 * sin(y * 7.0 + r0 * 10.0);
			if (reached) {
				w *= 1.0 + 1.6 * clamp(1.0 - (quad_size.y - y) / 0.25, 0.0, 1.0);
			}
			float ww = max(w * sqrt(clamp(toTip / (w * 1.5), 0.0, 1.0)), 0.0005);
			float wob = sin(y * (4.0 + r2 * 5.0) + r1 * 6.28) * 0.012 + sin(y * 13.0 + r3 * 9.0) * 0.004;
			float d = abs(x - cx - wob) / ww;
			float cov = 1.0 - smoothstep(0.75, 1.0, d);
			cover = max(cover, cov);
			core = max(core, cov * (1.0 - smoothstep(0.3, 0.8, d)));
			fresh = max(fresh, cov * exp(-toTip * 2.5));
		}
	}

	float dry = smoothstep(15.0, 120.0, age);
	float edge = cover * (1.0 - core);
	float wet = clamp(fresh + (1.0 - dry) * 0.45 * core, 0.0, 1.0);
	vec3 c = mix(vec3(0.15, 0.012, 0.012), vec3(0.52, 0.02, 0.03), wet);
	c = mix(c, vec3(0.07, 0.006, 0.006), edge * 0.8);
	ALBEDO = c;
	ALPHA = clamp(cover, 0.0, 1.0) * 0.95 * smoothstep(0.0, 1.5, age);
	ROUGHNESS = mix(0.65, 0.1, wet * (1.0 - edge));
	SPECULAR = 0.6;
	EMISSION = c * 0.12;
}
";

    const string PoolCode = @"
shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_disabled;

uniform float pool = 0.0;
uniform float salt = 0.0;

void vertex() {
	float dist = min(length((MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz), 120.0);
	VERTEX.z += 0.0004 * dist;
}

void fragment() {
	vec2 p = (UV - vec2(0.5)) * 2.0;
	float ang = atan(p.y, p.x);
	float rim = 0.62 + 0.16 * sin(ang * 3.0 + salt) + 0.09 * sin(ang * 7.0 + salt * 2.1) + 0.05 * sin(ang * 13.0 + salt * 3.3);
	float rr = length(p) / (rim * max(pool, 0.001));
	float cover = (1.0 - smoothstep(0.8, 1.0, rr)) * step(0.001, pool);
	float edge = smoothstep(0.55, 0.95, rr);
	vec3 c = mix(vec3(0.38, 0.015, 0.02), vec3(0.09, 0.008, 0.008), edge);
	ALBEDO = c;
	ALPHA = cover * 0.93;
	ROUGHNESS = mix(0.08, 0.5, edge);
	SPECULAR = 0.6;
	EMISSION = c * 0.1;
}
";

    readonly record struct Room(List<Vector2> Poly, float Z0, float Z1)
    {
        public bool Contains(Vector2 pt)
        {
            var inside = false;
            for (int i = 0, j = Poly.Count - 1; i < Poly.Count; j = i++)
            {
                var a = Poly[i]; var b = Poly[j];
                if ((a.Y > pt.Y) != (b.Y > pt.Y) && pt.X < (b.X - a.X) * (pt.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
            return inside;
        }
    }

    /// <summary>The stains of the church in <paramref name="mapDir"/>, or null on a headless client, a map without church.json or windows.json, or a church without windows.</summary>
    public static ChurchBlood? Create(string mapDir)
    {
        if (!ShotFx.Enabled) return null;
        var churchPath = Path.Combine(mapDir, "church.json");
        var windowsPath = Path.Combine(mapDir, "windows.json");
        if (!File.Exists(churchPath) || !File.Exists(windowsPath)) return null;
        try
        {
            using var church = JsonDocument.Parse(File.ReadAllText(churchPath));
            using var windows = JsonDocument.Parse(File.ReadAllText(windowsPath));
            var root = church.RootElement;
            if (!root.TryGetProperty("id", out var idEl) || idEl.GetString() is not { Length: > 0 } building) return null;
            if (!windows.RootElement.TryGetProperty("windows", out var list) || list.ValueKind != JsonValueKind.Array) return null;

            var rooms = new List<Room>();
            if (root.TryGetProperty("rooms", out var rs) && rs.ValueKind == JsonValueKind.Array)
                foreach (var r in rs.EnumerateArray())
                    rooms.Add(new Room(r.GetProperty("polygon").EnumerateArray().Select(p => new Vector2(p[0].GetSingle(), p[1].GetSingle())).ToList(), r.GetProperty("z0").GetSingle(), r.GetProperty("z1").GetSingle()));
            if (rooms.Count == 0) return null;
            var baseZ = rooms.Min(r => r.Z0);
            // the floor inside is the pastor's spot (the room outlines start in the foundation), the ground outside is at the exit
            var floorZ = root.TryGetProperty("pastor", out var pa) && pa.ValueKind == JsonValueKind.Object ? pa.GetProperty("pos")[2].GetSingle() : baseZ + 0.45f;
            var groundZ = root.TryGetProperty("exit", out var ex) && ex.ValueKind == JsonValueKind.Object ? ex.GetProperty("pos")[2].GetSingle() : baseZ + 0.2f;
            Vector2? doorXy = null; var doorHalf = 0f; var doorTop = 0f;
            if (root.TryGetProperty("doorway", out var dw) && dw.ValueKind == JsonValueKind.Object)
            {
                var dp = dw.GetProperty("pos");
                doorXy = new Vector2(dp[0].GetSingle(), dp[1].GetSingle());
                doorHalf = dw.GetProperty("width").GetSingle() / 2;
                doorTop = dp[2].GetSingle() + dw.GetProperty("height").GetSingle() + 0.03f;
            }

            var node = new ChurchBlood { Name = "ChurchBlood" };
            node._outside = new Node3D { Name = "Outside" };
            node._inside = new Node3D { Name = "Inside" };
            node.AddChild(node._outside);
            node.AddChild(node._inside);
            foreach (var w in list.EnumerateArray())
            {
                if (!w.TryGetProperty("building", out var b) || b.GetString() != building) continue;
                var id = w.GetProperty("id").GetString() ?? "";
                var cEl = w.GetProperty("centre"); var nEl = w.GetProperty("normal");
                var c = new Vector2(cEl[0].GetSingle(), cEl[1].GetSingle());
                var cz = cEl[2].GetSingle();
                var n = new Vector2(nEl[0].GetSingle(), nEl[1].GetSingle()).Normalized(); // points out of the church
                var ww = w.GetProperty("width_m").GetSingle(); var hh = w.GetProperty("height_m").GetSingle();
                var sill = cz - hh / 2;
                var overDoor = doorXy is { } dxy && (dxy - c).Length() < doorHalf + ww / 2;

                // outside: the ground, or the roof of a lower room the wall rises out of, or the lintel of the door under the window
                var outLow = groundZ;
                foreach (var r in rooms)
                    if (r.Z1 < sill - 0.05f && r.Z1 > outLow && r.Contains(c + n * 0.6f)) outLow = r.Z1;
                if (overDoor) outLow = MathF.Max(outLow, doorTop);
                // inside: the floor, or the arch tops of the arcades
                var inLow = sill - floorZ > ArchTopM + 0.3f ? floorZ + ArchTopM : floorZ;
                if (overDoor) inLow = MathF.Max(inLow, doorTop);

                node.AddStain(id + "o", c, n, ww, sill, outLow, false, floorZ);
                node.AddStain(id + "i", c, n, ww, sill, inLow, true, floorZ);
            }
            if (node._stains.Count == 0) { node.Free(); return null; }
            return node;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            GD.PushWarning($"ChurchBlood: church.json/windows.json not understood ({e.Message}); no blood");
            return null;
        }
    }

    static float Rand01(string s, int salt)
    {
        var h = 2166136261u ^ (uint)salt;
        foreach (var ch in s) h = (h ^ ch) * 16777619u;
        h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
        return (h & 0xFFFFFF) / (float)0x1000000;
    }

    /// <summary>One stain under a window: the wall quad from the sill down to <paramref name="low"/> on one face of the wall.</summary>
    void AddStain(string key, Vector2 c, Vector2 n, float winW, float sill, float low, bool inside, float floorZ)
    {
        var length = sill - low;
        if (length < 0.08f) return;
        _wallShader ??= new Shader { Code = WallCode };
        var outDir = new Vector3(n.X, 0, -n.Y); // out of the church, in the game's axes
        var g = inside ? -outDir : outDir;      // the face's normal
        var salt = Rand01(key, 1) * 20f;
        var mat = new ShaderMaterial { Shader = _wallShader };
        mat.SetShaderParameter("salt", salt);
        mat.SetShaderParameter("quad_size", new Vector2(winW + MarginM, length));
        mat.SetShaderParameter("win_w", winW);
        mat.SetShaderParameter("age", 0f);
        var pos = new Vector3(c.X, (sill + low) / 2, -c.Y) + g * (WallHalfM + OffsetM);
        var right = (-g).Cross(Vector3.Up); // the way along the wall as seen from the front
        var wall = new MeshInstance3D
        {
            Name = "Blood_" + key, Mesh = new QuadMesh { Size = new Vector2(winW + MarginM, length) }, MaterialOverride = mat,
            Transform = new Transform3D(new Basis(right, Vector3.Up, g), pos), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false,
        };
        (inside ? _inside : _outside).AddChild(wall);
        var s = new Stain { Wall = wall, Mat = mat, Start = Rand01(key, 2) * StartSpreadS };
        if (inside && MathF.Abs(low - floorZ) < 0.01f)
        {
            _poolShader ??= new Shader { Code = PoolCode };
            s.PoolMat = new ShaderMaterial { Shader = _poolShader };
            s.PoolMat.SetShaderParameter("salt", salt);
            s.PoolMat.SetShaderParameter("pool", 0f);
            s.Pool = new MeshInstance3D
            {
                Name = "Pool_" + key, Mesh = new QuadMesh { Size = new Vector2(PoolSizeM, PoolSizeM) }, MaterialOverride = s.PoolMat,
                Transform = new Transform3D(new Basis(Vector3.Right, -Mathf.Pi / 2), new Vector3(c.X, floorZ + OffsetM, -c.Y) + g * (WallHalfM + PoolSetM)),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false,
            };
            _inside.AddChild(s.Pool);
            s.PoolStart = length / StreakMps + s.Start;
        }
        _stains.Add(s);
    }

    public override void _EnterTree()
    {
        _inside.Visible = ChurchInterior.Visible;
        ChurchInterior.Changed += OnInterior;
    }

    public override void _ExitTree() => ChurchInterior.Changed -= OnInterior;

    void OnInterior(bool visible) => _inside.Visible = visible;

    /// <summary>The demon appeared: the stains start (staggered), already <paramref name="startAge"/> seconds old.</summary>
    public void Begin(float startAge)
    {
        _age = startAge; _running = true; _dirty = true;
    }

    /// <summary>The demon is gone: the stains stop growing and stay.</summary>
    public void Freeze() => _running = false;

    /// <summary>The event is over for good: the stains are gone.</summary>
    public void Reset()
    {
        _age = -1; _running = false;
        foreach (var s in _stains) { s.Wall.Visible = false; if (s.Pool != null) s.Pool.Visible = false; }
    }

    public override void _Process(double delta)
    {
        if (_age < 0) return;
        if (_running) _age += delta;
        else if (!_dirty) return;
        _dirty = false;
        foreach (var s in _stains)
        {
            var a = (float)(_age - s.Start);
            s.Wall.Visible = a > 0;
            if (a > 0) s.Mat.SetShaderParameter("age", a);
            if (s.Pool != null && s.PoolMat != null)
            {
                var p = Mathf.Clamp(((float)_age - s.PoolStart) / PoolGrowS, 0f, 1f);
                s.Pool.Visible = p > 0;
                if (p > 0) s.PoolMat.SetShaderParameter("pool", p * p * (3 - 2 * p));
            }
        }
    }
}
