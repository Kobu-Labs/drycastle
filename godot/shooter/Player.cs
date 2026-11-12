using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// First-person player: a CharacterBody3D (origin at the feet) that runs the shared <see cref="MovementStep"/>
/// each physics tick. Mouse look; the mouse is captured, Esc releases it, a click captures it again.
/// </summary>
public partial class Player : CharacterBody3D
{
    
    readonly CollisionShape3D _shape = new();
    readonly CapsuleShape3D _capsule = new() { Radius = MoveConsts.CapsuleRadius, Height = MoveConsts.CapsuleHeight };
    readonly Camera3D _camera = new() { Fov = 90, Far = 2000, Current = true };
    /// <summary>False when a UiShell owns Esc and the mouse mode (the match client); true keeps the old Esc/click behaviour for the offline view.</summary>
    public bool SelfManageMouse { get; set; } = true;
    MoveState _state;
    float _yaw, _pitch;
    bool _headless;

    public float Health { get; set; } = MoveConsts.MaxHealth;
    public float LastFallDamage { get; private set; }
    /// <summary>Drowning damage of the last tick (air used up with the head under); applied when authoritative.</summary>
    public float LastDrowningDamage { get; private set; }
    /// <summary>The map's water (set by the owner before the first tick); <see cref="Water.Empty"/> on a dry map.</summary>
    public Water Water { get; set; } = Water.Empty;

    /// <summary>The map's ladders (bunkers.json, set by the owner before the first tick); none on a map without bunkers.</summary>
    public Ladders Ladders { get; set; } = Ladders.Empty;
    /// <summary>True while the eyes are under the water surface (no firing, breath is running).</summary>
    public bool HeadUnder => _state.HeadUnder;
    bool _inVehicle;
    /// <summary>Seated in a vehicle: no prone here. Entering clears the prone latch (the body is not simulated while driving).</summary>
    public bool InVehicle
    {
        get => _inVehicle;
        set { _inVehicle = value; CollisionMask = value ? CollisionLayers.World : CollisionLayers.OnFoot; if (value) _state.ProneLatched = false; } // seated: inside the vehicle's own box
    }
    public MoveState State => _state;
    public float KillY { get; set; } = float.NegativeInfinity;
    /// <summary>Picks the respawn position (a random spawn point).</summary>
    public Func<Vector3>? PickSpawn { get; set; }
    /// <summary>When set, replaces keyboard/mouse input (smoke test).</summary>
    public MoveInput? InputOverride { get; set; }
    public bool CaptureMouse { get; set; } = true;
    /// <summary>False on the dedicated server (no camera node).</summary>
    public bool UseCamera { get; set; } = true;
    /// <summary>The first-person camera (viewmodel parent).</summary>
    public Camera3D Camera => _camera;
    /// <summary>True: this node runs its own physics tick from keyboard/mouse. False: a network driver calls <see cref="Simulate"/>.</summary>
    public bool AutoSim { get; set; } = true;
    /// <summary>True applies fall damage, death and respawn (offline play and the server); a predicting client leaves them to the server.</summary>
    public bool Authoritative { get; set; } = true;
    public float Yaw { get => _yaw; set => _yaw = value; }
    public float Pitch { get => _pitch; set => _pitch = value; }
    /// <summary>Recoil offset shown on top of the mouse aim (radians: x pitch, y yaw). Never sent: the server applies the same offset to shots.</summary>
    public Vector2 AimKick { get; set; }
    /// <summary>Cosmetic breath sway on the camera, radians (pitch, yaw). Never sent; the server's aim error is separate.</summary>
    public Vector2 ViewSway { get; set; }

    public override void _Ready()
    {
        _headless = DisplayServer.GetName() == "headless";
        CollisionMask = _inVehicle ? CollisionLayers.World : CollisionLayers.OnFoot; // solid vehicles stop on-foot movement (#217)
        AddChild(_shape);
        _shape.Shape = _capsule;
        if (UseCamera) AddChild(_camera);
        else _camera.Current = false;
        if (!AutoSim) SetPhysicsProcess(false);
        FloorSnapLength = 0.3f;
        FloorMaxAngle = Mathf.DegToRad(50);
        SetStanceShape(Stance.Stand);
        if (CaptureMouse && !_headless && SelfManageMouse) Input.MouseMode = Input.MouseModeEnum.Captured;
        ApplyFov();
        GameSettings.Changed += ApplyFov;
    }

