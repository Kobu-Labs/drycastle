using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The mechanic's garage (ticket #320, <see cref="MechanicGarageSite"/>): a plain shed of OSB plates under a wavy rubber roof on the garden plots by the road, built in code on the
/// server (solid boxes only) and on the clients (the same, drawn). The Golf Mk2 stands inside, nose to the road wall, whose one breakable section, the plate in front of it, is
/// a map glass pane (<see cref="MechanicGarageSite.AddPane"/>: the server owns its hit points, shots and blasts damage it, ADR 0007): while it stands the wall is solid and nothing
/// of the room can be seen; once it shatters its collider and its mesh go (<see cref="SetPlate"/>) and the opening is wide enough for the Golf to drive out. The room is lit by
/// <see cref="MechanicGarageLights"/>: dark when the plate has just gone, the roof lights flicker (the tubes only, nothing is lit), then they come on. Interior props are Synty
/// Construction models (<see cref="SyntyLib"/>, the private pack: glb/construction/, built by scripts/construction_pack_glb.py); without the pack the solid ones are boxes.
/// The look of every part is made here: OSB is a generated texture, the roof a corrugated mesh. Only on the map that has the garage.json of the bus garage (the village).
/// </summary>
public partial class MechanicGarage : Node3D
{
    public static MechanicGarage? Current { get; private set; }

    /// <summary>The floor height this garage was built at (the plate's pane is placed with it).</summary>
    public float FloorY { get; private set; }

    const string Atlas = "glb/atlas/construction_01_A.png", ModelDir = "construction/";
    /// <summary>Lights further than this from the camera (m) are not drawn or lit.</summary>
    const float DrawRange = 90f;

    readonly MechanicGarageLights _lights = new();
    bool _draw;
    Node3D _plate = null!;
    StaticBody3D _plateBody = null!;
    MeshInstance3D? _veil;
    StandardMaterial3D? _tubeMat, _veilMat;
    readonly List<OmniLight3D> _lamps = new();
    readonly List<Node3D> _fixtures = new();
    StandardMaterial3D _osb = null!, _concrete = null!, _dark = null!;
    float _indoor;

    /// <summary>The garage of the village map in <paramref name="mapDir"/>, or null on a map without the bus garage's garage.json (any other map) or without ground there.</summary>
    public static MechanicGarage? Create(Drycastle.MapLoader.MapLoader map, string mapDir, bool draw)
    {
        if (!File.Exists(Path.Combine(mapDir, "garage.json"))) return null;
        var floor = MechanicGarageSite.FloorY(map.GroundHeight);
        if (float.IsNaN(floor)) return null;
        var node = new MechanicGarage { Name = "MechanicGarage", _draw = draw, FloorY = floor };
        node.Position = new Vector3(MechanicGarageSite.Centre.X, floor, MechanicGarageSite.Centre.Y);
        node.Rotation = new Vector3(0, MechanicGarageSite.YawRad, 0);
        node.Build();
        return node;
    }

