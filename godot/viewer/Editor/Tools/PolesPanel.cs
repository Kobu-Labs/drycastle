using Drycastle.MapLoader.Objects;
using Drycastle.Viewer.Edits;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>The Poles page of the sidebar: the plan and connect buttons, the palette of poles, and what the selected pole, connector or cable offers.</summary>
public sealed class PolesPanel
{
    readonly IEditorHost _host;
    readonly PolesTool _poles;
    readonly List<(AssetInfo info, Button btn)> _poleButtons = new();
    Label _noOrigin = null!, _count = null!, _selInfo = null!, _cableInfo = null!, _planInfo = null!;
    Button _connect = null!, _plan = null!, _deletePole = null!, _deleteCable = null!, _cycle = null!, _build = null!, _clear = null!;
    VBoxContainer _selBox = null!, _selCables = null!, _cableBox = null!, _planBox = null!;
    GridContainer _grid = null!;
    string _rowsKey = "";

    public PolesPanel(IEditorHost host, PolesTool poles)
    {
        _host = host;
        _poles = poles;
    }

    PolePlan Plan => _poles.Plan;

    public void Build(VBoxContainer page)
    {
        _noOrigin = Ui.Note("This map has no origin_epsg5514: export it again (map 0.4.0+).", "Danger");
        page.AddChild(_noOrigin);

        var modes = new HBoxContainer();
        _plan = Ui.FocusBtn("Plan  (P)", Plan.Toggle, "PrimaryButton");
        _plan.TooltipText = "Plan a line: left click where poles go, right click a house wall or roof to connect it, then Build poles places and connects everything along the roads";
        _connect = Ui.FocusBtn("Connect  (C)", _poles.ToggleConnecting);
        _connect.TooltipText = "By hand: click a pole, then the next pole or a house; each click strings a cable from the last pole";
        foreach (var b in new[] { _plan, _connect }) { b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; modes.AddChild(b); }
        page.AddChild(modes);
        _count = Ui.Note("");
        page.AddChild(_count);

        _planBox = new VBoxContainer { Visible = false };
        _planInfo = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore };
        _planBox.AddChild(_planInfo);
        _build = Ui.FocusBtn("Build poles  (Enter)", () => Plan.Build(), "PrimaryButton");
        _build.TooltipText = "Place a pole at every planned point, string the cables along the roads and connect the houses. One Ctrl+Z undoes all of it.";
        _clear = Ui.FocusBtn("Clear plan", Plan.Clear);
        _planBox.AddChild(_build);
        _planBox.AddChild(_clear);
        page.AddChild(_planBox);

