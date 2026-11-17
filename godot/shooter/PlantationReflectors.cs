using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The reflector stands of a plantation (plantation.json <c>reflectors</c>; the stand itself is a placed object of the map, solid by its own model). Each shines a real spot light from its lamp bar
/// along the way it is turned and a little down, the kind a vehicle's headlamp is (<see cref="CarLights"/>: no shadow), and wears a lit bar in front of its lamps that blooms (ADR 0030). They come on by
/// themselves at night on the street lamps' curve (<see cref="LampCurve"/>, <see cref="DayNightCycle.NightFactor"/>) and go out by day; a beam runs only while its stand is near the camera. Client
/// only: nothing here is game state. Not seen in the game: the numbers are data.
/// </summary>
public partial class PlantationReflectors : Node3D
{
    /// <summary>The stands' transforms in map space (the placed objects).</summary>
    public IReadOnlyList<Transform3D> Stands { get; init; } = Array.Empty<Transform3D>();
    public ReflectorData Data { get; init; } = new();

    readonly List<SpotLight3D> _beams = new();
    StandardMaterial3D _lens = new();
    float _level = -1f;

    public override void _Ready()
    {
        if (Stands.Count == 0 || DisplayServer.GetName() == "headless") { SetProcess(false); return; }
        var colour = new Color(Data.Colour);
        _lens = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = colour, EmissionEnabled = true, Emission = colour, EmissionEnergyMultiplier = 0f };
        var bar = new BoxMesh { Size = new Vector3((float)Data.Lens[0], (float)Data.Lens[1], 0.01f) };
        var head = new Vector3((float)Data.Head[0], (float)Data.Head[1], (float)Data.Head[2]);
        // a spot shines along its own -Z, the model along +Z: turned round, then tipped
        var aim = new Basis(Vector3.Up, Mathf.Pi) * new Basis(Vector3.Right, Mathf.DegToRad((float)Data.PitchDeg));
        foreach (var stand in Stands)
        {
            var at = stand * new Transform3D(Basis.Identity, head);
            AddChild(new MeshInstance3D { Mesh = bar, MaterialOverride = _lens, Transform = at, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            var beam = new SpotLight3D
            {
                Transform = at * new Transform3D(aim, Vector3.Zero), SpotRange = (float)Data.RangeM, SpotAngle = (float)Data.AngleDeg, SpotAttenuation = 1f,
                LightColor = colour, LightEnergy = 0f, ShadowEnabled = false, Visible = false,
            };
            AddChild(beam);
            _beams.Add(beam);
        }
    }

    public override void _Process(double delta)
    {
        var level = LampCurve.Level(DayNightCycle.Current?.NightFactor ?? 0f);
        if (level != _level)
        {
            _level = level;
            _lens.EmissionEnergyMultiplier = (float)Data.LensEnergy * level;
            foreach (var b in _beams) b.LightEnergy = (float)Data.Energy * level;
        }
        var cam = GetViewport().GetCamera3D();
        var cull = (float)(Data.CullM * Data.CullM);
        foreach (var b in _beams)
            b.Visible = level > 0.01f && (cam == null || b.GlobalPosition.DistanceSquaredTo(cam.GlobalPosition) <= cull);
    }
}
