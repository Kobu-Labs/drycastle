using Godot;
using Drycastle.ShooterCore;
using Vector2 = Godot.Vector2;

namespace Drycastle.Shooter;

/// <summary>
/// The tactical map as the radar unit's screen (GLOSSARY "Radar unit"; ADR 0037): while <see cref="RadarDisplay.Seated"/> the sheet (north up, the labelled grid) is the operator's picture of the whole map, with a dark
/// wash over it and, on top, the sweep (a bright arm from the unit with a falling light behind it, drawn from the arm carried on between messages), the enemies where the sweep last found them fading over one revolution,
/// and the teammates live. Every blip says its grid reference beside it, and the panel gives the unit's, the keyboard cursor's (arrows, as the map's own) and the count, so the operator can read coordinates out to
/// the Scud crew by voice. Nothing is shared by the game. Shapes carry the kinds as well as colours: an enemy on foot is a diamond, an enemy vehicle a crossed square, a friendly a disc (a vehicle's inside a square).
/// </summary>
public sealed partial class TacticalMap
{
    Label _radarHead = null!, _radarStatus = null!, _radarContacts = null!, _radarCursorCaption = null!, _radarCursor = null!, _radarLegend = null!, _radarHint = null!;
    Control[] _radarRows = Array.Empty<Control>();
    bool _radarMode;
    int _shownContacts = -1;
    bool _shownLive;
    string _shownCursor = "";
    string? _shownRadarCursorCell;

    /// <summary>The panel's radar rows (hidden unless the map is the radar screen): the unit's heading word, whether the sweep is coming in, the count, the cursor's reference, the legend and the keys.</summary>
    void BuildRadarRows(VBoxContainer r)
    {
        var gap = Gap();
        _radarHead = Text(Strings.T("radar.title"), "HudSmall");
        _radarStatus = Text(Strings.T("radar.status.none"), "HudBig");
        _radarContacts = Text("", "HudSmall");
        _radarCursorCaption = Text(Strings.T("radar.cursor"), "HudSmall");
        _radarCursor = Text(Strings.T("map.none"), "HudBig");
        _radarLegend = Wrapped(Strings.T("radar.legend"));
        _radarHint = Wrapped(Strings.T("radar.hint"));
        _radarRows = new Control[] { gap, _radarHead, _radarStatus, _radarContacts, _radarCursorCaption, _radarCursor, _radarLegend, _radarHint };
        foreach (var row in _radarRows) { row.Visible = false; r.AddChild(row); }
    }

    static Label Wrapped(string text)
    {
        var l = Text(text, "HudSmall");
        l.ClipText = false;
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        return l;
    }

    /// <summary>
    /// Per frame while open: switches the sheet into and out of the radar screen when the operator sits down or gets out, and redraws the radar layer and the radar readouts (each label is written only when its value
    /// changes). Costs one flag check when nobody is at a radar.
    /// </summary>
    void UpdateRadar()
    {
        var on = _m.Radar.Seated;
        if (on != _radarMode) SetRadarMode(on);
        if (!on) return;
        _radar.QueueRedraw();
        var r = _m.Radar;
        var now = Time.GetTicksMsec() / 1000.0;
        var live = r.Live(now);
        if (live != _shownLive) { _shownLive = live; Set(_radarStatus, Strings.T(live ? "radar.status.live" : "radar.status.none")); }
        if (r.Contacts.Count != _shownContacts) { _shownContacts = r.Contacts.Count; Set(_radarContacts, Strings.F("radar.contacts", _shownContacts)); }
        var cell = CursorReference();
        if (cell != _shownRadarCursorCell) { _shownRadarCursorCell = cell; Set(_radarCursor, cell ?? Strings.T("map.none")); }
    }

    void SetRadarMode(bool on)
    {
        _radarMode = on;
        _shownContacts = -1; _shownLive = !on; _shownRadarCursorCell = "\0";
        foreach (var row in _radarRows) row.Visible = on;
        _roseRow.Visible = !on; // the sheet is north up and the arm's bearing is on it: the rose and the zoom text give way to the radar's rows
        _zoomHint.Visible = !on;
        Set(_gridCaption, Strings.T(on ? "radar.unit" : "map.grid"));
        if (!on) _radar.QueueRedraw();
    }

    /// <summary>The grid reference under the keyboard cursor, or under the mouse when it is free; null when neither is on the sheet.</summary>
    string? CursorReference()
    {
        if (_base is not { } b || _side <= 0) return null;
        Vector2? ground = null;
        if (_cursorShown)
        {
            var f = V.ToSheet((_cursor - new Vector2(Gutter, Gutter)) / _side, _tl, _zoom);
            ground = new Vector2(b.Origin.X + f.X * b.SideMetres, b.Origin.Y + f.Y * b.SideMetres);
        }
        else if (Input.MouseMode == Input.MouseModeEnum.Visible) ground = GroundAtMouse();
        return ground is { } g ? TacticalMapMath.ReferenceOf(new System.Numerics.Vector2(g.X, g.Y), b.Origin.X, b.SouthZ, b.SideMetres) : null;
    }

