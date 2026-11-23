using Godot;
using System;

namespace Drycastle.Viewer.Editor;

/// <summary>
/// The editor's free-fly camera and the walk-mode player it replaces: WASD flight at an adjustable speed, mouse look, the top-down view (T), and the player
/// parked while the editor is open (frozen and hidden; F2 puts it back where it was parked, F3 parks it on the ground below the camera first).
/// </summary>
public sealed class EditorCamera
{
    public const float TopDownHeight = 120f;
    const float MinSpeed = 1f, MaxSpeed = 800f;

    readonly IEditorHost _host;
    float _yaw, _pitch;
    (Vector3 pos, float yaw, float pitch)? _savedCam;          // the camera before the top-down view
    (Vector3 pos, Vector3 euler, bool input)? _savedPlayer;    // the parked player: where F2 puts it back
    HSlider? _speedSlider;
    Label? _speedLabel;

    public EditorCamera(IEditorHost host) => _host = host;

    /// <summary>The camera node, a child of the editor.</summary>
    public Camera3D Node { get; private set; } = null!;
    /// <summary>Fly speed in m/s.</summary>
    public float Speed { get; private set; } = 40f;
    public bool TopDown => _savedCam != null;

    public void Build(Node parent)
    {
        Node = new Camera3D { Name = "EditorCamera", Far = 3000f, Near = 0.1f, Fov = 70f };
        parent.AddChild(Node);
    }

    static double SliderToSpeed(double v) => MinSpeed * Math.Pow(MaxSpeed / MinSpeed, v / 100.0);
    static double SpeedToSlider(double s) => 100.0 * Math.Log(s / MinSpeed) / Math.Log(MaxSpeed / MinSpeed);

    /// <summary>Set the fly speed in m/s (clamped); keeps the slider in sync.</summary>
    public void SetSpeed(float speed, bool fromSlider = false)
    {
        Speed = Mathf.Clamp(speed, MinSpeed, MaxSpeed);
        if (!fromSlider && _speedSlider != null) _speedSlider.SetValueNoSignal(SpeedToSlider(Speed));
        if (_speedLabel != null) _speedLabel.Text = $"{Speed:F0} m/s";
    }

    public void LoadConfig(ConfigFile cfg) => SetSpeed((float)(double)cfg.GetValue("ui", "speed", (double)Speed));
    public void SaveConfig(ConfigFile cfg) => cfg.SetValue("ui", "speed", (double)Speed);

    /// <summary>The fly speed slider and its notes, the top of the View page.</summary>
    public void BuildSpeedSection(VBoxContainer page)
    {
        page.AddChild(new Label { Text = "Fly speed", ThemeTypeVariation = "Header" });
        _speedSlider = new HSlider
        {
            MinValue = 0, MaxValue = 100, Step = 0.5, Value = SpeedToSlider(Speed), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Scrollable = false, // the mouse wheel must scroll the sidebar, never move the slider
            TooltipText = "Fly speed (m/s, logarithmic). The mouse wheel over the 3D view changes it too.",
        };
        _speedSlider.ValueChanged += v => SetSpeed((float)SliderToSpeed(v), fromSlider: true);
        _speedSlider.DragEnded += _ => _host.SaveUiConfig();
        page.AddChild(_speedSlider);
        _speedLabel = new Label { Text = $"{Speed:F0} m/s" };
        page.AddChild(_speedLabel);
        page.AddChild(new Label { Text = "Mouse wheel over the 3D view changes it too. Hold Alt for x3.", ThemeTypeVariation = "Dim", AutowrapMode = TextServer.AutowrapMode.WordSmart });
    }

    // ---- the editor opens and closes ----

    /// <summary>Park the walk-mode player (frozen, hidden) and take over its view.</summary>
    public void EnterEditor()
    {
        var player = _host.Player;
        _savedPlayer = (player.GlobalPosition, player.RotationDegrees, player.InputEnabled);
        player.InputEnabled = false;
        player.Visible = false;
        player.Velocity = Vector3.Zero;
        player.SetPhysicsProcess(false);
        var e = player.Camera.GlobalBasis.GetEuler();
        _pitch = e.X; _yaw = e.Y;
        Node.GlobalPosition = player.Camera.GlobalPosition;
        ApplyRotation();
        _savedCam = null;
        Node.MakeCurrent();
    }

    /// <summary>Put the player back where it was parked and give it the view again.</summary>
    public void LeaveEditor()
    {
        var player = _host.Player;
        if (_savedPlayer is { } sp)
        {
            player.GlobalPosition = sp.pos;
            player.RotationDegrees = sp.euler;
            player.InputEnabled = sp.input;
        }
        player.Visible = true;
        player.Velocity = Vector3.Zero;
        player.SetPhysicsProcess(true);
        player.Camera.MakeCurrent();
    }

