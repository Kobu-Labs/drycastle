using Godot;
using Drycastle.ShooterCore;
using Vector2 = Godot.Vector2;
using NVec2 = System.Numerics.Vector2;

namespace Drycastle.Shooter;

/// <summary>
/// What the intel screen at the base shows (<see cref="IntelScreen"/> draws this into the texture of the wall screen). For the
/// event in front it gives the headline with its emblem, when it begins (a countdown, or "under way"), where (a grid reference
/// and a map of the surroundings), what it is, and its risks; with several events on the board a tab per event in the
/// header says which one this is. With none it says all is quiet. A test event carries a "not a real event" tag.
/// It lays out in pixels of the screen's own texture (<see cref="PxPerMetre"/>), with the shared theme's five text sizes,
/// so the type is as big on the wall as at the HUD's reference size seen from about two and a half metres. The screen is not
/// interactive and nothing on it moves (no blinking, no scrolling), so there is nothing for reduce motion to take away.
/// The view owns no clock: <see cref="Show"/> is called about once a second with the time, and rebuilds only what changed.
/// </summary>
public sealed partial class IntelScreenView : PanelContainer
{
    /// <summary>Pixels of the screen texture per metre of wall: 3 m is 960 px wide, the 1.1 m height is 352.</summary>
    public const float PxPerMetre = 320f;
    /// <summary>From this width (px) the map of the surroundings has room beside the text; narrower screens drop it and keep the grid reference.</summary>
    const int WideAt = 760;
    const int MapSide = 160, EmblemSide = 44;
    const int MaxTabChars = 12;

    readonly bool _wide;
    readonly Label _name, _whenCaption, _whenValue, _whereValue, _desc;
    readonly PanelContainer _tag;
    readonly IntelEmblem _emblem, _emptyEmblem;
    readonly IntelMapTile _map;
    readonly HFlowContainer _risks;
    readonly HBoxContainer _tabs;
    readonly Control _eventBox, _emptyBox;
    readonly Label _riskCaption;
    (int Version, int Featured) _shown = (-1, -1);
    bool? _underway;