    public override void _EnterTree() => Current = this;

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
        if (_indoor > 0 && DayNightCycle.Current is { } cycle) cycle.IndoorExtra = 0;
    }

    /// <summary>The plate stands (true) or has shattered. Its box and its mesh follow; the room's lights start their sequence when it has just broken (<paramref name="animate"/>).</summary>
    public void SetPlate(bool standing, bool animate)
    {
        _plateBody.CollisionLayer = standing ? CollisionLayers.World : 0;
        _plate.Visible = standing;
        if (_veil != null) _veil.Visible = !standing;
        _lights.PlateBroken(!standing, animate);
    }

    // ------------------------------------------------------------------ building

    void Build()
    {
        const float W = MechanicGarageSite.Width, D = MechanicGarageSite.Depth, T = MechanicGarageSite.Wall;
        if (_draw)
        {
            _osb = new StandardMaterial3D { AlbedoTexture = OsbTexture(), Roughness = 0.95f, Uv1Triplanar = true, Uv1Scale = Vector3.One / 2.5f };
            _concrete = new StandardMaterial3D { AlbedoColor = new Color(0.43f, 0.43f, 0.41f), Roughness = 0.95f };
            _dark = new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.11f, 0.10f), Roughness = 0.8f };
        }
        // floor slab, under the walls too
        Solid("floor", new Vector3(0, -0.15f, 0), new Vector3(W, 0.3f, D), _concrete);
        // road wall (east): the plate's opening between two runs and a lintel; the plate is its own box
        const float zE = -D / 2 + T / 2, roofE = MechanicGarageSite.RoofHigh - 0.02f;
        var plateLo = MechanicGarageSite.PlateX - MechanicGarageSite.PlateWidth / 2;
        var plateHi = MechanicGarageSite.PlateX + MechanicGarageSite.PlateWidth / 2;
        Solid("wall_east_north", Box(-W / 2, plateLo, -0.5f, roofE, zE, T), _osb);
        Solid("wall_east_south", Box(plateHi, W / 2, -0.5f, roofE, zE, T), _osb);
        Solid("wall_east_lintel", Box(plateLo, plateHi, MechanicGarageSite.PlateHeight, roofE, zE, T), _osb);
        Solid("wall_west", Box(-W / 2, W / 2, -0.5f, MechanicGarageSite.RoofAt(D / 2 - T / 2), D / 2 - T / 2, T), _osb);
        Wedge("wall_north", -W / 2, -W / 2 + T);
        Wedge("wall_south", W / 2 - T, W / 2);
        BuildPlate(zE, T);
        BuildRoof();
        if (_draw)
        {
            BuildLights();
            BuildVeil();
            Stains();
        }
        BuildProps();
        _lights.PlateBroken(false, false);
    }

    /// <summary>A box of the garage frame, x0..x1 along the wall and y0..y1 up, centred at depth <paramref name="z"/> with thickness <paramref name="t"/>: (centre, size).</summary>
    static (Vector3 Centre, Vector3 Size) Box(float x0, float x1, float y0, float y1, float z, float t) =>
        (new Vector3((x0 + x1) / 2, (y0 + y1) / 2, z), new Vector3(x1 - x0, y1 - y0, t));

    StaticBody3D Solid(string name, (Vector3 Centre, Vector3 Size) b, Material? mat) => Solid(name, b.Centre, b.Size, mat);

    /// <summary>A solid box on the World layer (a collider on the server too) that is drawn with <paramref name="mat"/> where something is drawn.</summary>
    StaticBody3D Solid(string name, Vector3 centre, Vector3 size, Material? mat, Node? parent = null)
    {
        var body = new StaticBody3D { Name = name, CollisionLayer = CollisionLayers.World, Position = centre };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        if (_draw) body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = mat });
        (parent ?? this).AddChild(body);
        return body;
    }

    /// <summary>A north or south wall: a prism whose top follows the roof's slope.</summary>
    void Wedge(string name, float x0, float x1)
    {
        const float D = MechanicGarageSite.Depth;
        float tA = MechanicGarageSite.RoofAt(-D / 2) - 0.02f, tB = MechanicGarageSite.RoofAt(D / 2) - 0.02f, y0 = -0.5f;
        var c = new[]
        {
            new Vector3(x0, y0, -D / 2), new Vector3(x1, y0, -D / 2), new Vector3(x1, y0, D / 2), new Vector3(x0, y0, D / 2),
            new Vector3(x0, tA, -D / 2), new Vector3(x1, tA, -D / 2), new Vector3(x1, tB, D / 2), new Vector3(x0, tB, D / 2),
        };
        var body = new StaticBody3D { Name = name, CollisionLayer = CollisionLayers.World };
        body.AddChild(new CollisionShape3D { Shape = new ConvexPolygonShape3D { Points = c } });
        if (_draw)
        {
            int[][] faces = { new[] { 0, 1, 2, 3 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 }, new[] { 3, 2, 6, 7 }, new[] { 0, 3, 7, 4 }, new[] { 1, 2, 6, 5 } };
            var centre = c.Aggregate(Vector3.Zero, (s, v) => s + v) / c.Length;
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            st.SetSmoothGroup(uint.MaxValue); // flat faces
            foreach (var f in faces)
            {
                var mid = (c[f[0]] + c[f[1]] + c[f[2]] + c[f[3]]) / 4;
                foreach (var tri in new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 3 } })
                {
                    Vector3 a = c[f[tri[0]]], b = c[f[tri[1]]], d = c[f[tri[2]]];
                    if ((d - a).Cross(b - a).Dot(mid - centre) < 0) (b, d) = (d, b); // Godot's front faces wind clockwise: outward
                    st.AddVertex(a); st.AddVertex(b); st.AddVertex(d);
                }
            }
            st.GenerateNormals();
            body.AddChild(new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = _osb });
        }
        AddChild(body);
    }

    /// <summary>The breakable plate: a box in the road wall (the pane's node, see <see cref="SetPlate"/>) with battens so it reads as a panel of its own.</summary>
    void BuildPlate(float zE, float t)
    {
        const float w = MechanicGarageSite.PlateWidth, h = MechanicGarageSite.PlateHeight;
        _plate = new Node3D { Name = MechanicGarageSite.PlateNode, Position = new Vector3(MechanicGarageSite.PlateX, h / 2, zE) };
        _plateBody = new StaticBody3D { Name = "plate_body", CollisionLayer = CollisionLayers.World };
        _plateBody.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(w - 0.02f, h, t) } });
        _plate.AddChild(_plateBody);
        if (_draw)
        {
            _plate.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(w - 0.02f, h, t) }, MaterialOverride = _osb });
            // two sheets 1.5 m wide: a dark joint down the middle and a batten along the top and the bottom, proud of the face by a centimetre
            void Strip(Vector3 size, Vector3 at) => _plate.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = _dark, Position = at });
            Strip(new Vector3(0.025f, h - 0.04f, t + 0.02f), Vector3.Zero);
            Strip(new Vector3(w - 0.02f, 0.07f, t + 0.02f), new Vector3(0, h / 2 - 0.035f, 0));
            Strip(new Vector3(w - 0.02f, 0.07f, t + 0.02f), new Vector3(0, -h / 2 + 0.035f, 0));
        }
        AddChild(_plate);
    }

    /// <summary>The roof: a tilted slab (OSB underneath, solid) with the corrugated rubber sheet on it.</summary>
    void BuildRoof()
    {
        const float W = MechanicGarageSite.Width, D = MechanicGarageSite.Depth, O = MechanicGarageSite.Overhang, Th = MechanicGarageSite.RoofThickness;
        var slope = MathF.Atan((MechanicGarageSite.RoofHigh - MechanicGarageSite.RoofLow) / D);
        var length = (D + 2 * O) / MathF.Cos(slope);
        var roof = new Node3D { Name = "roof", Rotation = new Vector3(slope, 0, 0) };
        var mid = (MechanicGarageSite.RoofHigh + MechanicGarageSite.RoofLow) / 2;
        roof.Position = new Vector3(0, mid + Th / 2 / MathF.Cos(slope), 0);
        AddChild(roof);
        Solid("slab", new Vector3(0, 0, 0), new Vector3(W + 2 * O, Th, length), _osb, roof);
        if (_draw) roof.AddChild(new MeshInstance3D { Name = "sheet", Mesh = RoofSheet(W + 2 * O, length), Position = new Vector3(0, Th / 2 + 0.04f, 0), MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.85f, CullMode = BaseMaterial3D.CullModeEnum.Disabled }, CastShadow = GeometryInstance3D.ShadowCastingSetting.On });
    }

    /// <summary>
    /// The wavy rubber sheet: waves along x (0.19 m apart, 4 cm high), flat along the slope, in sheets 0.95 x 2 m of three colours (red, a darker red, brown) so the roof reads as laid sheets.
    /// One flat-shaded colour per sheet in the vertex colours; smooth normals across the waves.
    /// </summary>
    static ArrayMesh RoofSheet(float width, float length)
    {
        const float Pitch = 0.19f, Amp = 0.04f, Step = Pitch / 6f, SheetW = 0.95f, SheetL = 2.0f;
        Color[] palette = { new(0.60f, 0.22f, 0.16f), new(0.52f, 0.18f, 0.13f), new(0.38f, 0.23f, 0.17f), new(0.58f, 0.20f, 0.14f) };
        var rng = new Random(11);
        var cols = (int)MathF.Ceiling(width / Step);
        var rows = (int)MathF.Ceiling(length / SheetL);
        var tone = new Color[(int)MathF.Ceiling(width / SheetW) + 1, rows];
        for (var i = 0; i < tone.GetLength(0); i++)
        for (var j = 0; j < rows; j++)
        {
            var c = palette[rng.Next(palette.Length)];
            var k = 0.92f + 0.16f * (float)rng.NextDouble();
            tone[i, j] = new Color(c.R * k, c.G * k, c.B * k);
        }
        var verts = new List<Vector3>(); var norms = new List<Vector3>(); var colours = new List<Color>(); var idx = new List<int>();
        float Y(float x) => Amp * MathF.Sin(x / Pitch * MathF.Tau);
        Vector3 N(float x) { var d = Amp * MathF.Tau / Pitch * MathF.Cos(x / Pitch * MathF.Tau); return new Vector3(-d, 1, 0).Normalized(); }
        for (var j = 0; j < rows; j++)
        {
            float z0 = -length / 2 + j * SheetL, z1 = MathF.Min(z0 + SheetL, length / 2);
            for (var i = 0; i < cols; i++)
            {
                float x0 = -width / 2 + i * Step, x1 = MathF.Min(x0 + Step, width / 2);
                var col = tone[(int)((x0 + width / 2) / SheetW), j];
                var b = verts.Count;
                // the sheet faces up (+Y): clockwise seen from above
                foreach (var (x, z) in new[] { (x0, z0), (x1, z0), (x1, z1), (x0, z1) })
                {
                    verts.Add(new Vector3(x, Y(x), z)); norms.Add(N(x)); colours.Add(col);
                }
                idx.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
            }
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = norms.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colours.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    // ------------------------------------------------------------------ OSB

    static ImageTexture? _osbTexture;

    /// <summary>
    /// An OSB panel, generated: 2.5 x 2.5 m at 205 px/m, strands lying mostly along the sheet (some across) in the pale-orange-to-brown of the real board, a dark joint along the
    /// left edge and across the middle (sheets of 1.25 x 2.5 m). Seeded, so every client has the same one; made once, in a few tenths of a second.
    /// </summary>
    static ImageTexture OsbTexture()
    {
        if (_osbTexture != null) return _osbTexture;
        const int S = 512;
        var px = new byte[S * S * 4];
        for (var i = 0; i < S * S; i++) { px[i * 4] = 170; px[i * 4 + 1] = 124; px[i * 4 + 2] = 70; px[i * 4 + 3] = 255; }
        var rng = new Random(2026);
        (int R, int G, int B)[] tones = { (205, 160, 98), (192, 146, 84), (222, 184, 122), (160, 112, 60), (212, 170, 106), (178, 130, 72), (236, 206, 150) };
        for (var n = 0; n < 1100; n++)
        {
            var cx = rng.Next(S); var cy = rng.Next(S);
            var len = 22 + rng.Next(60); var wid = 5 + rng.Next(10);
            var ang = (rng.NextDouble() < 0.7 ? 0.0 : Math.PI / 2) + (rng.NextDouble() - 0.5) * 0.6;
            var (sa, ca) = Math.SinCos(ang);
            var t = tones[rng.Next(tones.Length)];
            var jitter = rng.Next(-10, 11);
            var reach = len / 2 + wid;
            for (var dy = -reach; dy <= reach; dy++)
            for (var dx = -reach; dx <= reach; dx++)
            {
                var u = dx * ca + dy * sa; var v = -dx * sa + dy * ca;
                if (Math.Abs(u) > len / 2.0 || Math.Abs(v) > wid / 2.0) continue;
                var x = ((cx + dx) % S + S) % S; var y = ((cy + dy) % S + S) % S; // wraps: the texture tiles
                var edge = Math.Abs(v) > wid / 2.0 - 1.2 ? 0.82 : 1.0;
                var o = (y * S + x) * 4;
                px[o] = (byte)Math.Clamp((t.R + jitter) * edge, 0, 255); px[o + 1] = (byte)Math.Clamp((t.G + jitter) * edge, 0, 255); px[o + 2] = (byte)Math.Clamp((t.B + jitter) * edge, 0, 255);
            }
        }
        void Joint(int x0, int y0, int w, int h)
        {
            for (var y = y0; y < y0 + h; y++)
            for (var x = x0; x < x0 + w; x++) { var o = ((y % S) * S + x % S) * 4; px[o] = 70; px[o + 1] = 48; px[o + 2] = 28; }
        }
        Joint(0, 0, 3, S); Joint(0, S / 2 - 1, S, 3);
        var img = Image.CreateFromData(S, S, false, Image.Format.Rgba8, px);
        img.GenerateMipmaps();
        return _osbTexture = ImageTexture.CreateFromImage(img);
    }

    /// <summary>A few dark oil stains on the slab, flat quads just above it.</summary>
    void Stains()
    {
        var mat = new StandardMaterial3D { AlbedoColor = new Color(0.07f, 0.07f, 0.07f, 0.55f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, Roughness = 0.4f };
        foreach (var (x, z, r) in new[] { (MechanicGarageSite.GolfX + 0.4f, MechanicGarageSite.GolfZ - 1.2f, 0.55f), (MechanicGarageSite.GolfX - 0.7f, MechanicGarageSite.GolfZ + 0.9f, 0.4f), (0.8f, -1.6f, 0.35f) })
            AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(r * 2, r * 1.5f) }, MaterialOverride = mat, Position = new Vector3(x, 0.012f, z), Rotation = new Vector3(0, x * 3f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }

    // ------------------------------------------------------------------ lights

    void BuildLights()
    {
        _tubeMat = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.88f, 0.85f), EmissionEnabled = true, Emission = new Color(1f, 0.96f, 0.85f), EmissionEnergyMultiplier = 0f, Roughness = 0.4f };
        foreach (var (x, z) in MechanicGarageSite.Lamps)
        {
            var y = MechanicGarageSite.RoofAt(z) - 0.06f;
            var fixture = new Node3D { Name = "roof_light", Position = new Vector3(x, y, z) };
            fixture.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.22f, 0.07f, 1.7f) }, MaterialOverride = _dark });
            foreach (var dx in new[] { -0.06f, 0.06f }) // two tubes along the fixture
                fixture.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.05f, 0.04f, 1.55f) }, MaterialOverride = _tubeMat, Position = new Vector3(dx, -0.05f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            AddChild(fixture);
            _fixtures.Add(fixture);
            var lamp = new OmniLight3D
            {
                Position = new Vector3(x, y - 0.6f, z), LightColor = new Color(1f, 0.95f, 0.82f), LightEnergy = 0f, OmniRange = 8f, OmniAttenuation = 1.1f,
                ShadowEnabled = false, Visible = false,
            };
            AddChild(lamp);
            _lamps.Add(lamp);
        }
    }

    /// <summary>
    /// A black film over the opening, just inside it and facing out: seen from the road the room behind the broken plate is dark until the lights are on; seen from inside it is
    /// back-face culled, so the way out is clear. (The ambient light is global, so a dark room cannot be made of the lights alone.)
    /// </summary>
    void BuildVeil()
    {
        _veilMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0, 0, 0, 1f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Back,
        };
        _veil = new MeshInstance3D
        {
            Name = "veil", Mesh = new QuadMesh { Size = new Vector2(MechanicGarageSite.PlateWidth, MechanicGarageSite.PlateHeight) }, MaterialOverride = _veilMat,
            Position = new Vector3(MechanicGarageSite.PlateX, MechanicGarageSite.PlateHeight / 2, -MechanicGarageSite.Depth / 2 + MechanicGarageSite.Wall + 0.03f),
            Rotation = new Vector3(0, Mathf.Pi, 0), // a quad faces +Z; this one faces out of the road wall
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false,
        };
        AddChild(_veil);
    }

    public override void _Process(double delta)
    {
        if (!_draw) return;
        var dt = (float)delta;
        _lights.Update(dt);
        var cam = GetViewport()?.GetCamera3D();
        var near = cam != null && cam.GlobalPosition.DistanceTo(GlobalPosition) < DrawRange;
        var level = near ? _lights.Level : 0f;
        _tubeMat!.EmissionEnergyMultiplier = _lights.Glow * 1.9f; // under the glow threshold: the tubes do not bloom
        for (var i = 0; i < _lamps.Count; i++)
        {
            _lamps[i].Visible = level > 0.01f;
            _lamps[i].LightEnergy = 2.6f * level;
        }
        if (_veilMat != null && _veil != null && _veil.Visible) _veilMat.AlbedoColor = new Color(0, 0, 0, _lights.Veil);
        // inside the room the daylight's ambient is turned down while the lights are off (the sun through the opening still comes in)
        var inside = cam != null && Inside(cam.GlobalPosition);
        _indoor = Mathf.MoveToward(_indoor, inside ? 1f - _lights.Level : 0f, dt / 0.3f);
        if (DayNightCycle.Current is { } cycle) cycle.IndoorExtra = _indoor;
    }

    bool Inside(Vector3 world)
    {
        var p = ToLocal(world);
        return MechanicGarageSite.Inside(p.X, p.Z) && p.Y is > -0.5f and < MechanicGarageSite.RoofHigh;
    }

    // ------------------------------------------------------------------ props

    /// <summary>
    /// One interior prop: a Synty Construction model at (<paramref name="x"/>, <paramref name="z"/>) of the garage frame, <paramref name="yaw"/> degrees about Y (0: the model's front,
    /// +Z, faces the back wall; 90 faces south; 180 faces the road), <paramref name="y"/> up from the floor. <paramref name="solid"/> is its footprint box (garage axes, centred on
    /// the prop): a collider on the server too, and what is drawn without the pack.
    /// </summary>
    void Prop(string model, float x, float z, float yaw = 0, float y = 0, Vector3? solid = null)
    {
        var pivot = new Node3D { Name = model, Position = new Vector3(x, y, z), Rotation = new Vector3(0, Mathf.DegToRad(yaw), 0) };
        Node3D? drawn = _draw ? SyntyLib.Instance(ModelDir + model, Atlas) : null;
        if (drawn != null) pivot.AddChild(drawn);
        AddChild(pivot);
        if (solid is not { } size) return;
        var body = new StaticBody3D { CollisionLayer = CollisionLayers.World, Position = new Vector3(x, y + size.Y / 2, z) };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        if (_draw && drawn == null) body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.45f, 0.4f, 0.3f), Roughness = 0.9f } });
        AddChild(body);
    }

    /// <summary>The furnishing after the sketch: a workbench along the south wall (L2), a shelf on the road wall beside it (L3), the working stuff along the back wall (L4).</summary>
    void BuildProps()
    {
        const float south = MechanicGarageSite.HalfX, back = MechanicGarageSite.HalfZ, road = -MechanicGarageSite.HalfZ;
        // L2: the bench, a trestle table and a table end to end, with a vice, a toolbox and tools on it
        Prop("SM_Prop_Trestle_Table_01", south - 0.57f, -1.65f, 0, 0, new Vector3(1.1f, 0.8f, 3.1f));
        Prop("SM_Prop_Table_01", south - 0.5f, 1.3f, 90, 0, new Vector3(1.0f, 0.69f, 2.05f));
        Prop("SM_Prop_Vice_01", south - 0.6f, -2.8f, 90, 0.8f);
        Prop("SM_Prop_ToolBox_01", south - 0.55f, -1.45f, 90, 0.8f);
        Prop("SM_Tool_Drill_01", south - 0.5f, -0.6f, 20, 0.99f);
        Prop("SM_Tool_Angle_Grinder_01", south - 0.5f, 0.55f, 70, 0.78f);
        Prop("SM_Tool_Circular_Saw_01", south - 0.45f, 1.45f, 100, 0.78f);
        Prop("SM_Tool_Pipe_Wrench_01", south - 0.7f, 2.0f, 80, 0.74f);
        Prop("SM_Tool_Wrench_01", south - 0.35f, 0.15f, 10, 0.76f);
        // L3: a wooden shelf unit on the road wall, with cans and buckets
        Shelf(1.0f, 3.2f, road);
        // L4: the working stuff along the back wall
        Prop("SM_Prop_Compressor_01", -4.3f, back - 0.3f, 0, 0, new Vector3(1.26f, 0.97f, 0.46f));
        Prop("SM_Prop_Generator_Small_01", -3.1f, back - 0.4f, 90, 0, new Vector3(0.64f, 0.56f, 0.42f));
        Prop("SM_Prop_Barrel_Oil_01", -2.2f, back - 0.42f, 0, 0, new Vector3(0.72f, 1.59f, 0.72f));
        Prop("SM_Prop_Crate_01", -1.2f, back - 0.45f, 0, 0, new Vector3(0.83f, 0.82f, 0.83f));
        Prop("SM_Prop_Pallet_01", -0.1f, back - 0.75f, 0, 0, new Vector3(1.44f, 0.23f, 1.41f));
        Prop("SM_Prop_Crate_02", -0.1f, back - 0.75f, 90, 0.23f, new Vector3(1.35f, 0.88f, 0.9f));
        Prop("SM_Prop_Table_Saw_01", 1.5f, back - 0.55f, 180, 0, new Vector3(1.05f, 0.51f, 0.95f));
        Prop("SM_Prop_Ladder_Small_01", 2.8f, back - 0.5f, 180);
        Prop("SM_Prop_Light_Portable_01", 3.9f, 2.9f, 200);
        // the north end and the floor: tyres, a sawhorse, a wheelbarrow, buckets and cans
        Prop("SM_Prop_TireStack_01", -4.55f, 2.2f, 0, 0, new Vector3(0.75f, 0.68f, 0.75f));
        Prop("SM_Prop_Tire_01", -4.7f, 0.9f, 15, 0, new Vector3(0.75f, 0.22f, 0.75f));
        Prop("SM_Prop_Sawhorse_01", -4.5f, -0.9f, 0, 0, new Vector3(0.7f, 0.85f, 1.55f));
        Prop("SM_Prop_Wheelbarrow_01", -4.6f, -2.9f, 200, 0, new Vector3(0.8f, 0.9f, 1.9f));
        Prop("SM_Prop_Paint_Bucket_Closed_01", 3.7f, -3.45f, 0, 0, new Vector3(0.48f, 0.55f, 0.48f));
        Prop("SM_Prop_Tool_Bucket_01", 3.9f, -2.7f, 30, 0, new Vector3(0.48f, 0.51f, 0.47f));
        Prop("SM_Prop_Paint_Can_Open_White_01", 3.5f, 0.2f, 0, 0);
    }

    /// <summary>A shelf unit of OSB boards on posts against the road wall, <paramref name="x0"/> to <paramref name="x1"/> along it, with paint cans and buckets on the boards.</summary>
    void Shelf(float x0, float x1, float roadZ)
    {
        const float depth = 0.55f, height = 2.2f, board = 0.04f;
        var zc = roadZ + depth / 2 + 0.03f;
        foreach (var x in new[] { x0 + 0.04f, (x0 + x1) / 2, x1 - 0.04f })
            Solid("shelf_post", new Vector3(x, height / 2, zc), new Vector3(0.07f, height, depth), _dark);
        var boards = new[] { 0.45f, 1.05f, 1.65f, 2.2f };
        foreach (var by in boards) Solid("shelf_board", new Vector3((x0 + x1) / 2, by, zc), new Vector3(x1 - x0, board, depth), _osb);
        Prop("SM_Prop_Paint_Bucket_Closed_01", x0 + 0.4f, zc, 0, boards[0] + board / 2);
        Prop("SM_Prop_Tool_Bucket_01", x0 + 1.2f, zc, 40, boards[0] + board / 2);
        Prop("SM_Prop_Paint_Can_Closed_Black_01", x0 + 1.9f, zc, 0, boards[0] + board / 2);
        Prop("SM_Prop_Paint_Can_Closed_Black_01", x0 + 0.5f, zc, 0, boards[1] + board / 2);
        Prop("SM_Prop_Paint_Can_Open_White_01", x0 + 0.95f, zc, 30, boards[1] + board / 2);
        Prop("SM_Prop_Paint_Bucket_Closed_01", x0 + 1.7f, zc, 0, boards[1] + board / 2);
        Prop("SM_Prop_ToolBox_01", x0 + 0.8f, zc, 0, boards[2] + board / 2);
        Prop("SM_Prop_Tool_Bucket_01", x0 + 1.7f, zc, 0, boards[2] + board / 2);
    }
}
