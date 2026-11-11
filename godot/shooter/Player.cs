using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

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
    /// <summary>True while the eyes are under the water surface (no firing, breath is running).</summary>
    public bool HeadUnder => _state.HeadUnder;
    bool _inVehicle;
    /// <summary>Seated in a vehicle: no prone here. Entering clears the prone latch (the body is not simulated while driving).</summary>
    public bool InVehicle
    {
        get => _inVehicle;
        set { _inVehicle = value; if (value) _state.ProneLatched = false; }
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

    float _fovNow;

    void ApplyFov() { _fovNow = GameSettings.Current.Fov; _camera.Fov = _fovNow; }

    public override void _Process(double delta)
    {
        if (!_camera.IsInsideTree()) return;
        if (!_chase) _camera.Rotation = new Vector3(RecoilState.ClampPitch(_pitch + AimKick.X + ViewSway.X), _yaw + AimKick.Y + ViewSway.Y, 0);
        var target = ZoomFov > 0 ? ZoomFov : GameSettings.Current.Fov;
        if (Mathf.IsEqualApprox(_fovNow, target)) return;
        _fovNow = Mathf.Lerp(_fovNow, target, 1f - Mathf.Exp(-18f * (float)delta));
        _camera.Fov = _fovNow;
    }

    /// <summary>Mouse look scale: tan(fov/2) ratio while the camera is zoomed so the crosshair sweeps the same screen distance.</summary>
    float LookScale() => Mathf.Tan(Mathf.DegToRad(_camera.Fov) / 2) / Mathf.Tan(Mathf.DegToRad(GameSettings.Current.Fov) / 2);

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

    /// <summary>Third-person chase camera behind a car (driving): the body sits in the car, the camera follows its yaw.</summary>
    public void Chase(Vector3 carPos, float carYaw)
    {
        _chase = true;
        GlobalPosition = carPos;
        if (!_camera.IsInsideTree()) return; // headless bots have no camera
        _camera.GlobalPosition = carPos + Vector3.Up * 2.8f + new Vector3(Mathf.Sin(carYaw), 0, Mathf.Cos(carYaw)) * 6.5f;
        _camera.GlobalRotation = new Vector3(-0.22f, carYaw, 0);
    }

    /// <summary>Back to the first-person camera, looking where the car pointed.</summary>
    public void EndChase(float yaw)
    {
        if (!_chase) return;
        _chase = false;
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
        if (_state.HeadUnder) b &= ~MoveButtons.Fire; // no firing with the head under water
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

    void SetStanceShape(Stance stance)
    {
        var h = MovementStep.CapsuleHeight(stance);
        _capsule.Height = h;
        _shape.Position = new Vector3(0, h / 2, 0);
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
