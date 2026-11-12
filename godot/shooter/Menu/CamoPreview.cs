using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The customiser's live preview (ticket #141): the character alone in its own SubViewport, lit by a key and a fill light over the
/// menu backdrop, facing the camera. The turntable is turned by dragging or with the arrow keys (see <see cref="Turn"/>), the mouse
/// wheel and the plus and minus keys zoom (see <see cref="Zoom"/>), Home puts the view back (see <see cref="ResetView"/>). The camera
/// frames the whole figure or the head (<see cref="Frame"/>: the face paint cannot be judged from four metres away). The prone
/// toggle lays the figure down with the same pose remote players use (<see cref="CharacterView.SetStance"/>).
/// </summary>
public sealed class CamoPreview
{
    /// <summary>Degrees the turntable moves per pixel dragged and per arrow key press.</summary>
    public const float DegPerPixel = 0.6f, DegPerKey = 15f;
    /// <summary>The zoom range (1 is the framing's own distance, smaller is closer) and the step of one wheel notch or key press.</summary>
    public const float ZoomMin = 0.45f, ZoomMax = 1.25f, ZoomStep = 0.1f;
    /// <summary>Seconds the camera takes to move between framings (ui-ux.md section 3: 100 to 250 ms). Reduce-motion cuts instead.</summary>
    const float FrameTime = 0.2f;

    /// <summary>What the camera looks at: a point (metres above the feet) and the distance that fits it at zoom 1.</summary>
    public enum Framing { Body, Head }
    static readonly Vector3 BodyTarget = new(0, 0.95f, 0), HeadTarget = new(0, PlayerHitBox.ModelHeadHeight * PlayerHitBox.DrawScale, 0), ProneTarget = new(0, 0.35f, 0);
    const float BodyDistance = 3.3f, HeadDistance = 1.25f;

    /// <summary>The control to add to the menu. Its size is the preview's size.</summary>
    public readonly Control Root;
    readonly Node3D _turntable = new();
    readonly Camera3D _camera;
    readonly Control _overlay;
    readonly CharacterView? _view;
    float _turn, _zoom = 1f;
    Framing _framing = Framing.Body;
    bool _prone;
    Vector3 _target = BodyTarget;
    float _distance = BodyDistance;
    Tween? _move;

