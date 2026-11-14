using System.Text.Json;
using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// What hangs in the church, built from church.json alone (no map rebuild; the server never draws it): the <b>cross</b> on the chancel wall behind the altar
/// and the <b>lights</b> of the interior. Both follow <see cref="ChurchInterior"/>.
/// <para>
/// Cross: a wooden box cross (<see cref="CrossHeightM"/> tall) hung in front of the end wall straight behind the altar (or the pastor's spot): the altar's pose gives where and which way, a ray
/// to the edge of the room it stands in gives the wall (the room outlines of church.json stop <see cref="WallInsetM"/> inside the wall), and the cross hangs <see cref="RetableM"/> (the
/// retable's depth) and a hand's breadth in front of its inner face, with its foot <see cref="CrossFootM"/> above the floor so it clears the altar table. While a demon is in the church (<see cref="SetDemon"/>) it burns (a faint orange glow, small flames, a faint light) and slowly
/// turns over <see cref="InvertS"/> seconds until it hangs upside down; when the demon is gone it burns out and turns upright again in <see cref="ReturnS"/>.
/// </para>
/// <para>
/// Lights: warm point lights, none of them casting shadows: chandeliers along the nave's long axis hung <see cref="ChandelierDropM"/> under the vault (the lowest ceiling anchor of the demon's
/// graph; without anchors, under the room's top), candles on the altar and one over the chancel. A light that would land outside every room or above its vault is dropped with a warning. They are on in
/// every phase but the demon's: it appears, they go out, and the church is lit by fire only (the demon, its fireballs, the burning cross).
/// </para>
/// </summary>
public partial class ChurchDecor : Node3D
{
    public const float CrossHeightM = 2.2f;
    /// <summary>The room outlines of church.json run this far inside the walls' thickness (the pipeline's <c>room()</c>: the wall's inner face is 0.2 m nearer the room).</summary>
    const float WallInsetM = 0.2f;
    /// <summary>The high altar's backdrop (the retable) stands this deep off the end wall; the cross hangs <see cref="CrossClearM"/> before it.</summary>
    const float RetableM = 0.4f, CrossClearM = 0.2f;
    /// <summary>The cross's foot above the chancel floor (the altar table is about 1 m), and the room kept free over its top.</summary>
    const float CrossFootM = 1.9f, CrossHeadroomM = 0.6f;
    /// <summary>Chandeliers hang this far under the vault.</summary>
    const float ChandelierDropM = 1.2f;
    /// <summary>The cross's beam: its width and its depth.</summary>
    const float BeamW = 0.26f, CrossDepthM = 0.14f;
    /// <summary>Seconds the cross takes to turn over after the demon appears, and to turn back upright when it is gone.</summary>
    public const float InvertS = 35f, ReturnS = 4f;
    /// <summary>Seconds the cross takes to catch and to burn out, and the lights to go out and to come back.</summary>
    const float BurnRampS = 6f, BurnOutS = 1.5f, LightsOutS = 1.2f, LightsOnS = 1.5f;

    public static ChurchDecor? Current { get; private set; }

    sealed class Lamp
    {
        public OmniLight3D Light = null!;
        public float Energy, Flicker, Seed;
        public MeshInstance3D? Glow;
    }

    readonly List<Lamp> _lamps = new();
    List<Room> _rooms = new();
    float? _vaultZ;
    Node3D? _rot;
    OmniLight3D? _crossLight;
    StandardMaterial3D? _crossMat;
    readonly List<CpuParticles3D> _flames = new();
    bool _demon, _flamesOn;
    float _angle, _burn, _lightK = 1;
    double _t;
    ChurchBlood? _blood;
    /// <summary>How far the turning is, 0 to 1 (the lights flicker, ever worse, and are out at 1); negative: not turning.</summary>
    float _transK = -1;

    static string? _glassPrefix;
    static readonly StandardMaterial3D ClearPane = new()
    {
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha, AlbedoColor = new Color(0.62f, 0.78f, 0.88f, 0.22f),
        Roughness = 0.08f, Metallic = 0.1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };
    float _glassIn;
    int _glassDone;

