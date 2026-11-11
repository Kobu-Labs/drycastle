using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// In-match HUD (issue #88). Every element is built once in <see cref="_Ready"/>; the setters change text, fill,
/// visibility or modulate only when the value changed. <see cref="_Process"/> only advances time based items
/// (timer second, countdowns, fades, kill feed expiry, Tab, mouse hint, crosshair) and compares before writing.
/// Layout: all pieces sit on anchors with fixed-size cells (no shift when values change). The root is scaled by
/// viewport height / 1080 (never under 0.8) times the UI scale setting (75-150 %).
/// </summary>
public partial class Hud : CanvasLayer
{
    const int Slots = 3, FeedRows = HudLogic.FeedMax, BoardRows = 16, Indicators = 4;
    const float RefHeight = 1080f, MinBaseScale = 0.8f;
    const float CrossThick = 3, CrossLen = 12, CrossMinGap = 8;

    public GameSettings Settings { get; private set; } = new();
    /// <summary>Id of the local player (scoreboard highlight, score line).</summary>
    public ushort MyId { get; set; }
    /// <summary>Held-Tab input; the screenshot mode overrides it.</summary>
    public bool? ForceScoreboard { get; set; }
    /// <summary>Crosshair spread in degrees (set by the owner on shots and weapon changes; the crosshair reads it each frame).</summary>
    public Func<float>? Spread { get; set; }
    /// <summary>True while a scope is up (sniper zoom): the dark ring and thin reticle replace the crosshair.</summary>
    public Func<bool>? Scoped { get; set; }
    /// <summary>Iron-sight aim amount 0..1; the crosshair fades out as it rises (the weapon's own sight takes over).</summary>
    public Func<float>? Aim { get; set; }
    /// <summary>Sniper breath meter: whether it shows, the breath left (0..1) and the phase. Read each frame; the owner sets it.</summary>
    public Func<(bool Show, float Meter, BreathState.BreathPhase Phase)>? Breath { get; set; }
    /// <summary>The local player's stance for the stance badge (read each frame; the badge only redraws on change).</summary>
    public Func<Stance>? StanceOf { get; set; }
    float _crossAlpha = 1;
    Label? _stanceText;
    StanceIcon? _stanceIcon;
    Stance? _stanceShown;
    Control _scope = null!;
    Control _breath = null!;
    BreathBar _breathBar = null!;
    Label _breathLabel = null!;

