using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// Lamps of one vehicle (vehicles.json "lights", offsets in the vehicle frame). Always: emissive lamp lenses, red tail lamps that
/// brighten while braking. While the headlights are on: a faint light cone per headlamp, and a real SpotLight3D per headlamp for the
/// nearest <see cref="MaxBeams"/> lit vehicles (the spot budget: 2 vehicles x 2 beams = 4 spot lights, no shadows). Other lit
/// vehicles show the cones and the glowing lenses only.
/// </summary>
public sealed class CarLights
{
    /// <summary>Lit vehicles per frame that get real spot lights (see <see cref="NetClient"/> for the choice).</summary>
    public const int MaxBeams = 2;

    const float HeadOnEnergy = 4f, HeadOffEnergy = 0.4f, TailEnergy = 0.8f, BrakeEnergy = 4f;
    static readonly Color HeadColour = new(1f, 0.95f, 0.8f), TailColour = new(1f, 0.08f, 0.04f);

    readonly StandardMaterial3D _headMat, _tailMat;
    readonly List<SpotLight3D> _spots = new();
    readonly List<MeshInstance3D> _cones = new();
    bool _on, _braking, _beam;
    bool _set;

    CarLights(StandardMaterial3D head, StandardMaterial3D tail) { _headMat = head; _tailMat = tail; }

    /// <summary>Adds the lamps of <paramref name="def"/> under the vehicle node (origin on the ground, facing -Z). Null for a type without a lights block.</summary>
    public static CarLights? Attach(Node3D vehicle, VehicleDef def)
    {
        if (def.Lights is not { } l) return null;
        var headMat = new StandardMaterial3D { AlbedoColor = HeadColour, EmissionEnabled = true, Emission = HeadColour, EmissionEnergyMultiplier = HeadOffEnergy };
        var tailMat = new StandardMaterial3D { AlbedoColor = TailColour, EmissionEnabled = true, Emission = TailColour, EmissionEnergyMultiplier = TailEnergy };
        var coneMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            AlbedoColor = new Color(HeadColour.R, HeadColour.G, HeadColour.B, 0.07f),
        };
        var lens = new SphereMesh { Radius = 0.09f, Height = 0.18f, RadialSegments = 8, Rings = 4 };
        var c = new CarLights(headMat, tailMat);
        var range = l.BeamRange;
        var coneEnd = MathF.Tan(Mathf.DegToRad(l.BeamAngle)) * range;
        foreach (var p in l.Head)
        {
            var at = Lamp(p);
            vehicle.AddChild(new MeshInstance3D { Mesh = lens, Position = at, MaterialOverride = headMat });
            var spot = new SpotLight3D
            {
                Position = at, SpotRange = range, SpotAngle = l.BeamAngle, SpotAttenuation = 1f, LightColor = HeadColour,
                LightEnergy = 3f, ShadowEnabled = false, Visible = false,
            };
            vehicle.AddChild(spot);
            c._spots.Add(spot);
            // a cone from the lamp out to the beam's reach: the cylinder's top (+Y) turned to -Z is the far, wide end
            var cone = new CylinderMesh { TopRadius = coneEnd, BottomRadius = 0.05f, Height = range, RadialSegments = 12, Rings = 1, CapTop = false, CapBottom = false };
            var m = new MeshInstance3D { Mesh = cone, Position = at + new Vector3(0, 0, -range / 2), Rotation = new Vector3(-MathF.PI / 2, 0, 0), MaterialOverride = coneMat, Visible = false };
            vehicle.AddChild(m);
            c._cones.Add(m);
        }
        foreach (var p in l.Tail)
            vehicle.AddChild(new MeshInstance3D { Mesh = lens, Position = Lamp(p), MaterialOverride = tailMat });
        return c;
    }

    static Vector3 Lamp(float[] p) => new(p[0], p[1], p[2]);

    /// <summary>Per frame: the switched state, the braking state and whether this vehicle gets its real spot lights this frame. Cheap when nothing changed.</summary>
    public void Set(bool on, bool braking, bool beam)
    {
        if (_set && on == _on && braking == _braking && beam == _beam) return;
        _set = true; _on = on; _braking = braking; _beam = beam;
        _tailMat.EmissionEnergyMultiplier = braking ? BrakeEnergy : TailEnergy;
        _headMat.EmissionEnergyMultiplier = on ? HeadOnEnergy : HeadOffEnergy;
        foreach (var m in _cones) m.Visible = on;
        foreach (var s in _spots) s.Visible = on && beam;
    }
}
