using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Vehicles;
using Drycastle.Shooter.Waters;
using Drycastle.Shooter.Weapons;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Waters;

namespace Drycastle.Shooter.Net.Client;

/// <summary>The nodes of a driven vehicle's model that move, found by name (the model's own, see assets/drycastle-generated-models), and its water effects.</summary>
internal sealed class DriveNodes
{
    /// <summary>The body (leans over the wheels); riders and the driver's own figure are placed on it.</summary>
    public Node3D? Hull;
    Node3D _root = null!;
    Node3D? _propeller, _wakeBow, _wakeSternL, _wakeSternR;
    /// <summary>The model's wheels, whatever their count (<see cref="VehicleWheels"/>); null for a model without, or one that turns its own (the Scud truck).</summary>
    VehicleWheels? _wheels;
    float _turn;
    CpuParticles3D? _wake, _spray;
    bool _started, _wasAfloat;

    /// <summary>Beyond this the wake and the spray are not drawn, metres.</summary>
    const float FxRange = 160f;
    /// <summary>Afloat the wheels hang in the water and turn with it at this share of the speed.</summary>
    const float WaterWheelShare = 0.3f;

    public static DriveNodes? Find(Node3D root, VehicleDef def)
    {
        var n = new DriveNodes
        {
            _root = root, Hull = root.FindChild("hull", true, false) as Node3D, _propeller = root.FindChild("propeller", true, false) as Node3D,
            _wakeBow = root.FindChild("wake_bow", true, false) as Node3D,
            _wakeSternL = root.FindChild("wake_stern_l", true, false) as Node3D, _wakeSternR = root.FindChild("wake_stern_r", true, false) as Node3D,
        };
        if (def.Launcher == null) n._wheels = VehicleWheels.Find(root, def.Drive!.Body.WheelLockDeg * FlightMath.Deg2Rad); // the Scud truck turns its eight itself (NetClient.Scud)
        if (n.Hull == null && n._wheels == null && n._propeller == null) return null;
        return n;
    }

    /// <summary>
    /// One frame: the body's lean, the wheels, the propeller, the wake and the spray, from the state as it is drawn. Returns +1 on the frame it
    /// went into the water and -1 on the frame it came out (0 otherwise), for the splash.
    /// </summary>
    public int Step(VehicleDef def, in DriveState s, in DriveBodyPose pose, float distance, float dt)
    {
        var d = def.Drive!;
        if (Hull != null) Hull.Rotation = new Vector3(pose.BodyPitch, 0, pose.BodyRoll);

        // wheels: rolling forward (-Z) turns them backwards about X; the front pair take the angle the circle they steer needs
        var lockAngle = d.Body.WheelLockDeg * FlightMath.Deg2Rad;
        var angle = -s.Steer * Mathf.Min(lockAngle, Mathf.Atan(d.Wheelbase / DriveModel.TurnRadiusAt(def, s.Speed)));
        _wheels?.StepDriven(s.Speed * (s.Afloat ? WaterWheelShare : 1f), angle, dt);

        // propeller: it turns only in the water, slowly at idle, with the throttle ahead and astern
        if (_propeller != null && s.Afloat)
        {
            var drive = Mathf.Abs(s.Throttle) < 0.05f ? 0.12f : s.Throttle;
            _turn = Mathf.Wrap(_turn + drive * d.Body.PropellerRps * Mathf.Tau * dt, -Mathf.Pi, Mathf.Pi);
            _propeller.Rotation = new Vector3(0, 0, _turn);
        }

        // wake and bow spray, scaled by the speed (not drawn far away, nor with reduce motion)
        var wet = s.Afloat && distance < FxRange && !GameSettings.Current.ReduceMotion;
        var pace = Mathf.Clamp(Mathf.Abs(s.Speed) / Mathf.Max(0.1f, def.WaterTopSpeed), 0f, 1.5f);
        if (wet && pace > 0.1f && _wakeSternL != null && _wakeSternR != null)
        {
            _wake ??= MakeWake(_root);
            _wake.GlobalPosition = (_wakeSternL.GlobalPosition + _wakeSternR.GlobalPosition) / 2f;
            _wake.InitialVelocityMin = 0.3f * pace; _wake.InitialVelocityMax = 1.1f * pace;
            _wake.Color = new Color(1, 1, 1, 0.25f + 0.45f * Mathf.Min(1f, pace));
            _wake.Emitting = true;
        }
        else if (_wake != null) _wake.Emitting = false;
        if (wet && s.Speed > 0 && pace > 0.4f && _wakeBow != null)
        {
            _spray ??= MakeSpray(_root);
            _spray.GlobalPosition = _wakeBow.GlobalPosition;
            _spray.InitialVelocityMin = 1.2f * pace; _spray.InitialVelocityMax = 3.2f * pace;
            _spray.Emitting = true;
        }
        else if (_spray != null) _spray.Emitting = false;

        var crossed = _started && s.Afloat != _wasAfloat ? (s.Afloat ? 1 : -1) : 0;
        _started = true; _wasAfloat = s.Afloat;
        return crossed;
    }

