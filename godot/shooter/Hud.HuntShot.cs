using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The hit diagram (GLOSSARY "Hit diagram"): after a sniper rifle kills a wild animal, a card on the right of the screen shows the species' side
/// view with the place the bullet entered marked by a ring and a cross (shape, not colour alone), the species' name above, and the distance and
/// the weapon below. The side view is always the same picture of the animal, standing, whatever it was doing; the mark is the hit moved onto
/// it (<see cref="AnimalShape.MapOnto"/>: a head hit on the head, a body hit on the same place of the body). An animal without a shape shows
/// the species' baked picture or a plain body. It stays <see cref="HuntShotSeconds"/>, fades out and ignores the mouse. Built once; a kill only sets its values.
/// </summary>
public partial class Hud
{
    const double HuntShotSeconds = 5, HuntShotFade = 0.25;
    PanelContainer _huntPanel = null!;
    Label _huntTitle = null!, _huntLine = null!;
    HuntShotView _huntView = null!;
    double _huntUntil;

    void BuildHuntShot()
    {
        _huntPanel = Place(P("HudPanel"), 1, 0.5f, -ShooterTheme.Margin, 0, HuntShotView.W + 2 * ShooterTheme.Space, HuntShotView.H + 2 * ShooterTheme.FontSmall + 6 * ShooterTheme.Space);
        _huntPanel.Visible = false;
        _root.AddChild(_huntPanel);
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", ShooterTheme.Space / 2);
        _huntPanel.AddChild(v);
        _huntTitle = L("", "HudHead", HorizontalAlignment.Center);
        _huntLine = L("", "HudSmall", HorizontalAlignment.Center);
        _huntView = new HuntShotView();
        v.AddChild(_huntTitle); v.AddChild(_huntView); v.AddChild(_huntLine);
    }

    /// <summary>
    /// Shows the hit diagram: the species' name and side view (the hit shape <paramref name="shape"/>, else the picture <paramref name="silhouette"/>,
    /// else a plain body), the bullet's entry (<paramref name="along"/> 0 rear to 1 front, <paramref name="height"/> 0 ground to 1 top, of what is
    /// drawn) and the line under it. The animal always faces left.
    /// </summary>
    public void ShowHuntShot(string species, AnimalShape? shape, Texture2D? silhouette, float along, float height, string line)
    {
        Set(_huntTitle, species);
        Set(_huntLine, line);
        _huntView.Set(shape, silhouette, along, height);
        _huntPanel.Visible = true;
        Fade(_huntPanel, 1);
        _huntUntil = Now + HuntShotSeconds;
    }

    void UpdateHuntShot(double now)
    {
        var left = _huntUntil - now;
        if (left <= 0) { _huntUntil = 0; _huntPanel.Visible = false; return; }
        Fade(_huntPanel, Settings.ReduceMotion ? 1 : (float)Math.Clamp(left / HuntShotFade, 0, 1));
    }

    void ClearHuntShot() { _huntUntil = 0; if (_huntPanel.Visible) _huntPanel.Visible = false; }

    /// <summary>
    /// The side view: the animal always faces left (the nose on the left) whichever flank the bullet came in by, fitted in the box with its
    /// proportions kept, and the entry marked along its length and height.
    /// </summary>
    sealed partial class HuntShotView : Control
    {
        public const float W = 320, H = 208;
        const float MarkRadius = 9, MarkArm = 16;
        Texture2D? _tex;
        AnimalShape? _shape;
        float _along = 0.5f, _height = 0.5f;

        public HuntShotView() { CustomMinimumSize = new Vector2(W, H); MouseFilter = MouseFilterEnum.Ignore; }

        /// <summary>The pictures of the shapes shown so far, decoded once each (a shape is one object for the whole session).</summary>
        static readonly Dictionary<AnimalShape, Texture2D?> Pictures = new();

        static Texture2D? PictureOf(AnimalShape shape)
        {
            if (Pictures.TryGetValue(shape, out var known)) return known;
            Texture2D? tex = null;
            if (shape.Picture is { Length: > 0 } png)
            {
                var img = new Image();
                if (img.LoadPngFromBuffer(png) == Error.Ok) tex = ImageTexture.CreateFromImage(img);
            }
            return Pictures[shape] = tex;
        }

