using Godot;
using Drycastle.Shooter.Menu;

namespace Drycastle.Shooter.HeadsUp.Vision;

/// <summary>
/// The view through the binoculars (GLOSSARY "Binoculars"): black outside two overlapping circles, a small cross in the middle, the distance to
/// what is under it in large figures below ("320 m", or "---" with nothing in range) and the key that lowers them. The crosshair is hidden
/// meanwhile. The words change only when the number or the key does; the mask is drawn once per size.
/// </summary>
internal sealed class BinocularsView
{
    readonly BinocularOverlay _overlay;
    readonly Label _range, _hint;
    readonly Action _upChanged;
    int _rangeShown = int.MinValue;
    string? _key;

    /// <param name="upChanged">Called when the binoculars go up or down (the crosshair follows them).</param>
    public BinocularsView(Control root, Action upChanged)
    {
        _upChanged = upChanged;
        _overlay = new BinocularOverlay { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(_overlay);
        _range = HudUi.Place(HudUi.L("", "HudBig", HorizontalAlignment.Center), 0.5f, 0.5f, -160, 6 * ShooterTheme.Space, 320, 40);
        _hint = HudUi.Place(HudUi.L("", "HudSmall", HorizontalAlignment.Center), 0.5f, 0.5f, -160, 12 * ShooterTheme.Space, 320, 24);
        _range.Visible = _hint.Visible = false;
        root.AddChild(_range); root.AddChild(_hint);
    }

    public bool Up => _overlay.Visible;

    /// <summary>Whether they are up, and the distance read, metres (null: nothing in range). True while they are up (the crosshair and the scope stay hidden then).</summary>
    public bool Update((bool Up, int? RangeM, bool CanMark) b)
    {
        if (_overlay.Visible != b.Up)
        {
            _overlay.Visible = _range.Visible = _hint.Visible = b.Up;
            _upChanged();
        }
        if (!b.Up) return false;
        var range = b.RangeM ?? -1;
        if (range != _rangeShown)
        {
            _rangeShown = range;
            HudUi.Set(_range, range >= 0 ? Strings.F("hud.binoculars.range", range) : Strings.T("hud.binoculars.none"));
        }
        var key = InputBindings.Name("binoculars") + (b.CanMark ? "|" + InputBindings.Name("binoculars_mark") : "");
        if (key != _key)
        {
            _key = key;
            HudUi.Set(_hint, b.CanMark ? Strings.F("hud.binoculars.hint.mark", InputBindings.Name("binoculars"), InputBindings.Name("binoculars_mark")) : Strings.F("hud.binoculars.hint", InputBindings.Name("binoculars")));
        }
        return true;
    }
}

/// <summary>Black outside two overlapping circles (the two barrels seen as one picture), with a thin cross at the centre.</summary>
internal sealed partial class BinocularOverlay : Control
{
    const float Strip = 2f;

    public override void _Draw()
    {
        var s = Size; var c = s / 2;
        // two circles of radius r, their centres apart by r: as wide as fits the screen with a margin, and no higher than it
        var r = Mathf.Min(s.Y * 0.46f, s.X * 0.92f / 3f);
        var half = r / 2;
        // the mask in vertical strips: at each x the opening is the taller of the two circles' chords
        for (var x = 0f; x < s.X; x += Strip)
        {
            var mid = x + Strip / 2;
            var open = 0f;
            foreach (var cx in new[] { c.X - half, c.X + half })
            {
                var dx = mid - cx;
                if (Mathf.Abs(dx) < r) open = Mathf.Max(open, Mathf.Sqrt(r * r - dx * dx));
            }
            if (open <= 0) { DrawRect(new Rect2(x, 0, Strip, s.Y), Colors.Black); continue; }
            DrawRect(new Rect2(x, 0, Strip, c.Y - open), Colors.Black);
            DrawRect(new Rect2(x, c.Y + open, Strip, s.Y - c.Y - open), Colors.Black);
        }
        // the rims: each circle's arc outside the other, which also covers the strips' steps
        var inner = Mathf.Acos(half / r); // the half angle of the part of a circle that lies inside the other
        var rim = new Color(0, 0, 0, 0.9f);
        DrawArc(new Vector2(c.X + half, c.Y), r + 2, -(Mathf.Pi - inner), Mathf.Pi - inner, 128, rim, 6);
        DrawArc(new Vector2(c.X - half, c.Y), r + 2, inner, Mathf.Tau - inner, 128, rim, 6);
        var line = new Color(0, 0, 0, 0.85f);
        DrawLine(new Vector2(c.X - 24, c.Y), new Vector2(c.X - 6, c.Y), line, 1.5f);
        DrawLine(new Vector2(c.X + 6, c.Y), new Vector2(c.X + 24, c.Y), line, 1.5f);
        DrawLine(new Vector2(c.X, c.Y - 24), new Vector2(c.X, c.Y - 6), line, 1.5f);
        DrawLine(new Vector2(c.X, c.Y + 6), new Vector2(c.X, c.Y + 24), line, 1.5f);
        DrawCircle(c, 1.5f, line);
    }

    public override void _Notification(int what) { if (what == NotificationResized) QueueRedraw(); }
}