    public IntelScreenView(int widthPx)
    {
        Strings.Register();
        _wide = widthPx >= WideAt;
        Theme = ShooterTheme.Build();
        ThemeTypeVariation = "IntelGlass";
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;

        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        AddChild(col);

        var head = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        head.AddChild(Text(Strings.T("intel.title"), "IntelHead"));
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });
        _tabs = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        head.AddChild(_tabs);
        col.AddChild(head);

        // one event: headline, when and where, then what and risks (and the map beside them)
        var ev = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        _eventBox = ev;
        col.AddChild(ev);

        var nameRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _emblem = new IntelEmblem(EmblemSide);
        nameRow.AddChild(_emblem);
        _name = Text("", "IntelBig");
        _name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _name.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _name.ClipText = true;
        _name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        nameRow.AddChild(_name);
        _tag = new PanelContainer { ThemeTypeVariation = "IntelTag", MouseFilter = MouseFilterEnum.Ignore, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        _tag.AddChild(Text(Strings.T("intel.test"), "IntelWarn"));
        nameRow.AddChild(_tag);
        ev.AddChild(nameRow);

        var when = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _whenCaption = Text("", "IntelSmall");
        _whenCaption.SizeFlagsVertical = SizeFlags.ShrinkEnd;
        when.AddChild(_whenCaption);
        _whenValue = Text("", "IntelHead");
        when.AddChild(_whenValue);
        when.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });
        var whereCaption = Text(Strings.T("intel.where"), "IntelSmall");
        whereCaption.SizeFlagsVertical = SizeFlags.ShrinkEnd;
        when.AddChild(whereCaption);
        _whereValue = Text("", "IntelHead");
        when.AddChild(_whereValue);
        ev.AddChild(when);

        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        _desc = Text("", "IntelBody");
        _desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _desc.MaxLinesVisible = 3;
        _desc.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _desc.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        text.AddChild(_desc);
        _risks = new HFlowContainer { ThemeTypeVariation = "IntelFlow", MouseFilter = MouseFilterEnum.Ignore };
        _riskCaption = Text(Strings.T("intel.risks"), "IntelSmall");
        _riskCaption.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _risks.AddChild(_riskCaption);
        text.AddChild(_risks);
        body.AddChild(text);
        _map = new IntelMapTile(MapSide) { Visible = _wide };
        body.AddChild(_map);
        ev.AddChild(body);

        // nothing announced
        var empty = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore };
        _emptyBox = empty;
        var emptyEmblem = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        _emptyEmblem = new IntelEmblem(EmblemSide * 2);
        _emptyEmblem.Set("generic", ShooterTheme.IntelDim);
        emptyEmblem.AddChild(_emptyEmblem);
        empty.AddChild(emptyEmblem);
        var emptyTitle = Text(Strings.T("intel.empty.title"), "IntelBig");
        emptyTitle.HorizontalAlignment = HorizontalAlignment.Center;
        empty.AddChild(emptyTitle);
        var emptyBody = Text(Strings.T("intel.empty.body"), "IntelBody");
        emptyBody.HorizontalAlignment = HorizontalAlignment.Center;
        emptyBody.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        emptyBody.CustomMinimumSize = new Vector2(Math.Min(widthPx - 96, 640), 0);
        emptyBody.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        empty.AddChild(emptyBody);
        col.AddChild(empty);
    }

    /// <summary>
    /// Shows event <paramref name="featured"/> of the board. <paramref name="version"/> is the board's change counter: the text is
    /// rebuilt when it or the featured event moves, and the countdown is rewritten each call (only when it reads differently).
    /// <paramref name="now"/> is the clock the events' start times are on. <paramref name="map"/> (null: none loaded) turns a place into a grid reference and a map.
    /// </summary>
    public void Show(IReadOnlyList<IntelEvent> events, int featured, double now, MapBase? map, int version)
    {
        var n = events.Count;
        _emptyBox.Visible = n == 0;
        _eventBox.Visible = n > 0;
        if (n == 0)
        {
            _tabs.Visible = false;
            _shown = (-1, -1);
            return;
        }
        featured = Math.Clamp(featured, 0, n - 1);
        var e = events[featured];
        if (_shown != (version, featured))
        {
            _shown = (version, featured);
            Rebuild(events, featured, e, map);
        }
        var upcoming = IntelTime.Status(e, now) == IntelStatus.Upcoming;
        if (_underway != !upcoming)
        {
            _underway = !upcoming;
            _whenValue.ThemeTypeVariation = upcoming ? "IntelHead" : "IntelWarn"; // under way reads in the warning tone, and in words
        }
        if (upcoming)
        {
            Set(_whenCaption, Strings.T("intel.when.upcoming"));
            Set(_whenValue, IntelTime.Countdown(IntelTime.SecondsLeft(e, now)));
        }
        else
        {
            Set(_whenCaption, Strings.T("intel.when.underway"));
            Set(_whenValue, Strings.T("intel.status.underway"));
        }
    }

    void Rebuild(IReadOnlyList<IntelEvent> events, int featured, IntelEvent e, MapBase? map)
    {
        var n = events.Count;
        Set(_name, e.Name);
        _emblem.Set(e.Kind, ShooterTheme.Warn);
        _tag.Visible = e.Test;
        Set(_desc, e.Description);
        Set(_whereValue, e.Where is null ? Strings.T("intel.where.none")
            : map is null ? Strings.T("intel.where.nomap")
            : Strings.F("intel.where.grid", TacticalMapMath.ReferenceOf(e.Where.Value, map.Origin.X, map.SouthZ, map.SideMetres)));
        _map.Set(map, e.Where);

        Clear(_risks, _riskCaption);
        _risks.AddChild(_riskCaption);
        if (e.Risks.Count == 0) _risks.AddChild(Chip(Strings.T("intel.risks.none")));
        foreach (var risk in e.Risks) _risks.AddChild(Chip(risk));

        Clear(_tabs);
        _tabs.Visible = n > 1;
        for (var i = 0; i < n; i++)
        {
            var active = i == featured;
            var tab = new PanelContainer { ThemeTypeVariation = active ? "IntelTabActive" : "IntelTab", MouseFilter = MouseFilterEnum.Ignore };
            var name = !_wide ? "" : events[i].Name.Length > MaxTabChars ? events[i].Name[..(MaxTabChars - 1)] + "." : events[i].Name; // a narrow screen has room for the numbers only
            tab.AddChild(Text(Strings.F(active ? "intel.tab.active" : "intel.tab", i + 1, name).TrimEnd(), active ? "IntelBody" : "IntelSmall"));
            _tabs.AddChild(tab);
        }
    }

    static PanelContainer Chip(string text)
    {
        var chip = new PanelContainer { ThemeTypeVariation = "IntelChip", MouseFilter = MouseFilterEnum.Ignore };
        chip.AddChild(Text(text, "IntelBody"));
        return chip;
    }

    /// <summary>Takes the children off at once (a queued free would keep them in the layout until the frame ends) and frees them, except <paramref name="keep"/>.</summary>
    static void Clear(Container c, Node? keep = null)
    {
        foreach (var child in c.GetChildren())
        {
            c.RemoveChild(child);
            if (child != keep) child.QueueFree();
        }
    }

    static Label Text(string s, string variation) => new() { Text = s, ThemeTypeVariation = variation, MouseFilter = MouseFilterEnum.Ignore };

    static void Set(Label l, string s) { if (l.Text != s) l.Text = s; }
}