    public override void _ExitTree() => GameSettings.Changed -= ApplyFov;

    /// <summary>Scope: the vertical FOV wanted while zooming (0 = not scoped). The camera eases toward it and mouse sensitivity scales with the FOV.</summary>
    public float ZoomFov { get; set; }

    /// <summary>Iron-sight aim-in (#237): the camera eases to <see cref="IronSightFovScale"/> of the normal FOV. Scopes (<see cref="ZoomFov"/>) take precedence.</summary>
    public bool IronSightZoom { get; set; }

    /// <summary>FOV factor while aiming down iron sights: a small zoom (0.85 = 15% narrower).</summary>
    public const float IronSightFovScale = 0.85f;

    float _fovNow;

    void ApplyFov() { _fovNow = GameSettings.Current.Fov; _camera.Fov = _fovNow; }

    public override void _Process(double delta)
    {
        if (!_camera.IsInsideTree()) return;
        if (!_chase) _camera.Rotation = new Vector3(RecoilState.ClampPitch(_pitch + AimKick.X + ViewSway.X), _yaw + AimKick.Y + ViewSway.Y, 0);
        var target = ZoomFov > 0 ? ZoomFov : (GameSettings.Current.Fov + FovBoost) * (IronSightZoom ? IronSightFovScale : 1f);
        if (Mathf.IsEqualApprox(_fovNow, target)) return;
        _fovNow = Mathf.Lerp(_fovNow, target, 1f - Mathf.Exp(-18f * (float)delta));
        _camera.Fov = _fovNow;
    }

    /// <summary>Mouse look scale: tan(fov/2) ratio while the camera is zoomed so the crosshair sweeps the same screen distance.</summary>
    float LookScale() => TurretLook ? 1f : Mathf.Tan(Mathf.DegToRad(_camera.Fov) / 2) / Mathf.Tan(Mathf.DegToRad(GameSettings.Current.Fov) / 2);

    /// <summary>
    /// The turret's look (a gunship's owner, #160): the mouse delta is passed unscaled, and the turret divides it by its zoom, so a
    /// x4 zoom turns exactly a quarter as fast (the FOV ratio is only close to that).
    /// </summary>
    public bool TurretLook { get; set; }

    public void Teleport(Vector3 feet)
    {
        GlobalPosition = feet;
        Velocity = Vector3.Zero;
        _state = new MoveState { Position = ToN(feet), OnFloor = false, FallPeakY = feet.Y };
    }

    /// <summary>Add a velocity impulse (explosion knockback, server only) and leave the floor so gravity and the physics move take over.</summary>
    public void AddImpulse(Vector3 v)
    {
        _state.Velocity += ToN(v);
        _state.OnFloor = false;
        _state.FallPeakY = Mathf.Max(_state.FallPeakY, GlobalPosition.Y);
    }

    bool _chase;

    /// <summary>Third-person camera on a car (driving): the body sits in the car and the mouse orbits the camera freely around it; the keys alone steer. Starts behind the car.</summary>
    public void Chase(Vector3 carPos, float carYaw, VehicleDef? def = null, float dt = 0.016f)
    {
        var fresh = !_chase || _flightChase || _cockpit; // from the cockpit view too: the camera starts at its distance, it does not ease out from the seat
        if (!_chase || _cockpit) { _yaw = carYaw; _pitch = -0.22f; } // from the cockpit view too: the orbit starts behind the car
        _chase = true; _flightChase = false; _cockpit = false; FovBoost = 0;
        GlobalPosition = carPos;
        _pitch = Mathf.Clamp(_pitch, -1.2f, 0.6f);
        if (!_camera.IsInsideTree()) return; // headless bots have no camera
        var rot = new Vector3(_pitch, _yaw, 0);
        var fwd = -Basis.FromEuler(rot).Z;
        var (dist, height) = def != null ? ChaseCamera.Wanted(def) : (ChaseCamera.BaseDistance, ChaseCamera.BaseHeight);
        var focus = carPos + Vector3.Up * height;
        // world only (vehicles are on their own layer), so the camera comes in at walls, trees and buildings; coming in is instant, going out eased
        var q = PhysicsRayQueryParameters3D.Create(focus, focus - fwd * dist);
        q.CollisionMask = CollisionLayers.World;
        q.Exclude = _selfOnly ??= new Godot.Collections.Array<Rid> { GetRid() };
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
        var free = hit.Count > 0 ? (hit["position"].AsVector3() - focus).Length() - ChaseCamera.Margin : dist;
        _chaseDist = fresh ? Mathf.Max(ChaseCamera.MinDistance, Mathf.Min(dist, free)) : ChaseCamera.Step(_chaseDist, dist, free, dt);
        _camera.GlobalPosition = focus - fwd * _chaseDist;
        _camera.GlobalRotation = rot;
    }
    float _chaseDist;

