using System.Text.Json;
using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// What hangs in the church, built from church.json alone (no map rebuild; the server never draws it): the <b>cross</b> on the chancel wall behind the altar
/// and the <b>lights</b> of the interior. Both follow <see cref="ChurchInterior"/>.
/// <para>
/// Cross: a dark wooden box cross (<see cref="CrossHeightM"/> tall) on the wall straight behind the altar (or the pastor's spot): the altar's pose gives where and which way, a ray
/// to the edge of the room it stands in gives the wall. While a demon is in the church (<see cref="SetDemon"/>) it burns (a faint orange glow, small flames, a faint light) and slowly
/// turns over <see cref="InvertS"/> seconds until it hangs upside down; when the demon is gone it burns out and turns upright again in <see cref="ReturnS"/>.
/// </para>
/// <para>
/// Lights: warm point lights, none of them casting shadows: chandeliers along the nave's long axis near the vault, candles on the altar and one over the chancel. They are on in
/// every phase but the demon's: it appears, they go out, and the church is lit by fire only (the demon, its fireballs, the burning cross).
/// </para>
/// </summary>
public partial class ChurchDecor : Node3D
{
    public const float CrossHeightM = 2.2f;
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
    Node3D? _rot;
    OmniLight3D? _crossLight;
    StandardMaterial3D? _crossMat;
    readonly List<CpuParticles3D> _flames = new();
    bool _demon, _flamesOn;
    float _angle, _burn, _lightK = 1;
    double _t;

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
        Spot? altar = null, pastor = null;
        if (root.TryGetProperty("props", out var props))
            foreach (var p in props.EnumerateArray())
                if (p.GetProperty("kind").GetString() == "altar") { altar = SpotOf(p); break; }
        if (root.TryGetProperty("pastor", out var pa) && pa.ValueKind == JsonValueKind.Object) pastor = SpotOf(pa);
        var node = new ChurchDecor { Name = "ChurchDecor" };
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
    }

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
        ChurchInterior.Changed -= OnInterior;
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

    void AddLamp(Vector3 at, float energy, float range, float flicker, bool glow, float glowSize = 0.1f)
    {
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
        var height = nave.Z1 - 2.1f;
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

    /// <summary>The cross on the wall behind the altar: the ray from the altar away from the congregation (opposite its facing) to the room's edge.</summary>
    void BuildCross(Room room, Spot at)
    {
        var back = -Dir(at.Bearing);
        var t = room.RayToEdge(at.Pos, back) ?? 2.5f;
        var wall = at.Pos + back * (t - 0.12f);
        var centre = G(wall, at.Z + MathF.Min(3.1f, room.Z1 - at.Z - 1.4f));
        var b = G(back, 0); // towards the wall in the game's axes (y = 0)
        var root = new Node3D { Name = "Cross", Position = centre, Rotation = new Vector3(0, MathF.Atan2(b.X, b.Z), 0) }; // local +Z points at the wall
        AddChild(root);
        _rot = new Node3D { Name = "Turn" };
        root.AddChild(_rot);
        _crossMat = new StandardMaterial3D { AlbedoColor = new Color(0.17f, 0.095f, 0.05f), Roughness = 0.9f, EmissionEnabled = true, Emission = new Color(1f, 0.4f, 0.08f), EmissionEnergyMultiplier = 0 };
        // the beam is centred on the pivot, the arm a third from the top: turned half a circle, the cross is upside down
        _rot.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.2f, CrossHeightM, 0.14f), Material = _crossMat }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        _rot.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1.3f, 0.2f, 0.14f), Material = _crossMat }, Position = new Vector3(0, CrossHeightM * 0.2f, 0),
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
        _demon = active;
        if (!snap) return;
        _angle = active ? MathF.PI : 0;
        _burn = active ? 1 : 0;
        _lightK = active ? 0 : 1;
    }

    public override void _Process(double delta)
    {
        var dt = (float)delta;
        _t += delta;
        _burn = Mathf.MoveToward(_burn, _demon ? 1f : 0f, dt / (_demon ? BurnRampS : BurnOutS));
        _angle = Mathf.MoveToward(_angle, _demon ? MathF.PI : 0f, dt * MathF.PI / (_demon ? InvertS : ReturnS));
        _lightK = Mathf.MoveToward(_lightK, _demon ? 0f : 1f, dt / (_demon ? LightsOutS : LightsOnS));
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
            var k = _lightK * (l.Flicker > 0 ? DemonFx.Flicker(_t, l.Seed, 1f - l.Flicker, 1f + l.Flicker * 0.4f) : 1f);
            l.Light.LightEnergy = l.Energy * k;
            l.Light.Visible = _lightK > 0.01f;
            if (l.Glow != null) l.Glow.Visible = _lightK > 0.05f;
        }
    }
}