    /// <summary>The vehicle is gone (destroyed): no wake and no spray until it is back.</summary>
    public void Stop()
    {
        if (_wake != null) _wake.Emitting = false;
        if (_spray != null) _spray.Emitting = false;
        _started = false;
    }

    /// <summary>Where the bow, or the stern, meets the water, for the splash going in and coming out.</summary>
    public Vector3 Bow => _wakeBow?.GlobalPosition ?? _root.GlobalPosition;
    public Vector3 Stern => _wakeSternL != null && _wakeSternR != null ? (_wakeSternL.GlobalPosition + _wakeSternR.GlobalPosition) / 2f : _root.GlobalPosition;

    /// <summary>The wake: flat puffs of foam left on the surface behind the stern, spreading and fading.</summary>
    static CpuParticles3D MakeWake(Node3D root)
    {
        var mat = new StandardMaterial3D { VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha };
        var p = new CpuParticles3D
        {
            TopLevel = true, LocalCoords = false, Emitting = false, OneShot = false, Amount = 40, Lifetime = 2.4, Randomness = 0.5f,
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Box, EmissionBoxExtents = new Vector3(0.6f, 0.01f, 0.25f),
            Direction = Vector3.Right, Spread = 180f, Flatness = 1f, Gravity = Vector3.Zero, DampingMin = 0.4f, DampingMax = 0.9f,
            ScaleAmountMin = 0.7f, ScaleAmountMax = 1.5f, ScaleAmountCurve = RocketFx.Curve2(0.6f, 2.4f),
            ColorRamp = RocketFx.Ramp((0f, new Color(1, 1, 1, 0.9f)), (0.5f, new Color(1, 1, 1, 0.5f)), (1f, new Color(1, 1, 1, 0f))),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Mesh = new SphereMesh { Radius = 0.4f, Height = 0.12f, RadialSegments = 8, Rings = 3, Material = mat },
        };
        root.AddChild(p);
        return p;
    }

    /// <summary>The bow spray: droplets thrown up and to the sides where the stem cuts the water.</summary>
    static CpuParticles3D MakeSpray(Node3D root)
    {
        var mat = new StandardMaterial3D { AlbedoColor = new Color(0.86f, 0.94f, 1f), Roughness = 0.2f };
        var p = new CpuParticles3D
        {
            TopLevel = true, LocalCoords = false, Emitting = false, OneShot = false, Amount = 30, Lifetime = 0.8, Randomness = 0.6f,
            Direction = Vector3.Up, Spread = 55f, Gravity = new Vector3(0, -9.8f, 0),
            ScaleAmountMin = 0.7f, ScaleAmountMax = 1.6f, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Mesh = new SphereMesh { Radius = 0.045f, Height = 0.09f, RadialSegments = 6, Rings = 3, Material = mat },
        };
        root.AddChild(p);
        return p;
    }
}
