using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.HeadsUp.Streaks;

/// <summary>What the Lock-on launcher's sight shows this frame. <c>Target</c> is the held Aircraft's world position (null: none); <c>Range</c> its distance as a share of the lock range (above 1 only for a kind with no limit, <c>Unlimited</c>).</summary>
public readonly record struct LockSight(float ConeDeg, LockPhase Phase, bool Tracking, float Progress, Vector3? Target, float Range, bool Unlimited, int Loaded, int Reserve, bool Reloading);

/// <summary>
/// The Lock-on launcher's sight (issue #347, ADR 0031), up while the launcher is in the hand: the lock circle (as wide on screen as the Lock's cone
/// is in the world), an aim pip at its centre, the Lock's progress as an arc round the circle, a mark on the held Aircraft, a range track under the
/// circle and the missiles left as pips. Icons only, no words. A Lock that builds and a complete one differ in shape, not only in colour: open
/// corner brackets and an open arc while it builds, a closed diamond and a closed double ring when complete. Nothing pulses (reduce motion needs no
/// special case). Tokens and sizes come from <see cref="ShooterTheme"/>, scaled by the screen height against 1080 p.
/// </summary>
public partial class LockOnOverlay : CanvasLayer
{
    readonly SightPanel _panel = new();

    public override void _Ready()
    {
        Layer = 6; // over the world and the gunship overlay (5), under the warnings (9) and the HUD (10)
        Visible = false;
        _panel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _panel.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(_panel);
    }

    /// <summary>Shows the sight with this frame's state. Call every frame while the launcher is in the hand (the Aircraft moves across the screen).</summary>
    public void Present(in LockSight sight)
    {
        _panel.Sight = sight;
        Visible = true;
        _panel.QueueRedraw();
    }

    public void Dismiss() => Visible = false;

    /// <summary>The sight's drawing, all in one Control so the frame costs one draw.</summary>
    sealed partial class SightPanel : Control
    {
        public LockSight Sight;

        /// <summary>Reference pixels: the corner brackets' arm, the mark on the Aircraft (half its width, open and closed), the range track, a missile pip.</summary>
        const float Arm = 3 * ShooterTheme.Space, MarkOpen = 5 * ShooterTheme.Space, MarkClosed = 3 * ShooterTheme.Space, TrackW = 20 * ShooterTheme.Space, PipW = ShooterTheme.Space, PipH = 3 * ShooterTheme.Space;

        static readonly Vector2[] Corners = { new(1, 1), new(-1, 1), new(1, -1), new(-1, -1) };
        readonly Vector2[] _diamond = new Vector2[5];

