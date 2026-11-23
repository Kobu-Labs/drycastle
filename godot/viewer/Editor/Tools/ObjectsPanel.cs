using Drycastle.MapLoader;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>The Objects page of the sidebar: the fields of the selected object and the palette (category tabs and a grid of renders). Every control calls a method of <see cref="ObjectsTool"/>.</summary>
public sealed class ObjectsPanel
{
    readonly IEditorHost _host;
    readonly ObjectsTool _tool;

    // the fields of the selected object
    VBoxContainer _fields = null!;
    Label _asset = null!, _status = null!, _count = null!;
    SpinBox _x = null!, _y = null!, _rot = null!, _scale = null!;
    Button _colourReset = null!;
    ColorPickerButton _colour = null!;
    bool _syncing;

    // the palette
    LineEdit _filterEdit = null!;
    readonly List<(AssetInfo info, Button btn)> _palButtons = new();
    readonly Dictionary<string, (Button btn, string sample, int count)> _palTabs = new();
    Label _palTitle = null!;
    string _palActive = "";
    string _filter = "";
    AssetThumbs? _thumbs;

    public ObjectsPanel(IEditorHost host, ObjectsTool tool)
    {
        _host = host;
        _tool = tool;
    }

    /// <summary>A render of an asset arrived (the pole palette shows its own).</summary>
    public event Action<string, Texture2D>? ThumbnailReady;

    // what the smoke reads
    public bool FieldsShown => _fields.Visible;
    /// <summary>The panel's object fields (the real controls, so the smoke can set Value and fire the signals the mouse would).</summary>
    public SpinBox XSpin => _x;
    public SpinBox YSpin => _y;
    public SpinBox RotationSpin => _rot;
    public SpinBox ScaleSpin => _scale;
    public bool ColourSet => _colourReset.Visible;

    public void Build(VBoxContainer body)
    {
        _count = Ui.Note();
        body.AddChild(_count);
        BuildFields(body);
        BuildPalette(body);
    }

    void BuildFields(VBoxContainer body)
    {
        _fields = new VBoxContainer { Visible = false };
        var head = new Label { Text = "Selected object" };
        head.ThemeTypeVariation = "Header";
        _fields.AddChild(head);
        _asset = Ui.Note("");
        _fields.AddChild(_asset);
        _x = Ui.Spin(-3e6, 3e6, 0.001, " m");
        _y = Ui.Spin(-3e6, 3e6, 0.001, " m");
        _x.ValueChanged += _ => { if (!_syncing) _tool.SetObjectPosition(_x.Value, _y.Value); };
        _y.ValueChanged += _ => { if (!_syncing) _tool.SetObjectPosition(_x.Value, _y.Value); };
        _fields.AddChild(Ui.Row("x", _x, 60));
        _fields.AddChild(Ui.Row("y", _y, 60));
        _rot = Ui.Spin(-100000, 100000, 0.1, " deg");
        _rot.ValueChanged += v => { if (!_syncing) _tool.SetObjectRotation(v); };
        _fields.AddChild(Ui.Row("rotation", _rot, 60));
        _scale = Ui.Spin(0.01, 1000, 0.01);
        _scale.ValueChanged += v => { if (!_syncing) _tool.SetObjectScale(v); };
        _fields.AddChild(Ui.Row("scale", _scale, 60));
        _colour = new ColorPickerButton { CustomMinimumSize = new Vector2(80, 28), FocusMode = Control.FocusModeEnum.None, EditAlpha = false };
        _colourReset = Ui.ResetBtn(() => _tool.SetObjectColour(null));
        // one undo step per picked colour: commit when the picker closes
        _colour.PopupClosed += () =>
        {
            var hex = "#" + _colour.Color.ToHtml(false);
            if (!_syncing && !string.Equals(_tool.SelectedObject?.Colour, hex, StringComparison.OrdinalIgnoreCase)) _tool.SetObjectColour(hex);
        };
        var cr = Ui.Row("colour", _colour, 60); cr.AddChild(_colourReset);
        _fields.AddChild(cr);
        var br = new HBoxContainer();
        var dup = Ui.Btn("Duplicate (Ctrl+D)", () => _tool.DuplicateSelected());
        var del = Ui.Btn("Delete (Del)", () => _tool.DeleteSelected());
        foreach (var b in new[] { dup, del }) { b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; br.AddChild(b); }
        _fields.AddChild(br);
        _fields.AddChild(Ui.Note("drag the object to move it (0.5 m grid), drag the ring to rotate (15 degrees); Ctrl = free; arrows nudge 0.5 m"));
        body.AddChild(_fields);
    }

