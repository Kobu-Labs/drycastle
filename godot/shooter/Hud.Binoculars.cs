using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The view through the binoculars (GLOSSARY "Binoculars"): black outside two overlapping circles, a small cross in the middle, the distance to
/// what is under it in large figures below ("320 m", or "---" with nothing in range) and the key that lowers them. The crosshair is hidden
/// meanwhile. The words change only when the number or the key does; the mask is drawn once per size.
/// </summary>
public partial class Hud
{
    /// <summary>The binoculars: whether they are up, and the distance read, metres (null: nothing in range). Read each frame; the owner sets it.</summary>
    public Func<(bool Up, int? RangeM, bool CanMark)>? Binoculars { get; set; }

    BinocularOverlay _bino = null!;
    Label _binoRange = null!, _binoHint = null!;
    int _binoShown = int.MinValue;
    string? _binoKey;

    void BuildBinoculars()
    {
        _bino = new BinocularOverlay { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _bino.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_bino);
        _binoRange = Place(L("", "HudBig", HorizontalAlignment.Center), 0.5f, 0.5f, -160, 6 * ShooterTheme.Space, 320, 40);
        _binoHint = Place(L("", "HudSmall", HorizontalAlignment.Center), 0.5f, 0.5f, -160, 12 * ShooterTheme.Space, 320, 24);
        _binoRange.Visible = _binoHint.Visible = false;
        _root.AddChild(_binoRange); _root.AddChild(_binoHint);
    }

    /// <summary>True while the binoculars are up (the crosshair and the scope stay hidden then).</summary>
    bool UpdateBinoculars()
    {
        var b = Binoculars?.Invoke() ?? (false, null, false);
        if (_bino.Visible != b.Up)
        {
            _bino.Visible = _binoRange.Visible = _binoHint.Visible = b.Up;
            ApplyCrosshairVisibility();
        }
        if (!b.Up) return false;
        var range = b.RangeM ?? -1;
        if (range != _binoShown)
        {
            _binoShown = range;
            Set(_binoRange, range >= 0 ? Strings.F("hud.binoculars.range", range) : Strings.T("hud.binoculars.none"));
        }
        var key = InputBindings.Name("binoculars") + (b.CanMark ? "|" + InputBindings.Name("binoculars_mark") : "");
        if (key != _binoKey)
        {
            _binoKey = key;
            Set(_binoHint, b.CanMark ? Strings.F("hud.binoculars.hint.mark", InputBindings.Name("binoculars"), InputBindings.Name("binoculars_mark")) : Strings.F("hud.binoculars.hint", InputBindings.Name("binoculars")));
        }
        return true;
    }

    /// <summary>Black outside two overlapping circles (the two barrels seen as one picture), with a thin cross at the centre.</summary>
    sealed partial class BinocularOverlay : Control
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
}