        public override void _Draw()
        {
            var cam = GetViewport().GetCamera3D();
            if (cam == null) return;
            var size = Size;
            var s = ShooterTheme.ScreenScale(size);
            var centre = size / 2f;
            var thin = ShooterTheme.Border * 2 * s; var thick = ShooterTheme.BorderStrong * s;
            var locked = Sight.Phase == LockPhase.Locked;
            var state = locked ? ShooterTheme.Danger : ShooterTheme.Good;

            // the lock circle: as wide as the cone at this field of view, never off the screen
            var r = MathF.Tan(Mathf.DegToRad(Sight.ConeDeg)) / MathF.Tan(Mathf.DegToRad(cam.Fov) / 2f) * size.Y / 2f;
            r = MathF.Min(r, size.Y / 2f - ShooterTheme.Margin * s);
            DrawArc(centre, r + thin, 0, Mathf.Tau, 96, ShooterTheme.Outline, thin);
            DrawArc(centre, r, 0, Mathf.Tau, 96, Sight.Phase == LockPhase.Idle ? ShooterTheme.Dim : ShooterTheme.Text, thin);
            foreach (var c in Corners)
            {
                var p = centre + c * r * 0.7071f;
                DrawLine(p, p + new Vector2(c.X, 0) * Arm * s, ShooterTheme.Text, thick);
                DrawLine(p, p + new Vector2(0, c.Y) * Arm * s, ShooterTheme.Text, thick);
            }

            // the Lock's progress: an arc from the top, clockwise; closed and doubled when complete
            if (Sight.Phase != LockPhase.Idle)
            {
                var from = -Mathf.Pi / 2f;
                DrawArc(centre, r - thick * 1.5f, from, from + Mathf.Tau * Math.Clamp(Sight.Progress, 0f, 1f), 96, Sight.Tracking ? state : ShooterTheme.Dim, thick);
                if (locked) DrawArc(centre, r - thick * 3.5f, 0, Mathf.Tau, 96, state, thin);
            }

            // the aim pip
            DrawCircle(centre, thick, ShooterTheme.Outline);
            DrawCircle(centre, thin, ShooterTheme.Text);

            // the mark on the held Aircraft: open brackets that close in as the Lock builds, a closed diamond when complete
            if (Sight.Target is { } world && !cam.IsPositionBehind(world))
            {
                var at = cam.UnprojectPosition(world);
                if (locked)
                {
                    var h = MarkClosed * s;
                    _diamond[0] = at + new Vector2(0, -h); _diamond[1] = at + new Vector2(h, 0); _diamond[2] = at + new Vector2(0, h); _diamond[3] = at + new Vector2(-h, 0); _diamond[4] = _diamond[0];
                    DrawPolyline(_diamond, ShooterTheme.Outline, thick + thin);
                    DrawPolyline(_diamond, state, thick);
                    DrawLine(at - new Vector2(h * 0.4f, 0), at + new Vector2(h * 0.4f, 0), state, thin);
                    DrawLine(at - new Vector2(0, h * 0.4f), at + new Vector2(0, h * 0.4f), state, thin);
                }
                else
                {
                    var h = Mathf.Lerp(MarkOpen, MarkClosed, Math.Clamp(Sight.Progress, 0f, 1f)) * s;
                    var arm = h * 0.5f;
                    var colour = Sight.Tracking ? state : ShooterTheme.Dim;
                    foreach (var c in Corners)
                    {
                        var p = at + c * h;
                        DrawLine(p, p - new Vector2(c.X, 0) * arm, colour, thick);
                        DrawLine(p, p - new Vector2(0, c.Y) * arm, colour, thick);
                    }
                }
            }

            // under the circle: the range track (the stop at its right end is the lock range; a kind with no limit has no stop), then the missiles
            var y = MathF.Min(centre.Y + r + 4 * ShooterTheme.Space * s, size.Y - ShooterTheme.Margin * s - PipH * s - 3 * ShooterTheme.Space * s);
            var left = centre.X - TrackW * s / 2f; var right = centre.X + TrackW * s / 2f;
            DrawLine(new Vector2(left, y), new Vector2(right, y), ShooterTheme.Dim, thin);
            DrawLine(new Vector2(left, y - ShooterTheme.Space * s), new Vector2(left, y + ShooterTheme.Space * s), ShooterTheme.Dim, thin);
            if (Sight.Target == null || !Sight.Unlimited) DrawLine(new Vector2(right, y - ShooterTheme.Space * s), new Vector2(right, y + ShooterTheme.Space * s), ShooterTheme.Text, thick);
            if (Sight.Target != null)
            {
                var x = Mathf.Lerp(left, right, Math.Clamp(Sight.Range, 0f, 1f));
                var tip = new Vector2(x, y - thin);
                DrawLine(tip, tip + new Vector2(-ShooterTheme.Space * s * 0.75f, -ShooterTheme.Space * s * 1.5f), state, thick);
                DrawLine(tip, tip + new Vector2(ShooterTheme.Space * s * 0.75f, -ShooterTheme.Space * s * 1.5f), state, thick);
            }

            // the missiles: the one in the tube first (framed; hollow while it is being loaded), then the reserve
            var total = Sight.Loaded + Sight.Reserve;
            var slots = Math.Max(total, 1);
            var gap = ShooterTheme.Space * 1.5f * s;
            var w = PipW * s; var hgt = PipH * s;
            var x0 = centre.X - (slots * w + (slots - 1) * gap) / 2f;
            var top = y + 2 * ShooterTheme.Space * s;
            for (var i = 0; i < slots; i++)
            {
                var box = new Rect2(x0 + i * (w + gap), top, w, hgt);
                var inTube = i == 0;
                var have = i < total;
                var filled = have && !(inTube && Sight.Loaded == 0);
                if (filled) DrawRect(box, ShooterTheme.Text);
                else DrawRect(box, have ? ShooterTheme.Text : ShooterTheme.Dim, false, thin);
                if (inTube) DrawRect(box.Grow(ShooterTheme.Space * 0.5f * s), Sight.Loaded > 0 ? ShooterTheme.Text : ShooterTheme.Dim, false, thin);
            }
        }
    }
}
