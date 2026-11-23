using Godot;
using Drycastle.Shooter.HeadsUp;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Scud;

namespace Drycastle.Shooter.Scud;

/// <summary>
/// What the Scud screen draws this frame, filled by the client (<c>NetClient.Scud.cs</c>) and read by <see cref="ScudOverlay"/>: plain values, reused every frame, so the screen allocates nothing
/// while it is up. Strings are rebuilt by the client only when what they say changes.
/// </summary>
public sealed class ScudScreenState
{
    /// <summary>The operator's screen (pan, zoom, lock) rather than the read-only feed.</summary>
    public bool Operating;
    public ScudLauncherState Launcher;
    /// <summary>The open request is out and the server has not answered yet.</summary>
    public bool Pending;
    /// <summary>Whole seconds left of the erect or the ignition (ceiling), -1 when the state has no countdown.</summary>
    public int Countdown = -1;
    public Camera3D? Camera;
    /// <summary>The truck on the ground (the min-range ring is round it), the locked point if one is placed, and the minimum range, metres.</summary>
    public Vector3 Truck;
    public Vector3? Lock;
    public float MinRangeM;
    public float Zoom = 1f;
    /// <summary>The reticle's range from the truck, metres, and what it reads (grid reference and whether it is inside the minimum range).</summary>
    public int RangeM;
    public bool TooClose;
    public string Grid = "", LockGrid = "";
    public int LockRangeM;
    public readonly List<CrewRow> Crew = new();
    public string Status = "", Hint = "", Role = "";

    /// <summary>One line of the crew list, its text built by the client when the crew or the confirmations change.</summary>
    public readonly record struct CrewRow(string Text, bool Confirmed);
}

/// <summary>
/// The Scud truck's screen (GLOSSARY "Launcher", "Lock point"): the operator's bird's-eye infrared view and the crew's read-only feed of it. The picture is the world seen from straight above
/// through the AC-130's black-and-white thermal grade (<see cref="GunshipOverlay.ShaderPath"/>, white hot); over it: the reticle with its grid reference and range, the locked point, the truck and its
/// minimum-range ring, who has confirmed, the countdown, and the keys. Tokens and sizes come from <see cref="ShooterTheme"/>, scaled by the screen height against 1080 p; every state has a word,
/// never colour alone. Hidden except while the local player has a Scud screen up. Under the HUD (layer 10), like the gunship's.
/// </summary>
public partial class ScudOverlay : CanvasLayer
{
    readonly ColorRect _thermal = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    ShaderMaterial _grade = null!;
    readonly ScudPanel _panel = new();

    /// <summary>What the panel draws; the client fills it every frame while the screen is up.</summary>
    public ScudScreenState State { get; } = new();