    /// <summary>Degrees added to the field of view (the helicopter's chase camera widens with speed); eased like the scope.</summary>
    public float FovBoost { get; set; }

    bool _flightChase;
    Vector3 _chaseFocus, _chaseFocusVel;
    Godot.Collections.Array<Rid>? _selfOnly;

    /// <summary>
    /// The pilot's chase camera on a helicopter. The view turns with the mouse at once (its yaw is also the heading the nose swings to), so the
    /// response to the hand is never late; the camera's place follows the helicopter on a critically damped spring, so the airframe pulls away as
    /// it accelerates and settles back; the camera takes a share of the airframe's pitch and roll, so the horizon tilts with it; and the field of
    /// view widens with speed. The settings' reduce-motion switch turns the tilt and the widening off. <see cref="EndChase"/> ends it.
    /// </summary>
    /// <param name="at">The helicopter's origin as drawn this frame; <paramref name="yaw"/>, <paramref name="pitch"/>, <paramref name="roll"/> its attitude.</param>
    /// <param name="speedFraction">Its speed over its top speed, 0 to 1.</param>
    public void FlightChase(Vector3 at, float yaw, float pitch, float roll, float speedFraction, FlightCameraDef cam, float dt)
    {
        if (!_chase || !_flightChase || _driveChase)
        {
            if (!_cockpit) _yaw = yaw; // out of the cockpit view the look is the heading already asked for: it stays
            _pitch = -0.14f; _chaseFocus = at; _chaseFocusVel = Vector3.Zero;
        }
        _driveChase = false;
        SpringChase(at, yaw, pitch, roll, speedFraction, cam, dt);
    }

    bool _driveChase;
    float _driveYawSeen, _driveYawOwed;

    /// <summary>
    /// The driver's chase camera on a vehicle their client predicts (the amphibious transport): the helicopter's spring camera
    /// (<see cref="FlightChase"/>: the place follows on a spring, a share of the body's pitch and roll, wider with speed), and the view swings round
    /// with the vehicle, because here the keys steer and the mouse only looks. The vehicle's turning is owed to the view and paid off at
    /// <see cref="DriveCameraDef.TurnResponse"/>, so in a turn the vehicle swings in the frame first and the camera comes round behind it; the mouse
    /// still looks about freely on top of that. <see cref="EndChase"/> ends it.
    /// </summary>
    public void DriveChase(Vector3 at, float yaw, float pitch, float roll, float speedFraction, DriveCameraDef cam, float dt)
    {
        if (!_chase || !_flightChase || !_driveChase) { _yaw = yaw; _pitch = -0.2f; _chaseFocus = at; _chaseFocusVel = Vector3.Zero; _driveYawSeen = yaw; _driveYawOwed = 0; }
        _driveChase = true;
        _driveYawOwed += FlightMath.Wrap(yaw - _driveYawSeen) * cam.TurnFollow;
        _driveYawSeen = yaw;
        var paid = _driveYawOwed * (1f - Mathf.Exp(-cam.TurnResponse * Mathf.Min(dt, 0.25f)));
        _yaw += paid; _driveYawOwed -= paid;
        SpringChase(at, yaw, pitch, roll, speedFraction, cam, dt);
    }