    /// <summary>The palette: category tabs (a render of one item each), then a grid of item renders.</summary>
    void BuildPalette(VBoxContainer body)
    {
        var ph = new Label { Text = "Place an object", ThemeTypeVariation = "Header" };
        body.AddChild(ph);
        _status = Ui.Note("");
        body.AddChild(_status);
        _filterEdit = new LineEdit { PlaceholderText = "search all objects", ClearButtonEnabled = true };
        _filterEdit.TextChanged += t => SetPaletteFilter(t);
        body.AddChild(_filterEdit);

        var catalog = _host.Map.AssetCatalog;
        var cats = ObjectsTool.Categories.Concat(catalog.Select(a => a.Category).Distinct().Except(ObjectsTool.Categories).OrderBy(x => x, StringComparer.Ordinal)).ToList();
        var tabs = new HFlowContainer();
        tabs.AddThemeConstantOverride("h_separation", EditorTheme.Space / 2);
        tabs.AddThemeConstantOverride("v_separation", EditorTheme.Space / 2);
        body.AddChild(tabs);
        var tabGroup = new ButtonGroup();
        foreach (var cat in cats)
        {
            var items = catalog.Where(a => a.Category == cat).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (items.Count == 0) continue;
            var capture = cat;
            var tab = new Button
            {
                ToggleMode = true, ButtonGroup = tabGroup, FocusMode = Control.FocusModeEnum.None, ThemeTypeVariation = "TabButton",
                CustomMinimumSize = new Vector2(EditorTheme.RailButton, EditorTheme.RailButton), ExpandIcon = true,
                Text = items.Count == 0 ? "" : cat[..Math.Min(cat.Length, 3)], // replaced by the render when it arrives
                TooltipText = $"{Cap(cat)} ({items.Count})",
            };
            tab.Pressed += () => SetPaletteGroupOpen(capture, true);
            tabs.AddChild(tab);
            _palTabs[cat] = (tab, items[0].Id, items.Count);
        }
        _palTitle = new Label { ThemeTypeVariation = "Header" };
        body.AddChild(_palTitle);
        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", EditorTheme.Space / 2);
        grid.AddThemeConstantOverride("v_separation", EditorTheme.Space / 2);
        body.AddChild(grid);
        foreach (var a in catalog.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            var info = a;
            var b = new Button
            {
                Text = a.Name, ToggleMode = true, FocusMode = Control.FocusModeEnum.None, ClipText = true, ExpandIcon = true, AutowrapMode = TextServer.AutowrapMode.WordSmart,
                TooltipText = $"{a.Name}\n{a.Id}", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ThemeTypeVariation = "PaletteButton",
                CustomMinimumSize = new Vector2(0, 128), IconAlignment = HorizontalAlignment.Center, VerticalIconAlignment = VerticalAlignment.Top,
            };
            b.Pressed += () => { if (_tool.PlacementAsset == info.Id) _tool.DisarmPlacement(); else _tool.ArmPlacement(info.Id); };
            grid.AddChild(b);
            _palButtons.Add((a, b));
        }
        _palActive = cats.FirstOrDefault(c => _palTabs.ContainsKey(c)) ?? "";
        ApplyPaletteLayout();
        if (DisplayServer.GetName() != "headless") StartThumbnails(catalog);
    }

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    void StartThumbnails(IReadOnlyList<AssetInfo> catalog)
    {
        _thumbs = new AssetThumbs { Source = id => _host.Map.InstantiateAsset(id) };
        _host.AddChild(_thumbs);
        _thumbs.Ready += (id, tex) =>
        {
            foreach (var (a, b) in _palButtons)
                if (a.Id == id)
                {
                    b.Icon = tex;
                    if (ObjectsTool.IconOnlyCategories.Contains(a.Category)) b.Text = "";
                }
            ThumbnailReady?.Invoke(id, tex);
            foreach (var (_, t) in _palTabs) if (t.sample == id) { t.btn.Icon = tex; t.btn.Text = ""; }
        };
        // the visible category first, then the rest
        _thumbs.Request(catalog.Where(a => a.Category == _palActive).Select(a => a.Id).Concat(catalog.Select(a => a.Id)));
    }