    public override void _Ready()
    {
        Layer = 5;
        Visible = false;
        _thermal.Material = _grade = new ShaderMaterial { Shader = GD.Load<Shader>(GunshipOverlay.ShaderPath) };
        _grade.SetShaderParameter("mono", 1f);
        _thermal.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_thermal);
        _panel.State = State;
        _panel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _panel.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(_panel);
    }

    /// <summary>Shows or hides the screen. Idempotent; cheap to call every frame.</summary>
    public void SetOn(bool on)
    {
        if (Visible == on) return;
        Visible = on;
        _panel.SetProcess(on);
    }

    /// <summary>Redraws with the state as it is now (call once a frame while up).</summary>
    public void Refresh() => _panel.QueueRedraw();

    sealed partial class ScudPanel : Control
    {
        public ScudScreenState State = null!;

        public override void _Process(double delta) => QueueRedraw();

        static readonly Vector2[] Dirs = { Vector2.Right, Vector2.Left, Vector2.Down, Vector2.Up };
        readonly Vector2[] _tri = new Vector2[3], _triIn = new Vector2[3], _dia = new Vector2[5];

        // the strings that say numbers, built when the number changes (the screen allocates nothing per frame otherwise)
        string _ret = "", _retGrid = "", _lock = "", _lockGrid = "", _zoom = "", _count = "", _scale = "";
        int _retRange = -1, _lockRange = -1, _countN = -2, _scaleN = -1;
        bool _retClose;
        float _zoomN = -1;
        int _retMin = -1;

        string Reticle(ScudScreenState st)
        {
            var min = (int)MathF.Round(st.MinRangeM);
            if (st.RangeM != _retRange || st.Grid != _retGrid || st.TooClose != _retClose || min != _retMin)
            {
                _retRange = st.RangeM; _retGrid = st.Grid; _retClose = st.TooClose; _retMin = min;
                _ret = st.TooClose ? Strings.F("scud.reticle.close", min) : Strings.F("scud.reticle", st.Grid, st.RangeM);
            }
            return _ret;
        }

        string LockText(ScudScreenState st)
        {
            if (st.LockRangeM != _lockRange || st.LockGrid != _lockGrid) { _lockRange = st.LockRangeM; _lockGrid = st.LockGrid; _lock = Strings.F("scud.lock", st.LockGrid, st.LockRangeM); }
            return _lock;
        }

        string Zoom(float zoom)
        {
            if (MathF.Abs(zoom - _zoomN) > 0.001f) { _zoomN = zoom; _zoom = Strings.F("scud.zoom", zoom.ToString("0.#")); }
            return _zoom;
        }

        string Count(int n)
        {
            if (n != _countN) { _countN = n; _count = n.ToString(); }
            return _count;
        }

        string ScaleText(int metres)
        {
            if (metres != _scaleN) { _scaleN = metres; _scale = Strings.F("scud.scale", metres); }
            return _scale;
        }

        static Color Ink => ShooterTheme.Text;
        static string North => _north ??= Strings.T("scud.north");
        static string? _north;

        public override void _Draw()
        {
            var st = State;
            var font = ThemeDB.FallbackFont;
            var size = Size;
            var s = ShooterTheme.ScreenScale(size);
            var small = (int)Math.Round(ShooterTheme.FontSmall * s);
            var body = (int)Math.Round(ShooterTheme.FontBody * s);
            var big = (int)Math.Round(ShooterTheme.FontBig * s);
            var m = ShooterTheme.Margin * s;
            var gap = ShooterTheme.Space * s;
            var centre = size / 2f;

            if (st.Camera != null) DrawWorldMarks(st, font, small, s, size);

            // the reticle: a ring and four ticks with a gap at the middle (the aim point is the screen's centre)
            DrawArc(centre, 18f * s, 0, Mathf.Tau, 48, Ink, ShooterTheme.BorderStrong * s);
            var tick = 6f * s; var len = 14f * s; var wd = ShooterTheme.Border * 2 * s;
            foreach (var d in Dirs)
                DrawLine(centre + d * (18f * s + tick), centre + d * (18f * s + tick + len), Ink, wd);
            if (st.Operating && !st.Pending)
            {
                Plate(font, Reticle(st), new Vector2(centre.X, centre.Y + 60f * s), body, st.TooClose ? ShooterTheme.Danger : Ink, s);
            }

            // top left: the screen, who is looking and what the launcher is doing
            var y = m;
            var left = m;
            y = TextPlate(font, Strings.T("scud.title"), new Vector2(left, y), body, Ink, s, gap);
            y = TextPlate(font, st.Role, new Vector2(left, y), small, ShooterTheme.Dim, s, gap);
            y = TextPlate(font, st.Pending ? Strings.T("scud.status.pending") : st.Status, new Vector2(left, y), body, st.Launcher is ScudLauncherState.Erecting or ScudLauncherState.Launching ? ShooterTheme.Warn : Ink, s, gap);

            // under it: the crew and who has confirmed (words, so not colour alone)
            if (st.Crew.Count > 0 && st.Launcher is ScudLauncherState.Proposed or ScudLauncherState.Erecting or ScudLauncherState.Launching)
            {
                y += gap;
                y = TextPlate(font, Strings.T("scud.crew.title"), new Vector2(left, y), small, ShooterTheme.Dim, s, gap);
                foreach (var row in st.Crew)
                {
                    y = TextPlate(font, row.Text, new Vector2(left, y), small, row.Confirmed ? ShooterTheme.Good : ShooterTheme.Warn, s, gap);
                }
            }

            // top right: the countdown, and the zoom
            var right = size.X - m;
            var ry = m;
            if (st.Countdown >= 0) ry = RightPlate(font, Count(st.Countdown), new Vector2(right, ry), big, ShooterTheme.Warn, s, gap);
            if (st.Operating) RightPlate(font, Zoom(st.Zoom), new Vector2(right, ry), small, ShooterTheme.Dim, s, gap);

            // bottom: the keys, and a scale bar
            if (st.Hint.Length > 0) TextPlate(font, st.Hint, new Vector2(m, size.Y - m - small - gap * 2f), small, ShooterTheme.Dim, s, gap);
            DrawScaleBar(st, font, small, s, size);

            // north is up
            Plate(font, North, new Vector2(centre.X, m + body), body, Ink, s);
        }

        /// <summary>The truck, the ring that is the minimum range, and the locked point, projected through the camera that looks straight down.</summary>
        void DrawWorldMarks(ScudScreenState st, Font font, int small, float s, Vector2 size)
        {
            var cam = st.Camera!;
            var truck = Project(cam, st.Truck);
            var pxPerM = PxPerMetre(cam, st.Truck);
            if (pxPerM > 0)
            {
                // the minimum range: a dashed ring round the truck (the crew cannot target inside it)
                var r = st.MinRangeM * pxPerM;
                if (r > 6f && r < size.Length() * 3f)
                    for (var i = 0; i < 72; i += 2)
                    {
                        var a0 = i * Mathf.Tau / 72f; var a1 = (i + 1) * Mathf.Tau / 72f;
                        DrawArc(truck, r, a0, a1, 3, ShooterTheme.Danger, ShooterTheme.Border * 2 * s);
                    }
            }
            // the truck: a triangle and its word
            var t = 9f * s;
            _tri[0] = truck + new Vector2(0, -t); _tri[1] = truck + new Vector2(t, t); _tri[2] = truck + new Vector2(-t, t);
            _triIn[0] = truck + new Vector2(0, -t * 0.7f); _triIn[1] = truck + new Vector2(t * 0.7f, t * 0.8f); _triIn[2] = truck + new Vector2(-t * 0.7f, t * 0.8f);
            DrawColoredPolygon(_tri, ShooterTheme.Outline);
            DrawColoredPolygon(_triIn, Ink);
            Plate(font, Strings.T("scud.truck"), truck + new Vector2(0, t + 18f * s), small, Ink, s);
            if (st.Lock is { } lk)
            {
                var p = Project(cam, lk);
                var d = 14f * s;
                _dia[0] = p + new Vector2(0, -d); _dia[1] = p + new Vector2(d, 0); _dia[2] = p + new Vector2(0, d); _dia[3] = p + new Vector2(-d, 0); _dia[4] = _dia[0];
                DrawPolyline(_dia, ShooterTheme.Outline, 6f * s, true);
                DrawPolyline(_dia, ShooterTheme.Danger, 3f * s, true);
                DrawLine(p + new Vector2(-d * 0.5f, 0), p + new Vector2(d * 0.5f, 0), ShooterTheme.Danger, 2f * s);
                DrawLine(p + new Vector2(0, -d * 0.5f), p + new Vector2(0, d * 0.5f), ShooterTheme.Danger, 2f * s);
                Plate(font, LockText(st), p + new Vector2(0, d + 24f * s), small, ShooterTheme.Danger, s);
            }
        }

        void DrawScaleBar(ScudScreenState st, Font font, int small, float s, Vector2 size)
        {
            if (st.Camera == null) return;
            var pxPerM = PxPerMetre(st.Camera, st.Truck);
            if (pxPerM <= 0) return;
            // the longest round length that fits in a fifth of the screen
            var metres = Steps[0];
            foreach (var step in Steps) if (step * pxPerM <= size.X * 0.2f) metres = step;
            var w = metres * pxPerM;
            var x0 = size.X / 2f - w / 2f; var y0 = size.Y - ShooterTheme.Margin * s - 8f * s;
            DrawLine(new Vector2(x0, y0), new Vector2(x0 + w, y0), ShooterTheme.Outline, 6f * s);
            DrawLine(new Vector2(x0, y0), new Vector2(x0 + w, y0), Ink, 3f * s);
            DrawLine(new Vector2(x0, y0 - 6f * s), new Vector2(x0, y0 + 6f * s), Ink, 3f * s);
            DrawLine(new Vector2(x0 + w, y0 - 6f * s), new Vector2(x0 + w, y0 + 6f * s), Ink, 3f * s);
            Plate(font, ScaleText(metres), new Vector2(size.X / 2f, y0 - 14f * s), small, Ink, s);
        }

        static readonly int[] Steps = { 25, 50, 100, 200, 500, 1000, 2000 };

        static Vector2 Project(Camera3D cam, Vector3 world) => cam.IsPositionBehind(world) ? new Vector2(-10000, -10000) : cam.UnprojectPosition(world);

        /// <summary>Screen pixels in one metre on the ground near <paramref name="at"/> (the camera looks straight down, so it is the same all over the picture).</summary>
        static float PxPerMetre(Camera3D cam, Vector3 at)
        {
            var a = Project(cam, at); var b = Project(cam, at + new Vector3(100f, 0, 0));
            return Mathf.Abs(b.X - a.X) / 100f;
        }

        /// <summary>A line of text on a plate (the Surface token) so it reads on any ground; centred on <paramref name="centreTop"/>.</summary>
        void Plate(Font font, string text, Vector2 centreTop, int fontSize, Color colour, float s)
        {
            var w = font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X;
            var pad = ShooterTheme.Space * s;
            var rect = new Rect2(centreTop.X - w / 2f - pad, centreTop.Y - fontSize - pad * 0.5f, w + pad * 2f, fontSize + pad * 1.5f);
            DrawRect(rect, ShooterTheme.Surface);
            DrawString(font, new Vector2(rect.Position.X + pad, centreTop.Y), text, HorizontalAlignment.Left, -1, fontSize, colour);
        }

        /// <summary>A plate at the left edge <paramref name="topLeft"/> (its baseline row is below); returns the y of the next row.</summary>
        float TextPlate(Font font, string text, Vector2 topLeft, int fontSize, Color colour, float s, float gap)
        {
            if (text.Length == 0) return topLeft.Y;
            var w = font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X;
            var h = fontSize + gap;
            DrawRect(new Rect2(topLeft.X, topLeft.Y, w + gap * 2f, h), ShooterTheme.Surface);
            DrawString(font, new Vector2(topLeft.X + gap, topLeft.Y + fontSize), text, HorizontalAlignment.Left, -1, fontSize, colour);
            return topLeft.Y + h + gap * 0.5f;
        }

        float RightPlate(Font font, string text, Vector2 topRight, int fontSize, Color colour, float s, float gap)
        {
            var w = font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X;
            var h = fontSize + gap;
            DrawRect(new Rect2(topRight.X - w - gap * 2f, topRight.Y, w + gap * 2f, h), ShooterTheme.Surface);
            DrawString(font, new Vector2(topRight.X - w - gap, topRight.Y + fontSize), text, HorizontalAlignment.Left, -1, fontSize, colour);
            return topRight.Y + h + gap * 0.5f;
        }
    }
}