    public CamoPreview(Vector2 size)
    {
        var viewport = new SubViewport
        {
            Size = new Vector2I((int)size.X, (int)size.Y), OwnWorld3D = true, TransparentBg = false, Msaa3D = Viewport.Msaa.Msaa2X,
        };
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = ShooterTheme.Backdrop,
            AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.55f, 0.58f, 0.65f), AmbientLightEnergy = 0.9f,
        };
        viewport.AddChild(new WorldEnvironment { Environment = env });
        viewport.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-40, 30, 0), LightEnergy = 1.1f });
        // a weak fill from the other side, so the shaded half of the suit still shows its pattern
        viewport.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-15, -120, 0), LightEnergy = 0.35f });
        _camera = new Camera3D { Fov = 40, Current = true };
        viewport.AddChild(_camera);
        viewport.AddChild(_turntable);
        _view = CharacterView.Make();
        _turntable.AddChild(_view);
        _view.Apply(0, false, false, "");
        Turn(180f); // the figure's forward is -Z and the camera stands at +Z: turned round, it faces the camera
        PlaceCamera();

        var container = new SubViewportContainer { Stretch = true, MouseFilter = Control.MouseFilterEnum.Ignore };
        container.AddChild(viewport);
        // one framed panel: the viewport fills it, and a transparent overlay on top takes the drag, the wheel and the keys
        var frame = new PanelContainer { ThemeTypeVariation = "HudPanel", CustomMinimumSize = size, FocusMode = Control.FocusModeEnum.None };
        frame.AddChild(container);
        _overlay = new Control { MouseFilter = Control.MouseFilterEnum.Stop, FocusMode = Control.FocusModeEnum.All, MouseDefaultCursorShape = Control.CursorShape.Move };
        _overlay.GuiInput += OnGuiInput;
        // the keyboard focus is shown the same way as on every other control: the accent ring
        var ring = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _overlay.FocusEntered += () => { ring.AddThemeStyleboxOverride("panel", _overlay.GetThemeStylebox("focus", "Button")); ring.Visible = true; };
        _overlay.FocusExited += () => ring.Visible = false;
        frame.AddChild(_overlay);
        frame.AddChild(ring);
        Root = frame;
    }

    /// <summary>The control that takes the keyboard focus (for focus neighbours and tooltips).</summary>
    public Control FocusTarget => _overlay;

    /// <summary>Shows a look. A look that camo.json rejects leaves the previous one on screen.</summary>
    public void Show(CamoSpec spec, CamoFile data) => _view?.SetCamo(spec, data);

    public void SetProne(bool prone)
    {
        _prone = prone;
        _view?.SetStance(prone ? Stance.Prone : Stance.Stand);
        MoveCamera();
    }

    /// <summary>Holds a weapon (its id from weapons.json) in the figure's hands; "" holds nothing (the loadout editor, ticket #147).</summary>
    public void ShowWeapon(string weaponId) => _view?.Apply(0, false, false, weaponId);

    /// <summary>Turns the figure by <paramref name="degrees"/> about the vertical axis.</summary>
    public void Turn(float degrees)
    {
        _turn = Mathf.PosMod(_turn + degrees, 360f);
        _turntable.RotationDegrees = new Vector3(0, _turn, 0);
    }

    /// <summary>Moves the camera closer (negative) or further (positive) by <paramref name="steps"/> zoom steps, within the zoom range.</summary>
    public void Zoom(float steps)
    {
        _zoom = Math.Clamp(_zoom + steps * ZoomStep, ZoomMin, ZoomMax);
        PlaceCamera();
    }

    /// <summary>Frames the whole figure or the head. Framing the head also turns the figure to face the camera and resets the zoom.</summary>
    public void Frame(Framing framing)
    {
        if (framing == _framing) return;
        _framing = framing;
        _zoom = 1f;
        if (framing == Framing.Head) { _turn = 180f; _turntable.RotationDegrees = new Vector3(0, _turn, 0); }
        MoveCamera();
    }

    /// <summary>Faces the figure to the camera at the framing's own distance.</summary>
    public void ResetView()
    {
        _zoom = 1f;
        _turn = 180f;
        _turntable.RotationDegrees = new Vector3(0, _turn, 0);
        MoveCamera();
    }

    /// <summary>Where the current framing wants the camera. Lying prone there is no head-height close-up: the whole figure is framed.</summary>
    (Vector3 Target, float Distance) Wanted() =>
        _prone ? (ProneTarget, BodyDistance) : _framing == Framing.Head ? (HeadTarget, HeadDistance) : (BodyTarget, BodyDistance);

    void MoveCamera()
    {
        var (target, distance) = Wanted();
        _move?.Kill();
        if (GameSettings.Current.ReduceMotion || UiShell.Instance is not { Instant: false } || !Root.IsInsideTree())
        {
            _target = target; _distance = distance;
            PlaceCamera();
            return;
        }
        Vector3 fromTarget = _target; var fromDistance = _distance;
        _move = Root.CreateTween();
        _move.TweenMethod(Callable.From<float>(t =>
        {
            _target = fromTarget.Lerp(target, t); _distance = Mathf.Lerp(fromDistance, distance, t);
            PlaceCamera();
        }), 0f, 1f, FrameTime).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
    }

    void PlaceCamera() => _camera.Position = _target + new Vector3(0, 0, _distance * _zoom);

    void OnGuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { Pressed: true } b when b.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown:
                Zoom(b.ButtonIndex == MouseButton.WheelUp ? -1 : 1);
                _overlay.AcceptEvent();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }:
                _overlay.GrabFocus();
                break;
            case InputEventMouseMotion m when (m.ButtonMask & MouseButtonMask.Left) != 0:
                Turn(m.Relative.X * DegPerPixel);
                _overlay.AcceptEvent();
                break;
            case InputEventKey { Pressed: true } k when k.Keycode is Key.Left or Key.Right:
                Turn(k.Keycode == Key.Left ? -DegPerKey : DegPerKey);
                _overlay.AcceptEvent();
                break;
            case InputEventKey { Pressed: true } k when k.Keycode is Key.Plus or Key.Equal or Key.KpAdd or Key.Minus or Key.KpSubtract:
                Zoom(k.Keycode is Key.Minus or Key.KpSubtract ? 1 : -1);
                _overlay.AcceptEvent();
                break;
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Home }:
                ResetView();
                _overlay.AcceptEvent();
                break;
        }
    }
}
