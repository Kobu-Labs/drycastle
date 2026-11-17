using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// In-match HUD (issue #88). Every element is built once in <see cref="_Ready"/>; the setters change text, fill,
/// visibility or modulate only when the value changed. <see cref="_Process"/> only advances time based items
/// (timer second, countdowns, fades, kill feed expiry, Tab, mouse hint, crosshair) and compares before writing.
/// Layout: all pieces sit on anchors with fixed-size cells (no shift when values change). The root is scaled by
/// viewport height / 1080 (never under 0.8) times the UI scale setting (75-150 %).
/// </summary>
public partial class Hud : CanvasLayer
{
    const int Slots = 4, FeedRows = HudLogic.FeedMax, BoardRows = 16, Indicators = 4;
    const float CrossThick = 3, CrossLen = 12, CrossMinGap = 8;

    public GameSettings Settings { get; private set; } = new();
    /// <summary>Id of the local player (scoreboard highlight, score line).</summary>
    public ushort MyId { get; set; }
    /// <summary>Display name of a weapons.json index in a kill message (the killer's weapon, not the viewer's hotbar); null when unknown.</summary>
    public Func<byte, string?>? KillWeaponName { get; set; }
    /// <summary>Crosshair spread in degrees (set by the owner on shots and weapon changes; the crosshair reads it each frame).</summary>
    public Func<float>? Spread { get; set; }
    /// <summary>True while a scope is up (sniper zoom): the dark ring and thin reticle replace the crosshair.</summary>
    public Func<bool>? Scoped { get; set; }
    /// <summary>The scope that is up: its vertical FOV in degrees (the mil-dots are spaced by it) and the zero dialled, metres (0: the weapon has no bullet drop, so no dots and no zero).</summary>
    public Func<(float FovDeg, float ZeroM)>? Scope { get; set; }
    /// <summary>Iron-sight aim amount 0..1; the crosshair fades out as it rises (the weapon's own sight takes over).</summary>
    public Func<float>? Aim { get; set; }
    /// <summary>Sniper breath hold: whether the weapon has one and is scoped, the breath left (0..1) and the phase. Read each frame; the owner sets it.</summary>
    public Func<(bool Show, float Meter, BreathState.BreathPhase Phase)>? Breath { get; set; }
    float _crossAlpha = 1;
    ScopeOverlay _scope = null!;
    Label _zero = null!;
    (int Zero, int Mils) _zeroShown = (-1, -1);
    Control _breath = null!;
    Label _breathLabel = null!;

    Control _root = null!;
    // crosshair, hit marker, indicators
    readonly ColorRect[] _arms = new ColorRect[4], _armsBack = new ColorRect[4];
    float _gap = -1;
    Control _hit = null!, _crossRoot = null!;
    readonly Line2D[] _hitLines = new Line2D[4];
    double _hitUntil, _hitFade;
    readonly Control[] _ind = new Control[Indicators];
    readonly Polygon2D[] _indTri = new Polygon2D[Indicators];
    readonly double[] _indAt = new double[Indicators];
    int _indNext;
    // health
    ProgressBar _hpBar = null!;
    int _hp = -1;
    // ammo
    Label _wName = null!, _mag = null!, _reserve = null!, _speed = null!, _prompt = null!;
    RadialProgress _radial = null!;
    // top centre
    Label _timer = null!, _scoreLine = null!;
    float _timeLeft; double _timeAt; bool _timeRunning; int _timeShown = -1;
    MatchPhase _phase = MatchPhase.Waiting;
    // kill feed
    Control _feedRoot = null!, _feedBox = null!;
    readonly PanelContainer[] _feed = new PanelContainer[FeedRows];
    readonly Label[] _feedKiller = new Label[FeedRows], _feedWeapon = new Label[FeedRows], _feedVictim = new Label[FeedRows];
    readonly List<(KillMsg Msg, string Killer, string Victim)> _kills = new();
    readonly List<double> _killTimes = new();
    (int, int) _feedShown = (-1, -1);
    // respawn, protection, hints
    Control _respawn = null!;
    Label _respawnTitle = null!, _respawnBy = null!, _respawnIn = null!;
    bool _respawnWaits;
    double _respawnAt; bool _dead; int _respawnShown = -1;
    Control _protChip = null!, _protOutline = null!;
    Label _protText = null!;
    double _protUntil; int _protShown = -1;
    // scoreboard
    Control _board = null!;
    Label _boardTitle = null!, _boardFoot = null!;
    /// <summary>One scoreboard table: optional team heading, column header, pre-built rows, empty line.</summary>
    sealed class BoardCol
    {
        public VBoxContainer Root = null!;
        public Label Head = null!, Empty = null!;
        public readonly PanelContainer[] Row = new PanelContainer[BoardRows];
        public readonly Label[][] Cells = new Label[BoardRows][];
    }
    /// <summary>Left table (everyone in free-for-all, own team in team deathmatch), right table (other team), and players without a team below.</summary>
    readonly BoardCol[] _cols = { new(), new(), new() };
    const float BoardWidth = 800, BoardWidthTeams = 2 * 640 + 2 * ShooterTheme.Space;
    IReadOnlyList<ScoreRow> _rows = Array.Empty<ScoreRow>();
    IReadOnlyList<ushort> _winners = Array.Empty<ushort>();
    bool _boardShown, _boardDirty = true;
    Func<ushort, string> _nameOf = id => Strings.F("player.unknown", id);
    Vector2 _lastSize;
    Weapons4 _weapons = new();

    sealed class Weapons4 { public string[] Names = new string[Slots]; public int Count; }