/// <summary>
/// The warning for everyone near a Scud's target (GLOSSARY "Scud missile"): a banner at the top of the screen over the HUD, counting down to the impact and saying where and how far, shown for the last
/// seconds of the flight to players within the warning radius. Words and the countdown, not colour alone; no motion. The siren is the client's (<c>scud.siren</c>).
/// </summary>
public partial class ScudWarning : CanvasLayer
{
    readonly Banner _banner = new();

    public override void _Ready()
    {
        Layer = 12;
        Visible = false;
        _banner.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _banner.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(_banner);
    }

    /// <summary>Shows the banner (seconds left, the grid reference of the target, metres from the player) or hides it (seconds below zero). Cheap to call every frame: it redraws only on a change.</summary>
    public void Set(int seconds, string grid, int metres)
    {
        var on = seconds >= 0;
        if (Visible != on) Visible = on;
        if (on) _banner.Set(seconds, grid, metres);
    }

    sealed partial class Banner : Control
    {
        int _seconds = -1, _metres;
        string _grid = "", _title = "", _where = "";

        public void Set(int seconds, string grid, int metres)
        {
            if (seconds == _seconds && grid == _grid && metres == _metres) return;
            _seconds = seconds; _grid = grid; _metres = metres;
            _title = Strings.F("scud.incoming", seconds);
            _where = Strings.F("scud.incoming.where", grid, metres);
            QueueRedraw();
        }

