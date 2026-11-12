using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// What the amphibious transport shows beyond where it is: the body leans over its wheels (<see cref="DriveBody"/>), the wheels roll with the speed
/// and the front pair steer, the propeller turns in the water, a wake trails from the stern and spray flies from the bow in proportion to the
/// speed, and it goes in with a splash. Its riders are drawn on their seats, on the body as it is drawn (so they lean and bob with it, and stay
/// aboard the vehicle its driver's client draws in the present), seated or standing as the seat says, facing where they aim. All of it is the
/// client's own: nothing here is on the wire beyond the drive record and the players' own records.
/// </summary>
public partial class NetClient
{
    /// <summary>The nodes of a driven vehicle's model that move, found by name (the model's own, see assets/suchohrad-generated-models), and its water effects.</summary>
    sealed class DriveNodes
    {
        /// <summary>The body (leans over the wheels); riders and the driver's own figure are placed on it.</summary>
        public Node3D? Hull;
        Node3D _root = null!;
        Node3D? _propeller, _wakeBow, _wakeSternL, _wakeSternR;
        readonly Node3D?[] _wheels = new Node3D?[4];
        readonly Node3D?[] _steer = new Node3D?[2];
        float _wheelRadius = 0.4f, _roll, _turn;
        CpuParticles3D? _wake, _spray;
        bool _started, _wasAfloat;

        /// <summary>Beyond this the wake and the spray are not drawn, metres.</summary>
        const float FxRange = 160f;
        /// <summary>Afloat the wheels hang in the water and turn with it at this share of the speed.</summary>
        const float WaterWheelShare = 0.3f;