    /// <summary>
    /// The church's panes come from the map as opaque coloured meshes, like every house window; these stand in real openings, so they get a clear
    /// material. The chunk with the church may load after this node: looked for once a second until its panes are found (and a few times after, for stragglers).
    /// </summary>
    void ClearGlass(float dt)
    {
        if (_glassPrefix == null || _glassDone >= 3 || (_glassIn -= dt) > 0) return;
        _glassIn = 1f;
        var found = 0;
        foreach (var n in GetTree().Root.FindChildren(_glassPrefix + "*", "MeshInstance3D", true, false))
            if (n is MeshInstance3D m) { m.MaterialOverride = ClearPane; found++; }
        if (found > 0) _glassDone++;
    }

    /// <summary>The decor of the church.json in <paramref name="mapDir"/>, or null when the map has no church or the file lacks rooms.</summary>
    public static ChurchDecor? Create(string mapDir)
    {
        var path = Path.Combine(mapDir, "church.json");
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (!root.TryGetProperty("rooms", out var rooms) || rooms.ValueKind != JsonValueKind.Array || rooms.GetArrayLength() == 0) return null;
        var list = new List<Room>();
        foreach (var r in rooms.EnumerateArray())
        {
            var poly = r.GetProperty("polygon").EnumerateArray().Select(p => new Vector2(p[0].GetSingle(), p[1].GetSingle())).ToList();
            if (poly.Count >= 3) list.Add(new Room(poly, r.GetProperty("z0").GetSingle(), r.GetProperty("z1").GetSingle()));
        }
        if (list.Count == 0) return null;
        _glassPrefix = root.TryGetProperty("id", out var bid) && bid.GetString() is { Length: > 0 } id ? $"window_{id}_c" : null;
        Spot? altar = null, pastor = null;
        if (root.TryGetProperty("props", out var props))
            foreach (var p in props.EnumerateArray())
                if (p.GetProperty("kind").GetString() == "altar") { altar = SpotOf(p); break; }
        if (root.TryGetProperty("pastor", out var pa) && pa.ValueKind == JsonValueKind.Object) pastor = SpotOf(pa);
        // the vault: the lowest anchor whose normal points down (the demon's ceiling anchors sit just under it)
        float? vault = null;
        if (root.TryGetProperty("anchors", out var anchors) && anchors.ValueKind == JsonValueKind.Array)
            foreach (var an in anchors.EnumerateArray())
            {
                if (!an.TryGetProperty("normal", out var n) || !an.TryGetProperty("pos", out var ap) || n.GetArrayLength() != 3 || ap.GetArrayLength() != 3 || n[2].GetSingle() > -0.7f) continue;
                var z = ap[2].GetSingle();
                if (vault == null || z < vault) vault = z;
            }
        var node = new ChurchDecor { Name = "ChurchDecor", _rooms = list, _vaultZ = vault, _blood = ChurchBlood.Create(mapDir) };
        node.Build(list, altar ?? pastor, altar, pastor);
        return node;
    }

    /// <summary>A polygon in map metres (x east, y north) between two heights.</summary>
    readonly record struct Room(List<Vector2> Poly, float Z0, float Z1)
    {
        public float Area()
        {
            var a = 0f;
            for (var i = 0; i < Poly.Count; i++) { var p = Poly[i]; var q = Poly[(i + 1) % Poly.Count]; a += p.X * q.Y - q.X * p.Y; }
            return MathF.Abs(a) / 2;
        }

        public Vector2 Centroid() => Poly.Aggregate(Vector2.Zero, (s, p) => s + p) / Poly.Count;

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

        /// <summary>The distance from <paramref name="from"/> along the unit <paramref name="dir"/> to where the outline is crossed, or null.</summary>
        public float? RayToEdge(Vector2 from, Vector2 dir)
        {
            float? best = null;
            for (var i = 0; i < Poly.Count; i++)
            {
                var a = Poly[i]; var e = Poly[(i + 1) % Poly.Count] - a;
                var denom = dir.X * e.Y - dir.Y * e.X;
                if (MathF.Abs(denom) < 1e-6f) continue;
                var w = a - from;
                var t = (w.X * e.Y - w.Y * e.X) / denom;
                var u = (w.X * dir.Y - w.Y * dir.X) / denom;
                if (t > 0.05f && u >= 0 && u <= 1 && (best == null || t < best)) best = t;
            }
            return best;
        }
    }