        public override void _Draw()
        {
            var font = ThemeDB.FallbackFont;
            var s = ShooterTheme.ScreenScale(Size);
            var big = (int)Math.Round(ShooterTheme.FontBig * s);
            var body = (int)Math.Round(ShooterTheme.FontBody * s);
            var pad = ShooterTheme.Margin * s * 0.5f;
            var w = Mathf.Max(font.GetStringSize(_title, HorizontalAlignment.Left, -1, big).X, font.GetStringSize(_where, HorizontalAlignment.Left, -1, body).X) + pad * 2f;
            var h = big + body + pad * 2.5f;
            var rect = new Rect2((Size.X - w) / 2f, ShooterTheme.Margin * s * 2f, w, h);
            DrawRect(rect, ShooterTheme.Surface);
            DrawRect(rect, ShooterTheme.Danger, false, ShooterTheme.BorderStrong * s);
            DrawString(font, new Vector2(rect.Position.X + pad, rect.Position.Y + pad + big), _title, HorizontalAlignment.Left, -1, big, ShooterTheme.Danger);
            DrawString(font, new Vector2(rect.Position.X + pad, rect.Position.Y + pad * 1.5f + big + body), _where, HorizontalAlignment.Left, -1, body, ShooterTheme.Text);
        }
    }
}
