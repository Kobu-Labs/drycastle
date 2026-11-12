using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// The aircraft models (ticket #125). No free CC0 aircraft model was hand-checked for this ticket, so both kinds are built
/// from primitives: a helicopter (kind 0 in the table) and a gunship (kind 1). Rotors are child nodes that spin; the
/// rotor sound is generated (<see cref="RotorSound"/>), so nothing is loaded from the asset library.
/// Forward is -Z, as every model in the shooter.
/// </summary>
public sealed partial class AircraftModel : Node3D
{
    /// <summary>Each rotor with its axis and speed (radians per second).</summary>
    readonly List<(Node3D Rotor, Vector3 Axis, float Speed)> _spins = new();
    public static readonly Color Olive = new(0.29f, 0.33f, 0.20f), Dark = new(0.12f, 0.12f, 0.13f), Glass = new(0.18f, 0.26f, 0.32f), Grey = new(0.42f, 0.44f, 0.46f);

    /// <summary>Builds the model for a kind by its id in killstreaks.json ("helicopter" or "gunship"); other ids get the helicopter.</summary>
    public static AircraftModel Build(string kindId) => kindId == "gunship" ? BuildGunship() : BuildHelicopter();

    public void Spin(float dt)
    {
        foreach (var (rotor, axis, speed) in _spins) rotor.Rotate(axis, speed * dt);
    }

    static StandardMaterial3D Mat(Color c, float metal = 0.1f, float rough = 0.7f) =>
        new() { AlbedoColor = c, Metallic = metal, Roughness = rough };

    static MeshInstance3D Box(Node3D parent, Vector3 size, Vector3 at, StandardMaterial3D mat, string name = "")
    {
        var m = new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = mat, Position = at, Name = name };
        parent.AddChild(m);
        return m;
    }

    static Node3D Pivot(Node3D parent, Vector3 at, string name)
    {
        var n = new Node3D { Position = at, Name = name };
        parent.AddChild(n);
        return n;
    }

    static AircraftModel BuildHelicopter()
    {
        var m = new AircraftModel { Name = "Helicopter" };
        var olive = Mat(Olive); var dark = Mat(Dark, 0.4f, 0.5f); var glass = Mat(Glass, 0.3f, 0.2f);
        Box(m, new Vector3(1.6f, 1.2f, 3.4f), Vector3.Zero, olive, "Cabin");
        Box(m, new Vector3(1.45f, 0.8f, 1.0f), new Vector3(0, 0.15f, -1.4f), glass, "Canopy");
        Box(m, new Vector3(0.4f, 0.4f, 3.2f), new Vector3(0, 0.2f, 2.6f), olive, "Boom");
        Box(m, new Vector3(0.1f, 1.0f, 0.7f), new Vector3(0, 0.75f, 4.1f), olive, "Fin");
        for (var s = -1; s <= 1; s += 2)
            Box(m, new Vector3(0.08f, 0.08f, 3.0f), new Vector3(0.7f * s, -0.85f, 0), dark, "Skid");

        // main rotor: two blades crossed, spinning about the mast (Y)
        var main = Pivot(m, new Vector3(0, 0.95f, 0), "MainRotor");
        Box(main, new Vector3(0.2f, 0.04f, 9.0f), Vector3.Zero, dark);
        Box(main, new Vector3(0.2f, 0.04f, 9.0f), Vector3.Zero, dark).RotateY(MathF.PI / 2);
        m._spins.Add((main, Vector3.Up, 2f * MathF.PI * 6f));

        // tail rotor: spins about the tail (X)
        var tail = Pivot(m, new Vector3(0.12f, 0.75f, 4.1f), "TailRotor");
        Box(tail, new Vector3(0.04f, 0.9f, 0.14f), Vector3.Zero, dark);
        Box(tail, new Vector3(0.04f, 0.14f, 0.9f), Vector3.Zero, dark);
        m._spins.Add((tail, Vector3.Right, 2f * MathF.PI * 24f));
        return m;
    }

    static AircraftModel BuildGunship()
    {
        var m = new AircraftModel { Name = "Gunship" };
        var grey = Mat(Grey, 0.2f, 0.6f); var dark = Mat(Dark, 0.4f, 0.5f); var glass = Mat(Glass, 0.3f, 0.2f);
        Box(m, new Vector3(1.8f, 1.8f, 12f), Vector3.Zero, grey, "Fuselage");
        Box(m, new Vector3(1.2f, 1.0f, 2.2f), new Vector3(0, 0.5f, -4.6f), glass, "Cockpit");
        Box(m, new Vector3(16f, 0.15f, 3f), new Vector3(0, 0.2f, -1f), grey, "Wings");
        Box(m, new Vector3(0.12f, 2.4f, 2.2f), new Vector3(0, 1.2f, 5.2f), grey, "Fin");
        Box(m, new Vector3(6f, 0.12f, 1.4f), new Vector3(0, 0.4f, 5.2f), grey, "Tailplane");
        // turret under the nose, where the rounds leave (GunshipControl.TurretDropM below the airframe)
        Box(m, new Vector3(1.0f, 0.9f, 1.4f), new Vector3(0, -1.2f, -2.5f), dark, "Turret");
        // four engines, each with a two-blade propeller spinning about its own axis (Z, the nacelle's)
        foreach (var x in new[] { -6f, -3.5f, 3.5f, 6f })
        {
            Box(m, new Vector3(0.8f, 0.8f, 1.6f), new Vector3(x, -0.2f, -1.6f), dark, "Engine");
            var prop = Pivot(m, new Vector3(x, -0.2f, -2.5f), "Prop");
            Box(prop, new Vector3(0.1f, 2.2f, 0.12f), Vector3.Zero, grey);
            Box(prop, new Vector3(0.1f, 2.2f, 0.12f), Vector3.Zero, grey).RotateZ(MathF.PI / 2);
            m._spins.Add((prop, Vector3.Forward, 2f * MathF.PI * 12f));
        }
        return m;
    }
}

