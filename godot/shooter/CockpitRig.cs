using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// The cockpit rig of a vehicle's cockpit view (prototype, ticket #219): a rough interior built for the screen, not the model's own. It is
/// a child of the vehicle's node, drawn only on the driver's own client, in the vehicle frame (x right, y up, forward is -Z, origin on the
/// ground at the centre), so its numbers sit beside the model scripts' (scripts/make_*_model.py). While it shows, the parts of the model it
/// stands in for are not drawn for the driver (<see cref="HideModel"/>): they keep their shadow, and their child nodes (the turret, the
/// rotors) stay. The bus hides nothing: its own interior is modelled, so its rig is only the hands and the wheel's spokes, and the driver
/// sees the saloon and the riders behind the glass partition. The steering wheel turns with the keys and the hands ride it; a helicopter's
/// cyclic and collective move with the keys the same way. Everything is primitive meshes in flat colours: the real build generates proper models.
/// </summary>
public sealed partial class CockpitRig : Node3D
{
    /// <summary>One vehicle's cockpit: where the camera sits, which model nodes the rig replaces, and what builds the rig.</summary>
    public sealed record Spec(Vector3 Eye, string[] Hide, Action<CockpitRig> Build);

    static readonly Dictionary<string, Spec> Specs = new()
    {
        ["bus"] = new Spec(new Vector3(-0.70f, 1.60f, -4.75f), System.Array.Empty<string>(), BuildBus),
        ["flak"] = new Spec(new Vector3(-0.50f, 2.05f, -1.25f), new[] { "cab" }, BuildFlak),
        ["utility_helicopter"] = new Spec(new Vector3(0.55f, 1.78f, -1.80f), new[] { "fuselage" }, BuildHelicopter),
    };

    /// <summary>The cockpit of a vehicle type (vehicles.json id), or null when it has none.</summary>
    public static Spec? For(string vehicleId) => Specs.GetValueOrDefault(vehicleId);

    public static CockpitRig Make(Spec spec)
    {
        var rig = new CockpitRig { Name = "cockpit_rig" };
        spec.Build(rig);
        return rig;
    }

    // ------------------------------------------------------------------ moving parts

    /// <summary>How far the steering wheel turns at full lock, how far a stick tilts, radians, and how fast they follow the keys, per second.</summary>
    const float WheelLock = 1.3f, StickTilt = 0.2f, LeverLift = 0.25f, Follow = 7f;

    Node3D? _wheelPivot, _wheelSpin, _cyclic, _collective;
    float _steer, _fore, _side, _lift;

    /// <summary>An arm: the hand rides <see cref="Carrier"/> (a child of <see cref="Mount"/>, which is a child of the rig), the forearm runs to it from a fixed elbow.</summary>
    sealed record Arm(Node3D Mount, Node3D Carrier, Vector3 Grip, MeshInstance3D Forearm, Vector3 Elbow);

    readonly List<Arm> _arms = new();

    /// <summary>Poses the controls and the hands for this frame from the keys: steer or roll (-1..1, right is +), drive or pitch (forward is +), lift (up is +).</summary>
    public void Pose(float moveX, float moveY, float lift, float dt)
    {
        var k = 1f - Mathf.Exp(-Follow * dt);
        _steer = Mathf.Lerp(_steer, moveX, k);
        _fore = Mathf.Lerp(_fore, moveY, k);
        _side = Mathf.Lerp(_side, moveX, k);
        _lift = Mathf.Lerp(_lift, lift, k);
        // the wheel's axis points at the driver, so a turn to the right (clockwise as the driver sees it) is a negative angle
        if (_wheelSpin != null) _wheelSpin.Rotation = new Vector3(0, -_steer * WheelLock, 0);
        if (_cyclic != null) _cyclic.Rotation = new Vector3(-_fore * StickTilt, 0, -_side * StickTilt);
        if (_collective != null) _collective.Rotation = new Vector3(_lift * LeverLift, 0, 0);
        foreach (var a in _arms)
        {
            var hand = a.Mount == a.Carrier ? a.Mount.Transform * a.Grip : a.Mount.Transform * (a.Carrier.Transform * a.Grip);
            Span(a.Forearm, a.Elbow, hand);
        }
    }

