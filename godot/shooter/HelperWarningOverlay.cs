using Godot;

namespace Suchohrad.Shooter;

/// <summary>
/// The helicopter helper's targeting warning (killstreak "helicopter_helper"): for the one second before its burst, a red dot
/// sits on the helicopter's screen position, or on the screen edge in its direction when it is off screen or behind the
/// camera. The words HELICOPTER TARGETING YOU are always shown, so the cue is never colour alone. The dot does not pulse
/// (reduce-motion friendly). Tokens and sizes come from <see cref="ShooterTheme"/>, scaled by the screen height against 1080 p.
/// </summary>
public partial class HelperWarningOverlay : CanvasLayer
{
    /// <summary>Distance from the screen edge the dot is kept inside, reference pixels.</summary>
    const float EdgeInsetPx = 48f;

    readonly WarningPanel _panel = new();
    Func<Vector3?> _where = () => null;
    float _left;

    public override void _Ready()
    {
        Layer = 9; // over the world and the gunship overlay (5), under the HUD (10)
        Visible = false;
        _panel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _panel.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(_panel);
    }

    /// <summary>Shows the warning for <paramref name="seconds"/>. <paramref name="where"/> gives the helicopter's position each frame (null once it is gone).</summary>
    public void Show(Func<Vector3?> where, float seconds)
    {
        _where = where;
        _left = seconds;
        _panel.Text = Strings.T("helper.warning");
        Visible = true;
    }

    public void Dismiss()
    {
        Visible = false;
        _left = 0;
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _left -= (float)delta;
        if (_left <= 0)
        {
            Dismiss();
            return;
        }
        _panel.Place(DotPosition(_where()));
    }

    /// <summary>Where the dot goes: the helicopter's projection, pulled in to the screen edge when it lies outside. Null when there is no helicopter.</summary>
    Vector2? DotPosition(Vector3? world)
    {
        var viewport = GetViewport();
        var cam = viewport.GetCamera3D();
        if (world is not { } p || cam == null) return null;

        var size = viewport.GetVisibleRect().Size;
        var centre = size / 2f;
        var local = cam.GlobalTransform.AffineInverse() * p;
        // the camera looks down -Z: a point with Z >= 0 is behind it, so it is shown in its direction from the centre
        var target = local.Z < 0 ? cam.UnprojectPosition(p) : centre + new Vector2(local.X, -local.Y) * size.Length();

        var inset = EdgeInsetPx * ShooterTheme.ScreenScale(size);
        var half = size / 2f - new Vector2(inset, inset);
        var dir = target - centre;
        if (Math.Abs(dir.X) <= half.X && Math.Abs(dir.Y) <= half.Y) return target;
        if (dir.Length() < 0.001f) return centre;
        // scale the direction until it reaches the inset rectangle's edge
        var t = Math.Min(dir.X == 0 ? float.PositiveInfinity : half.X / Math.Abs(dir.X), dir.Y == 0 ? float.PositiveInfinity : half.Y / Math.Abs(dir.Y));
        return centre + dir * t;
    }

    /// <summary>The drawing: a red dot with a dark rim, and the warning words on a scrim at the top of the screen.</summary>
    sealed partial class WarningPanel : Control
    {
        public string Text = "";
        Vector2? _at;

        public void Place(Vector2? at)
        {
            _at = at;
            QueueRedraw();
        }

        public override void _Draw()
        {
            var font = ThemeDB.FallbackFont;
            var s = ShooterTheme.ScreenScale(Size);
            var body = (int)Math.Round(ShooterTheme.FontBody * s);
            var pad = ShooterTheme.Space * s;
            var top = ShooterTheme.Margin * s;

            var textW = font.GetStringSize(Text, HorizontalAlignment.Left, -1, body).X + 2 * pad;
            var box = new Rect2((Size.X - textW) / 2f, top, textW, body + 2 * pad);
            DrawRect(box, ShooterTheme.Scrim);
            DrawString(font, new Vector2(box.Position.X + pad, top + pad + body), Text, HorizontalAlignment.Left, -1, body, ShooterTheme.Text);

            if (_at is { } at)
            {
                var r = 10f * s;
                DrawCircle(at, r + ShooterTheme.BorderStrong * s, ShooterTheme.Outline);
                DrawCircle(at, r, ShooterTheme.Danger);
            }
        }
    }
}
