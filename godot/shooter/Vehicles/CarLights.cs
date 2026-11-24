using Godot;
using Drycastle.Shooter.Scenery;
using Drycastle.ShooterCore.Vehicles;

namespace Drycastle.Shooter.Vehicles;

/// <summary>
/// Lamps of one vehicle (vehicles.json "lights", offsets in the vehicle frame). Always: emissive lamp lenses and red tail lamps that
/// brighten while braking. Lit lamps are driven well above the environment's glow threshold (<see cref="DayNightCycle"/>), so the
/// bloom post-effect gives them their halo; there are no glow quads (they were clipped by the body, floated off it from the side and
/// did not lean with it). Lamp nodes hang on the model's "hull" (the body that leans), not on the vehicle root. While the headlights
/// are on, a real SpotLight3D per headlamp for the nearest <see cref="MaxBeams"/> lit vehicles (the spot budget: 2 vehicles x 2 beams = 4
/// spot lights, no shadows). A model with emissive lamp materials (<see cref="HeadLensMaterial"/> and friends, the Golf Mk2) has them
/// driven here from the same state, on a per-vehicle copy of each material, and gets no extra lens spheres. (There used to be a drawn
/// light cone; it showed as a huge faceted shape from behind and is gone.)
/// </summary>
public sealed class CarLights
{
    /// <summary>Lit vehicles per frame that get real spot lights (see <see cref="NetClient"/> for the choice).</summary>
    public const int MaxBeams = 2;

    /// <summary>
    /// Emission energy of the lamp lenses. The environment's glow threshold is <see cref="DayNightCycle.GlowThreshold"/> (full bloom from
    /// threshold + <see cref="DayNightCycle.GlowRamp"/>): lit values are well above it, off and dim ones below it. Colours peak at 1, so the
    /// energy is the HDR value: tails on bloom softly (4), braking sits at <see cref="DayNightCycle.GlowCap"/> for four times the halo.
    /// </summary>
    /// <summary>Spot light energy of one headlamp beam. High because the night exposure is low (0.2).</summary>
    const float BeamEnergy = 12f;
    const float HeadOnEnergy = 6f, HeadOffEnergy = 0.3f, TailEnergy = 0.5f, TailOnEnergy = 4f, BrakeEnergy = DayNightCycle.GlowCap;
    /// <summary>Model lamp materials: tail and reverse lamps get one colour here (the glTF emissive factors differ per model: tail 0.17 Synty, 0.5 Golf), so one set of energies fits every vehicle; tails use the fallback lens energies and are off with the lights off; reverse lamps bloom only while reversing.</summary>
    const float ModelReverseOn = 6f;
    /// <summary>Material names of the lamps a model may carry (assets/README.md, scripts/models/make_golf_mk2_model.py).</summary>
    public const string HeadLensMaterial = "Emissive warm headlight lens", TailLampMaterial = "Emissive red tail lamps",
        ReverseLampMaterial = "Emissive reverse lamps", IndicatorMaterial = "Emissive amber indicators";
    static readonly Color HeadColour = new(1f, 0.95f, 0.8f), TailColour = new(1f, 0.08f, 0.04f), ReverseColour = new(0.95f, 1f, 0.9f);

    readonly StandardMaterial3D _headMat, _tailMat;
    /// <summary>This vehicle's own copies of the model's lamp materials (null where the model has none).</summary>
    StandardMaterial3D? _modelHead, _modelTail, _modelReverse;
    readonly List<SpotLight3D> _spots = new();
    bool _on, _braking, _beam, _reversing;
    bool _set;

    CarLights(StandardMaterial3D head, StandardMaterial3D tail) { _headMat = head; _tailMat = tail; }

    /// <summary>Adds the lamps of <paramref name="def"/> under the vehicle node (origin on the ground, facing -Z). Null for a type without a lights block.</summary>
    public static CarLights? Attach(Node3D vehicle, VehicleDef def)
    {
        if (def.Lights is not { } l) return null;
        var headMat = LampKit.Lens(HeadColour, HeadOffEnergy);
        var tailMat = LampKit.Lens(TailColour, TailEnergy);
        var lens = new SphereMesh { Radius = 0.06f, Height = 0.12f, RadialSegments = 8, Rings = 4 };
        var c = new CarLights(headMat, tailMat);
        c.BindModelLamps(vehicle);
        // lamps follow the body: the hull leans (BodyPitch/BodyRoll), the vehicle root does not
        var host = vehicle.FindChild("hull", true, false) as Node3D ?? vehicle;
        var toHost = HostFrame(host, vehicle).AffineInverse();
        var range = l.BeamRange;
        foreach (var p in l.Head)
        {
            var at = toHost * Lamp(p);
            if (c._modelHead == null) host.AddChild(new MeshInstance3D { Mesh = lens, Transform = at, MaterialOverride = headMat });
            var spot = LampKit.Beam(at, HeadColour, range, l.BeamAngle, BeamEnergy);
            host.AddChild(spot);
            c._spots.Add(spot);
        }
        foreach (var p in l.Tail)
            if (c._modelTail == null) host.AddChild(new MeshInstance3D { Mesh = lens, Transform = toHost * Lamp(p), MaterialOverride = tailMat });
        return c;
    }

    /// <summary><paramref name="host"/>'s transform relative to <paramref name="vehicle"/> (identity when they are the same node or the hull is at rest).</summary>
    static Transform3D HostFrame(Node3D host, Node3D vehicle)
    {
        var t = Transform3D.Identity;
        for (Node? n = host; n is Node3D n3 && n3 != vehicle; n = n.GetParent()) t = n3.Transform * t;
        return t;
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
                    if (name == TailLampMaterial) copy.Emission = TailColour;
                    if (name == ReverseLampMaterial) copy.Emission = ReverseColour;
                    copy.EmissionEnergyMultiplier = name == IndicatorMaterial ? 0f : name == HeadLensMaterial ? HeadOffEnergy : 0f;
                    copies[name] = copy;
                }
                mi.SetSurfaceOverrideMaterial(i, copy);
            }
        copies.TryGetValue(HeadLensMaterial, out _modelHead);
        copies.TryGetValue(TailLampMaterial, out _modelTail);
        copies.TryGetValue(ReverseLampMaterial, out _modelReverse);
    }

    /// <summary>A lamp offset as a pure translation (the lamp frame is the vehicle's).</summary>
    static Transform3D Lamp(float[] p) => new(Basis.Identity, new Vector3(p[0], p[1], p[2]));

    /// <summary>Per frame: the switched state, the braking state, whether this vehicle gets its real spot lights this frame and whether it reverses. Cheap when nothing changed.</summary>
    public void Set(bool on, bool braking, bool beam, bool reversing = false)
    {
        if (_set && on == _on && braking == _braking && beam == _beam && reversing == _reversing) return;
        _set = true; _on = on; _braking = braking; _beam = beam; _reversing = reversing;
        if (_modelHead != null) _modelHead.EmissionEnergyMultiplier = on ? HeadOnEnergy : HeadOffEnergy;
        if (_modelTail != null) _modelTail.EmissionEnergyMultiplier = braking ? BrakeEnergy : on ? TailOnEnergy : 0f;
        if (_modelReverse != null) _modelReverse.EmissionEnergyMultiplier = reversing ? ModelReverseOn : 0f;
        _tailMat.EmissionEnergyMultiplier = braking ? BrakeEnergy : on ? TailOnEnergy : TailEnergy;
        _headMat.EmissionEnergyMultiplier = on ? HeadOnEnergy : HeadOffEnergy;
        foreach (var s in _spots) s.Visible = on && beam;
    }
}