    Control _root = null!;
    // crosshair, hit marker, indicators
    readonly ColorRect[] _arms = new ColorRect[4], _armsBack = new ColorRect[4];
    float _gap = -1;
    Control _hit = null!, _crossRoot = null!;
    readonly Line2D[] _hitLines = new Line2D[4];
    double _hitUntil, _hitFade;
    readonly Control[] _ind = new Control[Indicators];
    readonly double[] _indAt = new double[Indicators];
    int _indNext;
    // health
    Label _hpNum = null!, _hpLow = null!;
    ProgressBar _hpBar = null!;
    int _hp = -1;
    // breath: shown only while the head is under water (fades in and out)
    Control _air = null!;
    Label _airNum = null!, _airLow = null!;
    ProgressBar _airBar = null!;
    float _airShown = -1, _breathTarget;
    // ammo
    Label _wName = null!, _mag = null!, _reserve = null!, _reloadText = null!, _speed = null!, _prompt = null!;
    ProgressBar _reloadBar = null!;
    double _reloadUntil, _reloadLen;
    // hotbar
    readonly Control[] _cell = new Control[Slots];
    readonly PanelContainer[] _slot = new PanelContainer[Slots];
    readonly Label[] _slotNum = new Label[Slots], _slotName = new Label[Slots], _slotAmmo = new Label[Slots];
    int _selected = -1;
    // top centre
    Label _timer = null!, _scoreLine = null!;
    float _timeLeft; double _timeAt; bool _timeRunning; int _timeShown = -1;
    MatchPhase _phase = MatchPhase.Waiting;
    // kill feed
    readonly PanelContainer[] _feed = new PanelContainer[FeedRows];
    readonly Label[] _feedKiller = new Label[FeedRows], _feedWeapon = new Label[FeedRows], _feedVictim = new Label[FeedRows];
    readonly List<(KillMsg Msg, string Killer, string Victim)> _kills = new();
    readonly List<double> _killTimes = new();
    (int, int) _feedShown = (-1, -1);
    // respawn, protection, hints
    Control _respawn = null!;
    Label _respawnBy = null!, _respawnIn = null!;
    double _respawnAt; bool _dead; int _respawnShown = -1;
    Control _protChip = null!, _protOutline = null!;
    Label _protText = null!;
    double _protUntil; int _protShown = -1;
    Label _hint = null!;
    Input.MouseModeEnum? _hintMode;
    // scoreboard
    Control _board = null!;
    Label _boardTitle = null!, _boardFoot = null!;
    readonly PanelContainer[] _row = new PanelContainer[BoardRows];
    readonly Label[][] _cells = new Label[BoardRows][];
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
        _scope = new ScopeOverlay { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _scope.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_scope);
        BuildCrosshair();
        BuildBreath();
        BuildHealth();
        BuildAir();
        BuildStance();
        BuildAmmo();
        BuildHotbar();
        BuildTop();
        BuildFeed();
        BuildHints();
        BuildRespawn();
        BuildBoard();
        Relayout();
        GetViewport().SizeChanged += Relayout;
        SetHealth(100);
        SetMatch(MatchPhase.Waiting, 0, null);
        RefreshHint();
    }

    /// <summary>The live settings changed (settings screen preview/apply): rescale, crosshair visibility and FOV, key hints.</summary>
    void OnSettingsChanged()
    {
        Settings = GameSettings.Current;
        Relayout();
        _gap = -1;
        _hintMode = null;
        if (_crossRoot != null) _crossRoot.Visible = Settings.ShowCrosshair;
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
        _lastSize = vp;
        var s = UiFactor();
        _root.Scale = new Vector2(s, s);
        _root.Position = Vector2.Zero;
        _root.Size = vp / s;
        _gap = -1;
    }

    float UiFactor() => Math.Max(MinBaseScale, GetViewport().GetVisibleRect().Size.Y / RefHeight) * HudLogic.ClampUiScale(Settings.UiScale);

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
            _root.AddChild(pivot); _ind[i] = pivot;
        }
    }

    /// <summary>Stance badge above the health panel: a silhouette icon and its word, so the state never rests on colour.</summary>
    void BuildStance()
    {
        var p = Place(P("HudPanel"), 0, 1, ShooterTheme.Margin, -ShooterTheme.Margin - 120, 200, 40);
        _root.AddChild(p);
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", ShooterTheme.Space);
        p.AddChild(row);
        _stanceIcon = new StanceIcon { CustomMinimumSize = new Vector2(48, 32), MouseFilter = Control.MouseFilterEnum.Ignore };
        _stanceText = L("", "HudSmall");
        row.AddChild(_stanceIcon); row.AddChild(_stanceText);
    }

    void UpdateStance()
    {
        if (StanceOf == null || _stanceIcon == null || _stanceText == null) return;
        var st = StanceOf();
        if (st == _stanceShown) return;
        _stanceShown = st;
        _stanceIcon.Stance = st;
        _stanceIcon.QueueRedraw();
        Set(_stanceText, Strings.T(HudLogic.StanceKey(st)));
    }

    void BuildHealth()
    {
        var p = Place(P("HudPanel"), 0, 1, ShooterTheme.Margin, -ShooterTheme.Margin, 320, 112);
        _root.AddChild(p);
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", 0);
        p.AddChild(v);
        var top = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddChild(top);
        top.AddChild(L(Strings.T("hud.health"), "HudSmall", minW: 96));
        _hpLow = L(Strings.T("hud.low"), "HudDanger"); _hpLow.Modulate = new Color(1, 1, 1, 0);
        top.AddChild(_hpLow);
        _hpNum = L("100", "HudDisplay", HorizontalAlignment.Left, 120);
        v.AddChild(_hpNum);
        _hpBar = new ProgressBar { MaxValue = 100, Value = 100, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 16), ThemeTypeVariation = "HealthBar", MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddChild(_hpBar);
    }

    /// <summary>Breath meter above the health panel: "AIR" and seconds left, a bar, and "LOW" in the last seconds. Fixed size; invisible when dry.</summary>
    void BuildAir()
    {
        _air = Place(P("HudPanel"), 0, 1, ShooterTheme.Margin, -ShooterTheme.Margin - 112 - ShooterTheme.Space, 320, 64);
        _root.AddChild(_air);
        _air.Modulate = new Color(1, 1, 1, 0);
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", 0);
        _air.AddChild(v);
        var top = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddChild(top);
        top.AddChild(L(Strings.T("hud.air"), "HudSmall", minW: 96));
        _airLow = L(Strings.T("hud.low"), "HudDanger"); _airLow.Modulate = new Color(1, 1, 1, 0);
        top.AddChild(_airLow);
        _airNum = L("", "HudDisplay", HorizontalAlignment.Left, 120);
        v.AddChild(_airNum);
        _airBar = new ProgressBar { MaxValue = 100, Value = 100, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 12), MouseFilter = Control.MouseFilterEnum.Ignore, Modulate = ShooterTheme.Accent };
        v.AddChild(_airBar);
    }

    /// <summary>Breath meter state: seconds of air left and whether the head is under water. Text and bar change only on a new value.</summary>
    public void SetBreath(float airLeft, bool underwater)
    {
        _breathTarget = underwater ? 1f : 0f;
        var secs = MathF.Round(Math.Clamp(airLeft, 0f, Suchohrad.ShooterCore.Breath.Capacity), 1);
        if (secs == _airShown) return;
        _airShown = secs;
        var low = HudLogic.BreathLow(secs);
        Set(_airNum, HudLogic.BreathText(secs));
        _airBar.Value = secs / Suchohrad.ShooterCore.Breath.Capacity * 100;
        _airBar.Modulate = low ? ShooterTheme.Danger : ShooterTheme.Accent; // "LOW" text carries it too, not colour alone
        Fade(_airLow, low ? 1 : 0);
    }

    /// <summary>Breath meter fade, 150 ms (instant with reduce motion).</summary>
    void AdvanceBreath(float delta)
    {
        var a = _air.Modulate.A;
        if (Mathf.IsEqualApprox(a, _breathTarget)) return;
        var step = Settings.ReduceMotion ? 1f : delta / 0.15f;
        _air.Modulate = new Color(1, 1, 1, Mathf.MoveToward(a, _breathTarget, step));
    }

    void BuildAmmo()
    {
        _speed = Place(L("", "HudBig", HorizontalAlignment.Right), 1, 1, -ShooterTheme.Margin, -ShooterTheme.Margin - 136, 320, 40);
        _speed.Visible = false; // vehicle speed readout hook: SetVehicleSpeed
        _root.AddChild(_speed);
        _prompt = Place(L("", "HudHead", HorizontalAlignment.Center), 0.5f, 0.7f, -200, 0, 400, 32); // context prompt (enter / exit a vehicle)
        _prompt.Visible = false;
        _root.AddChild(_prompt);
        var p = Place(P("HudPanel"), 1, 1, -ShooterTheme.Margin, -ShooterTheme.Margin, 320, 112);
        _root.AddChild(p);
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", 0);
        p.AddChild(v);
        _wName = L("", "HudHead", HorizontalAlignment.Right); v.AddChild(_wName);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End, MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddChild(row);
        _mag = L("", "HudDisplay", HorizontalAlignment.Right, 96);
        _reserve = L("", "HudHead", HorizontalAlignment.Left, 88); _reserve.ThemeTypeVariation = "HudDim";
        row.AddChild(_mag); row.AddChild(_reserve);
        var rl = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddChild(rl);
        _reloadText = L(Strings.T("hud.reloading"), "HudSmall", minW: 128);
        _reloadBar = new ProgressBar { MaxValue = 1, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 16), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, ThemeTypeVariation = "ReloadBar", MouseFilter = Control.MouseFilterEnum.Ignore };
        rl.AddChild(_reloadText); rl.AddChild(_reloadBar);
        Fade(_reloadText, 0); Fade(_reloadBar, 0);
    }

    void BuildHotbar()
    {
        var bar = Place(new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 0.5f, 1, 0, -ShooterTheme.Margin, Slots * 120 + (Slots - 1) * ShooterTheme.Space, 88);
        bar.AddThemeConstantOverride("separation", ShooterTheme.Space);
        _root.AddChild(bar);
        for (var i = 0; i < Slots; i++)
        {
            // fixed cell: the selected slot is taller and framed inside it, so selecting never moves a neighbour
            var cell = new Control { CustomMinimumSize = new Vector2(120, 88), MouseFilter = Control.MouseFilterEnum.Ignore };
            bar.AddChild(cell); _cell[i] = cell;
            var slot = P("HudSlot");
            cell.AddChild(slot); _slot[i] = slot;
            var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            v.AddThemeConstantOverride("separation", 0);
            slot.AddChild(v);
            var top = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            v.AddChild(top);
            _slotNum[i] = L((i + 1).ToString(), "HudAccent", minW: 24);
            _slotAmmo[i] = L("", "HudSmall", HorizontalAlignment.Right); _slotAmmo[i].SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            top.AddChild(_slotNum[i]); top.AddChild(_slotAmmo[i]);
            _slotName[i] = L("", "HudSmall"); _slotName[i].ClipText = true;
            v.AddChild(_slotName[i]);
        }
        PlaceSlots(0);
    }

    /// <summary>Unselected: 112 x 72 low in the cell; selected: full 120 x 88 cell with the strong frame.</summary>
    void PlaceSlots(int selected)
    {
        for (var i = 0; i < Slots; i++)
        {
            var sel = i == selected;
            var s = _slot[i];
            s.ThemeTypeVariation = sel ? "HudSlotSelected" : "HudSlot";
            s.OffsetLeft = sel ? 0 : 4; s.OffsetRight = sel ? 120 : 116;
            s.OffsetTop = sel ? 0 : 16; s.OffsetBottom = 88;
        }
    }

    void BuildTop()
    {
        var p = Place(P("HudPanel"), 0.5f, 0, 0, ShooterTheme.Margin, 480, 104);
        _root.AddChild(p);
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", 0);
        p.AddChild(v);
        _timer = L("", "HudDisplay", HorizontalAlignment.Center); v.AddChild(_timer);
        _scoreLine = L("", null, HorizontalAlignment.Center); _scoreLine.ClipText = true; v.AddChild(_scoreLine);
    }

    void BuildFeed()
    {
        var v = Place(new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }, 1, 0, -ShooterTheme.Margin, ShooterTheme.Margin, 480, FeedRows * 40 + (FeedRows - 1) * 4);
        v.AddThemeConstantOverride("separation", 4);
        _root.AddChild(v);
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

    void BuildHints()
    {
        _hint = Place(L("", "HudSmall"), 0, 0, ShooterTheme.Margin, ShooterTheme.Margin, 640, 24);
        _root.AddChild(_hint);
        // spawn protection chip above the hotbar (reserved space, faded in and out)
        var chip = Place(P("HudPanel"), 0.5f, 1, 0, -ShooterTheme.Margin - 88 - ShooterTheme.Space, 320, 48);
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
        v.AddChild(L(Strings.T("hud.respawn.title"), "HudHead", HorizontalAlignment.Center));
        _respawnBy = L("", "HudBig", HorizontalAlignment.Center); _respawnBy.ThemeTypeVariation = "HudDanger"; _respawnBy.ClipText = true; v.AddChild(_respawnBy);
        _respawnIn = L("", "HudDisplay", HorizontalAlignment.Center); v.AddChild(_respawnIn);
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
        v.AddChild(BoardRow(null, out var header, true));
        _ = header;
        for (var i = 0; i < BoardRows; i++)
        {
            _row[i] = BoardRow(i, out _cells[i], false);
            v.AddChild(_row[i]);
        }
        _boardFoot = L("", "HudSmall", HorizontalAlignment.Center); v.AddChild(_boardFoot);
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
        var low = hp <= HudLogic.LowHealth;
        Set(_hpNum, hp.ToString());
        _hpNum.ThemeTypeVariation = low ? "HudDisplayDanger" : "HudDisplay";
        _hpBar.Value = hp;
        _hpBar.ThemeTypeVariation = low ? "HealthBarLow" : "HealthBar";
        Fade(_hpLow, low ? 1 : 0); // "LOW" text next to the colour change
    }

    /// <summary>Hotbar names in slot order (display names, already translated).</summary>
    public void SetWeapons(IReadOnlyList<string> names)
    {
        _weapons.Count = Math.Min(Slots, names.Count);
        for (var i = 0; i < Slots; i++) { _weapons.Names[i] = i < names.Count ? names[i] : ""; Set(_slotName[i], _weapons.Names[i]); _cell[i].Modulate = new Color(1, 1, 1, i < names.Count ? 1 : 0.35f); }
    }

    /// <summary>Selected slot (1-based), its mag/reserve (mag &lt; 0: no ammo, melee), per-slot mags, and reload seconds left.</summary>
    public void SetAmmo(int slot, int mag, int reserve, IReadOnlyList<(int Mag, int Reserve, bool Melee)> all, float reloadLeft, float reloadTotal)
    {
        var sel = Math.Clamp(slot, 1, Slots) - 1;
        if (sel != _selected) { _selected = sel; PlaceSlots(sel); }
        Set(_wName, _weapons.Names[sel]);
        Set(_mag, mag < 0 ? Strings.T("hud.ammo.none") : mag.ToString());
        Set(_reserve, mag < 0 ? "" : Strings.F("hud.ammo.reserve", reserve));
        _mag.ThemeTypeVariation = mag == 0 && reserve == 0 && mag >= 0 ? "HudDisplayDanger" : "HudDisplay";
        for (var i = 0; i < Slots && i < all.Count; i++)
            Set(_slotAmmo[i], all[i].Melee ? "" : all[i].Mag.ToString());
        if (reloadLeft > 0) { _reloadLen = Math.Max(0.01, reloadTotal); _reloadUntil = Now + reloadLeft; }
        else { _reloadUntil = 0; Fade(_reloadText, 0); Fade(_reloadBar, 0); }
    }

    /// <summary>Phase, seconds left (null: unchanged clock) and the winner/ending state.</summary>
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
            _ => line.IAmLeader ? Strings.F("hud.score.leading", line.MyKills) : Strings.F("hud.score.behind", line.MyKills, line.LeaderName, line.LeaderKills),
        });
        UpdateTimer();
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
        _feedShown = (-1, -1);
    }

    public void ClearKills() { _kills.Clear(); _killTimes.Clear(); _feedShown = (-1, -1); }

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
        for (var i = 0; i < Indicators; i++) { _indAt[i] = 0; Fade(_ind[i], 0); }
    }

    /// <summary>Respawn screen: show with the killer and seconds left; <paramref name="killer"/> empty = no killer line.</summary>
    public void SetDead(bool dead, string killer = "", float secondsLeft = 0)
    {
        _dead = dead;
        _respawn.Visible = dead;
        if (!dead) return;
        _respawnAt = Now + secondsLeft; _respawnShown = -1;
        Set(_respawnBy, killer == "" ? Strings.T("hud.respawn.self") : Strings.F("hud.respawn.by", killer));
        UpdateRespawn();
    }

    void UpdateRespawn()
    {
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

    /// <summary>Vehicle speed readout (km/h); null hides it. Hook for the vehicles issue, unused by the match HUD today.</summary>
    public void SetVehicleSpeed(float? kmh)
    {
        _speed.Visible = kmh != null;
        if (kmh != null) Set(_speed, Strings.F("hud.vehicle.speed", (int)Math.Round(kmh.Value)));
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
        AdvanceBreath((float)delta);
        if (_timeRunning) UpdateTimer();
        if (_dead) UpdateRespawn();
        if (_protUntil > 0)
        {
            if (now >= _protUntil) SetProtection(0); else UpdateProtection();
        }
        if (_reloadUntil > 0)
        {
            if (now >= _reloadUntil) { _reloadUntil = 0; Fade(_reloadText, 0); Fade(_reloadBar, 0); }
            else { _reloadBar.Value = 1 - (_reloadUntil - now) / _reloadLen; Fade(_reloadText, 1); Fade(_reloadBar, 1); }
        }
        if (_hitUntil > 0) UpdateHit(now);
        for (var i = 0; i < Indicators; i++) UpdateIndicator(i, now);
        UpdateFeed(now);
        UpdateCrosshair();
        UpdateBreath();
        UpdateStance();
        RefreshHint();
        var want = ForceScoreboard ?? (Input.IsActionPressed("scoreboard") || _phase == MatchPhase.Ending);
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
        var w = HudLogic.FeedWindow(_killTimes, now);
        if (w == _feedShown) return;
        _feedShown = w;
        for (var i = 0; i < FeedRows; i++)
        {
            if (i >= w.Count) { _feed[i].Visible = false; continue; }
            var (k, killer, victim) = _kills[w.Start + i];
            var mine = k.Killer == MyId || k.Victim == MyId;
            var suicide = k.Killer == k.Victim;
            _feed[i].ThemeTypeVariation = mine ? "HudRowMine" : "HudRow";
            Set(_feedKiller[i], suicide ? victim : killer);
            Set(_feedWeapon[i], $"[{(k.Cause == KillCause.Roadkill ? Strings.T("feed.roadkill") : WeaponName(k.Weapon))}]");
            Set(_feedVictim[i], suicide ? "" : victim);
            _feed[i].Visible = true;
        }
    }

    string WeaponName(byte id)
    {
        if (id < _weapons.Count) return _weapons.Names[id];
        return id switch { SpecialWeapons.Fall => Strings.T("weapon.fall"), SpecialWeapons.Drown => Strings.T("weapon.drown"), SpecialWeapons.Void => Strings.T("weapon.void"), SpecialWeapons.Debug => Strings.T("weapon.debug"), SpecialWeapons.Vehicle => Strings.T("weapon.vehicle"), SpecialWeapons.VehicleGun => Strings.T("weapon.vehicle_gun"), SpecialWeapons.Turret => Strings.T("weapon.turret"), _ => Strings.T("weapon.unknown") };
    }

    /// <summary>Black outside a centred circle with a thin cross, drawn over the zoomed view.</summary>
    sealed partial class ScopeOverlay : Control
    {
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
            DrawCircle(c, 2f, new Color(0.9f, 0.1f, 0.1f));
        }

        public override void _Notification(int what) { if (what == NotificationResized) QueueRedraw(); }
    }

    /// <summary>Breath meter under the crosshair, shown only while scoped with a breath hold. The state is in words as well as colour.</summary>
    void BuildBreath()
    {
        const float w = 240;
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _breathLabel = L("", "HudSmall", HorizontalAlignment.Center);
        _breathBar = new BreathBar { CustomMinimumSize = new Vector2(w, 12), MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddChild(_breathLabel);
        box.AddChild(_breathBar);
        _breath = Place(box, 0.5f, 0.5f, -w / 2, 40, w, 40);
        _root.AddChild(_breath);
    }

    void UpdateBreath()
    {
        var b = Breath?.Invoke() ?? (false, 0f, BreathState.BreathPhase.Ready);
        if (_breath.Visible != b.Show) _breath.Visible = b.Show;
        if (!b.Show) return;
        _breathBar.Set(b.Meter, b.Phase);
        var key = InputBindings.Name("sprint");
        Set(_breathLabel, b.Phase switch
        {
            BreathState.BreathPhase.Holding => Strings.T("hud.breath.held"),
            BreathState.BreathPhase.Exhaled => Strings.T("hud.breath.exhaled"),
            _ => b.Meter <= 0.01f ? Strings.T("hud.breath.empty") : Strings.F("hud.breath.ready", key),
        });
    }

    /// <summary>Breath bar: fill = breath left. Blue while held, amber when low, red while forced out, green otherwise.</summary>
    sealed partial class BreathBar : Control
    {
        float _meter = 1;
        BreathState.BreathPhase _phase;

        public void Set(float meter, BreathState.BreathPhase phase)
        {
            if (Mathf.IsEqualApprox(meter, _meter) && phase == _phase) return;
            _meter = meter; _phase = phase; QueueRedraw();
        }

        public override void _Draw()
        {
            var s = Size;
            var b = ShooterTheme.Border;
            DrawRect(new Rect2(Vector2.Zero, s), ShooterTheme.Surface);
            var fill = _phase switch
            {
                BreathState.BreathPhase.Holding => ShooterTheme.Accent,
                BreathState.BreathPhase.Exhaled => ShooterTheme.Danger,
                _ => _meter < 0.25f ? ShooterTheme.Warn : ShooterTheme.Good,
            };
            DrawRect(new Rect2(b, b, Mathf.Max(0, (s.X - 2 * b) * _meter), s.Y - 2 * b), fill);
            DrawRect(new Rect2(Vector2.Zero, s), ShooterTheme.Line, false, b);
        }

        public override void _Notification(int what) { if (what == NotificationResized) QueueRedraw(); }
    }

    void UpdateCrosshair()
    {
        var scoped = Scoped?.Invoke() ?? false;
        if (_scope.Visible != scoped) { _scope.Visible = scoped; _scope.QueueRedraw(); _crossRoot.Visible = Settings.ShowCrosshair && !scoped; }
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

    void RefreshHint()
    {
        var mode = MouseModeOverride ?? Input.MouseMode;
        if (_hintMode == mode) return;
        _hintMode = mode;
        Set(_hint, mode == Input.MouseModeEnum.Captured ? Strings.F("hud.hint.captured", InputBindings.Name("scoreboard"), InputBindings.Name("slot_1"), InputBindings.Name("slot_4")) : Strings.T("hud.hint.released"));
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
            Set(_boardTitle, names == "" ? Strings.T("hud.phase.ending") : Strings.F(_winners.Count > 1 ? "score.ending.tie" : "score.ending", names));
        }
        else Set(_boardTitle, Strings.T("score.title"));
        Set(_boardFoot, Strings.T(ending ? "score.hint.next" : "score.hint.hold"));
        var sorted = HudLogic.Sort(_rows);
        // keep the local row visible even when the room exceeds the pre-built rows
        if (sorted.Count > BoardRows)
        {
            var me = sorted.FindIndex(r => r.Id == MyId);
            if (me >= BoardRows) sorted[BoardRows - 1] = sorted[me];
        }
        for (var i = 0; i < BoardRows; i++)
        {
            if (i >= sorted.Count) { _row[i].Visible = false; continue; }
            var r = sorted[i];
            var mine = r.Id == MyId;
            _row[i].ThemeTypeVariation = mine ? "HudRowMine" : "HudRow";
            var c = _cells[i];
            Set(c[0], mine ? Strings.F("score.you", r.Name) : r.Name);
            Set(c[1], r.Kills.ToString()); Set(c[2], r.Deaths.ToString()); Set(c[3], Strings.F("score.ping", r.Ping));
            _row[i].Visible = true;
        }
    }
}