    /// <summary>A prop or spot: the point in map metres (x east, y north), the floor height and the compass bearing it faces.</summary>
    readonly record struct Spot(Vector2 Pos, float Z, float Bearing);

    static Spot SpotOf(JsonElement e)
    {
        var p = e.GetProperty("pos");
        return new Spot(new Vector2(p[0].GetSingle(), p[1].GetSingle()), p[2].GetSingle(), e.TryGetProperty("yaw", out var y) ? y.GetSingle() : 0);
    }

    static Vector3 G(Vector2 p, float z) => new(p.X, z, -p.Y);
    static Vector2 Dir(float bearingDeg) => new(MathF.Sin(Mathf.DegToRad(bearingDeg)), MathF.Cos(Mathf.DegToRad(bearingDeg)));

    public override void _EnterTree()
    {
        Current = this;
        Visible = ChurchInterior.Visible;
        ChurchInterior.Changed += OnInterior;
        // the blood is a sibling, not a child: this node hides with the interior, the outside walls bleed all the same
        if (_blood != null) Callable.From(() => { if (IsInsideTree() && _blood.GetParent() == null) GetParent().AddChild(_blood); }).CallDeferred();
    }

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
        ChurchInterior.Changed -= OnInterior;
        if (_blood != null) { if (_blood.GetParent() != null) _blood.QueueFree(); else _blood.Free(); }
    }

    void OnInterior(bool visible) => Visible = visible;

    // ---- build ----

    void Build(List<Room> rooms, Spot? anchor, Spot? altar, Spot? pastor)
    {
        var nave = rooms.OrderByDescending(r => r.Area()).First();
        BuildChandeliers(nave);
        if (anchor is { } a)
        {
            var room = rooms.FirstOrDefault(r => r.Contains(a.Pos));
            if (room.Poly == null) room = rooms.OrderBy(r => (r.Centroid() - a.Pos).LengthSquared()).First();
            BuildCross(room, a);
            if (altar is { } al) BuildCandles(al);
            var over = pastor ?? a;
            AddLamp(G(over.Pos, over.Z + 3.2f), 1.0f, 9f, 0.05f, glow: false);
        }
    }

    static readonly Color Warm = new(1f, 0.74f, 0.42f);

    /// <summary>Is a point in the air of a room: inside an outline, between its floor and the vault (or its top)?</summary>
    bool InAir(Vector3 p)
    {
        var xy = new Vector2(p.X, -p.Z);
        foreach (var r in _rooms)
            if (r.Contains(xy) && p.Y > r.Z0 && p.Y < VaultOf(r)) return true;
        return false;
    }

    /// <summary>The ceiling of a room: the vault when the anchors name one inside its height, else its top.</summary>
    float VaultOf(Room r) => _vaultZ is { } v && v < r.Z1 && v > r.Z0 + 2f ? v : r.Z1;

    void AddLamp(Vector3 at, float energy, float range, float flicker, bool glow, float glowSize = 0.1f)
    {
        if (!InAir(at)) { GD.PushWarning($"ChurchDecor: a light at {at} is outside the church's rooms or above its vault; left out"); return; }
        var l = new Lamp
        {
            Energy = energy, Flicker = flicker, Seed = _lamps.Count * 2.7f,
            Light = new OmniLight3D { Position = at, LightColor = Warm, LightEnergy = energy, OmniRange = range, OmniAttenuation = 1.2f, ShadowEnabled = false },
        };
        AddChild(l.Light);
        if (glow)
        {
            l.Glow = new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = glowSize, Height = glowSize * 2, RadialSegments = 8, Rings = 4, Material = Lit(new Color(1f, 0.85f, 0.55f)) },
                Position = at, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(l.Glow);
        }
        _lamps.Add(l);
    }

    static StandardMaterial3D Lit(Color c) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = c, EmissionEnabled = true, Emission = c, EmissionEnergyMultiplier = 2f,
    };

    /// <summary>Three chandeliers on the nave's long axis (its longest edge), at 20, 50 and 80 percent of its length, hung under the vault.</summary>
    void BuildChandeliers(Room nave)
    {
        var poly = nave.Poly;
        var longest = Vector2.Zero; var len = 0f;
        for (var i = 0; i < poly.Count; i++)
        {
            var e = poly[(i + 1) % poly.Count] - poly[i];
            if (e.Length() > len) { len = e.Length(); longest = e; }
        }
        var axis = longest / len;
        var c = nave.Centroid();
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var p in poly) { var d = (p - c).Dot(axis); lo = MathF.Min(lo, d); hi = MathF.Max(hi, d); }
        var height = VaultOf(nave) < nave.Z1 ? VaultOf(nave) - ChandelierDropM : nave.Z1 - 2.1f;
        foreach (var f in new[] { 0.2f, 0.5f, 0.8f })
            AddLamp(G(c + axis * Mathf.Lerp(lo, hi, f), height), 1.5f, 13f, 0.04f, glow: true, glowSize: 0.14f);
    }

    /// <summary>Two candles on the altar, either side of its middle.</summary>
    void BuildCandles(Spot altar)
    {
        var across = new Vector2(Dir(altar.Bearing).Y, -Dir(altar.Bearing).X);
        foreach (var s in new[] { -0.7f, 0.7f })
            AddLamp(G(altar.Pos + across * s, altar.Z + 1.2f), 0.9f, 6f, 0.25f, glow: true, glowSize: 0.05f);
    }

    /// <summary>The cross in front of the end wall behind the altar: the ray from the altar away from the congregation (opposite its facing) to the room's edge, back to the wall's inner face, then out again past the retable.</summary>
    void BuildCross(Room room, Spot at)
    {
        var back = -Dir(at.Bearing);
        var t = room.RayToEdge(at.Pos, back) ?? 2.5f + WallInsetM;
        var face = t - WallInsetM; // the wall's inner face, from the altar
        var d = MathF.Max(0.8f, face - RetableM - CrossClearM - CrossDepthM / 2); // the cross's middle, never nearer the altar than a table's depth
        var wall = at.Pos + back * d;
        var height = MathF.Min(CrossFootM + CrossHeightM / 2, room.Z1 - at.Z - CrossHeadroomM - CrossHeightM / 2);
        var centre = G(wall, at.Z + height);
        var b = G(back, 0); // towards the wall in the game's axes (y = 0)
        var root = new Node3D { Name = "Cross", Position = centre, Rotation = new Vector3(0, MathF.Atan2(b.X, b.Z), 0) }; // local +Z points at the wall, -Z at the nave
        AddChild(root);
        _rot = new Node3D { Name = "Turn" };
        root.AddChild(_rot);
        // light oak, so it reads against the retable's dark canvas; the burn's glow is added to it
        _crossMat = new StandardMaterial3D { AlbedoColor = new Color(0.58f, 0.38f, 0.19f), Roughness = 0.75f, EmissionEnabled = true, Emission = new Color(1f, 0.4f, 0.08f), EmissionEnergyMultiplier = 0 };
        // the beam is centred on the pivot, the arm a third from the top: turned half a circle, the cross is upside down
        _rot.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(BeamW, CrossHeightM, CrossDepthM), Material = _crossMat }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        _rot.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1.4f, BeamW, CrossDepthM), Material = _crossMat }, Position = new Vector3(0, CrossHeightM * 0.2f, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        if (ShotFx.Enabled)
        {
            _crossLight = new OmniLight3D { Position = new Vector3(0, 0, -0.7f), LightColor = new Color(1f, 0.45f, 0.12f), LightEnergy = 0, OmniRange = 6f, ShadowEnabled = false };
            root.AddChild(_crossLight);
            foreach (var (x, y) in new[] { (0f, -0.8f), (0f, 0.1f), (0f, 0.8f), (-0.5f, CrossHeightM * 0.2f), (0.5f, CrossHeightM * 0.2f) })
                if (DemonFx.Flames(_rot, 10, 0.22f, new Vector3(x, y, -0.1f)) is { } f) _flames.Add(f);
        }
    }

    // ---- the demon ----

    /// <summary>The demon is in the church (its entity exists) or is gone. <paramref name="snap"/>: a joiner who finds it mid-fight sees the end state at once.</summary>
    public void SetDemon(bool active, bool snap = false)
    {
        if (active && !_demon) _blood?.Begin(snap ? ChurchBlood.JoinAgeS : 0f);
        else if (!active && _demon) _blood?.Freeze();
        _demon = active;
        if (!snap) return;
        _angle = active ? MathF.PI : 0;
        _burn = active ? 1 : 0;
        _lightK = active ? 0 : 1;
    }

    /// <summary>The stains of the windows are gone (a new Mass begins); they stay after the demon dies until then.</summary>
    public void ResetBlood() => _blood?.Reset();

    /// <summary>The turning of the pastor, <paramref name="k"/> from 0 to 1 over its first part: the lights flicker ever worse, with a few blackouts, and are out at 1. Negative or 1 and over: not turning.</summary>
    public void SetTransform(float k) => _transK = k >= 1f ? -1f : k;

    static float Hash(float x) { var h = MathF.Sin(x * 127.1f) * 43758.547f; return h - MathF.Floor(h); }

    static float Noise(float x)
    {
        var i = MathF.Floor(x); var f = x - i;
        f = f * f * (3 - 2 * f);
        return Mathf.Lerp(Hash(i), Hash(i + 1), f);
    }

    static bool Between(float k, float a, float b) => k >= a && k <= b;

    /// <summary>A light's share of its energy while the pastor turns: irregular dips that quicken and deepen, a blackout around 45 % and 73 % (a little off for each lamp) and one at the end.</summary>
    float TurningLight(float k, float seed)
    {
        var n = Noise((float)_t * (6f + 14f * k) + seed * 5f);
        var depth = 0.15f + 0.85f * k * k;
        var d = Mathf.Clamp((n - 0.3f) / 0.6f, 0f, 1f);
        var f = 1f - depth * d * d * (3 - 2 * d);
        var kk = k + (Hash(seed * 3.7f) - 0.5f) * 0.03f;
        if (Between(kk, 0.43f, 0.48f) || Between(kk, 0.71f, 0.76f) || kk > 0.94f) f = 0f;
        return f;
    }

    public override void _Process(double delta)
    {
        var dt = (float)delta;
        _t += delta;
        _burn = Mathf.MoveToward(_burn, _demon ? 1f : 0f, dt / (_demon ? BurnRampS : BurnOutS));
        _angle = Mathf.MoveToward(_angle, _demon ? MathF.PI : 0f, dt * MathF.PI / (_demon ? InvertS : ReturnS));
        if (_transK >= 0) _lightK = TurningLight(_transK, 0f); // the fade to dark carries on from where the flicker left it
        else _lightK = Mathf.MoveToward(_lightK, _demon ? 0f : 1f, dt / (_demon ? LightsOutS : LightsOnS));
        ClearGlass(dt);
        if (!Visible) return;

        if (_rot != null) _rot.Rotation = new Vector3(0, 0, _angle);
        if (_crossMat != null) _crossMat.EmissionEnergyMultiplier = _burn * 0.9f * (0.75f + 0.25f * DemonFx.Flicker(_t, 11, 0, 1));
        if (_crossLight != null) _crossLight.LightEnergy = _burn * DemonFx.Flicker(_t, 12, 0.5f, 1.1f);
        var on = _burn > 0.05f;
        if (on != _flamesOn)
        {
            _flamesOn = on;
            foreach (var f in _flames) ParticleFx.Run(f, on);
        }
        foreach (var l in _lamps)
        {
            var lit = _transK >= 0 ? TurningLight(_transK, l.Seed) : _lightK;
            var k = lit * (l.Flicker > 0 ? DemonFx.Flicker(_t, l.Seed, 1f - l.Flicker, 1f + l.Flicker * 0.4f) : 1f);
            l.Light.LightEnergy = l.Energy * k;
            l.Light.Visible = lit > 0.01f;
            if (l.Glow != null) l.Glow.Visible = lit > 0.05f;
        }
    }
}