/// <summary>
/// An event's emblem, drawn as shapes (so it needs no icon file): a hazard triangle for any kind, a footprint for "creature". It sits
/// beside the headline, never alone. More kinds are one more case in <see cref="_Draw"/>.
/// </summary>
sealed partial class IntelEmblem : Control
{
    string _kind = "";
    Color _ink;

    public IntelEmblem(int side)
    {
        CustomMinimumSize = new Vector2(side, side);
        MouseFilter = MouseFilterEnum.Ignore;
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
    }

    public void Set(string kind, Color ink)
    {
        if (kind == _kind && ink == _ink) return;
        _kind = kind;
        _ink = ink;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var s = Size;
        if (_kind == "creature") Footprint(s);
        else Hazard(s);
    }

    void Hazard(Vector2 s)
    {
        var line = Math.Max(3f, s.X * 0.08f);
        var top = new Vector2(s.X * 0.5f, s.Y * 0.1f);
        var left = new Vector2(s.X * 0.07f, s.Y * 0.9f);
        var right = new Vector2(s.X * 0.93f, s.Y * 0.9f);
        DrawPolyline(new[] { top, right, left, top }, _ink, line, true);
        DrawRect(new Rect2(s.X * 0.46f, s.Y * 0.36f, s.X * 0.08f, s.Y * 0.28f), _ink);
        DrawRect(new Rect2(s.X * 0.46f, s.Y * 0.69f, s.X * 0.08f, s.X * 0.08f), _ink);
    }

    void Footprint(Vector2 s)
    {
        DrawColoredPolygon(new[]
        {
            new Vector2(s.X * 0.5f, s.Y * 0.50f), new Vector2(s.X * 0.72f, s.Y * 0.62f), new Vector2(s.X * 0.68f, s.Y * 0.88f),
            new Vector2(s.X * 0.50f, s.Y * 0.94f), new Vector2(s.X * 0.32f, s.Y * 0.88f), new Vector2(s.X * 0.28f, s.Y * 0.62f),
        }, _ink);
        foreach (var (x, y, r) in new[] { (0.22f, 0.44f, 0.08f), (0.38f, 0.26f, 0.09f), (0.58f, 0.22f, 0.09f), (0.76f, 0.34f, 0.08f), (0.86f, 0.52f, 0.06f) })
            DrawCircle(new Vector2(s.X * x, s.Y * y), s.X * r, _ink);
    }
}

/// <summary>
/// A square map of the surroundings of an event: the shared map base around its place (terrain texture and roads), a ring and a
/// cross on the place, and a north mark. Without a place it is a hatched panel that says so. Redrawn only when it is set.
/// </summary>
sealed partial class IntelMapTile : Control
{
    /// <summary>Metres across the window the tile shows, and the side of the terrain texture it takes from the shared base.</summary>
    const float WindowM = 600f;
    const int TextureSide = 1024;

    MapBase? _map;
    NVec2? _at;
    ImageTexture? _tex;

    public IntelMapTile(int side)
    {
        CustomMinimumSize = new Vector2(side, side);
        SizeFlagsVertical = SizeFlags.ShrinkBegin;
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;
    }

    public void Set(MapBase? map, NVec2? at)
    {
        if (!ReferenceEquals(map, _map)) _tex = null;
        _map = map;
        _at = at;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var r = new Rect2(Vector2.Zero, Size);
        DrawRect(r, ShooterTheme.IntelCard);
        if (_map is { } b && _at is { } g) DrawSurroundings(b, g, r);
        else DrawNone(r);
        DrawRect(r, ShooterTheme.IntelLine, false, 2f);
    }