    /// <summary>Park the player on the ground below the camera, facing where the camera looks (the editor then closes and the player walks from there).</summary>
    public void DropPlayer()
    {
        var from = Node.GlobalPosition;
        var ground = _host.RayToGround(from + Vector3.Up * 2f, Vector3.Down, out var hit) ? hit : new Vector3(from.X, from.Y, from.Z);
        ParkPlayer(ground + Vector3.Up * 1.0f, Mathf.RadToDeg(_yaw));
    }

    /// <summary>Where F2 puts the player back: <paramref name="feet"/>, facing <paramref name="yawDeg"/> (degrees about +Y).</summary>
    public void ParkPlayer(Vector3 feet, float yawDeg)
        => _savedPlayer = (feet, new Vector3(0, yawDeg, 0), _savedPlayer?.input ?? true);

    // ---- moving the camera ----

    void ApplyRotation() => Node.Basis = Basis.FromEuler(new Vector3(_pitch, _yaw, 0));

    /// <summary>Mouse look: turn by a mouse movement (pixels).</summary>
    public void Look(Vector2 relative)
    {
        _yaw -= relative.X * 0.0025f;
        _pitch = Mathf.Clamp(_pitch - relative.Y * 0.0025f, -1.5698f, 1.5698f);
        ApplyRotation();
    }

    public void Fly(float dt)
    {
        if (_host.Modal || _host.GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit) return; // typing is not flying
        if (Input.IsKeyPressed(Key.Ctrl)) return; // Ctrl+D, Ctrl+Z ... are editor commands, not flying
        var dir = Vector3.Zero;
        var fwd = -Node.GlobalBasis.Z;
        if (Mathf.Abs(fwd.Y) > 0.98f) fwd = Node.GlobalBasis.Y; // looking straight down: W goes screen-up
        var right = Node.GlobalBasis.X;
        if (Input.IsKeyPressed(Key.W)) dir += fwd;
        if (Input.IsKeyPressed(Key.S)) dir -= fwd;
        if (Input.IsKeyPressed(Key.D)) dir += right;
        if (Input.IsKeyPressed(Key.A)) dir -= right;
        if (Input.IsKeyPressed(Key.E)) dir += Vector3.Up;
        if (Input.IsKeyPressed(Key.Q)) dir += Vector3.Down;
        if (Input.IsKeyPressed(Key.Space)) dir += Vector3.Up;
        if (Input.IsKeyPressed(Key.Shift)) dir += Vector3.Down;
        if (dir == Vector3.Zero) return;
        var speed = Speed * (Input.IsKeyPressed(Key.Alt) ? 3f : 1f);
        Node.GlobalPosition += dir.Normalized() * speed * dt;
    }

    /// <summary>Place the fly camera at pos looking at target (smoke tests, screenshots).</summary>
    public void LookFrom(Vector3 pos, Vector3 target)
    {
        Node.GlobalPosition = pos;
        var d = (target - pos).Normalized();
        _yaw = Mathf.Atan2(-d.X, -d.Z);
        _pitch = Mathf.Asin(Mathf.Clamp(d.Y, -1f, 1f));
        ApplyRotation();
    }

    /// <summary>T: snap to a top-down view above <paramref name="hoverPoint"/> (or the screen centre); again restores the camera.</summary>
    public void ToggleTopDown(Vector3? hoverPoint)
    {
        if (_savedCam is { } s)
        {
            Node.GlobalPosition = s.pos; _yaw = s.yaw; _pitch = s.pitch;
            ApplyRotation();
            _savedCam = null;
            return;
        }
        Vector3 ground;
        if (hoverPoint is { } hp) ground = hp;
        else
        {
            var size = _host.GetViewport().GetVisibleRect().Size;
            if (!_host.RayToGround(Node.ProjectRayOrigin(size / 2), Node.ProjectRayNormal(size / 2), out ground))
                ground = Node.GlobalPosition;
        }
        _savedCam = (Node.GlobalPosition, _yaw, _pitch);
        _pitch = -Mathf.Pi / 2;
        Node.GlobalPosition = new Vector3(ground.X, _host.Map.GroundHeightOrMean(ground.X, ground.Z) + TopDownHeight, ground.Z);
        ApplyRotation();
    }

    /// <summary>Bring the camera along with the character, which moved from <paramref name="before"/> to <paramref name="feet"/> (see <see cref="Tools.YouDot"/>).</summary>
    public void Follow(Vector3 before, Vector3 feet, bool first)
    {
        var cam = Node.GlobalPosition;
        if (TopDown) Node.GlobalPosition = new Vector3(feet.X, cam.Y, feet.Z); // centred on the character, same height
        else if (first)
        {
            // the first position: look straight down on the character (T takes the camera back to where it was)
            _savedCam = (cam, _yaw, _pitch);
            _pitch = -Mathf.Pi / 2;
            Node.GlobalPosition = new Vector3(feet.X, _host.Map.GroundHeightOrMean(feet.X, feet.Z) + TopDownHeight, feet.Z);
            ApplyRotation();
        }
        else Node.GlobalPosition = cam + new Vector3(feet.X - before.X, 0, feet.Z - before.Z);
    }
}