    // ------------------------------------------------------------------ hiding the model

    readonly List<(GeometryInstance3D Node, GeometryInstance3D.ShadowCastingSetting Was)> _hidden = new();

    /// <summary>The named nodes of the vehicle's model are not drawn (their own mesh only: they keep casting their shadow, and their children stay).</summary>
    public void HideModel(Node3D vehicle, string[] names)
    {
        foreach (var name in names)
        {
            if (vehicle.FindChild(name, true, false) is GeometryInstance3D g)
            {
                _hidden.Add((g, g.CastShadow));
                g.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
            }
            else GD.PrintErr($"cockpit rig: the model has no mesh node '{name}' to hide");
        }
    }

    public void RestoreModel()
    {
        foreach (var (g, was) in _hidden)
            if (IsInstanceValid(g)) g.CastShadow = was;
        _hidden.Clear();
    }

    // ------------------------------------------------------------------ parts

    static readonly Dictionary<Color, StandardMaterial3D> Materials = new();

    static StandardMaterial3D Mat(Color c)
    {
        if (!Materials.TryGetValue(c, out var m)) Materials[c] = m = new StandardMaterial3D { AlbedoColor = c, Roughness = 0.85f };
        return m;
    }

    static readonly Color Trim = new(0.30f, 0.31f, 0.33f), Black = new(0.06f, 0.06f, 0.07f), DarkMetal = new(0.13f, 0.14f, 0.14f),
        Olive = new(0.29f, 0.33f, 0.20f), OliveDark = new(0.20f, 0.23f, 0.15f), OliveLight = new(0.37f, 0.41f, 0.27f), Khaki = new(0.52f, 0.47f, 0.33f),
        Dial = new(0.80f, 0.80f, 0.74f), Sleeve = new(0.30f, 0.33f, 0.22f), Glove = new(0.11f, 0.11f, 0.11f);

    static MeshInstance3D Part(Mesh mesh, Color c) =>
        new() { Mesh = mesh, MaterialOverride = Mat(c), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };

    /// <summary>An axis-aligned box between two corners.</summary>
    static MeshInstance3D Box(Color c, float x0, float x1, float y0, float y1, float z0, float z1)
    {
        var b = Part(new BoxMesh { Size = new Vector3(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0), Mathf.Abs(z1 - z0)) }, c);
        b.Position = new Vector3((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2);
        return b;
    }

    /// <summary>A bar from <paramref name="a"/> to <paramref name="b"/>, <paramref name="w"/> wide and <paramref name="h"/> thick (square when h is 0).</summary>
    static MeshInstance3D Bar(Color c, Vector3 a, Vector3 b, float w, float h = 0)
    {
        var bar = Part(new BoxMesh { Size = new Vector3(w, h > 0 ? h : w, 1) }, c);
        Span(bar, a, b);
        return bar;
    }

    /// <summary>Lays a unit-length bar (its length along its own z) from <paramref name="a"/> to <paramref name="b"/>, in its parent's frame.</summary>
    static void Span(Node3D bar, Vector3 a, Vector3 b)
    {
        var d = b - a;
        var len = d.Length();
        if (len < 1e-4f) return;
        var z = d / len;
        var x = (Mathf.Abs(z.Y) > 0.95f ? Vector3.Back : Vector3.Up).Cross(z).Normalized();
        bar.Transform = new Transform3D(new Basis(x, z.Cross(x), z * len), (a + b) / 2);
    }

    /// <summary>A round gauge face on a panel, facing the seat (+Z).</summary>
    static MeshInstance3D Gauge(float x, float y, float z, float r)
    {
        var g = Part(new CylinderMesh { TopRadius = r, BottomRadius = r, Height = 0.012f, RadialSegments = 12, Rings = 1 }, Dial);
        g.Position = new Vector3(x, y, z);
        g.Rotation = new Vector3(Mathf.Pi / 2, 0, 0);
        return g;
    }