    void DrawSurroundings(MapBase b, NVec2 g, Rect2 r)
    {
        var window = Math.Min(WindowM, b.SideMetres);
        var x0 = Math.Clamp(g.X - window / 2, b.Origin.X, b.Origin.X + b.SideMetres - window); // the window stays on the map
        var z0 = Math.Clamp(g.Y - window / 2, b.Origin.Y, b.Origin.Y + b.SideMetres - window);
        _tex ??= b.Texture(TextureSide);
        var texels = TextureSide / b.SideMetres;
        DrawTextureRectRegion(_tex, r, new Rect2((x0 - b.Origin.X) * texels, (z0 - b.Origin.Y) * texels, window * texels, window * texels));
        var px = r.Size.X / window;
        var origin = new Vector2(x0, z0);
        var win = new Rect2(x0, z0, window, window);
        var roads = new List<(MapRoad Road, Vector2[] Points)>();
        foreach (var road in b.Roads)
        {
            if (!road.Bounds.Intersects(win)) continue;
            var pts = new Vector2[road.Ground.Length];
            for (var i = 0; i < pts.Length; i++) pts[i] = (road.Ground[i] - origin) * px;
            roads.Add((road, pts));
        }
        foreach (var (road, pts) in roads) DrawPolyline(pts, ShooterTheme.MapRoadCasing, Math.Max(1.5f, road.WidthM * px) + 2f, true);
        foreach (var (road, pts) in roads) DrawPolyline(pts, road.Colour, Math.Max(1.5f, road.WidthM * px), true);

        // the place: a ring with a gap-toothed cross, dark outlined so it reads on any ground
        var m = (new Vector2(g.X, g.Y) - origin) * px;
        const float ring = 14f, reach = 30f;
        foreach (var (w, c) in new[] { (6f, ShooterTheme.Outline), (3f, ShooterTheme.Warn) })
        {
            DrawArc(m, ring, 0f, Mathf.Tau, 32, c, w, true);
            DrawLine(m + new Vector2(-reach, 0), m + new Vector2(-ring - 4, 0), c, w);
            DrawLine(m + new Vector2(ring + 4, 0), m + new Vector2(reach, 0), c, w);
            DrawLine(m + new Vector2(0, -reach), m + new Vector2(0, -ring - 4), c, w);
            DrawLine(m + new Vector2(0, ring + 4), m + new Vector2(0, reach), c, w);
        }
        DrawNorth(r);
    }

    void DrawNone(Rect2 r)
    {
        for (var x = -r.Size.Y; x < r.Size.X; x += 16f)
            DrawLine(new Vector2(x, r.Size.Y), new Vector2(x + r.Size.Y, 0), new Color(ShooterTheme.IntelLine, 0.5f), 2f);
        var font = GetThemeDefaultFont();
        const int fs = ShooterTheme.FontSmall;
        var text = Strings.T("intel.map.nolocation");
        var size = font.GetMultilineStringSize(text, HorizontalAlignment.Center, -1, fs);
        var plate = new Rect2((r.Size - size) / 2 - new Vector2(8, 8), size + new Vector2(16, 16));
        DrawRect(plate, ShooterTheme.IntelGlass);
        DrawRect(plate, ShooterTheme.IntelLine, false, 2f);
        DrawMultilineString(font, new Vector2(plate.Position.X + 8, plate.Position.Y + 8 + font.GetAscent(fs)), text, HorizontalAlignment.Center, size.X, fs, -1, ShooterTheme.IntelInk);
    }

    /// <summary>"N" with an arrow in the top-right corner: the map is always north up.</summary>
    void DrawNorth(Rect2 r)
    {
        var font = GetThemeDefaultFont();
        const int fs = ShooterTheme.FontSmall;
        var plate = new Rect2(r.Size.X - 30, 6, 24, 40);
        DrawRect(plate, ShooterTheme.IntelGlass);
        DrawRect(plate, ShooterTheme.IntelLine, false, 1f);
        var mid = plate.Position.X + plate.Size.X / 2;
        DrawColoredPolygon(new[] { new Vector2(mid, plate.Position.Y + 4), new Vector2(mid + 6, plate.Position.Y + 16), new Vector2(mid - 6, plate.Position.Y + 16) }, ShooterTheme.IntelInk);
        DrawString(font, new Vector2(plate.Position.X, plate.Position.Y + 20 + font.GetAscent(fs) * 0.9f), Strings.T("intel.map.north"), HorizontalAlignment.Center, plate.Size.X, fs, ShooterTheme.IntelInk);
    }
}