    /// <summary>The spring camera both chase cameras share: see <see cref="FlightChase"/>.</summary>
    void SpringChase(Vector3 at, float yaw, float pitch, float roll, float speedFraction, FlightCameraDef cam, float dt)
    {
        _chase = true; _flightChase = true; _cockpit = false;
        GlobalPosition = at;
        _pitch = Mathf.Clamp(_pitch, -1.1f, 0.5f);
        if (!_camera.IsInsideTree()) return; // headless bots have no camera
        // the spring is stepped in slices no longer than a tick, so a slow frame cannot make it ring
        for (var left = Mathf.Min(dt, 0.25f); left > 1e-5f; left -= 1f / 30f)
        {
            var h = Mathf.Min(left, 1f / 30f);
            _chaseFocusVel += (cam.Follow * cam.Follow * (at - _chaseFocus) - 2f * cam.Follow * _chaseFocusVel) * h;
            _chaseFocus += _chaseFocusVel * h;
        }
        var lag = at - _chaseFocus;
        if (lag.Length() > cam.MaxLag) _chaseFocus = at - lag.Normalized() * cam.MaxLag;

        // the airframe's tilt as the camera sees it from its own heading: side on, a pitch reads as a roll
        var share = GameSettings.Current.ReduceMotion ? 0f : cam.Tilt;
        var (sd, cd) = Mathf.SinCos(_yaw - yaw);
        var rot = new Vector3(_pitch + share * (pitch * cd - roll * sd), _yaw, share * (pitch * sd + roll * cd));
        var look = -Basis.FromEuler(new Vector3(_pitch, _yaw, 0)).Z;
        var focus = _chaseFocus + Vector3.Up * cam.Height;
        var want = focus - look * cam.Distance;
        // keep the camera out of the ground and the walls: it comes in along its own line of sight
        var q = PhysicsRayQueryParameters3D.Create(focus, want);
        q.CollisionMask = CollisionLayers.World;
        q.Exclude = _selfOnly ??= new Godot.Collections.Array<Rid> { GetRid() };
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
        if (hit.Count > 0) want = hit["position"].AsVector3() + look * 0.4f;
        _camera.GlobalPosition = want;
        _camera.GlobalRotation = rot;
        FovBoost = GameSettings.Current.ReduceMotion ? 0f : cam.FovBoostDeg * Mathf.Clamp(speedFraction, 0f, 1f) * Mathf.Clamp(speedFraction, 0f, 1f);
    }

    /// <summary>
    /// First person from a vehicle seat whose rider sits (or stands) in the open: the body is on the seat and the eye is the seated figure's,
    /// <paramref name="eyeHeight"/> above it. The stance shape puts the camera back when the body is its own again (<see cref="SetState"/>, <see cref="EndChase"/>).
    /// </summary>
    public void SeatView(Vector3 feet, float eyeHeight)
    {
        GlobalPosition = feet;
        _camera.Position = new Vector3(0, eyeHeight, 0);
    }

    /// <summary>Turret camera (a gunship's owner): the camera sits at the turret and looks along the given world yaw and pitch. The body does not move; <see cref="EndChase"/> ends it.</summary>
    public void Turret(Vector3 at, float yaw, float pitch)
    {
        _chase = true; _flightChase = false; _cockpit = false; FovBoost = 0;
        if (!_camera.IsInsideTree()) return;
        _camera.GlobalPosition = at;
        _camera.GlobalRotation = new Vector3(pitch, yaw, 0);
    }

    bool _cockpit;
    float _cockpitYaw;
    /// <summary>How far the head turns from straight ahead in a road vehicle's cockpit view (pi: all the way round, to see the riders), and how far the look goes down and up in any, radians.</summary>
    const float CockpitYawLimit = Mathf.Pi, CockpitPitchDown = -0.9f, CockpitPitchUp = 0.7f;