    public override void _Ready()
    {
        Strings.Register();
        Layer = 10;
        Settings = GameSettings.Current;
        GameSettings.Changed += OnSettingsChanged;
        _root = new Control { Theme = ShooterTheme.Build(), MouseFilter = Control.MouseFilterEnum.Ignore };
        AddChild(_root);
        BuildProtection();
        BuildPounce();
        _scope = new ScopeOverlay { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _scope.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_scope);
        // the zero and what one dot is worth, inside the lower part of the scope's circle
        _zero = Place(L("", "HudSmall", HorizontalAlignment.Center), 0.5f, 0.76f, -240, 0, 480, 24);
        _zero.Visible = false;
        _root.AddChild(_zero);
        BuildCrosshair();
        BuildBreath();
        BuildHuntShot();
        BuildBinoculars();
        BuildHealth();
        BuildStatus();
        BuildRozhlas(); // the set holder's hint and ON AIR chip
        BuildAmmo();
        BuildSeats(); // the vehicle seat schematic above the vehicle readout
        BuildTop();
        BuildFeed();
        BuildProtectionChip();
        BuildRespawn();
        BuildBlood(); // above the scrim
        BuildBoard();
        BuildXpPanel();
        BuildMinimap();
        BuildStreaks();
        BuildUav();
        BuildSandbox();
        BuildTacticalMap();
        BuildChat();
        ApplyMinimapSettings();
        Relayout();
        GetViewport().SizeChanged += Relayout;
        SetHealth(100);
        SetMatch(MatchPhase.Waiting, 0, null);
    }

    /// <summary>The live settings changed (settings screen preview/apply): rescale, crosshair visibility and FOV, key hints.</summary>
    void OnSettingsChanged()
    {
        Settings = GameSettings.Current;
        Relayout();
        ApplyMinimapSettings();
        _gap = -1;
        _rozhlasHintStale = true; // the talk key may have been rebound
        _seatsDef = null; // the seat hint names the (maybe rebound) keys: rebuilt on the next frame
        foreach (var tri in _indTri) tri.Color = ShooterTheme.Danger; // read at build time otherwise; the palette may have switched
        ApplyCrosshairVisibility();
    }

    bool _crossCovered;
    /// <summary>A panel stands in the middle of the screen (the corpse chooser): the crosshair would show through it, so it is hidden meanwhile.</summary>
    public void CoverCrosshair(bool on)
    {
        if (_crossCovered == on) return;
        _crossCovered = on;
        ApplyCrosshairVisibility();
    }

    /// <summary>The one writer of the crosshair's Visible: the setting, the cover, the scope, the binoculars and the pounce all go through <see cref="CrosshairRules"/>.</summary>
    void ApplyCrosshairVisibility()
    {
        if (_crossRoot == null) return;
        _crossRoot.Visible = CrosshairRules.Visible(Settings.ShowCrosshair, _crossCovered, _scope?.Visible ?? false, _bino?.Visible ?? false, _pounceHide || _flungHide);
    }

    public override void _ExitTree() => GameSettings.Changed -= OnSettingsChanged;

    // ---- layout helpers ----

    /// <summary>Anchors the control at (ax, ay) of the screen and offsets it so a w x h box is aligned to that point (0 = left/top, 1 = right/bottom).</summary>
    static T Place<T>(T c, float ax, float ay, float ox, float oy, float w, float h) where T : Control
    {
        c.AnchorLeft = c.AnchorRight = ax; c.AnchorTop = c.AnchorBottom = ay;
        c.OffsetLeft = ox - w * ax; c.OffsetRight = ox + w * (1 - ax);
        c.OffsetTop = oy - h * ay; c.OffsetBottom = oy + h * (1 - ay);
        return c;
    }

    static Label L(string text, string? variation = null, HorizontalAlignment align = HorizontalAlignment.Left, float minW = 0)
    {
        var l = new Label { Text = text, HorizontalAlignment = align, MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(minW, 0) };
        if (variation != null) l.ThemeTypeVariation = variation;
        return l;
    }

    static PanelContainer P(string variation) => new() { ThemeTypeVariation = variation, MouseFilter = Control.MouseFilterEnum.Ignore };

    static void Set(Label l, string text) { if (l.Text != text) l.Text = text; }

    static void Fade(CanvasItem c, float a) { if (!Mathf.IsEqualApprox(c.Modulate.A, a)) c.Modulate = new Color(1, 1, 1, a); }

    /// <summary>Root size is the viewport divided by the scale, so anchored children fill the real screen at any size and aspect.</summary>
    void Relayout()
    {
        var vp = GetViewport().GetVisibleRect().Size;
        if (vp == _lastSize && Mathf.IsEqualApprox(_root.Scale.X, UiFactor())) return;
        _lastSize = vp; _boardDirty = true; // the board's width is capped to the screen
        var s = UiFactor();
        _root.Scale = new Vector2(s, s);
        _root.Position = Vector2.Zero;
        _root.Size = vp / s;
        // the feed sits beside _root, so it follows the same resolution and UI scale itself
        _feedRoot.Scale = new Vector2(s, s);
        _feedRoot.Position = Vector2.Zero;
        _feedRoot.Size = vp / s;
        _gap = -1;
    }

    float UiFactor() => HudLogic.ScreenScale(GetViewport().GetVisibleRect().Size.Y, Settings.UiScale);

    // ---- build ----

