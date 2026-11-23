using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.HeadsUp;

/// <summary>
/// The warning an Aircraft's owner sees while a Lock-on launcher is on their Aircraft (issue #347, ADR 0031), in whatever view they are in (the
/// gunship's sight, the AC-130's, on foot under their UAV). One icon at the top of the screen on a scrim, no words, and each level has its own
/// shape so the colour is never the only cue: open corner brackets while a Lock builds, a closed diamond when it is complete, and the diamond with
/// a missile's arrowhead driving into it once a missile is in the air. Nothing pulses (the beeps carry the urgency; reduce motion needs no special
/// case). Tokens and sizes come from <see cref="ShooterTheme"/>, scaled by the screen height against 1080 p.
/// </summary>
public partial class LockAlertOverlay : CanvasLayer
{
    readonly AlertPanel _panel = new();

    public override void _Ready()
    {
        Layer = 9; // over the world and the gunship overlay (5), under the HUD (10): the helper warning's layer
        Visible = false;
        _panel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _panel.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(_panel);
    }

    /// <summary>Shows the icon of <paramref name="level"/>; None hides it. Redraws only on a change.</summary>
    public void Present(LockAlert level)
    {
        Visible = level != LockAlert.None;
        if (_panel.Level == level) return;
        _panel.Level = level;
        _panel.QueueRedraw();
    }

    sealed partial class AlertPanel : Control
    {
        public LockAlert Level;

        /// <summary>Reference pixels: half the icon's box, and how far under the top margin it sits (clear of the helper warning's words).</summary>
        const float Half = 4 * ShooterTheme.Space, Drop = 8 * ShooterTheme.Space;

        static readonly Vector2[] Corners = { new(1, 1), new(-1, 1), new(1, -1), new(-1, -1) };
        readonly Vector2[] _diamond = new Vector2[5];
        readonly Vector2[] _head = new Vector2[3];

        public override void _Draw()
        {
            if (Level == LockAlert.None) return;
            var s = ShooterTheme.ScreenScale(Size);
            var half = Half * s;
            var pad = 2 * ShooterTheme.Space * s;
            var at = new Vector2(Size.X / 2f, ShooterTheme.Margin * s + Drop * s + half);
            var thick = ShooterTheme.BorderStrong * s;
            var colour = Level == LockAlert.Locking ? ShooterTheme.Warn : ShooterTheme.Danger;

            var wide = Level == LockAlert.Launched ? half * 1.5f : 0f; // room for the missile on the left
            DrawRect(new Rect2(at.X - half - pad - wide, at.Y - half - pad, 2 * (half + pad) + wide, 2 * (half + pad)), ShooterTheme.Scrim);

            if (Level == LockAlert.Locking)
            {
                foreach (var c in Corners)
                {
                    var p = at + c * half;
                    DrawLine(p, p - new Vector2(c.X, 0) * half * 0.5f, colour, thick);
                    DrawLine(p, p - new Vector2(0, c.Y) * half * 0.5f, colour, thick);
                }
                DrawCircle(at, thick, colour);
                return;
            }

            _diamond[0] = at + new Vector2(0, -half); _diamond[1] = at + new Vector2(half, 0); _diamond[2] = at + new Vector2(0, half); _diamond[3] = at + new Vector2(-half, 0); _diamond[4] = _diamond[0];
            DrawPolyline(_diamond, colour, thick);
            DrawCircle(at, thick * 1.5f, colour);
            if (Level != LockAlert.Launched) return;

            // the missile: a shaft from the left with an arrowhead at the diamond's edge
            var tip = at + new Vector2(-half * 0.45f, 0);
            var tail = at + new Vector2(-half - wide, 0);
            DrawLine(tail, tip, colour, thick);
            _head[0] = tip; _head[1] = tip + new Vector2(-half * 0.5f, -half * 0.3f); _head[2] = tip + new Vector2(-half * 0.5f, half * 0.3f);
            DrawColoredPolygon(_head, colour);
        }
    }
}