    /// <summary>
    /// The cockpit view (prototype, ticket #219): the camera sits at <paramref name="eye"/>, a point of the vehicle's cockpit rig, and looks
    /// where the mouse points inside the vehicle's <paramref name="frame"/>. In a road vehicle the look turns with the vehicle
    /// (<paramref name="lookTurnsWithVehicle"/>): it starts straight ahead, stays on the same part of the cab through a corner and turns as
    /// far as <see cref="CockpitYawLimit"/>. In a helicopter the look is the heading the nose swings to, so it stays where the hand put it and the
    /// airframe turns under it. The keys alone steer, as with the chase camera. <see cref="EndChase"/> ends it.
    /// </summary>
    /// <param name="bodyAt">Where the body rides (the vehicle's origin, as with the chase cameras).</param>
    /// <param name="frame">The basis the look is taken in: the vehicle's own (the view tilts with it), or its heading alone (the view stays level).</param>
    /// <param name="frameYaw">The vehicle's heading.</param>
    public void Cockpit(Vector3 bodyAt, Vector3 eye, Basis frame, float frameYaw, bool lookTurnsWithVehicle)
    {
        if (!_chase || !_cockpit)
        {
            if (lookTurnsWithVehicle || !_chase) _yaw = frameYaw; // a pilot coming from the chase camera keeps the heading asked for
            _pitch = 0;
            _cockpitYaw = frameYaw;
        }
        if (lookTurnsWithVehicle)
        {
            _yaw += Mathf.Wrap(frameYaw - _cockpitYaw, -Mathf.Pi, Mathf.Pi);
            _yaw = frameYaw + Mathf.Clamp(Mathf.Wrap(_yaw - frameYaw, -Mathf.Pi, Mathf.Pi), -CockpitYawLimit, CockpitYawLimit);
        }
        _cockpitYaw = frameYaw;
        _pitch = Mathf.Clamp(_pitch, CockpitPitchDown, CockpitPitchUp);
        _chase = true; _cockpit = true; _flightChase = false; FovBoost = 0;
        GlobalPosition = bodyAt;
        if (!_camera.IsInsideTree()) return; // headless bots have no camera
        _camera.GlobalPosition = eye;
        _camera.GlobalBasis = frame * Basis.FromEuler(new Vector3(_pitch, Mathf.Wrap(_yaw - frameYaw, -Mathf.Pi, Mathf.Pi), 0));
    }

    /// <summary>Back to the first-person camera, looking where the car pointed.</summary>
    public void EndChase(float yaw)
    {
        if (!_chase) return;
        _chase = false; _flightChase = false; _cockpit = false; FovBoost = 0;
        _yaw = yaw; _pitch = 0;
        _camera.Rotation = new Vector3(0, _yaw, 0);
        SetStanceShape(_state.Stance);
    }

    public void Respawn()
    {
        Health = MoveConsts.MaxHealth;
        if (PickSpawn != null) Teleport(PickSpawn() + Vector3.Up * 0.1f);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!CaptureMouse) return;
        if (e is InputEventMouseMotion m && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            var gs = GameSettings.Current;
            var k = GameSettings.BaseSensitivity * gs.Sensitivity * Mathf.Min(1f, LookScale());
            _yaw -= m.Relative.X * k;
            _pitch = Mathf.Clamp(_pitch - m.Relative.Y * k * (gs.InvertY ? -1 : 1), -1.55f, 1.55f);
            _camera.Rotation = new Vector3(_pitch, _yaw, 0);
        }
        else if (!SelfManageMouse) return;
        else if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
            Input.MouseMode = Input.MouseModeEnum.Visible;
        else if (e is InputEventMouseButton { Pressed: true } && Input.MouseMode != Input.MouseModeEnum.Captured)
            Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    /// <summary>The current input: the override if set, else keyboard and mouse.</summary>
    public MoveInput Sample() => ReadInput();

    /// <summary>Put the body in an exact movement state (reconciliation, spawn).</summary>
    public void SetState(MoveState s)
    {
        _state = s;
        GlobalPosition = ToG(s.Position);
        Velocity = ToG(s.Velocity);
        SetStanceShape(s.Stance);
    }

