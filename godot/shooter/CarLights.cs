using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Lamps of one vehicle (vehicles.json "lights", offsets in the vehicle frame). Always: emissive lamp lenses, red tail lamps that
/// brighten while braking, with a small soft glow around each lamp so it reads at a distance. While the headlights are on, a real
/// SpotLight3D per headlamp for the nearest <see cref="MaxBeams"/> lit vehicles (the spot budget: 2 vehicles x 2 beams = 4 spot
/// lights, no shadows). Other lit vehicles show the glowing lamps only. A model with emissive lamp materials (<see cref="HeadLensMaterial"/> and
/// friends, the Golf Mk2) has them driven here from the same state, on a per-vehicle copy of each material, and gets no extra lens spheres. (There used to be a drawn light cone; it showed as a huge
/// faceted shape from behind and is gone.)
/// </summary>
public sealed class CarLights
{
    /// <summary>Lit vehicles per frame that get real spot lights (see <see cref="NetClient"/> for the choice).</summary>
    public const int MaxBeams = 2;

    const float HeadOnEnergy = 3f, HeadOffEnergy = 0.3f, TailEnergy = 0.5f, BrakeEnergy = 2.5f;
    /// <summary>Glow sizes, metres: headlamps when lit, tail lamps at rest and braking.</summary>
    const float HeadGlow = 0.7f, TailGlowRest = 0.12f, TailGlowBrake = 0.45f;
    /// <summary>Emission energy of a model's lamp materials (their glTF emissive colour is the base): tails dim when the lights are on, bright while braking, off otherwise; reverse lamps only while reversing.</summary>
    const float ModelTailOn = 1f, ModelTailBrake = 5f, ModelReverseOn = 2f;
    /// <summary>Material names of the lamps a model may carry (assets/README.md, scripts/make_golf_mk2_model.py).</summary>
    public const string HeadLensMaterial = "Emissive warm headlight lens", TailLampMaterial = "Emissive red tail lamps",
        ReverseLampMaterial = "Emissive reverse lamps", IndicatorMaterial = "Emissive amber indicators";
    /// <summary>The glow quads sit this fraction of their size outward from the lamp (along the vehicle's length), so the body around the lamp does not slice them.</summary>
    const float GlowStandOff = 0.5f;
    static Texture2D? _glowTexture;
    static readonly Color HeadColour = new(1f, 0.95f, 0.8f), TailColour = new(1f, 0.08f, 0.04f);

    readonly StandardMaterial3D _headMat, _tailMat;
    /// <summary>This vehicle's own copies of the model's lamp materials (null where the model has none).</summary>
    StandardMaterial3D? _modelHead, _modelTail, _modelReverse;
    readonly List<SpotLight3D> _spots = new();
    readonly List<MeshInstance3D> _headGlows = new(), _tailGlows = new();
    readonly List<Vector3> _tailLamps = new();
    bool _on, _braking, _beam, _reversing;
    bool _set;

    CarLights(StandardMaterial3D head, StandardMaterial3D tail) { _headMat = head; _tailMat = tail; }

