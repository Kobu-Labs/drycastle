using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The hit diagram (GLOSSARY "Hit diagram"): after a sniper rifle kills a wild animal, a card on the right of the screen shows the species' side
/// view with the place the bullet entered marked by a ring and a cross (shape, not colour alone), the species' name above, and the distance and
/// the weapon below. It stays <see cref="HuntShotSeconds"/>, fades out and ignores the mouse. Built once; a kill only sets its values.
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
    /// Shows the hit diagram: the species' name and side view (<paramref name="silhouette"/> null draws a plain body), the bullet's entry
    /// (<paramref name="along"/> 0 tail to 1 nose, <paramref name="height"/> 0 feet to 1 top), the flank it came in by, and the line under it.
    /// </summary>
    public void ShowHuntShot(string species, Texture2D? silhouette, float along, float height, bool rightFlank, string line)
    {
        Set(_huntTitle, species);
        Set(_huntLine, line);
        _huntView.Set(silhouette, along, height, rightFlank);
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
    /// The side view: the animal as seen from the flank the bullet came in by (its right flank shows the nose on the right, its left flank is the
    /// mirror image), fitted in the box with its proportions kept, and the entry marked.
    /// </summary>
    sealed partial class HuntShotView : Control
    {
        public const float W = 320, H = 208;
        const float MarkRadius = 9, MarkArm = 16;
        Texture2D? _tex;
        float _along = 0.5f, _height = 0.5f;
        bool _right = true;

        public HuntShotView() { CustomMinimumSize = new Vector2(W, H); MouseFilter = MouseFilterEnum.Ignore; }

        public void Set(Texture2D? tex, float along, float height, bool rightFlank)
        {
            _tex = tex; _along = Math.Clamp(along, 0f, 1f); _height = Math.Clamp(height, 0f, 1f); _right = rightFlank;
            QueueRedraw();
        }

        public override void _Draw()
        {
            // the body's rectangle: the silhouette's proportions (a plain 2:1 body without one), as large as fits
            var aspect = _tex != null && _tex.GetHeight() > 0 ? _tex.GetWidth() / (float)_tex.GetHeight() : 2f;
            var size = Size.X / Size.Y > aspect ? new Vector2(Size.Y * aspect, Size.Y) : new Vector2(Size.X, Size.X / aspect);
            var rect = new Rect2((Size - size) / 2, size);
            if (_tex != null)
                DrawTextureRect(_tex, _right ? rect : new Rect2(rect.Position.X + rect.Size.X, rect.Position.Y, -rect.Size.X, rect.Size.Y), false, ShooterTheme.Dim);
            else
                DrawRect(rect, ShooterTheme.Dim, false, ShooterTheme.BorderStrong);
            var x = _right ? _along : 1f - _along;
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
