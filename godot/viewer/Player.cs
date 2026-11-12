using Godot;

/// <summary>First-person character: WASD, mouse look, jump, gravity, sprint. Origin is at the feet.</summary>
public partial class Player : CharacterBody3D
{
    // Debug viewer speeds (the village is 1.4 km across); override with DRYCASTLE_SPEED=<multiplier>.
    static readonly float SpeedScale = float.TryParse(System.Environment.GetEnvironmentVariable("DRYCASTLE_SPEED"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var k) ? k : 1f;
    static readonly float WalkSpeed = 5f * SpeedScale;
    static readonly float SprintSpeed = 10f * SpeedScale;
    const float JumpSpeed = 5f;
    const float Gravity = 20f;
    const float EyeHeight = 1.65f;

    Node3D _head = null!;
    Camera3D _camera = null!;
    float _pitch;

    /// <summary>When false the character ignores keyboard and mouse (smoke tests, screenshots).</summary>
    public bool InputEnabled = true;

    public Camera3D Camera => _camera;

    public override void _Ready()
    {
        var shape = new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = 0.35f, Height = 1.8f },
            Position = new Vector3(0, 0.9f, 0),
        };
        AddChild(shape);
        _head = new Node3D { Position = new Vector3(0, EyeHeight, 0) };
        AddChild(_head);
        _camera = new Camera3D { Far = 3000f, Near = 0.1f, Fov = 75f };
        _head.AddChild(_camera);
        FloorSnapLength = 0.4f;
        FloorMaxAngle = Mathf.DegToRad(50f);
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public void LookYawPitch(float yawDeg, float pitchDeg)
    {
        RotationDegrees = new Vector3(0, yawDeg, 0);
        _pitch = Mathf.DegToRad(pitchDeg);
        _head.Rotation = new Vector3(_pitch, 0, 0);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!InputEnabled) return;
        if (e is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            RotateY(-mm.Relative.X * 0.0025f);
            _pitch = Mathf.Clamp(_pitch - mm.Relative.Y * 0.0025f, -1.5f, 1.5f);
            _head.Rotation = new Vector3(_pitch, 0, 0);
        }
        else if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
                ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
        }
        else if (e is InputEventMouseButton { Pressed: true } && Input.MouseMode != Input.MouseModeEnum.Captured)
        {
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        var d = (float)delta;
        var v = Velocity;
        if (!IsOnFloor()) v.Y -= Gravity * d;

        var dir = Vector2.Zero;
        var sprint = false;
        if (InputEnabled)
        {
            if (Input.IsKeyPressed(Key.W)) dir.Y -= 1;
            if (Input.IsKeyPressed(Key.S)) dir.Y += 1;
            if (Input.IsKeyPressed(Key.A)) dir.X -= 1;
            if (Input.IsKeyPressed(Key.D)) dir.X += 1;
            sprint = Input.IsKeyPressed(Key.Shift);
            if (Input.IsKeyPressed(Key.Space) && IsOnFloor()) v.Y = JumpSpeed;
        }
        var wish = (GlobalTransform.Basis * new Vector3(dir.X, 0, dir.Y)).Normalized();
        var speed = sprint ? SprintSpeed : WalkSpeed;
        v.X = wish.X * speed;
        v.Z = wish.Z * speed;
        Velocity = v;
        MoveAndSlide();
    }
}