    /// <summary>Adds the lamps of <paramref name="def"/> under the vehicle node (origin on the ground, facing -Z). Null for a type without a lights block.</summary>
    public static CarLights? Attach(Node3D vehicle, VehicleDef def)
    {
        if (def.Lights is not { } l) return null;
        var headMat = new StandardMaterial3D { AlbedoColor = HeadColour, EmissionEnabled = true, Emission = HeadColour, EmissionEnergyMultiplier = HeadOffEnergy };
        var tailMat = new StandardMaterial3D { AlbedoColor = TailColour, EmissionEnabled = true, Emission = TailColour, EmissionEnergyMultiplier = TailEnergy };
        var glowMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add, BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            AlbedoTexture = GlowTexture(), AlbedoColor = new Color(HeadColour.R, HeadColour.G, HeadColour.B, 0.6f), NoDepthTest = false,
        };
        var tailGlowMat = (StandardMaterial3D)glowMat.Duplicate();
        tailGlowMat.AlbedoColor = new Color(TailColour.R, TailColour.G, TailColour.B, 0.7f);
        var lens = new SphereMesh { Radius = 0.06f, Height = 0.12f, RadialSegments = 8, Rings = 4 };
        var glow = new QuadMesh { Size = Vector2.One };
        var c = new CarLights(headMat, tailMat);
        c.BindModelLamps(vehicle);
        var range = l.BeamRange;
        var coneEnd = MathF.Tan(Mathf.DegToRad(l.BeamAngle)) * range;
        foreach (var p in l.Head)
        {
            var at = Lamp(p);
            if (c._modelHead == null) vehicle.AddChild(new MeshInstance3D { Mesh = lens, Position = at, MaterialOverride = headMat });
            var spot = new SpotLight3D
            {
                Position = at, SpotRange = range, SpotAngle = l.BeamAngle, SpotAttenuation = 1f, LightColor = HeadColour,
                LightEnergy = 3f, ShadowEnabled = false, Visible = false,
            };
            vehicle.AddChild(spot);
            c._spots.Add(spot);
            var g = new MeshInstance3D { Mesh = glow, Position = GlowAt(at, HeadGlow), MaterialOverride = glowMat, Scale = Vector3.One * HeadGlow, Visible = false };
            vehicle.AddChild(g);
            c._headGlows.Add(g);
        }
        foreach (var p in l.Tail)
        {
            if (c._modelTail == null) vehicle.AddChild(new MeshInstance3D { Mesh = lens, Position = Lamp(p), MaterialOverride = tailMat });
            var g = new MeshInstance3D { Mesh = glow, Position = GlowAt(Lamp(p), TailGlowRest), MaterialOverride = tailGlowMat, Scale = Vector3.One * TailGlowRest };
            vehicle.AddChild(g);
            c._tailGlows.Add(g);
            c._tailLamps.Add(Lamp(p));
        }
        return c;
    }

    /// <summary>
    /// Finds the model's emissive lamp materials by name and gives this vehicle its own copy of each (Node.Duplicate shares the resources, so a
    /// shared one would light every vehicle of the type). Indicators stay off.
    /// </summary>
    void BindModelLamps(Node3D vehicle)
    {
        Dictionary<string, StandardMaterial3D> copies = new();
        foreach (var mi in vehicle.FindChildren("*", "MeshInstance3D", true, false).Cast<MeshInstance3D>())
            for (var i = 0; i < mi.Mesh?.GetSurfaceCount(); i++)
            {
                if (mi.Mesh!.SurfaceGetMaterial(i) is not StandardMaterial3D bm) continue;
                var name = bm.ResourceName;
                if (name is not (HeadLensMaterial or TailLampMaterial or ReverseLampMaterial or IndicatorMaterial)) continue;
                if (!copies.TryGetValue(name, out var copy))
                {
                    copy = (StandardMaterial3D)bm.Duplicate();
                    copy.EmissionEnabled = true;
                    copy.EmissionEnergyMultiplier = name == IndicatorMaterial ? 0f : name == HeadLensMaterial ? HeadOffEnergy : 0f;
                    copies[name] = copy;
                }
                mi.SetSurfaceOverrideMaterial(i, copy);
            }
        copies.TryGetValue(HeadLensMaterial, out _modelHead);
        copies.TryGetValue(TailLampMaterial, out _modelTail);
        copies.TryGetValue(ReverseLampMaterial, out _modelReverse);
    }

    /// <summary>A soft round falloff, white in the middle to clear at the edge (shared).</summary>
    static Texture2D GlowTexture() => _glowTexture ??= new GradientTexture2D
    {
        Width = 64, Height = 64, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(0.5f, 0f),
        Gradient = new Gradient { Colors = new[] { new Color(1, 1, 1, 1), new Color(1, 1, 1, 0.25f), new Color(1, 1, 1, 0) }, Offsets = new[] { 0f, 0.35f, 1f } },
    };

    static Vector3 Lamp(float[] p) => new(p[0], p[1], p[2]);

    /// <summary>Glow position for a lamp: pushed outward (front lamps toward -Z, rear toward +Z) by <see cref="GlowStandOff"/> of its size.</summary>
    static Vector3 GlowAt(Vector3 lamp, float size) => lamp + new Vector3(0, 0, MathF.Sign(lamp.Z) * size * GlowStandOff);

    /// <summary>Per frame: the switched state, the braking state, whether this vehicle gets its real spot lights this frame and whether it reverses. Cheap when nothing changed.</summary>
    public void Set(bool on, bool braking, bool beam, bool reversing = false)
    {
        if (_set && on == _on && braking == _braking && beam == _beam && reversing == _reversing) return;
        _set = true; _on = on; _braking = braking; _beam = beam; _reversing = reversing;
        if (_modelHead != null) _modelHead.EmissionEnergyMultiplier = on ? HeadOnEnergy : HeadOffEnergy;
        if (_modelTail != null) _modelTail.EmissionEnergyMultiplier = braking ? ModelTailBrake : on ? ModelTailOn : 0f;
        if (_modelReverse != null) _modelReverse.EmissionEnergyMultiplier = reversing ? ModelReverseOn : 0f;
        _tailMat.EmissionEnergyMultiplier = braking ? BrakeEnergy : TailEnergy;
        _headMat.EmissionEnergyMultiplier = on ? HeadOnEnergy : HeadOffEnergy;
        foreach (var g in _headGlows) g.Visible = on;
        var tail = braking ? TailGlowBrake : TailGlowRest;
        for (var i = 0; i < _tailGlows.Count; i++) { _tailGlows[i].Scale = Vector3.One * tail; _tailGlows[i].Position = GlowAt(_tailLamps[i], tail); }
        foreach (var s in _spots) s.Visible = on && beam;
    }
}