    void BuildProtection()
    {
        _protOutline = P("HudProtection");
        _protOutline.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_protOutline);
        _protOutline.Modulate = new Color(1, 1, 1, 0);
    }

    void BuildCrosshair()
    {
        var c = _crossRoot = Place(new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = Settings.ShowCrosshair }, 0.5f, 0.5f, 0, 0, 0, 0);
        _root.AddChild(c);
        for (var i = 0; i < 4; i++)
        {
            _armsBack[i] = new ColorRect { Color = ShooterTheme.Outline, MouseFilter = Control.MouseFilterEnum.Ignore };
            _arms[i] = new ColorRect { Color = ShooterTheme.Text, MouseFilter = Control.MouseFilterEnum.Ignore };
            c.AddChild(_armsBack[i]); c.AddChild(_arms[i]);
        }
        // hit marker: four diagonal ticks around the centre
        _hit = Place(new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, 0) }, 0.5f, 0.5f, 0, 0, 0, 0);
        _root.AddChild(_hit);
        for (var i = 0; i < 4; i++)
        {
            var dir = new Vector2(i % 2 == 0 ? 1 : -1, i < 2 ? 1 : -1).Normalized();
            var l = new Line2D { Width = 3, DefaultColor = ShooterTheme.Text, Points = new[] { dir * 12, dir * 24 } };
            _hit.AddChild(l); _hitLines[i] = l;
        }
        // damage indicators: chevrons on a ring, rotated around the centre
        for (var i = 0; i < Indicators; i++)
        {
            var pivot = Place(new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, 0) }, 0.5f, 0.5f, 0, 0, 0, 0);
            var back = new Polygon2D { Color = ShooterTheme.Outline, Polygon = new[] { new Vector2(0, -216), new Vector2(-28, -176), new Vector2(28, -176) } };
            var tri = new Polygon2D { Color = ShooterTheme.Danger, Polygon = new[] { new Vector2(0, -208), new Vector2(-20, -180), new Vector2(20, -180) } };
            pivot.AddChild(back); pivot.AddChild(tri);
            _root.AddChild(pivot); _ind[i] = pivot; _indTri[i] = tri;
        }
    }

    /// <summary>Health: one bar, no panel, no text. It turns the danger colour when low.</summary>
    void BuildHealth()
    {
        _hpBar = Place(new ProgressBar { MaxValue = 100, Value = 100, ShowPercentage = false, ThemeTypeVariation = "HealthBar", MouseFilter = Control.MouseFilterEnum.Ignore }, 0, 1, ShooterTheme.Margin, -ShooterTheme.Margin, 320, 16);
        _root.AddChild(_hpBar);
    }

    void BuildAmmo()
    {
        _speed = Place(L("", "HudBig", HorizontalAlignment.Right), 1, 1, -ShooterTheme.Margin, -ShooterTheme.Margin - 104, 320, 40);
        _speed.Visible = false; // vehicle speed readout hook: SetVehicleSpeed
        _root.AddChild(_speed);
        _prompt = Place(L("", "HudHead", HorizontalAlignment.Center), 0.5f, 0.7f, -200, 0, 400, 32); // context prompt (enter / exit a vehicle)
        _prompt.Visible = false;
        _root.AddChild(_prompt);
        // the hold ring (care package open, #151) sits just above the prompt line, its label under the ring
        _radial = new RadialProgress { Text = Strings.T("hud.crate.opening"), Visible = false };
        _radial.AnchorLeft = _radial.AnchorRight = 0.5f;
        _radial.AnchorTop = _radial.AnchorBottom = 0.7f;
        _radial.OffsetLeft = -RadialProgress.Width / 2;
        _radial.OffsetRight = RadialProgress.Width / 2;
        _radial.OffsetBottom = -ShooterTheme.Space * 4;
        _radial.OffsetTop = _radial.OffsetBottom - RadialProgress.Height;
        _root.AddChild(_radial);
        // weapon name and ammo only: no panel, no border
        var v = Place(new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 1, 1, -ShooterTheme.Margin, -ShooterTheme.Margin, 320, 88);
        v.AddThemeConstantOverride("separation", 0);
        _root.AddChild(v);
        _wName = L("", "HudHead", HorizontalAlignment.Right); v.AddChild(_wName);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End, MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddChild(row);
        _mag = L("", "HudDisplay", HorizontalAlignment.Right, 96);
        _reserve = L("", "HudHead", HorizontalAlignment.Left, 88); _reserve.ThemeTypeVariation = "HudDim";
        row.AddChild(_mag); row.AddChild(_reserve);
    }

    void BuildTop()
    {
        var p = Place(P("HudPanel"), 0.5f, 0, 0, ShooterTheme.Margin, 224, 64);
        _root.AddChild(p);
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", 0);
        p.AddChild(v);
        _timer = L("", "HudHead", HorizontalAlignment.Center); v.AddChild(_timer);
        _scoreLine = L("", "HudSmall", HorizontalAlignment.Center); _scoreLine.ClipText = true; v.AddChild(_scoreLine);
    }

    void BuildFeed()
    {
        // the feed sits beside the panels root, not under it, so a control session can hide the panels and keep the feed (ticket #154)
        _feedRoot = new Control { Theme = _root.Theme, MouseFilter = Control.MouseFilterEnum.Ignore };
        AddChild(_feedRoot);
        var v = Place(new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 1, 0, -ShooterTheme.Margin, ShooterTheme.Margin, 480, FeedRows * 40 + (FeedRows - 1) * 4);
        v.PivotOffset = new Vector2(480, 0); // the compact feed shrinks towards the top right corner
        v.AddThemeConstantOverride("separation", 4);
        _feedRoot.AddChild(v);
        _feedBox = v;
        for (var i = 0; i < FeedRows; i++)
        {
            var row = P("HudRow"); row.CustomMinimumSize = new Vector2(0, 40); row.Visible = false;
            var h = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End, MouseFilter = Control.MouseFilterEnum.Ignore };
            h.AddThemeConstantOverride("separation", ShooterTheme.Space);
            row.AddChild(h);
            _feedKiller[i] = L("");
            _feedWeapon[i] = L("", "HudDim");
            _feedVictim[i] = L("");
            h.AddChild(_feedKiller[i]); h.AddChild(_feedWeapon[i]); h.AddChild(_feedVictim[i]);
            v.AddChild(row); _feed[i] = row;
        }
    }

    void BuildProtectionChip()
    {
        // spawn protection chip, bottom centre (reserved space, faded in and out)
        var chip = Place(P("HudPanel"), 0.5f, 1, 0, -ShooterTheme.Margin, 320, 48);
        var h = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        h.AddThemeConstantOverride("separation", ShooterTheme.Space);
        chip.AddChild(h);
        h.AddChild(new ShieldIcon { CustomMinimumSize = new Vector2(24, 24), MouseFilter = Control.MouseFilterEnum.Ignore });
        _protText = L("", "HudAccent"); h.AddChild(_protText);
        _root.AddChild(chip); _protChip = chip;
        chip.Modulate = new Color(1, 1, 1, 0);
    }

    void BuildRespawn()
    {
        _respawn = P("HudScrim");
        _respawn.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _respawn.Visible = false;
        _root.AddChild(_respawn);
        var c = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _respawn.AddChild(c);
        var v = new VBoxContainer { CustomMinimumSize = new Vector2(640, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
        c.AddChild(v);
        _respawnTitle = L(Strings.T("hud.respawn.title"), "HudHead", HorizontalAlignment.Center); v.AddChild(_respawnTitle);
        _respawnBy = L("", "HudBig", HorizontalAlignment.Center); _respawnBy.ThemeTypeVariation = "HudDanger"; _respawnBy.ClipText = true; v.AddChild(_respawnBy);
        _respawnIn = L("", "HudDisplay", HorizontalAlignment.Center); v.AddChild(_respawnIn);
        BuildRespawnLoadouts(v);
    }

    void BuildBoard()
    {
        _board = Place(P("HudPanel"), 0.5f, 0.5f, 0, 0, 800, 0);
        _board.Visible = false;
        _root.AddChild(_board);
        // grow from the centre in both directions
        _board.GrowHorizontal = Control.GrowDirection.Both; _board.GrowVertical = Control.GrowDirection.Both;
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _board.AddChild(v);
        _boardTitle = L("", "HudBig", HorizontalAlignment.Center); _boardTitle.ClipText = true; v.AddChild(_boardTitle);
        var tables = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        tables.AddThemeConstantOverride("separation", 2 * ShooterTheme.Space);
        v.AddChild(tables);
        tables.AddChild(BoardTable(_cols[0])); tables.AddChild(BoardTable(_cols[1]));
        v.AddChild(BoardTable(_cols[2]));
        _boardFoot = L("", "HudSmall", HorizontalAlignment.Center); v.AddChild(_boardFoot);
    }

    VBoxContainer BoardTable(BoardCol col)
    {
        var v = col.Root = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        col.Head = L("", "HudHead", HorizontalAlignment.Center); col.Head.ClipText = true; v.AddChild(col.Head);
        v.AddChild(BoardRow(null, out _, true));
        for (var i = 0; i < BoardRows; i++)
        {
            col.Row[i] = BoardRow(i, out col.Cells[i], false);
            v.AddChild(col.Row[i]);
        }
        col.Empty = L(Strings.T("score.empty"), "HudDim", HorizontalAlignment.Center); v.AddChild(col.Empty);
        return v;
    }

    PanelContainer BoardRow(int? index, out Label[] cells, bool header)
    {
        var p = P(header ? "HudBare" : "HudRow"); p.CustomMinimumSize = new Vector2(0, 32);
        var h = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        p.AddChild(h);
        cells = new[]
        {
            L(header ? Strings.T("score.col.player") : "", header ? "HudSmall" : null), L(header ? Strings.T("score.col.kills") : "", header ? "HudSmall" : null, HorizontalAlignment.Right, 80),
            L(header ? Strings.T("score.col.deaths") : "", header ? "HudSmall" : null, HorizontalAlignment.Right, 80), L(header ? Strings.T("score.col.ping") : "", header ? "HudSmall" : null, HorizontalAlignment.Right, 112),
        };
        cells[0].SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; cells[0].ClipText = true;
        foreach (var c in cells) h.AddChild(c);
        if (index != null) p.Visible = false;
        return p;
    }

    // ---- setters (call on change) ----

    public void SetHealth(int hp)
    {
        hp = Math.Clamp(hp, 0, 100);
        if (hp == _hp) return;
        _hp = hp;
        _hpBar.Value = hp;
        _hpBar.ThemeTypeVariation = hp <= HudLogic.LowHealth ? "HealthBarLow" : "HealthBar";
    }

    /// <summary>Display names in slot order (the selected one is named beside the ammo).</summary>
    public void SetWeapons(IReadOnlyList<string> names)
    {
        for (var i = 0; i < Slots; i++) _weaponNames[i] = i < names.Count ? names[i] : "";
        Set(_wName, _weaponNames[_slotShown]);
    }

    readonly string[] _weaponNames = { "", "", "", "" };
    int _slotShown;

    /// <summary>Selected slot (1-based) and its mag/reserve (mag &lt; 0: no ammo, melee). The per-slot list and reload fields are kept for the callers.</summary>
    public void SetAmmo(int slot, int mag, int reserve, IReadOnlyList<(int Mag, int Reserve, bool Melee)> all, float reloadLeft, float reloadTotal)
    {
        _slotShown = Math.Clamp(slot, 1, Slots) - 1;
        Set(_wName, _weaponNames[_slotShown]);
        Set(_mag, mag < 0 ? Strings.T("hud.ammo.none") : mag.ToString());
        Set(_reserve, mag < 0 ? "" : Strings.F("hud.ammo.reserve", reserve));
        _mag.ThemeTypeVariation = mag == 0 && reserve == 0 && mag >= 0 ? "HudDisplayDanger" : "HudDisplay";
    }

    /// <summary>Phase, seconds left (null: unchanged clock) and the winner/ending state.</summary>
    /// <summary>Team deathmatch: the client's match state, read for teams and team scores. Call before <see cref="SetMatch"/>; null or free-for-all hides every team cue.</summary>
    public void SetTeams(ClientMatchState? match) { _team = match; _boardDirty = true; _feedShown = (-1, -1); Minimap.TeamColours = TeamMode; }
    ClientMatchState? _team;
    bool TeamMode => _team is { IsTeamMode: true };
    byte TeamOf(ushort id) => _team?.TeamOf(id) ?? Teams.None;
    byte MyTeam => TeamOf(MyId);

    /// <summary>Friend or enemy colour of <paramref name="team"/> as seen by the local player (the HUD text colour when neutral).</summary>
    Color? RelationColor(byte team) => HudLogic.RelationOf(MyTeam, team) switch
    {
        HudLogic.Relation.Friend => ShooterTheme.Friend,
        HudLogic.Relation.Enemy => ShooterTheme.Enemy,
        _ => null,
    };

    /// <summary>A name with its team tag in team deathmatch (so the team is readable without colour), plain otherwise.</summary>
    string Tagged(string name, byte team) => TeamMode && team != Teams.None ? Strings.F("score.team.tag", Teams.Tag(team), name) : name;

    /// <summary>The label's text colour for a team relation; null restores the theme colour.</summary>
    static void Tint(Label l, Color? c) { if (c is { } col) l.AddThemeColorOverride("font_color", col); else l.RemoveThemeColorOverride("font_color"); }

    public void SetMatch(MatchPhase phase, float timeRemaining, IReadOnlyList<ScoreRow>? rows, IReadOnlyList<ushort>? winners = null, Func<ushort, string>? nameOf = null)
    {
        _phase = phase;
        _timeLeft = timeRemaining; _timeAt = Now; _timeRunning = phase == MatchPhase.Running; _timeShown = -1;
        if (rows != null) { _rows = rows; _boardDirty = true; }
        if (winners != null) { _winners = winners; _boardDirty = true; }
        if (nameOf != null) _nameOf = nameOf;
        var line = HudLogic.Score(_rows, MyId);
        Set(_scoreLine, phase switch
        {
            MatchPhase.Waiting => Strings.T("hud.phase.waiting"),
            MatchPhase.Ending => Strings.T("hud.phase.ending"),
            _ when TeamMode => TeamScoreText(),
            _ => line.IAmLeader ? Strings.F("hud.score.leading", line.MyKills) : Strings.F("hud.score.behind", line.MyKills, line.LeaderName, line.LeaderKills),
        });
        UpdateTimer();
        RefreshXpPanel();
    }

    string TeamScoreText()
    {
        var (mine, theirs, team) = HudLogic.TeamScores(MyTeam, _team!.ScoreA, _team.ScoreB);
        return Strings.F("hud.score.team", Teams.Tag(team), mine, theirs);
    }

    void UpdateTimer()
    {
        var left = _timeRunning ? Math.Max(0f, _timeLeft - (float)(Now - _timeAt)) : _timeLeft;
        var shown = HudLogic.CeilSeconds(left);
        if (shown == _timeShown) return;
        _timeShown = shown;
        Set(_timer, _phase == MatchPhase.Waiting ? Strings.T("hud.timer.waiting") : HudLogic.TimeText(left));
    }

    public void AddKill(KillMsg k, string killer, string victim)
    {
        _kills.Add((k, killer, victim)); _killTimes.Add(Now);
        if (_kills.Count > 64) { _kills.RemoveRange(0, 32); _killTimes.RemoveRange(0, 32); }
        AddOwnKill(k, killer, victim);
        _feedShown = (-1, -1);
    }

    public void ClearKills() { _kills.Clear(); _killTimes.Clear(); _mineKills.Clear(); _mineTimes.Clear(); _feedShown = (-1, -1); }

    /// <summary>Direction of a damage source relative to the view (radians, 0 = ahead, clockwise).</summary>
    public void Damage(float angle)
    {
        var i = _indNext++ % Indicators;
        _ind[i].Rotation = angle;
        _indAt[i] = Now;
        Fade(_ind[i], 1);
    }

    /// <summary>Hit marker on a successful hit; kill and headshot get a larger, stronger mark (shape, not colour only).</summary>
    public void Hit(bool headshot, bool kill)
    {
        var big = kill ? 1.6f : headshot ? 1.25f : 1f;
        _hit.Scale = new Vector2(big, big);
        foreach (var l in _hitLines) { l.Width = kill || headshot ? 4 : 3; l.DefaultColor = kill ? ShooterTheme.Danger : headshot ? ShooterTheme.Warn : ShooterTheme.Text; }
        _hitFade = Settings.ReduceMotion ? 0 : 0.25; _hitUntil = Now + (Settings.ReduceMotion ? 0.2 : 0.15) + _hitFade;
        Fade(_hit, 1);
    }

    /// <summary>Drops hit marker and damage indicators at once (used on spawn and by the screenshot mode).</summary>
    public void ClearTransients()
    {
        _hitUntil = 0; Fade(_hit, 0);
        ClearHuntShot();
        for (var i = 0; i < Indicators; i++) { _indAt[i] = 0; Fade(_ind[i], 0); }
    }

    /// <summary>
    /// Respawn screen: show with the killer and seconds left; <paramref name="killer"/> empty = no killer line. <paramref name="fadeIn"/>: the scrim starts clear and the caller fades it in
    /// with <see cref="SetRespawnScrim"/> (the death screen, #283); the countdown runs from now either way, and the respawn keys work while it is still clear.
    /// </summary>
    public void SetDead(bool dead, string killer = "", float secondsLeft = 0, bool fadeIn = false)
    {
        _dead = dead;
        _respawn.Visible = dead;
        _respawn.Modulate = new Color(1, 1, 1, dead && fadeIn ? 0f : 1f);
        if (!dead) return;
        _respawnWaits = secondsLeft < 0; // an admin took the player out of the round (#312): no countdown
        Set(_respawnTitle, Strings.T(_respawnWaits ? "hud.respawn.out" : "hud.respawn.title"));
        if (_respawnWaits) { Set(_respawnBy, ""); Set(_respawnIn, Strings.T("hud.respawn.waits")); return; }
        _respawnAt = Now + secondsLeft; _respawnShown = -1;
        Set(_respawnBy, killer == "" ? Strings.T("hud.respawn.self") : Strings.F("hud.respawn.by", killer));
        UpdateRespawn();
    }

    /// <summary>The respawn scrim's opacity 0..1 (its text, the countdown and the loadout fade with it); only while dead.</summary>
    public void SetRespawnScrim(float alpha)
    {
        if (_dead) _respawn.Modulate = new Color(1, 1, 1, Mathf.Clamp(alpha, 0f, 1f));
    }

    void UpdateRespawn()
    {
        if (_respawnWaits) return;
        var s = HudLogic.CeilSeconds(_respawnAt - Now);
        if (s == _respawnShown) return;
        _respawnShown = s;
        Set(_respawnIn, Strings.F("hud.respawn.in", s));
    }

    /// <summary>Spawn protection seconds remaining (0 hides the chip and the outline).</summary>
    public void SetProtection(float seconds)
    {
        _protUntil = seconds > 0 ? Now + seconds : 0;
        _protShown = -1;
        if (seconds <= 0) { Fade(_protChip, 0); Fade(_protOutline, 0); }
        else UpdateProtection();
    }

    void UpdateProtection()
    {
        var tenths = (int)Math.Ceiling(Math.Max(0, _protUntil - Now) * 10);
        if (tenths == _protShown) return;
        _protShown = tenths;
        Set(_protText, Strings.F("hud.protection", $"{tenths / 10.0:0.0}s"));
        Fade(_protChip, 1); Fade(_protOutline, 1);
    }

    /// <summary>Context prompt under the crosshair, e.g. "E  Enter car"; null hides it.</summary>
    public void SetPrompt(string? text)
    {
        _prompt.Visible = text != null;
        if (text != null) Set(_prompt, text);
    }

    /// <summary>The hold ring above the prompt (care package open, #151; taking the rozhlas set): a fill from 0 to 1, or null to hide it.</summary>
    public void SetRadial(float? fraction)
    {
        _radial.Visible = fraction != null;
        if (fraction is { } f) _radial.Value = f;
    }

    /// <summary>Vehicle speed readout (km/h); null hides it. Hook for the vehicles issue, unused by the match HUD today.</summary>
    public void SetVehicleSpeed(float? kmh)
    {
        _speed.Visible = kmh != null;
        if (kmh != null) Set(_speed, Strings.F("hud.vehicle.speed", (int)Math.Round(kmh.Value)));
    }

    /// <summary>The vehicle readout as text (the flak gunner's ammo or a reload word) in the place of the speed; null hides it. A change of text only.</summary>
    public void SetVehicleReadout(string? text)
    {
        _speed.Visible = text != null;
        if (text != null) Set(_speed, text);
    }

    // ---- per frame (cheap, change-gated) ----

    /// <summary>Screenshot mode freezes the clock so transient elements (hit marker, indicators, feed, countdowns) stay as set.</summary>
    public static double? FrozenNow { get; set; }
    /// <summary>Screenshot mode: pretend the mouse is captured (or not) for the control hint.</summary>
    public Input.MouseModeEnum? MouseModeOverride { get; set; }

    static double Now => FrozenNow ?? Time.GetTicksMsec() / 1000.0;

    public override void _Process(double delta)
    {
        var now = Now;
        if (_timeRunning) UpdateTimer();
        if (_dead) { UpdateRespawn(); PollRespawnKeys(); }
        if (_protUntil > 0)
        {
            if (now >= _protUntil) SetProtection(0); else UpdateProtection();
        }
        if (_hitUntil > 0) UpdateHit(now);
        if (_huntUntil > 0) UpdateHuntShot(now);
        for (var i = 0; i < Indicators; i++) UpdateIndicator(i, now);
        UpdateFeed(now);
        UpdateCrosshair();
        UpdateBreath();
        UpdateTacticalMap();
        UpdateEquipment();
        var want = (Input.IsActionPressed("scoreboard") && !ChatOpen) || _phase == MatchPhase.Ending; // Tab completes in the chat box
        if (want != _boardShown || (want && _boardDirty)) UpdateBoard(want);
    }

    void UpdateHit(double now)
    {
        if (now >= _hitUntil) { _hitUntil = 0; Fade(_hit, 0); return; }
        Fade(_hit, _hitFade > 0 ? (float)Math.Clamp((_hitUntil - now) / _hitFade, 0, 1) : 1f);
    }

    void UpdateIndicator(int i, double now)
    {
        if (_indAt[i] <= 0) return;
        var age = now - _indAt[i];
        const double life = 1.2;
        if (age >= life) { _indAt[i] = 0; Fade(_ind[i], 0); return; }
        Fade(_ind[i], Settings.ReduceMotion ? 1f : (float)Math.Clamp((life - age) / 0.6, 0, 1));
    }

    void UpdateFeed(double now)
    {
        // a control session shows only the owner's kills, compactly (ticket #154); the rows and their text are the same
        var compact = ControlMode;
        var w = compact ? HudLogic.FeedWindow(_mineTimes, now, ShooterTheme.FeedCompactRows) : HudLogic.FeedWindow(_killTimes, now);
        if (w == _feedShown) return;
        _feedShown = w;
        var list = compact ? _mineKills : _kills;
        for (var i = 0; i < FeedRows; i++)
        {
            if (i >= w.Count) { _feed[i].Visible = false; continue; }
            var (k, killer, victim) = list[w.Start + i];
            var mine = k.Killer == MyId || k.Victim == MyId;
            var suicide = k.Killer == k.Victim;
            _feed[i].ThemeTypeVariation = mine ? "HudRowMine" : "HudRow";
            // your own rows say so in words as well as colour (as the scoreboard does)
            var killerTeam = TeamOf(k.Killer); var victimTeam = TeamOf(k.Victim);
            Set(_feedKiller[i], Tagged(WithYou(suicide ? victim : killer, k.Killer == MyId), killerTeam));
            Set(_feedWeapon[i], $"[{(k.Cause == KillCause.Roadkill ? Strings.T("feed.roadkill") : WeaponName(k.Weapon))}]");
            Set(_feedVictim[i], suicide ? "" : Tagged(WithYou(victim, k.Victim == MyId), victimTeam));
            Tint(_feedKiller[i], RelationColor(killerTeam)); Tint(_feedVictim[i], RelationColor(victimTeam));
            _feed[i].Visible = true;
        }
    }

    static string WithYou(string name, bool mine) => mine ? Strings.F("score.you", name) : name;

    string WeaponName(byte id)
    {
        return id switch { SpecialWeapons.Fall => Strings.T("weapon.fall"), SpecialWeapons.Bigfoot => Strings.T("weapon.bigfoot"), SpecialWeapons.Gang => Strings.T("weapon.gang"), SpecialWeapons.Void => Strings.T("weapon.void"), SpecialWeapons.Debug => Strings.T("weapon.debug"), SpecialWeapons.Redeploy => Strings.T("weapon.redeploy"), SpecialWeapons.Vehicle => Strings.T("weapon.vehicle"), SpecialWeapons.VehicleGun => Strings.T("weapon.vehicle_gun"), SpecialWeapons.Flak => Strings.T("weapon.flak"), SpecialWeapons.Burn => Strings.T("weapon.burn"), SpecialWeapons.Moab => Strings.T("weapon.moab"), SpecialWeapons.Missile => Strings.T("weapon.attack_missile"), SpecialWeapons.Turret => Strings.T("weapon.turret"), SpecialWeapons.AttackHelicopter => Strings.T("heli.weapon"), SpecialWeapons.Ac130 => Strings.T("ac130.kill"), SpecialWeapons.Helper => Strings.T("helper.weapon"), _ => KillWeaponName?.Invoke(id) ?? Strings.T("weapon.unknown") };
    }

    /// <summary>The mil-dots and the zero of a scope with bullet drop: the dots follow the scope's FOV, the words change only with the zero or the dot spacing.</summary>
    void UpdateScope(bool scoped)
    {
        (float FovDeg, float ZeroM) sc = scoped ? Scope?.Invoke() ?? (0f, 0f) : (0f, 0f);
        var on = sc.ZeroM > 0 && sc.FovDeg > 0;
        if (_zero.Visible != on) _zero.Visible = on;
        _scope.SetMils(on ? sc.FovDeg : 0f);
        if (!on) return;
        var shown = ((int)MathF.Round(sc.ZeroM), _scope.DotMils);
        if (shown == _zeroShown) return;
        _zeroShown = shown;
        Set(_zero, Strings.F("hud.scope.zero", shown.Item1, shown.Item2));
    }

    /// <summary>
    /// Black outside a centred circle with a thin cross, drawn over the zoomed view. A scope with bullet drop has mil-dots on the cross: a dot every
    /// <see cref="DotMils"/> mils (1 mil = 1 m at 1000 m), spaced from the FOV so they are true angles on the world: a target of a known height that
    /// spans n mils is height x 1000 / n metres away, and a dot under the centre is the hold-over for a target beyond the zero.
    /// </summary>
    sealed partial class ScopeOverlay : Control
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

    /// <summary>
    /// The breath hold has no meter (the heartbeat and the exhale say how much is left, see <see cref="BreathCues"/>): the HUD only names the key,
    /// under the crosshair, while scoped with a full breath that is not being held.
    /// </summary>
    void BuildBreath()
    {
        const float w = 240;
        _breathLabel = L("", "HudSmall", HorizontalAlignment.Center);
        _breath = Place(_breathLabel, 0.5f, 0.5f, -w / 2, 40, w, 24);
        _breath.Visible = false;
        _root.AddChild(_breath);
    }

    void UpdateBreath()
    {
        var b = Breath?.Invoke() ?? (false, 0f, BreathState.BreathPhase.Ready);
        var show = b.Show && b.Phase == BreathState.BreathPhase.Ready && b.Meter >= 0.999f;
        if (_breath.Visible != show) _breath.Visible = show;
        if (!show) return;
        // the words depend only on the bound key: text is built when it changes (#173, T3-032)
        var key = InputBindings.Name("sprint");
        if (key == _breathKey) return;
        _breathKey = key;
        Set(_breathLabel, Strings.F("hud.breath.ready", key));
    }

    string? _breathKey;

    void UpdateCrosshair()
    {
        if (_pounceHide || _flungHide) return; // lying under the demon, or in the Bigfoot's fist or flying from it: no crosshair
        if (UpdateBinoculars())
        {
            // the binoculars are up: their own view, no crosshair, and no scope left over from the moment they were raised
            if (_scope.Visible) _scope.Visible = false;
            UpdateScope(false);
            return;
        }
        var scoped = Scoped?.Invoke() ?? false;
        if (_scope.Visible != scoped) { _scope.Visible = scoped; _scope.QueueRedraw(); ApplyCrosshairVisibility(); }
        UpdateScope(scoped);
        if (scoped) return;
        var alpha = 1f - Math.Clamp(Aim?.Invoke() ?? 0f, 0f, 1f);
        if (Math.Abs(alpha - _crossAlpha) > 0.004f) { _crossAlpha = alpha; _crossRoot.Modulate = new Color(1, 1, 1, alpha); }
        var deg = Spread?.Invoke() ?? 0f;
        var gap = HudLogic.CrosshairGap(deg, Settings.Fov, GetViewport().GetVisibleRect().Size.Y, CrossMinGap * UiFactor()) / UiFactor();
        if (Math.Abs(gap - _gap) < 0.25f) return;
        _gap = gap;
        const float o = 1; // outline width
        var arms = new (float X, float Y, float W, float H)[]
        {
            (-CrossThick / 2, -gap - CrossLen, CrossThick, CrossLen), (-CrossThick / 2, gap, CrossThick, CrossLen),
            (-gap - CrossLen, -CrossThick / 2, CrossLen, CrossThick), (gap, -CrossThick / 2, CrossLen, CrossThick),
        };
        for (var i = 0; i < 4; i++)
        {
            var a = arms[i];
            _arms[i].Position = new Vector2(a.X, a.Y); _arms[i].Size = new Vector2(a.W, a.H);
            _armsBack[i].Position = new Vector2(a.X - o, a.Y - o); _armsBack[i].Size = new Vector2(a.W + 2 * o, a.H + 2 * o);
        }
    }

    void UpdateBoard(bool show)
    {
        _boardShown = show; _boardDirty = false;
        _board.Visible = show;
        if (!show) return;
        var ending = _phase == MatchPhase.Ending;
        if (ending)
        {
            var names = string.Join(", ", _winners.Select(_nameOf));
            if (TeamMode && _winners.Count > 0) Set(_boardTitle, Strings.F("score.ending.team", Teams.Tag(TeamOf(_winners[0]))));
            else Set(_boardTitle, names == "" ? Strings.T("hud.phase.ending") : Strings.F(_winners.Count > 1 ? "score.ending.tie" : "score.ending", names));
        }
        else Set(_boardTitle, Strings.T("score.title"));
        Set(_boardFoot, Strings.T(ending ? "score.hint.next" : "score.hint.hold"));
        // two tables side by side in team deathmatch: own team left, so the sides never swap with the lead
        var w = Math.Min(TeamMode ? BoardWidthTeams : BoardWidth, _root.Size.X - 2 * ShooterTheme.Margin);
        _board.OffsetLeft = -w / 2; _board.OffsetRight = w / 2;
        if (!TeamMode)
        {
            FillTable(_cols[0], HudLogic.Sort(_rows), Teams.None);
            _cols[1].Root.Visible = _cols[2].Root.Visible = false;
            return;
        }
        var split = HudLogic.SplitTeams(_rows, MyTeam);
        FillTable(_cols[0], split.Left, split.LeftTeam);
        FillTable(_cols[1], split.Right, Teams.Other(split.LeftTeam));
        _cols[2].Root.Visible = split.NoTeam.Count > 0;
        if (split.NoTeam.Count > 0) FillTable(_cols[2], split.NoTeam, Teams.None);
    }

    /// <summary>Fills one table with already sorted rows; <paramref name="team"/> names its heading (free-for-all has none).</summary>
    void FillTable(BoardCol col, List<ScoreRow> sorted, byte team)
    {
        col.Root.Visible = true;
        col.Head.Visible = TeamMode;
        if (TeamMode)
        {
            var key = HudLogic.RelationOf(MyTeam, team) switch { HudLogic.Relation.Friend => "score.team.mine", HudLogic.Relation.Enemy => "score.team.enemy", _ => "score.team.other" };
            Set(col.Head, team == Teams.None ? Strings.T("score.team.none") : Strings.F(key, Teams.Tag(team), team == Teams.A ? _team!.ScoreA : _team!.ScoreB));
            Tint(col.Head, RelationColor(team));
        }
        col.Empty.Visible = sorted.Count == 0;
        // keep the local row visible even when the table exceeds the pre-built rows
        if (sorted.Count > BoardRows)
        {
            var me = sorted.FindIndex(r => r.Id == MyId);
            if (me >= BoardRows) sorted[BoardRows - 1] = sorted[me];
        }
        for (var i = 0; i < BoardRows; i++)
        {
            if (i >= sorted.Count) { col.Row[i].Visible = false; continue; }
            var r = sorted[i];
            var mine = r.Id == MyId;
            col.Row[i].ThemeTypeVariation = mine ? "HudRowMine" : "HudRow";
            var c = col.Cells[i];
            Set(c[0], mine ? Strings.F("score.you", r.Name) : r.Name); // the team is the table's heading
            Tint(c[0], RelationColor(r.Team));
            Set(c[1], r.Kills.ToString()); Set(c[2], r.Deaths.ToString()); Set(c[3], Strings.F("score.ping", r.Ping));
            col.Row[i].Visible = true;
        }
    }
}

/// <summary>Small shield icon for spawn protection, drawn once.</summary>
public partial class ShieldIcon : Control
{
    public override void _Draw()
    {
        var w = Size.X; var h = Size.Y;
        var pts = new[] { new Vector2(w * 0.5f, 0), new Vector2(w, h * 0.2f), new Vector2(w * 0.88f, h * 0.65f), new Vector2(w * 0.5f, h), new Vector2(w * 0.12f, h * 0.65f), new Vector2(0, h * 0.2f) };
        DrawColoredPolygon(pts, ShooterTheme.Accent);
        DrawPolyline(new[] { pts[0], pts[1], pts[2], pts[3], pts[4], pts[5], pts[0] }, ShooterTheme.Outline, 1.5f);
    }
}