    MoveInput ReadInput()
    {
        if (InputOverride is { } o) return o;
        var b = MoveButtons.None;
        if (Input.IsActionPressed("jump")) b |= MoveButtons.Jump;
        if (Input.IsActionPressed("sprint")) b |= MoveButtons.Sprint;
        if (Input.IsActionPressed("crouch")) b |= MoveButtons.Crouch;
        if (Input.IsActionPressed("prone")) b |= MoveButtons.Prone;
        if (Input.IsActionPressed("fire")) b |= MoveButtons.Fire;
        if (Input.IsActionPressed("zoom")) b |= MoveButtons.Aim;
        if (Input.IsActionPressed("reload")) b |= MoveButtons.Reload;
        if (Input.IsActionPressed("interact")) b |= MoveButtons.Use;
        if (Input.IsActionPressed("headlights")) b |= MoveButtons.Headlights;
        if (Input.IsActionPressed("killstreak")) b |= MoveButtons.Killstreak;
        if (_state.HeadUnder || _state.OnLadder) b &= ~MoveButtons.Fire; // no firing with the head under water, nor with both hands on a ladder
        var captured = Input.MouseMode == Input.MouseModeEnum.Captured;
        return new MoveInput
        {
            MoveX = captured ? (Input.IsActionPressed("move_right") ? 1 : 0) - (Input.IsActionPressed("move_left") ? 1 : 0) : 0,
            MoveY = captured ? (Input.IsActionPressed("move_forward") ? 1 : 0) - (Input.IsActionPressed("move_back") ? 1 : 0) : 0,
            Yaw = _yaw,
            Buttons = captured ? b : MoveButtons.None,
            SpeedScale = 1,
        };
    }

    public override void _PhysicsProcess(double delta)
    {
        var input = ReadInput();
        _yaw = input.Yaw;
        if (InputOverride != null) _camera.Rotation = new Vector3(_pitch, _yaw, 0);
        Simulate(input, (float)delta);
    }

    /// <summary>One movement tick: the shared step, the physics move and (when authoritative) fall damage and respawn.</summary>
    public void Simulate(in MoveInput input, float dt)
    {
        _state.HeadBlocked = _state.Stance != Stance.Stand && HeadBlockedCheck(_state.Stance);
        Water.Sample(ref _state);
        LastDrowningDamage = Breath.Tick(ref _state, dt);
        _state.FloorTooSteep = IsOnFloor() && GetFloorNormal().Y < Mathf.Cos(Mathf.DegToRad(MoveConsts.ProneMaxSlopeDegrees));
        _state.ProneBlocked = _inVehicle;
        if (Ladders.Count > 0 && Ladders.Climb(ref _state, input, dt))
        {
            // on a ladder the step moves the body, not the physics: no gravity, no collision, no fall damage
            GlobalPosition = ToG(_state.Position);
            Velocity = Vector3.Zero;
            LastFallDamage = 0;
            SetStanceShape(_state.Stance);
            return;
        }
        MovementStep.Step(ref _state, input, dt);
        SetStanceShape(_state.Stance);

        Velocity = ToG(_state.Velocity);
        MoveAndSlide();
        LastFallDamage = MovementStep.PostMove(ref _state, ToN(GlobalPosition), ToN(Velocity), IsOnFloor(), Water.DepthAt(ToN(GlobalPosition)));
        if (Authoritative && LastFallDamage > 0)
        {
            Health -= LastFallDamage;
            GD.Print($"player: fall damage {LastFallDamage:F1}, health {Health:F1}");
        }
        if (Authoritative && LastDrowningDamage > 0) Health -= LastDrowningDamage;
        if (Authoritative && (Health <= 0 || GlobalPosition.Y < KillY)) Respawn();
    }

    bool HeadBlockedCheck(Stance current)
    {
        // would the capsule one stance up collide? (the body rises by the height difference)
        var up = MovementStep.CapsuleHeight((Stance)((int)current - 1)) - MovementStep.CapsuleHeight(current);
        return TestMove(GlobalTransform, Vector3.Up * (up + 0.02f));
    }

    /// <summary>The stance whose capsule is on the body now; the capsule is only written when the stance changes (T3-010).</summary>
    Stance? _shapeStance;

    void SetStanceShape(Stance stance)
    {
        if (_shapeStance != stance)
        {
            _shapeStance = stance;
            var h = MovementStep.CapsuleHeight(stance);
            _capsule.Height = h;
            _shape.Position = new Vector3(0, h / 2, 0);
        }
        // the camera is set every call: the chase camera moves it, and EndChase relies on this to put it back
        _camera.Position = new Vector3(0, MovementStep.EyeHeight(stance), 0);
    }

    static NVec3 ToN(Vector3 v) => new(v.X, v.Y, v.Z);
    static Vector3 ToG(NVec3 v) => new(v.X, v.Y, v.Z);
}

/// <summary>
/// Headless scripted-input check on the real map collision: stand, walk, jump, take fall damage.
/// Exit 0 with SHOOTER_SMOKE_MOVE_OK, else 2.
/// </summary>
public partial class SmokeMove : Node
{
    public Player Player = null!;
    public Vector3 Spawn;

