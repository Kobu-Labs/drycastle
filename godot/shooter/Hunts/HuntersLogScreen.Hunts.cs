using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Logs;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Hunts;

namespace Drycastle.Shooter.Hunts;

/// <summary>
/// The All hunts tab (docs/hunters-log.md "All hunts"): filters, a sortable table of one page (50 rows, pooled: built once, refilled by each answer), a pager, and the hit card of
/// the selected row beside the table (under it when the panel is narrow). The state of the query is <see cref="HuntTableState"/>; this class draws it and sends changes back.
/// </summary>
public sealed partial class HuntersLogScreen
{
    /// <summary>The table's columns, left to right, with their share of the width.</summary>
    static readonly (HuntSort Sort, string Label, float Ratio, bool Number)[] Columns =
    {
        (HuntSort.Time, "log.col.when", 3, false), (HuntSort.Hunter, "log.col.player", 3, false), (HuntSort.Species, "log.col.species", 3, false),
        (HuntSort.Kg, "log.col.weight", 2, true), (HuntSort.Distance, "log.col.distance", 2, true), (HuntSort.Weapon, "log.col.weapon", 3, false),
    };

    sealed class RowCells
    {
        public ContentButton Btn = null!;
        public Label When = null!, Species = null!, Kg = null!, Distance = null!, Weapon = null!;
        public PlayerLink Player = null!;
        public AnimalPicture Icon = null!;
        public BlurText Blur = null!;
        public RecordBadge Pill = null!;
        public Control PlayerCell = null!;
    }

    readonly OptionButton _fSpecies = new(), _fPlayer = new();
    readonly List<string> _speciesKeys = new();
    readonly List<long> _playerIds = new();
    string _speciesSig = "";
    int _peopleSig = -1;
    Button _fClear = null!;
    readonly Button[] _heads = new Button[Columns.Length];
    readonly List<RowCells> _rows = new();
    VBoxContainer _rowsBox = null!;
    Control _tableState = null!;
    readonly Control _tableStateHost = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
    ScrollContainer _rowsScroll = null!;
    BoxContainer _body = null!;
    Button _prev = null!, _next = null!;
    readonly Button[] _pageBtns = new Button[9];
    Label _range = null!;
    int _selected = -1;
    long _viewer = -1; // the viewer the shown rows were tinted for

    // the hit card
    PanelContainer _hit = null!, _ledger = null!;
    Control _hitBox = null!, _hitFigure = null!;
    AnimalPicture _hitPic = null!;
    Control _hitHidden = null!;
    Label _hitName = null!, _hitKg = null!, _hitNone = null!;
    VBoxContainer _hitFacts = null!;
    Control _headPad = null!;
    BlurText _hitBlur = null!;

    void BuildHuntsPane()
    {
        var host = NewHost(_panes[2]);

        // filters
        var filters = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        filters.AddThemeConstantOverride("h_separation", ShooterTheme.Space * 2);
        filters.AddThemeConstantOverride("v_separation", ShooterTheme.Space);
        filters.AddChild(Field("log.filter.species", _fSpecies));
        filters.AddChild(Field("log.filter.player", _fPlayer));
        _fClear = Ui.Btn(Strings.T("log.filter.clear"), ClearFilters);
        _fClear.Visible = false;
        filters.AddChild(_fClear);
        host.AddChild(filters);
        _fSpecies.ItemSelected += i => { if (i >= 0 && i < _speciesKeys.Count && _speciesKeys[(int)i] != Data.Table.Species) { Data.Table.SetSpecies(_speciesKeys[(int)i]); AskHunts(); } };
        _fPlayer.ItemSelected += i => { if (i >= 0 && i < _playerIds.Count && _playerIds[(int)i] != Data.Table.PlayerId) { Data.Table.SetPlayer(_playerIds[(int)i]); AskHunts(); } };

        // the table beside (or over) the hit card
        _body = new BoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        _body.AddThemeConstantOverride("separation", ShooterTheme.Space);
        host.AddChild(_body);
        _body.AddChild(BuildTable());
        BuildHitCard();
        _body.AddChild(_hit);
    }

    Control Field(string captionKey, OptionButton ob)
    {
        var h = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        h.AddChild(new Label { Text = Strings.T(captionKey), ThemeTypeVariation = "HudSmall", VerticalAlignment = VerticalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore });
        ob.FocusMode = FocusModeEnum.All;
        ob.MouseDefaultCursorShape = CursorShape.PointingHand;
        ob.CustomMinimumSize = new Vector2(ShooterTheme.LogFilterW, 0);
        ob.ClipText = true;
        h.AddChild(ob);
        return h;
    }