/// <summary>Small shield icon for spawn protection, drawn once (the HUD's one icon set so far).</summary>
/// <summary>Stance silhouette: standing (tall), crouched (lower) and prone (flat), drawn in the HUD text colour.</summary>
public partial class StanceIcon : Control
{
    public Stance Stance { get; set; }

    public override void _Draw()
    {
        var w = Size.X; var h = Size.Y;
        var head = h * 0.14f;
        switch (Stance)
        {
            case Stance.Prone:
                DrawCircle(new Vector2(w * 0.86f, h * 0.55f), head, ShooterTheme.Text);
                DrawRect(new Rect2(w * 0.08f, h * 0.66f, w * 0.80f, h * 0.20f), ShooterTheme.Text);
                break;
            case Stance.Crouch:
                DrawCircle(new Vector2(w * 0.5f, h * 0.30f), head, ShooterTheme.Text);
                DrawRect(new Rect2(w * 0.34f, h * 0.46f, w * 0.32f, h * 0.54f), ShooterTheme.Text);
                break;
            default:
                DrawCircle(new Vector2(w * 0.5f, h * 0.14f), head, ShooterTheme.Text);
                DrawRect(new Rect2(w * 0.36f, h * 0.30f, w * 0.28f, h * 0.70f), ShooterTheme.Text);
                break;
        }
    }
}

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