    int _frame;
    int _phase;
    int _phaseStart;
    Vector3 _mark;
    float _peak;
    float _best;
    int _dir;
    readonly List<string> _log = new();

    void Fail(string why)
    {
        GD.PrintErr($"SMOKE_MOVE FAIL: {why}");
        GetTree().Quit(2);
        SetPhysicsProcess(false);
    }

    void Next() { _phase++; _phaseStart = _frame; _mark = Player.GlobalPosition; _peak = _mark.Y; }

    public override void _PhysicsProcess(double delta)
    {
        _frame++;
        var t = _frame - _phaseStart;
        var p = Player;
        var pos = p.GlobalPosition;
        switch (_phase)
        {
            case 0: // drop onto the ground from just above the spawn and stand
                if (t == 1) p.Teleport(Spawn + Vector3.Up * 0.5f);
                p.InputOverride = new MoveInput { SpeedScale = 1 };
                if (t > 6 && p.State.OnFloor)
                {
                    if (p.LastFallDamage > 0 || p.Health < 100) { Fail("damaged by tiny drop"); return; }
                    GD.Print($"smoke: standing at {pos} (spawn {Spawn}, dy {pos.Y - Spawn.Y:F2})");
                    Next();
                }
                else if (t > 90) Fail($"never stood on ground (y={pos.Y:F2}, spawn y={Spawn.Y:F2}); is the map collision loaded?");
                break;
            case 1: // stand still for 1 s: must stay on the floor
                if (!p.State.OnFloor || Mathf.Abs(pos.Y - _mark.Y) > 0.1f) { Fail($"did not stay grounded (y {_mark.Y:F2} -> {pos.Y:F2})"); return; }
                if (t >= 30) { _dir = 0; Next(); }
                break;
            case 2: // walk forward 1 s in up to 4 directions (a wall may block one)
                p.InputOverride = new MoveInput { MoveY = 1, Yaw = _dir * Mathf.Pi / 2, SpeedScale = 1 };
                if (t >= 30)
                {
                    var d = new Vector2(pos.X - _mark.X, pos.Z - _mark.Z).Length();
                    _best = Mathf.Max(_best, d);
                    GD.Print($"smoke: walked {d:F2} m facing yaw {_dir * 90} deg");
                    if (_best >= 3f || _dir == 3)
                    {
                        if (_best < 3f) { Fail($"walked only {_best:F2} m in 1 s in every direction"); return; }
                        Next();
                    }
                    else { _dir++; _phaseStart = _frame; _mark = pos; }
                }
                break;
            case 3: // settle, then jump in place
                p.InputOverride = new MoveInput { SpeedScale = 1 };
                if (t > 20 && p.State.OnFloor) { Next(); }
                break;
            case 4:
                p.InputOverride = new MoveInput { SpeedScale = 1, Buttons = t == 1 ? MoveButtons.Jump : MoveButtons.None };
                _peak = Mathf.Max(_peak, pos.Y);
                if (t > 5 && p.State.OnFloor)
                {
                    var h = _peak - _mark.Y;
                    GD.Print($"smoke: jump height {h:F2} m");
                    if (h < 0.85f || h > 1.35f) { Fail($"jump height {h:F2} m, expected about 1.1"); return; }
                    if (p.Health < 100) { Fail("jump caused damage"); return; }
                    Next();
                }
                else if (t > 90) Fail("jump never landed");
                break;
            case 5: // fall 8 m: expect about 40 damage
                if (t == 1) p.Teleport(pos + Vector3.Up * 8f);
                p.InputOverride = new MoveInput { SpeedScale = 1 };
                if (t > 3 && p.State.OnFloor)
                {
                    var dmg = 100f - p.Health;
                    GD.Print($"smoke: 8 m fall dealt {dmg:F1} damage");
                    if (dmg < 30f || dmg > 50f) { Fail($"fall damage {dmg:F1}, expected about 40"); return; }
                    GD.Print("SHOOTER_SMOKE_MOVE_OK");
                    GetTree().Quit(0);
                    SetPhysicsProcess(false);
                }
                else if (t > 200) Fail("fall never landed");
                break;
        }
    }
}