        BuildPalette(page);
        BuildSelection(page);
    }

    /// <summary>The poles alone, as a grid of renders; a click arms the pole for placing (the Objects tool does the placing).</summary>
    void BuildPalette(VBoxContainer page)
    {
        var objects = _host.Objects;
        _grid = new GridContainer { Columns = 3 };
        _grid.AddThemeConstantOverride("h_separation", EditorTheme.Space / 2);
        _grid.AddThemeConstantOverride("v_separation", EditorTheme.Space / 2);
        page.AddChild(_grid);
        foreach (var a in _host.Map.AssetCatalog.Where(a => EditsFile.IsPoleAsset(a.Id)).OrderBy(a => a.Id, StringComparer.Ordinal))
        {
            var info = a;
            var b = new Button
            {
                // the short name shows until the render arrives (and stays if it never does)
                Text = a.Id[EditsFile.PoleAssetPrefix.Length..].Replace('_', ' '), ToggleMode = true, FocusMode = Control.FocusModeEnum.All, ClipText = true, ExpandIcon = true,
                AutowrapMode = TextServer.AutowrapMode.WordSmart, TooltipText = $"{a.Name}\n{a.Id}\nClick, then click on the ground to place it", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ThemeTypeVariation = "PaletteButton", CustomMinimumSize = new Vector2(0, 128), IconAlignment = HorizontalAlignment.Center,
            };
            b.Pressed += () => { if (objects.PlacementAsset == info.Id) objects.DisarmPlacement(); else objects.ArmPlacement(info.Id); b.ReleaseFocus(); };
            _grid.AddChild(b);
            _poleButtons.Add((a, b));
        }
        objects.Panel.ThumbnailReady += (id, tex) => { foreach (var (a, b) in _poleButtons) if (a.Id == id) { b.Icon = tex; b.Text = ""; } };
        if (_poleButtons.Count == 0) page.AddChild(Ui.Note("No pole assets in the asset manifest. Run make poles-manifest.", "Danger"));
    }

    void BuildSelection(VBoxContainer page)
    {
        _selBox = new VBoxContainer { Visible = false };
        _selBox.AddChild(new HSeparator());
        _selInfo = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore };
        _selBox.AddChild(_selInfo);
        _selCables = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _selBox.AddChild(_selCables);
        _cycle = Ui.FocusBtn("Another kind of connector", () => _poles.CycleConnector());
        _cycle.TooltipText = "Swap this house connector for the next kind made for the same surface";
        _selBox.AddChild(_cycle);
        _deletePole = Ui.FocusBtn("Delete  (Del)", () => _host.Objects.DeleteSelected(), "DangerButton");
        _deletePole.TooltipText = "Delete it and its cables (Ctrl+Z brings them back)";
        _selBox.AddChild(_deletePole);
        page.AddChild(_selBox);

        _cableBox = new VBoxContainer { Visible = false };
        _cableBox.AddChild(new HSeparator());
        _cableInfo = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore };
        _cableBox.AddChild(_cableInfo);
        _deleteCable = Ui.FocusBtn("Remove the cable  (Del)", () => _poles.DeleteSelectedCable(), "DangerButton");
        _cableBox.AddChild(_deleteCable);
        page.AddChild(_cableBox);
    }

    public void Refresh()
    {
        // what the file no longer has cannot stay selected or be the start of a cable
        _poles.DropMissing();

        var objects = _host.Objects;
        var hasOrigin = _host.Map.OriginEpsg5514 != null;
        _noOrigin.Visible = !hasOrigin;
        foreach (var (a, b) in _poleButtons) { b.SetPressedNoSignal(a.Id == objects.PlacementAsset); b.Disabled = !hasOrigin; }

        var poles = _poles.AllPoles();
        var cables = _poles.AllCables();
        var houses = objects.AllObjects().Count(o => EditsFile.IsConnectorAsset(o.Asset));
        _count.Text = $"{poles.Count} poles, {cables.Count} cables, {houses} house connectors"
            + (cables.Count(c => c.LengthM < 0) is var stale and > 0 ? $" ({stale} with a missing end)" : "");
        _plan.Text = Plan.Planning ? "Stop planning  (Esc)" : "Plan  (P)";
        _plan.Disabled = !hasOrigin;
        _connect.Text = _poles.Connecting ? "Stop  (Esc)" : "Connect  (C)";
        _connect.Disabled = !_poles.Connecting && poles.Count == 0;

        // plan mode shows the plan alone
        _planBox.Visible = Plan.Planning;
        _grid.Visible = _connect.Visible = _count.Visible = !Plan.Planning;
        if (Plan.Planning)
        {
            _planInfo.Text = Plan.Summary();
            _build.Disabled = Plan.PoleCount == 0;
            _clear.Disabled = Plan.Empty;
        }

        var pole = !Plan.Planning && objects.SelectedObject is { } so && EditsFile.IsWireAsset(so.Asset) ? so : null;
        var cable = Plan.Planning || _poles.SelectedCableId == null ? null : cables.FirstOrDefault(c => c.Id == _poles.SelectedCableId);
        _selBox.Visible = pole != null;
        _cableBox.Visible = cable != null;
        _cycle.Visible = pole != null && EditsFile.IsConnectorAsset(pole.Asset);
        if (cable != null)
            _cableInfo.Text = $"{cable.Id}: {cable.From} to {cable.To}" + (cable.LengthM >= 0 ? $", {cable.LengthM:0.0} m" : " (an end is missing)");
        var key = pole == null ? "" : pole.Id + "|" + string.Join(",", cables.Where(c => c.From == pole.Id || c.To == pole.Id).Select(c => $"{c.Id}:{c.From}:{c.To}:{c.LengthM:0.0}"));
        if (pole != null) _selInfo.Text = $"{pole.Id}  {objects.AssetName(pole.Asset)}";
        if (key == _rowsKey) return;
        _rowsKey = key;
        foreach (var c in _selCables.GetChildren()) { _selCables.RemoveChild(c); c.QueueFree(); }
        if (pole == null) return;
        foreach (var c in cables.Where(c => c.From == pole.Id || c.To == pole.Id))
            _selCables.AddChild(CableRow(c, pole.Id));
    }

    /// <summary>One cable of the selected pole: where it goes, its length and a button that removes it.</summary>
    HBoxContainer CableRow(PolesTool.CableInfo c, string poleId)
    {
        var id = c.Id;
        var row = new HBoxContainer();
        row.AddChild(new Label
        {
            Text = $"{c.Id} to {(c.From == poleId ? c.To : c.From)}" + (c.LengthM >= 0 ? $"  {c.LengthM:0.0} m" : "  (missing)"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore, ClipText = true,
        });
        var rm = Ui.FocusBtn("Remove", () => _poles.DisconnectCable(id));
        rm.TooltipText = $"Remove {c.Id} (Ctrl+Z brings it back)";
        row.AddChild(rm);
        return row;
    }
}