    /// <summary>The radar's picture over the sheet, in the sheet square's own pixels: clipped to it, so the arm may be as long as it likes.</summary>
    sealed partial class RadarLayer : Control
    {
        const int Wedges = 36;
        readonly TacticalMap _o;
        readonly Vector2[] _tri = new Vector2[3];
        readonly Color[] _triColours = new Color[3];

        public RadarLayer(TacticalMap owner) => _o = owner;

        /// <summary>A ground point in this layer's pixels, kept on the sheet (inside its edge) so a far blip shows on the edge of a zoomed window.</summary>
        Vector2 At(float x, float z, MapBase b)
        {
            var p = (new Vector2(x, z) - _o.WinOrigin(b)) * _o._k;
            return new Vector2(Math.Clamp(p.X, 10f, Size.X - 10f), Math.Clamp(p.Y, 10f, Size.Y - 10f));
        }

        public override void _Draw()
        {
            var radar = _o._m.Radar;
            if (!radar.Seated || _o._base is not { } b || _o._side <= 0) return;
            DrawRect(new Rect2(Vector2.Zero, Size), ShooterTheme.RadarWash);
            var now = Time.GetTicksMsec() / 1000.0;
            var unit = (new Vector2(radar.UnitX, radar.UnitZ) - _o.WinOrigin(b)) * _o._k; // not clamped: the arm turns about the true place, off the window too
            if (radar.Live(now)) DrawArm(unit, radar.Arm(now));
            var font = GetThemeDefaultFont();
            const int fs = ShooterTheme.FontSmall;
            foreach (var c in radar.Contacts)
            {
                var a = radar.Alpha(c, now);
                if (a < 0.03f) continue;
                var p = At(c.X, c.Z, b);
                MapGlyphs.RadarContact(this, p, c.Kind, a);
                Tag(font, fs, c.Label, p, a);
            }
            foreach (var f in radar.Friends)
            {
                var p = At(f.X, f.Z, b);
                MapGlyphs.RadarFriend(this, p, f.Kind);
                Tag(font, fs, f.Label, p, 1f);
            }
            MapGlyphs.RadarUnit(this, new Vector2(Math.Clamp(unit.X, 10f, Size.X - 10f), Math.Clamp(unit.Y, 10f, Size.Y - 10f)));
        }

        /// <summary>The grid reference beside a blip, outlined so it reads on any ground; as faint as its blip, but never fainter than half (it must stay readable while the blip lasts).</summary>
        void Tag(Font font, int fs, string text, Vector2 p, float alpha)
        {
            if (text.Length == 0) return;
            var at = p + new Vector2(13f, -11f);
            var shell = ShooterTheme.Outline; shell.A *= Math.Max(0.5f, alpha);
            var col = ShooterTheme.Text; col.A = Math.Max(0.5f, alpha);
            DrawStringOutline(font, at, text, HorizontalAlignment.Left, -1, fs, 6, shell);
            DrawString(font, at, text, HorizontalAlignment.Left, -1, fs, col);
        }

        /// <summary>The arm, bright with a dark casing, and behind it the falling light: thin wedges from the unit whose opacity follows <see cref="RadarScreenMath.TrailAlpha"/>.</summary>
        void DrawArm(Vector2 unit, float arm)
        {
            var reach = RadarScreenMath.Reach(unit.X, unit.Y, 0f, 0f, Size.X, Size.Y) + 4f;
            var glow = ShooterTheme.Accent;
            for (var i = 0; i < Wedges; i++)
            {
                var b0 = RadarScreenMath.TrailRadians * i / Wedges;
                var b1 = RadarScreenMath.TrailRadians * (i + 1) / Wedges;
                var d0 = RadarScreenMath.Direction(arm - b0);
                var d1 = RadarScreenMath.Direction(arm - b1);
                _tri[0] = unit;
                _tri[1] = unit + new Vector2(d0.X, d0.Y) * reach;
                _tri[2] = unit + new Vector2(d1.X, d1.Y) * reach;
                var a0 = RadarScreenMath.TrailAlpha(b0) * ShooterTheme.RadarTrailAlpha;
                var a1 = RadarScreenMath.TrailAlpha(b1) * ShooterTheme.RadarTrailAlpha;
                _triColours[0] = new Color(glow, (a0 + a1) * 0.5f);
                _triColours[1] = new Color(glow, a0);
                _triColours[2] = new Color(glow, a1);
                DrawPolygon(_tri, _triColours);
            }
            var tip = unit + new Vector2(RadarScreenMath.Direction(arm).X, RadarScreenMath.Direction(arm).Y) * reach;
            DrawLine(unit, tip, ShooterTheme.Outline, 6f, true);
            DrawLine(unit, tip, glow, 3f, true);
        }
    }
}