/// <summary>
/// Rotor and engine sound of an aircraft, generated on the fly: a low-passed noise pulsed at the blade-pass frequency plus an
/// engine tone. A 3D player with a long range, so a gunship is heard across the map. Only created where audio runs.
/// </summary>
public partial class RotorSound : AudioStreamPlayer3D
{
    const float MixRate = 22050f;
    /// <summary>Blade-pass frequency (blades x revolutions per second) and the engine tone, Hz.</summary>
    public float BladeHz = 24f, EngineHz = 70f;
    /// <summary>Overall level, 0 to 1.</summary>
    public float Level = 0.5f;

    AudioStreamGeneratorPlayback? _pb;
    float _blade, _engine, _lp;
    readonly Random _rng = new(7);

    public override void _Ready()
    {
        var gen = new AudioStreamGenerator { MixRate = MixRate, BufferLength = 0.15f };
        Stream = gen;
        MaxDistance = 4000f;
        UnitSize = 120f;
        VolumeDb = -6f;
        Play();
        _pb = GetStreamPlayback() as AudioStreamGeneratorPlayback;
    }

    public override void _Process(double delta)
    {
        if (_pb == null) return;
        var n = _pb.GetFramesAvailable();
        for (var i = 0; i < n; i++)
        {
            _blade += BladeHz / MixRate;
            if (_blade >= 1f) _blade -= 1f;
            _engine += EngineHz / MixRate;
            if (_engine >= 1f) _engine -= 1f;
            var noise = (float)(_rng.NextDouble() * 2 - 1);
            _lp += 0.08f * (noise - _lp);
            // a blade slap: a sharp pulse once per blade, the noise shaped by it
            var slap = MathF.Pow(0.5f + 0.5f * MathF.Sin(2f * MathF.PI * _blade), 6f);
            var tone = 0.5f + 0.5f * MathF.Sin(2f * MathF.PI * _engine);
            var s = Level * (_lp * (0.35f + 0.65f * slap) * 0.9f + 0.12f * tone);
            if (!_pb.PushFrame(new Vector2(s, s))) break;
        }
    }
}