        public void Set(AnimalShape? shape, Texture2D? tex, float along, float height)
        {
            // a shape's own picture covers its grid exactly, so it is drawn in the grid's place; the cells are the fallback
            if (shape != null && PictureOf(shape) is { } picture) tex = picture;
            _shape = shape; _tex = tex; _along = Math.Clamp(along, 0f, 1f); _height = Math.Clamp(height, 0f, 1f);
            QueueRedraw();
        }

        public override void _Draw()
        {
            // the body's rectangle: the silhouette's proportions (a plain 2:1 body without one), as large as fits
            var aspect = _shape != null ? _shape.Length / _shape.Height : _tex != null && _tex.GetHeight() > 0 ? _tex.GetWidth() / (float)_tex.GetHeight() : 2f;
            var size = Size.X / Size.Y > aspect ? new Vector2(Size.Y * aspect, Size.Y) : new Vector2(Size.X, Size.X / aspect);
            var rect = new Rect2((Size - size) / 2, size);
            if (_shape != null && _tex == null)
            {
                // a shape with no picture: the hit shape itself, cell by cell (each row's covered runs as one rectangle), the front of the animal on the left
                var cw = rect.Size.X / _shape.Cols; var ch = rect.Size.Y / _shape.Rows;
                for (var row = 0; row < _shape.Rows; row++)
                {
                    var y = rect.Position.Y + (_shape.Rows - 1 - row) * ch;
                    for (var col = 0; col < _shape.Cols; col++)
                    {
                        if (!_shape.Covered(col, row)) continue;
                        var start = col;
                        while (col + 1 < _shape.Cols && _shape.Covered(col + 1, row)) col++;
                        // columns run from the rear: column c is drawn at Cols - 1 - c from the left
                        DrawRect(new Rect2(rect.Position.X + (_shape.Cols - 1 - col) * cw, y, (col - start + 1) * cw + 0.5f, ch + 0.5f), ShooterTheme.Dim);
                    }
                }
            }
            else if (_tex != null)
                // a shape's picture or the species' baked one, both with the nose on the right
                // the picture is baked with the nose on the right: a negative width mirrors it in place (the rectangle keeps its corner)
                DrawTextureRect(_tex, new Rect2(rect.Position, new Vector2(-rect.Size.X, rect.Size.Y)), false, ShooterTheme.Dim);
            else
                DrawRect(rect, ShooterTheme.Dim, false, ShooterTheme.BorderStrong);
            var x = 1f - _along; // the nose is on the left
            var at = rect.Position + new Vector2(x * rect.Size.X, (1f - _height) * rect.Size.Y);
            // the entry: a dark-edged ring and a cross through it, so it reads on the light body and on the panel alike
            DrawArc(at, MarkRadius, 0, Mathf.Tau, 32, ShooterTheme.Outline, ShooterTheme.BorderStrong + 2f);
            DrawLine(at - new Vector2(MarkArm, 0), at + new Vector2(MarkArm, 0), ShooterTheme.Outline, ShooterTheme.BorderStrong + 2f);
            DrawLine(at - new Vector2(0, MarkArm), at + new Vector2(0, MarkArm), ShooterTheme.Outline, ShooterTheme.BorderStrong + 2f);
            DrawArc(at, MarkRadius, 0, Mathf.Tau, 32, ShooterTheme.Danger, ShooterTheme.BorderStrong);
            DrawLine(at - new Vector2(MarkArm, 0), at + new Vector2(MarkArm, 0), ShooterTheme.Danger, ShooterTheme.BorderStrong - 1f);
            DrawLine(at - new Vector2(0, MarkArm), at + new Vector2(0, MarkArm), ShooterTheme.Danger, ShooterTheme.BorderStrong - 1f);
            DrawCircle(at, 2.5f, ShooterTheme.Danger);
        }

        public override void _Notification(int what) { if (what == NotificationResized) QueueRedraw(); }
    }
}