    static StandardMaterial3D? _glass;

    /// <summary>A glass pane between four corners (in order round the pane): lightly tinted, see-through from both sides.</summary>
    static MeshInstance3D Pane(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        _glass ??= new StandardMaterial3D
        {
            AlbedoColor = new Color(0.42f, 0.58f, 0.66f, 0.18f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, Roughness = 0.12f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        var n = (b - a).Cross(d - a).Normalized();
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new[] { a, b, c, d };
        arrays[(int)Mesh.ArrayType.Normal] = new[] { n, n, n, n };
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2, 0, 2, 3 };
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return new MeshInstance3D { Mesh = mesh, MaterialOverride = _glass, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    }

    void Add(params Node3D[] parts) { foreach (var p in parts) AddChild(p); }

    /// <summary>The same parts on both sides: <paramref name="make"/> is called with the side's sign (+1 right, -1 left).</summary>
    void Both(Func<float, Node3D> make) { AddChild(make(1)); AddChild(make(-1)); }

    /// <summary>
    /// A steering wheel at <paramref name="centre"/> whose plane leans <paramref name="leanDeg"/> from level towards the driver, with two
    /// hands at ten and two and their forearms from elbows either side of <paramref name="elbows"/>. With <paramref name="onModel"/> the
    /// model has the wheel already (a plain disc, so it cannot be seen to turn): the rig adds only spokes on its face, and the hands.
    /// </summary>
    void SteeringWheel(Vector3 centre, float radius, float leanDeg, Vector3 elbows, float elbowSpread, bool onModel = false)
    {
        var (s, c) = Mathf.SinCos(Mathf.DegToRad(leanDeg));
        var axis = new Vector3(0, c, s); // out of the wheel's face, up and back at the driver
        _wheelPivot = new Node3D { Transform = new Transform3D(new Basis(Vector3.Right, axis, Vector3.Right.Cross(axis)), centre) };
        _wheelSpin = new Node3D();
        _wheelPivot.AddChild(_wheelSpin);
        AddChild(_wheelPivot);
        var spoke = onModel ? Trim : Black;
        if (!onModel) _wheelSpin.AddChild(Part(new TorusMesh { InnerRadius = radius - 0.02f, OuterRadius = radius + 0.02f, Rings = 24, RingSegments = 8 }, Black));
        _wheelSpin.AddChild(Bar(spoke, new Vector3(-radius, 0, 0), new Vector3(radius, 0, 0), 0.045f, 0.02f));
        _wheelSpin.AddChild(Bar(spoke, Vector3.Zero, new Vector3(0, 0, radius), 0.045f, 0.02f));
        _wheelSpin.AddChild(Box(DarkMetal, -0.06f, 0.06f, -0.02f, 0.03f, -0.06f, 0.06f));
        if (!onModel) _wheelPivot.AddChild(Bar(Black, new Vector3(0, -0.02f, 0), new Vector3(0, -0.30f, 0), 0.06f)); // the column, down into the dash
        foreach (var side in new[] { 1f, -1f })
            Hand(_wheelPivot, _wheelSpin, new Vector3(side * radius * 0.87f, 0.02f, -radius * 0.5f), elbows + new Vector3(side * elbowSpread, 0, 0));
    }

    /// <summary>A gloved hand at <paramref name="grip"/> on <paramref name="carrier"/> and its forearm from <paramref name="elbow"/> (rig frame).</summary>
    void Hand(Node3D mount, Node3D carrier, Vector3 grip, Vector3 elbow)
    {
        var hand = Box(Glove, -0.05f, 0.05f, -0.035f, 0.035f, -0.06f, 0.06f);
        hand.Position = grip;
        carrier.AddChild(hand);
        var forearm = Part(new BoxMesh { Size = new Vector3(0.085f, 0.085f, 1) }, Sleeve);
        AddChild(forearm);
        _arms.Add(new Arm(mount, carrier, grip, forearm, elbow));
    }

    // ------------------------------------------------------------------ the bus
    // The bus's model has its interior (scripts/make_bus_model.py: the cab with its dashboard, the saloon, the glass partition behind the
    // driver), so nothing is hidden and nothing is rebuilt: the driver looks round the real bus and sees the riders. The model's steering
    // wheel is a plain disc on its column, its face 0.045 m along the column from (-0.70, 1.22, -5.095) and 0.235 m in radius; the rig lays
    // spokes on that face and puts the hands on its rim. The eye is lower than a seated driver's, so the wheel and the hands are in the view.

    static void BuildBus(CockpitRig r)
    {
        var axis = new Vector3(0, 0.20f, 0.11f).Normalized(); // the model's column, up and back at the driver
        var face = new Vector3(-0.70f, 1.22f, -5.095f) + axis * 0.062f;
        r.SteeringWheel(face, 0.215f, Mathf.RadToDeg(Mathf.Atan2(axis.Z, axis.Y)), new Vector3(-0.70f, 1.16f, -4.80f), 0.32f, onModel: true);
    }

    // ------------------------------------------------------------------ the flak truck
    // After scripts/make_flak_model.py: the cab is a box x +-1.2, y 0.95 .. 1.78, z -2.15 .. -0.80 with a glasshouse over it that narrows to
    // x +-1.03 at its top and leans back at the front; the bonnet runs from z -3.36 (top 1.48) to the cab (top 1.76). The rig's glasshouse is
    // 0.1 m taller than the model's, for the view.

    static void BuildFlak(CockpitRig r)
    {
        const float sill = 1.78f, top = 2.38f, xs = 1.17f, xt = 1.03f, zf = -2.12f, zft = -1.76f, zb = -0.83f, zbt = -0.90f;
        // floor, bonnet, snorkel
        r.Add(Box(OliveDark, -xs, xs, 0.90f, 0.95f, -2.15f, -0.80f));
        r.Add(Bar(Olive, new Vector3(0, 1.61f, -2.13f), new Vector3(0, 1.33f, -3.36f), 1.60f, 0.30f));
        r.Add(Bar(DarkMetal, new Vector3(0.62f, 1.60f, -2.75f), new Vector3(0.62f, 2.02f, -2.75f), 0.11f), Box(OliveDark, 0.50f, 0.74f, 2.02f, 2.12f, -2.87f, -2.63f));
        // dash under the windscreen, the instrument cluster in front of the driver
        r.Add(Box(OliveDark, -1.15f, 1.15f, 1.40f, sill, zf, -1.88f), Box(Black, -0.80f, -0.20f, 1.48f, 1.74f, -1.88f, -1.87f));
        r.Add(Gauge(-0.62f, 1.61f, -1.868f, 0.085f), Gauge(-0.38f, 1.61f, -1.868f, 0.085f));
        r.SteeringWheel(new Vector3(-0.50f, 1.66f, -1.72f), 0.20f, 45f, new Vector3(-0.50f, 1.50f, -1.30f), 0.28f);
        // windscreen: side posts, the centre post of the split screen, the header; then the roof
        r.Both(s => Bar(Olive, new Vector3(s * xs, sill, zf), new Vector3(s * xt, top, zft), 0.10f));
        r.Add(Bar(Olive, new Vector3(0, sill, zf), new Vector3(0, top, zft), 0.07f), Bar(Olive, new Vector3(-xt, top, zft), new Vector3(xt, top, zft), 0.09f));
        r.Add(Box(OliveLight, -xt, xt, top, top + 0.06f, zft, zbt));
        // doors: the panel under the window, the rail over it, the post behind
        r.Both(s => Box(OliveLight, s * xs, s * (xs + 0.03f), 0.95f, sill, -2.15f, -0.80f));
        r.Both(s => Bar(Olive, new Vector3(s * xt, top, zft), new Vector3(s * xt, top, zbt), 0.09f));
        r.Both(s => Bar(Olive, new Vector3(s * xs, sill, zb), new Vector3(s * xt, top, zbt), 0.10f));
        // glass: the two panes of the windscreen, the door windows, the rear window
        r.Both(s => Pane(new Vector3(0, sill, zf), new Vector3(s * xs, sill, zf), new Vector3(s * xt, top, zft), new Vector3(0, top, zft)));
        r.Both(s => Pane(new Vector3(s * xs, sill, zf), new Vector3(s * xs, sill, zb), new Vector3(s * xt, top, zbt), new Vector3(s * xt, top, zft)));
        r.Add(Pane(new Vector3(-0.47f, sill + 0.09f, -0.865f), new Vector3(0.47f, sill + 0.09f, -0.865f), new Vector3(0.47f, top - 0.12f, -0.865f), new Vector3(-0.47f, top - 0.12f, -0.865f)));
        // back wall with the rear window (the turret shows through it)
        r.Add(Box(OliveLight, -xs, xs, 0.95f, sill + 0.09f, zb, zb + 0.03f), Box(OliveLight, -xt, xt, top - 0.12f, top, zbt, zbt + 0.03f));
        r.Both(s => Box(OliveLight, s * 0.47f, s * 1.10f, sill, top, -0.88f, -0.85f));
    }

    // ------------------------------------------------------------------ the utility helicopter
    // After scripts/make_utility_helicopter_model.py: floor 0.72, instrument panel at z -2.75, the cabin from z -1.20 to 1.30, half width 1.2,
    // roof slab 2.12 .. 2.25, the windscreen from the sill (y 1.52 at z -2.75) back to the roof at z -2.00. The pilot sits front right. The
    // cyclic is taller and further forward than the model's, so the hand on it shows under the panel.

    static void BuildHelicopter(CockpitRig r)
    {
        const float floor = 0.72f, panel = -2.75f, cabin0 = -1.20f, cabin1 = 1.30f, half = 1.2f, roof0 = 2.12f, roof1 = 2.25f;
        // floor, nose, the coaming across the sill, instrument panel with gauges on the pilot's side, centre console
        r.Add(Box(DarkMetal, -1.14f, 1.14f, floor - 0.06f, floor, panel, cabin1));
        r.Add(Bar(Olive, new Vector3(0, 1.36f, -2.78f), new Vector3(0, 1.00f, -3.70f), 1.70f, 0.24f));
        r.Add(Box(OliveDark, -1.14f, 1.14f, 1.44f, 1.52f, -2.82f, -2.62f), Box(Black, -0.62f, 0.62f, 1.10f, 1.56f, -2.77f, -2.65f));
        for (var row = 0; row < 2; row++)
        for (var col = 0; col < 4; col++)
            r.AddChild(Gauge(0.10f + col * 0.145f, 1.24f + row * 0.17f, -2.643f, 0.055f));
        r.Add(Gauge(-0.20f, 1.41f, -2.643f, 0.055f), Gauge(-0.40f, 1.41f, -2.643f, 0.055f), Box(DarkMetal, -0.16f, 0.16f, floor, 1.10f, -2.70f, -2.15f));
        // the bulkhead under the coaming, from door to door: the nose behind it is the model's and is not drawn
        r.Add(Box(OliveDark, -1.17f, 1.17f, floor - 0.10f, 1.46f, -2.84f, -2.78f));
        // glass: the two panes of the windscreen, the door windows, the overhead windows
        r.Both(s => Pane(new Vector3(s * 0.04f, 1.52f, panel), new Vector3(s * 1.09f, 1.52f, panel), new Vector3(s * 1.11f, roof0 + 0.04f, -2.00f), new Vector3(s * 0.04f, roof0 + 0.04f, -2.00f)));
        r.Both(s => Pane(new Vector3(s * 1.145f, 1.38f, panel), new Vector3(s * 1.145f, roof0, -2.00f), new Vector3(s * 1.145f, roof0, cabin0 - 0.08f), new Vector3(s * 1.145f, 1.38f, cabin0 - 0.08f)));
        r.Both(s => Pane(new Vector3(s * 0.25f, roof0 + 0.07f, -1.90f), new Vector3(s * 0.95f, roof0 + 0.07f, -1.90f), new Vector3(s * 0.95f, roof0 + 0.07f, -1.40f), new Vector3(s * 0.25f, roof0 + 0.07f, -1.40f)));
        // windscreen posts, door sills and the posts behind the doors
        r.Both(s => Bar(OliveDark, new Vector3(s * 1.13f, 1.50f, panel), new Vector3(s * 1.15f, roof0 + 0.05f, -2.00f), 0.09f));
        r.Add(Bar(OliveDark, new Vector3(0, 1.50f, panel), new Vector3(0, roof0 + 0.05f, -2.00f), 0.08f));
        r.Both(s => Box(OliveLight, s * 1.12f, s * 1.17f, floor - 0.10f, 1.38f, panel, cabin0 - 0.08f));
        r.Both(s => Bar(OliveDark, new Vector3(s * 1.15f, 1.38f, cabin0 - 0.08f), new Vector3(s * 1.15f, roof0, cabin0 - 0.08f), 0.09f));
        // roof: a frame round the two overhead windows (the rotor shows through them), then the slab over the cabin
        r.Add(Box(OliveLight, -half, half, roof0, roof1, -2.00f, -1.90f), Box(OliveLight, -half, half, roof0, roof1, -1.40f, cabin1));
        r.Add(Box(OliveLight, -0.25f, 0.25f, roof0, roof1, -1.90f, -1.40f));
        r.Both(s => Box(OliveLight, s * 0.95f, s * half, roof0, roof1, -1.90f, -1.40f));
        // both seats, the cabin's rear wall
        r.Both(s => Box(OliveDark, s * 0.28f, s * 0.82f, floor, 1.02f, -2.02f, -1.52f));
        r.Both(s => Box(OliveDark, s * 0.28f, s * 0.82f, 1.02f, 1.86f, -1.56f, -1.47f));
        r.Add(Box(OliveLight, -half, half, floor, roof0, cabin1, cabin1 + 0.05f), Box(Khaki, -0.80f, 0.80f, floor, 1.15f, cabin1 - 0.45f, cabin1));
        // the co-pilot's cyclic (still), the pilot's cyclic and collective with the hands on them
        r.Add(Bar(DarkMetal, new Vector3(-0.55f, floor, -2.40f), new Vector3(-0.55f, 1.36f, -2.36f), 0.035f));
        r._cyclic = new Node3D { Position = new Vector3(0.55f, floor, -2.40f) };
        r._cyclic.AddChild(Bar(DarkMetal, Vector3.Zero, new Vector3(0, 0.62f, 0.04f), 0.035f));
        r._cyclic.AddChild(Bar(Black, new Vector3(0, 0.60f, 0.04f), new Vector3(0, 0.74f, 0.02f), 0.05f));
        r.AddChild(r._cyclic);
        r.Hand(r._cyclic, r._cyclic, new Vector3(0, 0.68f, 0.03f), new Vector3(0.82f, 1.22f, -1.74f));
        r._collective = new Node3D { Position = new Vector3(0.20f, 0.82f, -1.62f) };
        r._collective.AddChild(Bar(DarkMetal, Vector3.Zero, new Vector3(0, 0.14f, -0.40f), 0.035f));
        r._collective.AddChild(Bar(Black, new Vector3(0, 0.13f, -0.36f), new Vector3(0, 0.17f, -0.48f), 0.05f));
        r.AddChild(r._collective);
        r.Hand(r._collective, r._collective, new Vector3(0, 0.15f, -0.42f), new Vector3(0.30f, 1.24f, -1.70f));
    }
}