    Control BuildTable()
    {
        var outer = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        outer.AddThemeConstantOverride("separation", ShooterTheme.Space + ShooterTheme.Space / 2);
        // the table is a ledger page: the header and the ruled rows on one paper; a state box (loading, empty, error) stands on the board in the paper's place
        _ledger = new PanelContainer { ThemeTypeVariation = "LogLedger", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        var table = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        table.AddThemeConstantOverride("separation", 0);
        _ledger.AddChild(table);
        outer.AddChild(_ledger);

        var head = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        head.AddThemeConstantOverride("separation", 0);
        for (var c = 0; c < Columns.Length; c++)
        {
            var col = Columns[c];
            var b = new Button
            {
                ThemeTypeVariation = "LogHead", Text = Strings.T(col.Label), FocusMode = FocusModeEnum.All, ExpandIcon = true,
                IconAlignment = HorizontalAlignment.Right, Alignment = col.Number ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = col.Ratio, MouseDefaultCursorShape = CursorShape.PointingHand,
                TooltipText = Strings.F("log.sort.tip", Strings.T(col.Label)), Icon = HuntArt.Arrow(null),
            };
            b.CustomMinimumSize = new Vector2(ShooterTheme.LogColMin[c], ShooterTheme.Space * 5);
            var sort = col.Sort;
            b.Pressed += () => { Sfx.Ui("ui.click"); Data.Table.ClickHeader(sort); AskHunts(); };
            _heads[c] = b;
            head.AddChild(b);
        }
        // the rows' scrollbar takes width the header does not have: a spacer of the same width keeps every header over its column
        _headPad = new Control { MouseFilter = MouseFilterEnum.Ignore };
        head.AddChild(_headPad);
        table.AddChild(head);

        // the rows scroll, the header and the pager do not; the state box (loading, empty, error) takes the rows' place
        _rowsScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true, SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, ShooterTheme.LogRowH * 6), MouseFilter = MouseFilterEnum.Pass };
        _rowsBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        _rowsBox.AddThemeConstantOverride("separation", 0);
        _rowsScroll.AddChild(_rowsBox);
        var bar = _rowsScroll.GetVScrollBar();
        void SyncPad() => _headPad.CustomMinimumSize = new Vector2(bar.Visible ? bar.GetCombinedMinimumSize().X : 0, 0);
        bar.VisibilityChanged += SyncPad;
        table.AddChild(_rowsScroll);
        _tableStateHost.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        outer.AddChild(_tableStateHost);