        public static DriveNodes? Find(Node3D root)
        {
            var n = new DriveNodes
            {
                _root = root, Hull = root.FindChild("hull", true, false) as Node3D, _propeller = root.FindChild("propeller", true, false) as Node3D,
                _wakeBow = root.FindChild("wake_bow", true, false) as Node3D,
                _wakeSternL = root.FindChild("wake_stern_l", true, false) as Node3D, _wakeSternR = root.FindChild("wake_stern_r", true, false) as Node3D,
            };
            var names = new[] { "wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr" };
            for (var i = 0; i < 4; i++) n._wheels[i] = root.FindChild(names[i], true, false) as Node3D;
            n._steer[0] = root.FindChild("wheel_fl_steer", true, false) as Node3D;
            n._steer[1] = root.FindChild("wheel_fr_steer", true, false) as Node3D;
            if (n.Hull == null && n._wheels[0] == null && n._propeller == null) return null;
            if (n._wheels[2] is { } rear && rear.Position.Y > 0.05f) n._wheelRadius = rear.Position.Y; // it stands on the ground: its centre is its radius up
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
            _roll = Mathf.Wrap(_roll - s.Speed * (s.Afloat ? WaterWheelShare : 1f) / _wheelRadius * dt, -Mathf.Pi, Mathf.Pi);
            foreach (var w in _wheels) if (w != null) w.Rotation = new Vector3(_roll, 0, 0);
            var lockAngle = d.Body.WheelLockDeg * FlightMath.Deg2Rad;
            var angle = -s.Steer * Mathf.Min(lockAngle, Mathf.Atan(d.Wheelbase / DriveModel.TurnRadiusAt(def, s.Speed)));
            foreach (var p in _steer) if (p != null) p.Rotation = new Vector3(0, angle, 0);

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

    /// <summary>The frame's body, wheels, propeller and water effects of one driven vehicle, as it was posed by <see cref="PoseDrive"/>.</summary>
    void StepDriveFx(CarView v, VehicleDef def, Vector3 p, float dt)
    {
        if (v.Drive == null || !_drives.TryGetValue(v.Id, out var view) || !view.Drawn) return;
        var crossed = v.Drive.Step(def, view.Shown, view.Pose, Sfx.ListenerDistance(p), dt);
        if (crossed == 0) return;
        // going in: a big splash at the bow; coming out: a smaller one where the stern leaves the water
        var at = crossed > 0 ? v.Drive.Bow : v.Drive.Stern;
        var surface = Water.SurfaceY(at.X, at.Z);
        if (float.IsNaN(surface)) surface = Water.SurfaceY(p.X, p.Z);
        if (float.IsNaN(surface)) surface = p.Y + def.Drive!.Water.Draft;
        WaterFx.Splash(Owned, new Vector3(at.X, surface, at.Z), crossed > 0 ? 2.6f : 1.3f);
    }

    /// <summary>The node riders stand on: the body of a driven vehicle (it leans over its wheels), else the vehicle itself.</summary>
    static Node3D SeatAnchor(CarView v) => v.Drive?.Hull is { } hull && GodotObject.IsInstanceValid(hull) ? hull : v.Node;

    /// <summary>
    /// Puts every remote rider of a driven vehicle on their seat as the vehicle is drawn this frame. The server puts a rider's body on the seat, so
    /// the snapshot says where they are; but they are drawn a tenth of a second in the past, and the vehicle its driver's client draws is in the
    /// present, so drawn from the snapshot they would trail behind it. Each rider is matched to a seat by where the newest snapshot has them and
    /// the vehicle, then drawn on that seat of the drawn body: leaning and bobbing with it, seated or standing as the seat says, the driver facing
    /// the bow and the others where they aim, with their weapon pitched to that aim.
    /// </summary>
    void SeatRiders()
    {
        if (Vehicles == null) return;
        foreach (var (id, r) in _remotes)
        {
            if (r.State != EntityState.InVehicle || !_dec.States.TryGetValue(id, out var rec) || !MatchSeat(r, rec.Position))
            {
                if (r.RideSeat >= 0) { r.RideSeat = -1; r.RideStanding = false; r.View?.SetAimPitch(0); }
                continue;
            }
            var v = _cars[r.RideCar];
            var seat = Vehicles.Vehicles[v.Kind].Seats[r.RideSeat];
            var anchor = SeatAnchor(v);
            var carYaw = v.Node.Rotation.Y;
            r.Node.GlobalPosition = anchor.GlobalTransform * new Vector3(seat.Offset[0], seat.Offset[1], seat.Offset[2]);
            r.Node.GlobalBasis = anchor.GlobalBasis.Orthonormalized() * new Basis(Vector3.Up, r.RideSeat == 0 ? 0f : FlightMath.Wrap(r.ShownYaw - carYaw));
            r.RideStanding = seat.Standing;
            r.View?.SetAimPitch(seat.Armed || seat.Role == "gunner" ? r.ShownPitch : 0f);
        }
    }

    /// <summary>How far a rider's snapshot position may be from a seat's to be on it, metres: to take the seat, and to keep it once taken.</summary>
    const float SeatTake = 0.6f, SeatKeep = 1.2f;

    /// <summary>
    /// The seat of a driven vehicle the remote player at <paramref name="at"/> (the newest snapshot) sits on: the one they were matched to while
    /// it still fits, else the nearest within reach. False when they ride something else (or nothing this client draws that way).
    /// </summary>
    bool MatchSeat(Remote r, NVec3 at)
    {
        if (r.RideSeat >= 0 && _cars.TryGetValue(r.RideCar, out var held) && !held.Destroyed && SeatAt(held, r.RideSeat) is { } hp && NVec3.Distance(hp, at) <= SeatKeep) return true;
        r.RideSeat = -1;
        var best = SeatTake;
        foreach (var v in _cars.Values)
        {
            if (v.Destroyed || DriveDef(v) is not { } def) continue;
            for (var s = 0; s < def.Seats.Count; s++)
            {
                if (SeatAt(v, s) is not { } p) continue;
                var d = NVec3.Distance(p, at);
                if (d > best) continue;
                best = d; r.RideCar = v.Id; r.RideSeat = s;
            }
        }
        return r.RideSeat >= 0;
    }

    /// <summary>Where the newest snapshot has a seat of a driven vehicle (the server's own arithmetic: the seat on the slope the vehicle stands on), or null before its records are in.</summary>
    NVec3? SeatAt(CarView v, int seat)
    {
        if (Vehicles == null || v.Kind >= Vehicles.Vehicles.Count || seat >= Vehicles.Vehicles[v.Kind].Seats.Count) return null;
        if (!_drives.TryGetValue(v.Id, out var d) || !_dec.States.TryGetValue(v.Id, out var vehicle) || d.Rec.Type != EntityType.Drive) return null;
        return DriveRecord.State(vehicle, d.Rec).ToWorld(Vehicles.Vehicles[v.Kind].Seats[seat].Offset);
    }
}