    /// <summary>Show one category of the palette (open = true); false does nothing, a category is always shown.</summary>
    public void SetPaletteGroupOpen(string category, bool open)
    {
        if (!open || !_palTabs.ContainsKey(category)) return;
        _palActive = category;
        ApplyPaletteLayout();
    }

    public void SetPaletteFilter(string text)
    {
        _filter = text.Trim();
        if (_filterEdit.Text != text) _filterEdit.Text = text;
        ApplyPaletteLayout();
    }

    static bool Matches(AssetInfo a, string f)
        => f == "" || a.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || a.Id.Contains(f, StringComparison.OrdinalIgnoreCase)
           || a.Category.Contains(f, StringComparison.OrdinalIgnoreCase);

    void ApplyPaletteLayout()
    {
        foreach (var (a, b) in _palButtons) b.Visible = _filter == "" ? a.Category == _palActive : Matches(a, _filter);
        foreach (var (cat, t) in _palTabs) t.btn.SetPressedNoSignal(_filter == "" && cat == _palActive);
        var n = _palButtons.Count(p => p.btn.Visible);
        _palTitle.Text = _filter == "" ? $"{Cap(_palActive)} ({n})" : n == 0 ? $"Nothing matches \"{_filter}\"" : $"{n} match \"{_filter}\"";
    }

    /// <summary>The palette as listed: every manifest asset with its category and name; Visible is false when the filter or category hides it.</summary>
    public IReadOnlyList<ObjectsTool.PaletteEntry> Palette => _palButtons.Select(p => new ObjectsTool.PaletteEntry(p.info.Category, p.info.Id, p.info.Name, p.btn.Visible)).ToList();
    public string PaletteGroupTitle(string category) => _palTabs.TryGetValue(category, out var t) ? $"{category} ({t.count})" : "";
    public bool PaletteGroupShown(string category) => _palTabs.ContainsKey(category) && (_filter == "" || _palButtons.Any(p => p.info.Category == category && p.btn.Visible));
    public bool PaletteGroupBodyShown(string category) => _filter == "" ? category == _palActive : _palButtons.Any(p => p.info.Category == category && p.btn.Visible);

    public void Refresh()
    {
        var o = _tool.SelectedObject;
        _fields.Visible = o != null;
        if (o != null) ShowObject(o);
        var placing = _tool.PlacementAsset;
        _status.Text = placing != null
            ? $"placing {_tool.AssetName(placing)}, rotation {_tool.GhostRotation:0} deg. Click to place, R / Shift+R rotate, Esc stops."
            : "Choose an asset to place it; click an object to select it.";
        foreach (var (a, b) in _palButtons) b.SetPressedNoSignal(a.Id == placing);
        var parcels = _host.Selection.ToList();
        var inFile = _tool.ObjectCount;
        _count.Text = parcels.Count == 0
            ? $"{inFile} placed objects in the file"
            : $"{_tool.AllObjects().Count(x => parcels.Contains(x.Parcel))} placed objects on {(parcels.Count == 1 ? parcels[0] : "the selection")}, {inFile} in the file";
    }

    void ShowObject(ObjectsTool.ObjectInfo o)
    {
        _syncing = true;
        try
        {
            _asset.Text = $"{o.Id}  {_tool.AssetName(o.Asset)}\n{o.Asset}\n{(o.Parcel == "" ? "on no parcel" : $"in {o.Parcel}")}";
            _x.SetValueNoSignal(o.X); _y.SetValueNoSignal(o.Y);
            _rot.SetValueNoSignal(o.Rotation); _scale.SetValueNoSignal(o.Scale);
            _colourReset.Visible = o.Colour != null;
            _colour.Color = Color.FromHtml(o.Colour ?? "#cccccc");
            _colour.Modulate = o.Colour == null ? EditorTheme.Dim : Colors.White;
        }
        finally { _syncing = false; }
    }
}