        // the pager
        var pager = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        var numbers = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        numbers.AddThemeConstantOverride("h_separation", ShooterTheme.Space / 2);
        _prev = Ui.Btn(Strings.T("log.page.prev"), () => GoToPage(Data.Table.Page - 1));
        _next = Ui.Btn(Strings.T("log.page.next"), () => GoToPage(Data.Table.Page + 1));
        numbers.AddChild(_prev);
        for (var i = 0; i < _pageBtns.Length; i++)
        {
            var b = Ui.Btn("", () => { });
            b.ToggleMode = true;
            b.CustomMinimumSize = new Vector2(ShooterTheme.LogPageButton, b.CustomMinimumSize.Y);
            var slot = i;
            b.Pressed += () => { if (b.GetMeta("page", -1).AsInt32() is >= 0 and var p) GoToPage(p); };
            _pageBtns[slot] = b;
            numbers.AddChild(b);
        }
        numbers.AddChild(_next);
        pager.AddChild(numbers);
        _range = Text("", "HudSmall", HorizontalAlignment.Right);
        _range.VerticalAlignment = VerticalAlignment.Center;
        pager.AddChild(_range);
        outer.AddChild(pager);
        return outer;
    }

    void BuildHitCard()
    {
        _hit = new LogPaper(Skin, "LogHit") { CustomMinimumSize = new Vector2(ShooterTheme.LogHitW, 0), SizeFlagsVertical = SizeFlags.ShrinkBegin };
        var outer = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _hit.AddChild(outer);
        var v = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", ShooterTheme.Space / 2);
        _hitBox = v;
        outer.AddChild(v);
        var artSize = new Vector2(ShooterTheme.LogHitW - 5 * ShooterTheme.Space, ShooterTheme.LogHitArtH);
        _hitPic = PaperPicture(artSize);
        _hitFigure = Figure(_hitPic);
        _hitHidden = HiddenFigure(artSize, caught: true);
        v.AddChild(_hitFigure); v.AddChild(_hitHidden);

        var title = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _hitName = Text("", "InkHead"); _hitName.ClipText = true; _hitName.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _hitBlur = new BlurText(Strings.T("hunt.hidden.name"), "InkHead");
        _hitKg = Text("", "InkHead");
        title.AddChild(_hitName); title.AddChild(_hitBlur); title.AddChild(_hitKg);
        v.AddChild(title);
        _hitFacts = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        v.AddChild(_hitFacts);
        _hitNone = Text(Strings.T("log.hit.none"), "InkDimSmall", wrap: true);
        v.AddChild(_hitNone);
        ShowHit(null);
    }

    /// <summary>The panel is narrow (the UI scale is large, the window small): the hit card goes under the table instead of beside it.</summary>
    void OnPanelResized()
    {
        var stack = _panel.Size.X < ShooterTheme.LogStackBelow;
        if (_body.Vertical != stack)
        {
            _body.Vertical = stack;
            _hit.CustomMinimumSize = new Vector2(stack ? 0 : ShooterTheme.LogHitW, 0);
            _rowsScroll.CustomMinimumSize = new Vector2(0, ShooterTheme.LogRowH * (stack ? 8 : 6));
        }
        // the weapon column goes first when the table cannot hold every column at its minimum (the hit card names the weapon)
        var width = _panel.Size.X - (stack ? 0 : ShooterTheme.LogHitW) - 6 * ShooterTheme.Space;
        var needed = ShooterTheme.LogColMin.Sum() + (Columns.Length - 1) * ShooterTheme.Space;
        SetWeaponColumn(width >= needed);
    }

    bool _weaponCol = true;

    void SetWeaponColumn(bool on)
    {
        if (on == _weaponCol) return;
        _weaponCol = on;
        _heads[Columns.Length - 1].Visible = on;
        foreach (var r in _rows) r.Weapon.Visible = on;
    }

    // ---- the data ----

    /// <summary>A filter, a sort or a page changed: ask again. The rows held stay (dimmed) until the answer comes, so the table does not jump.</summary>
    void AskHunts()
    {
        _selected = -1;
        Data.RequestHunts(drop: false);
    }

    void GoToPage(int page)
    {
        Sfx.Ui("ui.click");
        Data.Table.SetPage(page);
        AskHunts();
    }

    void ClearFilters()
    {
        Sfx.Ui("ui.click");
        Data.Table.ClearFilters();
        AskHunts();
    }

    void FocusFirstRow()
    {
        if (!_open) return;
        if (_rows.Count > 0 && _rows[0].Btn.Visible) _rows[0].Btn.GrabFocus();
        else _heads[0].GrabFocus();
    }

    /// <summary>Draws the tab from what the loaders hold: filters, headers, rows or a state box, pager, hit card.</summary>
    void RefreshHunts()
    {
        SyncFilters();
        var t = Data.Table;
        for (var c = 0; c < Columns.Length; c++)
        {
            var on = Columns[c].Sort == t.Sort;
            _heads[c].ThemeTypeVariation = on ? "LogHeadSorted" : "LogHead";
            _heads[c].Icon = HuntArt.Arrow(on ? !t.Descending : null); // an up arrow ascending, a down one descending: the shape says it, not the colour
        }
        _fClear.Visible = t.Filtered;

        var view = Data.Hunts.Show;
        var page = Data.Hunts.Data;
        var rows = view == LoadView.Data && page != null ? page.Rows : null;
        var loading = Data.Hunts.Phase == HuntLoad.Loading;
        var dimmed = loading && rows != null;
        _rowsBox.Modulate = dimmed ? new Color(1, 1, 1, ShooterTheme.DisabledAlpha) : Colors.White;

        // rows or a state box in their place
        ClearStateBox();
        if (rows is { Count: > 0 })
        {
            EnsureRows(rows.Count);
            for (var i = 0; i < _rows.Count; i++)
            {
                if (i < rows.Count) FillRow(i, rows[i]);
                else _rows[i].Btn.Visible = false;
            }
            if (_selected < 0 || _selected >= rows.Count) _selected = 0;
        }
        else
        {
            foreach (var r in _rows) r.Btn.Visible = false;
            _selected = -1;
            switch (view)
            {
                case LoadView.Spinner:
                    ShowStateBox(StateBox(Strings.T("log.loading"), Strings.T("log.loading.body"), spinner: true));
                    break;
                case LoadView.Error or LoadView.Missing:
                    ShowStateBox(StateBox(Strings.T("log.error"), Strings.T("log.error.body"), actionText: Strings.T("log.retry"), action: () => { _selected = -1; Data.RequestHunts(drop: false); }));
                    break;
                default:
                    ShowStateBox(t.Filtered
                        ? StateBox(Strings.T("log.empty.hunts"), Strings.T("log.empty.hunts.body"), actionText: Strings.T("log.filter.clear"), action: ClearFilters)
                        : StateBox(Strings.T("log.empty.book"), ""));
                    break;
            }
        }
        _ledger.Visible = _tableState == null;
        _tableStateHost.Visible = _tableState != null; // the box takes the rows' room only while it shows
        SyncSelection(rows);
        RefreshPager(page, rows?.Count ?? 0);
    }

    void ShowStateBox(Control box) { _tableState = box; _tableStateHost.AddChild(box); }

    void ClearStateBox()
    {
        if (_tableState == null) return;
        _tableStateHost.RemoveChild(_tableState);
        _tableState.QueueFree();
        _tableState = null!;
    }

    void SyncSelection(IReadOnlyList<HuntView>? rows)
    {
        for (var i = 0; i < _rows.Count; i++) _rows[i].Btn.SetPressedNoSignal(i == _selected);
        ShowHit(rows != null && _selected >= 0 && _selected < rows.Count ? rows[_selected] : null);
    }

    void SelectRow(int i)
    {
        if (Data.Hunts.Data is not { } page || i < 0 || i >= page.Rows.Count) return;
        Sfx.Ui("ui.click");
        _selected = i;
        SyncSelection(page.Rows);
    }

    // ---- rows ----

    void EnsureRows(int count)
    {
        while (_rows.Count < count)
        {
            var index = _rows.Count;
            var cells = NewRow();
            cells.Weapon.Visible = _weaponCol;
            cells.Btn.Pressed += () => SelectRow(index);
            cells.Btn.GuiInput += e => RowInput(index, e);
            cells.Btn.MouseExited += () => cells.Player.Hot = false;
            _rows.Add(cells);
            _rowsBox.AddChild(cells.Btn);
        }
    }

    RowCells NewRow()
    {
        var r = new RowCells();
        r.Btn = new ContentButton { ThemeTypeVariation = "LogRow", ToggleMode = true, MinSize = new Vector2(0, ShooterTheme.LogRowH), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var h = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        h.AddThemeConstantOverride("separation", ShooterTheme.Space);
        r.When = Cell("InkSmall", HorizontalAlignment.Left, Columns[0].Ratio);
        // player: a person icon and the name as a link (underlined, filled when the pointer is over it), so it reads as something to click
        r.Player = new PlayerLink { SizeFlagsHorizontal = SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        var who = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = Columns[1].Ratio, MouseFilter = MouseFilterEnum.Ignore };
        who.AddThemeConstantOverride("separation", ShooterTheme.Space / 2);
        who.AddChild(new TextureRect { Texture = HuntArt.Person, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = MouseFilterEnum.Ignore, Modulate = Skin.InkDim,
            CustomMinimumSize = new Vector2(ShooterTheme.LogStatIcon, ShooterTheme.LogStatIcon), SizeFlagsVertical = SizeFlags.ShrinkCenter });
        who.AddChild(r.Player);
        r.PlayerCell = who;
        // species: the name (or the blurred placeholder), then the record seal
        // the record seal floats over the cell's right end, in no layout: a strip of the cell is reserved for it so the name is never covered, and a seal cannot move the columns
        var species = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = Columns[2].Ratio, MouseFilter = MouseFilterEnum.Ignore };
        var names = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        names.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        names.OffsetRight = -ShooterTheme.LogSealStrip;
        names.AddThemeConstantOverride("separation", ShooterTheme.Space / 2);
        species.AddChild(names);
        r.Icon = new AnimalPicture(ShooterTheme.LogRowArt) { FillInk = Skin.InkDim, OutlineInk = Skin.InkDim, SizeFlagsVertical = SizeFlags.ShrinkCenter, ClipContents = true, MouseFilter = MouseFilterEnum.Ignore };
        names.AddChild(r.Icon);
        r.Species = Cell("InkSmall", HorizontalAlignment.Left, 1);
        r.Blur = new BlurText(Strings.T("hunt.hidden.name"), "InkSmall") { Visible = false };
        r.Pill = new RecordBadge(Skin) { Visible = false };
        names.AddChild(r.Species); names.AddChild(r.Blur);
        r.Pill.SetAnchorsPreset(Control.LayoutPreset.CenterRight);
        r.Pill.GrowHorizontal = Control.GrowDirection.Begin; r.Pill.GrowVertical = Control.GrowDirection.Both;
        r.Pill.OffsetRight = -ShooterTheme.Space / 2;
        species.AddChild(r.Pill);
        r.Kg = Cell("InkSmall", HorizontalAlignment.Right, Columns[3].Ratio);
        r.Distance = Cell("InkSmall", HorizontalAlignment.Right, Columns[4].Ratio);
        r.Weapon = Cell("InkSmall", HorizontalAlignment.Left, Columns[5].Ratio);
        h.AddChild(r.When); h.AddChild(who); h.AddChild(species); h.AddChild(r.Kg); h.AddChild(r.Distance); h.AddChild(r.Weapon);
        // no cell is narrower than its column's minimum, so none is cut and the cells stay under their headers
        Control[] cells = { r.When, who, species, r.Kg, r.Distance, r.Weapon };
        for (var c = 0; c < cells.Length; c++) cells[c].CustomMinimumSize = new Vector2(ShooterTheme.LogColMin[c], 0);
        r.Btn.SetContent(h);
        return r;
    }

    static Label Cell(string variation, HorizontalAlignment align, float ratio) => new()
    {
        ThemeTypeVariation = variation, HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Center, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = ratio, MouseFilter = MouseFilterEnum.Ignore,
    };

    void FillRow(int i, HuntView h)
    {
        var r = _rows[i];
        var mine = Data.ViewerKnown && Data.ViewerId != 0 && h.PlayerId == Data.ViewerId;
        var variation = mine ? "LogRowMine" : "LogRow";
        if (r.Btn.ThemeTypeVariation != variation) r.Btn.ThemeTypeVariation = variation;
        r.When.Text = AgoText(h.AtUnix);
        r.Player.Text = mine ? Strings.F("score.you", h.Hunter) : h.Hunter; // a tinted row also says so in words
        r.Species.Visible = !h.Hidden;
        r.Icon.Visible = !h.Hidden;
        if (!h.Hidden) r.Icon.SetSpecies(_env.Art.ForSpecies(h.Exact != "" ? h.Exact : h.Species), filled: true);
        r.Blur.Visible = h.Hidden;
        r.Species.Text = h.Hidden ? "" : HuntSpeciesName(h);
        var mark = HuntRecords.MarkOf(Data.Records.Data?.Records, h);
        r.Pill.Set(mark);
        r.Pill.TooltipText = mark == RecordMark.Weight ? Strings.T("log.pill.kg") : mark == RecordMark.Shot ? Strings.T("log.pill.shot") : "";
        r.Kg.Text = Kg(h.Kg);
        r.Distance.Text = Metres(h.DistanceM);
        r.Weapon.Text = _env.WeaponName(h.Weapon);
        r.Btn.Visible = true;
    }

    /// <summary>The pointer over a player's name lights the name (its own hover state: a border); a click on it opens that player's log; a click anywhere else on the row selects it.</summary>
    void RowInput(int i, InputEvent e)
    {
        if (i >= _rows.Count) return;
        var link = _rows[i].Player;
        if (e is InputEventMouseMotion mm)
        {
            var over = OverName(link, mm.GlobalPosition);
            _rows[i].Player.Hot = over;
            _rows[i].Btn.TooltipText = over && Data.Hunts.Data is { } pg && i < pg.Rows.Count ? Strings.F("log.hit.open", pg.Rows[i].Hunter) : "";
            return;
        }
        if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb || Data.Hunts.Data is not { } page || i >= page.Rows.Count) return;
        if (!OverName(link, mb.GlobalPosition)) return;
        _rows[i].Btn.AcceptEvent();
        var h = page.Rows[i];
        OpenPlayerLog(h.PlayerId, h.Hunter);
    }

    /// <summary>Whether a screen position is over the name's text itself (not its column: a click beside the name only selects the row).</summary>
    static bool OverName(PlayerLink name, Vector2 global) => name.NameRect().HasPoint(name.GetGlobalTransformWithCanvas().AffineInverse() * global);

    // ---- the hit card ----

    void ShowHit(HuntView? h)
    {
        _hit.Visible = h != null; // nothing selected: no card, no hint
        _hitBox.Visible = h != null;
        if (h == null) return;
        _hitFigure.Visible = !h.Hidden;
        _hitHidden.Visible = h.Hidden;
        if (!h.Hidden)
        {
            var spec = HuntPicture.Resolve(_env.Art.Shapes, h);
            _hitPic.SetHit(_env.Art.Art(spec.Standing, spec.SpeciesId), spec.HasMark, spec.Along, spec.Height);
        }
        _hitName.Visible = !h.Hidden;
        _hitBlur.Visible = h.Hidden;
        _hitName.Text = h.Hidden ? "" : HuntSpeciesName(h);
        _hitKg.Text = h.Kg is { } kg ? Strings.F("log.kg", HuntFormat.Kg(kg)) : "";
        Clear(_hitFacts);
        var id = h.PlayerId; var name = h.Hunter;
        _hitFacts.AddChild(HitFacts(h, openHunter: () => OpenPlayerLog(id, name), full: true));
        _hitNone.Visible = h.Diagram == null;
    }

    // ---- filters and pager ----

    void SyncFilters()
    {
        var t = Data.Table;
        // species: the index species, the Mystery creatures the viewer has caught, and the one "Mystery hunts" option while hidden hunts exist
        var options = new List<(string Key, string Name)> { ("", Strings.T("log.filter.all")) };
        foreach (var s in IndexSpecies.All) options.Add((s, _env.SpeciesName(s)));
        if (Data.Records.Data?.Records is { } cards)
        {
            foreach (var c in cards) if (c.Caught && !c.Hidden && Mysteries.Is(c.Species)) options.Add((c.Species, _env.SpeciesName(c.Species)));
            if (cards.Any(c => c.Caught && c.Hidden)) options.Add((Mysteries.AllHidden, Strings.T("log.filter.mystery")));
        }
        if (t.Species != "" && options.All(o => o.Key != t.Species)) options.Add((t.Species, t.Species == Mysteries.AllHidden ? Strings.T("log.filter.mystery") : _env.SpeciesName(t.Species)));
        var sig = string.Join("|", options.Select(o => o.Key));
        if (sig != _speciesSig)
        {
            _speciesSig = sig;
            _speciesKeys.Clear(); _fSpecies.Clear();
            foreach (var (k, n) in options) { _speciesKeys.Add(k); _fSpecies.AddItem(n); }
        }
        _fSpecies.Select(Math.Max(0, _speciesKeys.IndexOf(t.Species)));

        // players: those the log has seen (the server sends no list of them)
        var people = Data.People;
        if (people.Count != _peopleSig)
        {
            _peopleSig = people.Count;
            _playerIds.Clear(); _fPlayer.Clear();
            _playerIds.Add(0); _fPlayer.AddItem(Strings.T("log.filter.allplayers"));
            foreach (var (id, name) in people.Sorted()) { _playerIds.Add(id); _fPlayer.AddItem(name); }
        }
        if (t.PlayerId != 0 && !_playerIds.Contains(t.PlayerId)) { _playerIds.Add(t.PlayerId); _fPlayer.AddItem(people.NameOf(t.PlayerId) is { Length: > 0 } n ? n : $"#{t.PlayerId}"); }
        _fPlayer.Select(Math.Max(0, _playerIds.IndexOf(t.PlayerId)));
    }

    void RefreshPager(HuntsAnswer? page, int rowsShown)
    {
        var total = page?.Total ?? 0;
        var cur = Data.Table.Page;
        var pages = HuntTableState.PageCount(total);
        var window = HuntTableState.PageWindow(cur, pages);
        for (var i = 0; i < _pageBtns.Length; i++)
        {
            var b = _pageBtns[i];
            if (i >= window.Count) { b.Visible = false; continue; }
            b.Visible = true;
            var p = window[i];
            b.SetMeta("page", p);
            b.Text = p < 0 ? Strings.T("log.gap") : (p + 1).ToString();
            b.Disabled = p < 0;
            b.TooltipText = p < 0 ? "" : Strings.F("log.page.tip", p + 1);
            b.SetPressedNoSignal(p == cur);
        }
        _prev.Disabled = cur <= 0;
        _next.Disabled = cur >= pages - 1;
        var (from, to) = HuntTableState.Range(cur, rowsShown);
        _range.Text = rowsShown > 0 ? Strings.F("log.range", from, to, total) : "";
    }
}
