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
    public const float MouseSensitivity = 0.0025f;

    readonly CollisionShape3D _shape = new();
    readonly CapsuleShape3D _capsule = new() { Radius = MoveConsts.CapsuleRadius, Height = MoveConsts.CapsuleHeight };
    readonly Camera3D _camera = new() { Fov = 90, Far = 2000, Current = true };
    MoveState _state;
    float _yaw, _pitch;
    bool _headless;

    public float Health { get; set; } = MoveConsts.MaxHealth;
    public float LastFallDamage { get; private set; }
    public MoveState State => _state;
    public float KillY { get; set; } = float.NegativeInfinity;
    /// <summary>Picks the respawn position (a random spawn point).</summary>
    public Func<Vector3>? PickSpawn { get; set; }
    /// <summary>When set, replaces keyboard/mouse input (smoke test).</summary>
    public MoveInput? InputOverride { get; set; }
    public bool CaptureMouse { get; set; } = true;
    /// <summary>False on the dedicated server (no camera node).</summary>
    public bool UseCamera { get; set; } = true;
    /// <summary>True: this node runs its own physics tick from keyboard/mouse. False: a network driver calls <see cref="Simulate"/>.</summary>
    public bool AutoSim { get; set; } = true;
    /// <summary>True applies fall damage, death and respawn (offline play and the server); a predicting client leaves them to the server.</summary>
    public bool Authoritative { get; set; } = true;
    public float Yaw { get => _yaw; set => _yaw = value; }
    public float Pitch { get => _pitch; set => _pitch = value; }

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
        SetCrouchShape(false);
        if (CaptureMouse && !_headless) Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public void Teleport(Vector3 feet)
    {
        GlobalPosition = feet;
        Velocity = Vector3.Zero;
        _state = new MoveState { Position = ToN(feet), OnFloor = false, FallPeakY = feet.Y };
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
            _yaw -= m.Relative.X * MouseSensitivity;
            _pitch = Mathf.Clamp(_pitch - m.Relative.Y * MouseSensitivity, -1.55f, 1.55f);
            _camera.Rotation = new Vector3(_pitch, _yaw, 0);
        }
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
        SetCrouchShape(s.Crouching);
    }

    MoveInput ReadInput()
    {
        if (InputOverride is { } o) return o;
        var b = MoveButtons.None;
        if (Input.IsKeyPressed(Key.Space)) b |= MoveButtons.Jump;
        if (Input.IsKeyPressed(Key.Shift)) b |= MoveButtons.Sprint;
        if (Input.IsKeyPressed(Key.Ctrl) || Input.IsKeyPressed(Key.C)) b |= MoveButtons.Crouch;
        var captured = Input.MouseMode == Input.MouseModeEnum.Captured;
        return new MoveInput
        {
            MoveX = captured ? (Input.IsKeyPressed(Key.D) ? 1 : 0) - (Input.IsKeyPressed(Key.A) ? 1 : 0) : 0,
            MoveY = captured ? (Input.IsKeyPressed(Key.W) ? 1 : 0) - (Input.IsKeyPressed(Key.S) ? 1 : 0) : 0,
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
        _state.HeadBlocked = _state.Crouching && HeadBlockedCheck();
        MovementStep.Step(ref _state, input, dt);
        SetCrouchShape(_state.Crouching);

        Velocity = ToG(_state.Velocity);
        MoveAndSlide();
        LastFallDamage = MovementStep.PostMove(ref _state, ToN(GlobalPosition), ToN(Velocity), IsOnFloor());
        if (Authoritative && LastFallDamage > 0)
        {
            Health -= LastFallDamage;
            GD.Print($"player: fall damage {LastFallDamage:F1}, health {Health:F1}");
        }
        if (Authoritative && (Health <= 0 || GlobalPosition.Y < KillY)) Respawn();
    }

    bool HeadBlockedCheck()
    {
        // would the standing capsule collide if we stood up?
        var up = MoveConsts.CapsuleHeight - MoveConsts.CrouchHeight;
        return TestMove(GlobalTransform, Vector3.Up * (up + 0.02f));
    }

    void SetCrouchShape(bool crouching)
    {
        var h = MovementStep.CapsuleHeight(crouching);
        _capsule.Height = h;
        _shape.Position = new Vector3(0, h / 2, 0);
        _camera.Position = new Vector3(0, MovementStep.EyeHeight(crouching), 0);
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
