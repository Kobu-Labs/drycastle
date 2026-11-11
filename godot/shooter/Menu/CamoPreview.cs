using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// The customiser's live preview (ticket #141): the character alone in its own SubViewport, lit by one key light over the
/// menu backdrop. The turntable is turned by dragging or with the arrow keys (see <see cref="Turn"/>); the prone toggle
/// lays the figure down with the same pose remote players use (<see cref="CharacterView.SetStance"/>).
/// </summary>
public sealed class CamoPreview
{
    /// <summary>Degrees the turntable moves per pixel dragged and per arrow key press.</summary>
    public const float DegPerPixel = 0.6f, DegPerKey = 15f;

    /// <summary>The control to add to the menu. Its size is the preview's size.</summary>
    public readonly Control Root;
    readonly Node3D _turntable = new();
    readonly CharacterView? _view;
    float _turn;

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
        viewport.AddChild(new Camera3D { Position = new Vector3(0, 1.0f, 3.4f), RotationDegrees = new Vector3(-8, 0, 0), Fov = 40, Current = true });
        viewport.AddChild(_turntable);
        _view = CharacterView.Make();
        _turntable.AddChild(_view);
        _view.Apply(0, false, false, "");

        var container = new SubViewportContainer { Stretch = true, MouseFilter = Control.MouseFilterEnum.Ignore };
        container.AddChild(viewport);
        // one framed panel: the viewport fills it, and a transparent overlay on top takes the drag and the arrow keys
        var frame = new PanelContainer { ThemeTypeVariation = "HudPanel", CustomMinimumSize = size, FocusMode = Control.FocusModeEnum.None };
        frame.AddChild(container);
        var overlay = new Control { MouseFilter = Control.MouseFilterEnum.Stop, FocusMode = Control.FocusModeEnum.All, MouseDefaultCursorShape = Control.CursorShape.Move };
        overlay.GuiInput += e => OnGuiInput(e, overlay);
        frame.AddChild(overlay);
        Root = frame;
    }

    /// <summary>Shows a look. A look that camo.json rejects leaves the previous one on screen.</summary>
    public void Show(CamoSpec spec, CamoFile data) => _view?.SetCamo(spec, data);

    public void SetProne(bool prone) => _view?.SetStance(prone ? Stance.Prone : Stance.Stand);

    /// <summary>Turns the figure by <paramref name="degrees"/> about the vertical axis.</summary>
    public void Turn(float degrees)
    {
        _turn = Mathf.PosMod(_turn + degrees, 360f);
        _turntable.RotationDegrees = new Vector3(0, _turn, 0);
    }

    void OnGuiInput(InputEvent e, Control overlay)
    {
        switch (e)
        {
            case InputEventMouseMotion m when (m.ButtonMask & MouseButtonMask.Left) != 0:
                Turn(m.Relative.X * DegPerPixel);
                overlay.AcceptEvent();
                break;
            case InputEventKey { Pressed: true, Echo: false } k when k.Keycode is Key.Left or Key.Right:
                Turn(k.Keycode == Key.Left ? -DegPerKey : DegPerKey);
                overlay.AcceptEvent();
                break;
        }
    }
}
