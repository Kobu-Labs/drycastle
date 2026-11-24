using Godot;
using Drycastle.Shooter.Menu;

namespace Drycastle.Shooter.HeadsUp.Vision;

/// <summary>The mil-dots and the zero of a scope with bullet drop: the dots follow the scope's FOV, the words change only with the zero or the dot spacing.</summary>
internal sealed class ScopeView
{
    readonly ScopeOverlay _overlay;
    readonly Label _zero;
    (int Zero, int Mils) _zeroShown = (-1, -1);

    public ScopeView(Control root)
    {
        _overlay = new ScopeOverlay { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(_overlay);
        // the zero and what one dot is worth, inside the lower part of the scope's circle
        _zero = HudUi.Place(HudUi.L("", "HudSmall", HorizontalAlignment.Center), 0.5f, 0.76f, -240, 0, 480, 24);
        _zero.Visible = false;
        root.AddChild(_zero);
    }

    public bool Up => _overlay.Visible;

    /// <summary>Raises or lowers the scope; true when that changed it.</summary>
    public bool SetUp(bool up)
    {
        if (_overlay.Visible == up) return false;
        _overlay.Visible = up;
        _overlay.QueueRedraw();
        return true;
    }

    /// <summary>Lowers it without a redraw (the binoculars took the view).</summary>
    public void Hide()
    {
        if (_overlay.Visible) _overlay.Visible = false;
    }

    /// <summary>The scope that is up: its vertical FOV in degrees and the zero dialled, metres (0: no bullet drop, so no dots and no zero); (0, 0) when none.</summary>
    public void Update((float FovDeg, float ZeroM) sc)
    {
        var on = sc.ZeroM > 0 && sc.FovDeg > 0;
        if (_zero.Visible != on) _zero.Visible = on;
        _overlay.SetMils(on ? sc.FovDeg : 0f);
        if (!on) return;
        var shown = ((int)MathF.Round(sc.ZeroM), _overlay.DotMils);
        if (shown == _zeroShown) return;
        _zeroShown = shown;
        HudUi.Set(_zero, Strings.F("hud.scope.zero", shown.Item1, shown.Item2));
    }
}

/// <summary>
/// Black outside a centred circle with a thin cross, drawn over the zoomed view. A scope with bullet drop has mil-dots on the cross: a dot every
/// <see cref="DotMils"/> mils (1 mil = 1 m at 1000 m), spaced from the FOV so they are true angles on the world: a target of a known height that
/// spans n mils is height x 1000 / n metres away, and a dot under the centre is the hold-over for a target beyond the zero.
/// </summary>
internal sealed partial class ScopeOverlay : Control
{
    /// <summary>The dot spacings tried, mils: the first that puts the dots at least <see cref="MinDotPx"/> apart.</summary>
    static readonly int[] Steps = { 1, 2, 5, 10, 20 };
    const float MinDotPx = 10f, DotRadius = 2f;
    const int MaxDots = 10;
    float _fov;

    /// <summary>Mils between two dots at the current FOV and size (0: no dots).</summary>
    public int DotMils { get; private set; }

    /// <summary>Screen pixels one mil spans at a vertical FOV, for a view this high.</summary>
    float PxPerMil(float fovDeg) => Size.Y / 2f / Mathf.Tan(Mathf.DegToRad(fovDeg) / 2f) / 1000f;

    public void SetMils(float fovDeg)
    {
        if (Mathf.IsEqualApprox(fovDeg, _fov)) return;
        _fov = fovDeg;
        Respace();
        QueueRedraw();
    }

    void Respace()
    {
        DotMils = 0;
        if (_fov <= 0 || Size.Y <= 0) return;
        var px = PxPerMil(_fov);
        DotMils = Steps[^1];
        foreach (var step in Steps) if (step * px >= MinDotPx) { DotMils = step; break; }
    }

    public override void _Draw()
    {
        var s = Size; var c = s / 2; var r = Mathf.Min(s.X, s.Y) * 0.46f;
        var outer = c.Length();
        var w = outer - r + 4;
        DrawArc(c, r + w / 2, 0, Mathf.Tau, 128, Colors.Black, w);
        DrawArc(c, r, 0, Mathf.Tau, 128, new Color(0, 0, 0, 0.9f), 3);
        var line = new Color(0, 0, 0, 0.85f);
        DrawLine(new Vector2(c.X - r, c.Y), new Vector2(c.X + r, c.Y), line, 1.5f);
        DrawLine(new Vector2(c.X, c.Y - r), new Vector2(c.X, c.Y + r), line, 1.5f);
        if (DotMils > 0)
        {
            var gap = DotMils * PxPerMil(_fov);
            for (var i = 1; i <= MaxDots && i * gap < r * 0.9f; i++)
            {
                DrawCircle(new Vector2(c.X, c.Y + i * gap), DotRadius, Colors.Black);
                DrawCircle(new Vector2(c.X, c.Y - i * gap), DotRadius, Colors.Black);
                DrawCircle(new Vector2(c.X + i * gap, c.Y), DotRadius, Colors.Black);
                DrawCircle(new Vector2(c.X - i * gap, c.Y), DotRadius, Colors.Black);
            }
        }
        DrawCircle(c, 2f, new Color(0.9f, 0.1f, 0.1f));
    }

    public override void _Notification(int what) { if (what == NotificationResized) { Respace(); QueueRedraw(); } }
}
