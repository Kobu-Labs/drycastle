using Drycastle.ShooterCore.Editor;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// The sidebar page of the Photo to building tool: the building, its photos (captures and your own), the Build button and its progress, the review of a result, and the live
/// adjustments (form and openings). It shows what <see cref="PhotoTool"/> holds and calls its actions; nothing here writes a file. Colours and sizes come from
/// <see cref="EditorTheme"/> (theme type variations only).
/// </summary>
public sealed class PhotoPanel
{
    const string CaptureCommand = "cd pipeline && uv run python -m drycastle_pipeline.photos.streetview_follow --launch --capture";
    static readonly Vector2 ThumbSize = new(EditorTheme.Space * 14, EditorTheme.Space * 8);
    static readonly Vector2 ReviewSize = new(EditorTheme.Space * 19, EditorTheme.Space * 11);
    static readonly Dictionary<string, ImageTexture?> Thumbs = new();

    readonly IEditorHost _host;
    readonly PhotoTool _t;

    public PhotoPanel(IEditorHost host, PhotoTool tool) => (_host, _t) = (host, tool);

    // top
    Label _noBuilding = null!, _buildingOne = null!;
    Button _copyIds = null!;
    VBoxContainer _buildingList = null!;
    string _buildingSig = "";
    // photos
    VBoxContainer _photosSec = null!, _photoList = null!;
    Label _photoEmpty = null!, _photoNote = null!;
    Button _copyCmd = null!, _addBtn = null!;
    string _photoSig = "";
    FileDialog? _dialog;
    // build
    VBoxContainer _buildSec = null!, _runBox = null!, _failBox = null!;
    PanelContainer _keyBanner = null!;
    Button _recheck = null!, _buildBtn = null!, _cancelBtn = null!, _tryAgain = null!, _replaceBtn = null!, _failBack = null!;
    Label _blocker = null!, _stageLabel = null!, _timerLabel = null!, _logLabel = null!, _failText = null!, _failLog = null!;
    ProgressBar _bar = null!;
    // review
    VBoxContainer _reviewSec = null!, _reviewList = null!;
    Label _reviewNote = null!, _reviewStats = null!;
    Button _askEnds = null!, _askWalls = null!, _newBuild = null!;
    string _reviewSig = "";
    // adjust
    VBoxContainer _adjustSec = null!, _formBox = null!, _openingList = null!, _openingForm = null!;
    Label _adjustState = null!, _noEntry = null!, _detailError = null!, _openingHead = null!, _openingsEmpty = null!, _measuredNote = null!;
    Button _retryForm = null!, _resetBtn = null!, _dupBtn = null!, _delBtn = null!;
    OptionButton _roof = null!, _wallColour = null!, _roofColour = null!, _frameColour = null!, _surroundColour = null!, _kind = null!, _wall = null!;
    SpinBox _wallTop = null!, _top = null!, _overhang = null!, _plinth = null!, _along = null!, _width = null!, _height = null!, _sill = null!;
    CheckBox _plinthOn = null!, _ridgeCaps = null!, _roofTiles = null!, _gutter = null!, _corners = null!, _bands = null!, _interior = null!;
    readonly Dictionary<OptionButton, List<string>> _hexes = new();
    string _openingSig = "";
    bool _refreshing; // a refresh moves the fields (a new maximum clamps a value): that is no edit

    static readonly (string Value, string Text)[] Roofs = { ("flat", "Flat"), ("gabled", "Gabled"), ("hipped", "Hipped") };
    static readonly (string Value, string Text)[] Kinds = { ("window", "Window"), ("door", "Door"), ("garage_door", "Garage door"), ("recess", "Recess") };

    // ---- building the page ----

    public void Build(VBoxContainer page)
    {
        page.AddChild(new Label { Text = "Photo to building", ThemeTypeVariation = "Header" });
        page.AddChild(Ui.Note("Model a real house from its Street View captures or a photo of your own, then adjust it: every change shows in 3D within a second."));
        _noBuilding = Ui.Note("Select a parcel with a house: click it in the 3D view.", "Warn");
        page.AddChild(_noBuilding);
        _buildingOne = new Label { ThemeTypeVariation = "Dim", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _copyIds = Ui.FocusBtn("Copy IDs", CopyIds);
        _copyIds.TooltipText = "Copy the group ID (or the parcel ID if it is in no group) and the building ID";
        var idRow = new HBoxContainer();
        idRow.AddChild(_buildingOne);
        idRow.AddChild(_copyIds);
        page.AddChild(idRow);
        _buildingList = new VBoxContainer();
        page.AddChild(_buildingList);

        BuildPhotos(page);
        BuildBuild(page);
        BuildReview(page);
        BuildAdjust(page);
    }

    void BuildPhotos(VBoxContainer page)
    {
        _photosSec = new VBoxContainer();
        page.AddChild(_photosSec);
        _photosSec.AddChild(new HSeparator());
        _photosSec.AddChild(new Label { Text = "Photos", ThemeTypeVariation = "Header" });
        _photoEmpty = Ui.Note("No Street View captures of this building yet. In a terminal run the command below, aim the middle of the Street View tab at the house, press C (two captures from opposite corners are best). Or add a photo of your own.");
        _photosSec.AddChild(_photoEmpty);
        _copyCmd = Ui.FocusBtn("Copy the capture command", () => { DisplayServer.ClipboardSet(CaptureCommand); _host.Say("copied: " + CaptureCommand); });
        _copyCmd.TooltipText = CaptureCommand;
        _photosSec.AddChild(_copyCmd);
        _photoList = new VBoxContainer();
        _photosSec.AddChild(_photoList);
        _addBtn = Ui.FocusBtn("Add photo...", OpenFileDialog);
        _addBtn.TooltipText = "A photo of your own: say where it was taken (a side of the house, or a sketch point) and the model reads it";
        _photosSec.AddChild(_addBtn);
        _photoNote = Ui.Note("Captures and your own photos are not mixed: they are measured in different ways, captures far better.");
        _photosSec.AddChild(_photoNote);
    }

    void BuildBuild(VBoxContainer page)
    {
        _buildSec = new VBoxContainer();
        page.AddChild(_buildSec);
        _keyBanner = new PanelContainer { ThemeTypeVariation = "BannerPanel" };
        var kb = new VBoxContainer();
        _keyBanner.AddChild(kb);
        kb.AddChild(new Label { Text = "ANTHROPIC_API_KEY is missing", ThemeTypeVariation = "Warn" });
        kb.AddChild(Ui.Note("The model reads the photos with it. Put ANTHROPIC_API_KEY=... in the repo-root .env (and run uv sync --extra reader once), then check again."));
        _recheck = Ui.FocusBtn("Check again", _t.RecheckKey);
        kb.AddChild(_recheck);
        _buildSec.AddChild(_keyBanner);

        _buildBtn = Ui.FocusBtn("Build", _t.Build, "PrimaryButton");
        _buildSec.AddChild(_buildBtn);
        _blocker = Ui.Note("", "Warn");
        _buildSec.AddChild(_blocker);

        _runBox = new VBoxContainer();
        _buildSec.AddChild(_runBox);
        _stageLabel = new Label { ThemeTypeVariation = "Header", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _bar = new ProgressBar { MinValue = 0, MaxValue = 1, ShowPercentage = false, CustomMinimumSize = new Vector2(0, EditorTheme.Space * 2) };
        _timerLabel = new Label { ThemeTypeVariation = "Dim" };
        _logLabel = Ui.Note("");
        _cancelBtn = Ui.FocusBtn("Cancel  (Esc)", _t.CancelBuild);
        foreach (var c in new Control[] { _stageLabel, _bar, _timerLabel, _logLabel, _cancelBtn }) _runBox.AddChild(c);

        _failBox = new VBoxContainer();
        _buildSec.AddChild(_failBox);
        _failBox.AddChild(new Label { Text = "The build failed", ThemeTypeVariation = "Danger" });
        _failText = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _failBox.AddChild(_failText);
        _failLog = Ui.Note("");
        _failBox.AddChild(_failLog);
        _tryAgain = Ui.FocusBtn("Try again", _t.Build, "PrimaryButton");
        _replaceBtn = Ui.FocusBtn("Replace and build", _t.BuildReplacing);
        _failBack = Ui.FocusBtn("Back", _t.Back);
        foreach (var b in new[] { _tryAgain, _replaceBtn, _failBack }) _failBox.AddChild(b);
    }

    void BuildReview(VBoxContainer page)
    {
        _reviewSec = new VBoxContainer();
        page.AddChild(_reviewSec);
        _reviewSec.AddChild(new HSeparator());
        _reviewSec.AddChild(new Label { Text = "Result", ThemeTypeVariation = "Header" });
        _reviewNote = Ui.Note("");
        _reviewStats = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _reviewSec.AddChild(_reviewStats);
        _reviewSec.AddChild(_reviewNote);
        _reviewList = new VBoxContainer();
        _reviewSec.AddChild(_reviewList);
        var row = new HFlowContainer();
        _askEnds = Ui.FocusBtn("Re-ask wall ends", () => _t.ReAsk("ends"));
        _askEnds.TooltipText = "The outline of a wall ends beside the house's corner: ask the model again where each wall stops, then the rest follows";
        _askWalls = Ui.FocusBtn("Re-ask walls", () => _t.ReAsk("walls"));
        _askWalls.TooltipText = "A window or door is wrong, or a colour: ask the model again what is on each wall";
        _newBuild = Ui.FocusBtn("Back to the photos", _t.Back);
        foreach (var b in new[] { _askEnds, _askWalls, _newBuild }) row.AddChild(b);
        _reviewSec.AddChild(row);
    }

    OptionButton Colour(string key, Action<string> pick)
    {
        var ob = new OptionButton { ClipText = true, FocusMode = Control.FocusModeEnum.All, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "A colour of the pipeline's list" };
        ob.ItemSelected += i => { if (_hexes.TryGetValue(ob, out var h) && i >= 0 && i < h.Count && h[(int)i] != "") pick(h[(int)i]); ob.ReleaseFocus(); };
        _hexes[ob] = new();
        return ob;
    }

    SpinBox Num(double min, double max, double step, string tip, Action<double> set)
    {
        var s = Ui.Nudging(Ui.NumberField(min, max, step, tip + "  Up and Down step it, Shift five steps.", () => _host.MarkPanelDirty()));
        s.Suffix = " m";
        s.ValueChanged += v => { if (!_refreshing) set(v); };
        return s;
    }

    CheckBox Check(string text, string tip, Action<bool> set)
    {
        var c = new CheckBox { Text = text, TooltipText = tip, FocusMode = Control.FocusModeEnum.All };
        c.Toggled += on => { set(on); c.ReleaseFocus(); };
        return c;
    }

    void BuildAdjust(VBoxContainer page)
    {
        _adjustSec = new VBoxContainer();
        page.AddChild(_adjustSec);
        _adjustSec.AddChild(new HSeparator());
        _adjustSec.AddChild(new Label { Text = "Adjust, live", ThemeTypeVariation = "Header" });
        _adjustState = Ui.Note("");
        _adjustSec.AddChild(_adjustState);
        _retryForm = Ui.FocusBtn("Try again", _t.LoadForm);
        _adjustSec.AddChild(_retryForm);
        _formBox = new VBoxContainer();
        _adjustSec.AddChild(_formBox);
        _noEntry = Ui.Note("This building has no photo model yet: the numbers are what the rules made of it. Changing one writes an entry for it.", "Dim");
        _formBox.AddChild(_noEntry);
        _detailError = Ui.Note("", "Danger");
        _formBox.AddChild(_detailError);

        _roof = new OptionButton { ClipText = true, FocusMode = Control.FocusModeEnum.All, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "The roof's form" };
        foreach (var (_, text) in Roofs) _roof.AddItem(text);
        _roof.ItemSelected += i => { if (i >= 0 && i < Roofs.Length) _t.SetRoof(Roofs[i].Value); _roof.ReleaseFocus(); };
        _formBox.AddChild(Ui.FieldRow("Roof", _roof));
        _wallTop = Num(1.5, 15, 0.05, "Height of the wall top (the eave) above the ground.", v => _t.SetForm("wall_top_m", v, "wall top"));
        _formBox.AddChild(Ui.FieldRow("Wall top", _wallTop));
        _top = Num(2, 25, 0.05, "Height of the ridge (flat roof: of the roof) above the ground.", v => _t.SetForm("top_m", v, "ridge"));
        _formBox.AddChild(Ui.FieldRow("Ridge", _top));
        _overhang = Num(0, 2, 0.05, "How far the roof sticks out past the walls.", v => _t.SetDetail("overhang_m", Math.Round(v, 2), $"overhang {v:0.##} m", true));
        _formBox.AddChild(Ui.FieldRow("Overhang", _overhang));
        _plinthOn = Check("Plinth", "A band round the foot of every wall", on => _t.SetDetail("plinth_m", on ? 0.3 : null, on ? "plinth on" : "plinth off"));
        _plinth = Num(0.05, 1.5, 0.05, "Height of the plinth.", v => _t.SetDetail("plinth_m", Math.Round(v, 2), $"plinth {v:0.##} m", true));
        var plinthRow = new HBoxContainer();
        plinthRow.AddChild(_plinthOn);
        plinthRow.AddChild(_plinth);
        _formBox.AddChild(plinthRow);

        _formBox.AddChild(new Label { Text = "Colours", ThemeTypeVariation = "Header" });
        _wallColour = Colour("wall", h => _t.SetOverride("wall_colour", h, "wall colour"));
        _roofColour = Colour("roof", h => _t.SetOverride("roof_colour", h, "roof colour"));
        _frameColour = Colour("frame", h => _t.SetDetail("frame_colour", h, "frame colour"));
        _surroundColour = Colour("surround", h => _t.SetDetail("surround_colour", h, "surround colour"));
        _formBox.AddChild(Ui.FieldRow("Walls", _wallColour));
        _formBox.AddChild(Ui.FieldRow("Roof", _roofColour));
        _formBox.AddChild(Ui.FieldRow("Frames", _frameColour));
        _formBox.AddChild(Ui.FieldRow("Surrounds", _surroundColour));

        _formBox.AddChild(new Label { Text = "Trim", ThemeTypeVariation = "Header" });
        _ridgeCaps = Check("Ridge caps", "Cap tiles along the ridge and the hips", on => _t.SetDetail("ridge_caps", on, "ridge caps " + (on ? "on" : "off")));
        _roofTiles = Check("Roof tiles", "The roof laid in courses", on => _t.SetDetail("roof_tiles", on, "roof tiles " + (on ? "on" : "off")));
        _gutter = Check("Gutters", "A gutter along every eave", on => _t.SetDetail("gutter", on, "gutters " + (on ? "on" : "off")));
        _corners = Check("Corner strips", "Upright strips at the outside corners", on => _t.SetDetail("corners", on, "corner strips " + (on ? "on" : "off")));
        _bands = Check("Cornice band", "A band under the eave", on => _t.SetDetail("bands", on, "cornice " + (on ? "on" : "off")));
        _interior = Check("Hollow (doors open)", "The house is hollow with a generated floor plan; its doors open", on => _t.SetDetail("interior", on, "hollow " + (on ? "on" : "off")));
        foreach (var c in new[] { _ridgeCaps, _roofTiles, _gutter, _corners, _bands, _interior }) _formBox.AddChild(c);

        BuildOpenings();

        _formBox.AddChild(new HSeparator());
        _resetBtn = Ui.FocusBtn("Reset to measured", _t.ResetToMeasured);
        _resetBtn.TooltipText = "Both files go back to what the last build wrote (Ctrl+Z undoes this too)";
        _formBox.AddChild(_resetBtn);
        _measuredNote = Ui.Note("");
        _formBox.AddChild(_measuredNote);
    }

    void BuildOpenings()
    {
        _formBox.AddChild(new HSeparator());
        _openingHead = new Label { Text = "Openings", ThemeTypeVariation = "Header" };
        _formBox.AddChild(_openingHead);
        _openingsEmpty = Ui.Note("No openings yet. Add one below, or build from photos.");
        _formBox.AddChild(_openingsEmpty);
        _openingList = new VBoxContainer();
        _formBox.AddChild(_openingList);
        var add = new HFlowContainer();
        foreach (var (kind, text) in new[] { ("window", "Add window"), ("door", "Add door"), ("garage_door", "Add garage door") })
        {
            var k = kind;
            add.AddChild(Ui.FocusBtn(text, () => _t.AddOpening(k)));
        }
        _formBox.AddChild(add);

        _openingForm = new VBoxContainer { Visible = false };
        _formBox.AddChild(_openingForm);
        _kind = new OptionButton { ClipText = true, FocusMode = Control.FocusModeEnum.All, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        foreach (var (_, text) in Kinds) _kind.AddItem(text);
        _kind.ItemSelected += i => { if (_t.SelectedOpening != null && i >= 0 && i < Kinds.Length) _t.SetOpening(_t.SelectedOpening, "kind", Kinds[i].Value, "kind " + Kinds[i].Text.ToLowerInvariant(), false); _kind.ReleaseFocus(); };
        _wall = new OptionButton { ClipText = true, FocusMode = Control.FocusModeEnum.All, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "The wall of the footprint it is on (numbered as in the detail file)" };
        _wall.ItemSelected += i => { if (_t.SelectedOpening != null && _t.Form is { } f && i >= 0 && i < f.Edges.Count) _t.SetOpeningWall(_t.SelectedOpening, f.Edges[(int)i].Index); _wall.ReleaseFocus(); };
        _along = Num(0, 60, 0.05, "Distance along the wall from its first corner to the opening's left side.", v => OpeningSet("at_m", v, "along"));
        _width = Num(0.3, 6, 0.05, "Width of the opening.", v => OpeningSet("width_m", v, "width"));
        _height = Num(0.3, 4, 0.05, "Height of the opening.", v => OpeningSet("height_m", v, "height"));
        _sill = Num(0, 6, 0.05, "Height of the opening's bottom above the ground.", v => OpeningSet("sill_m", v, "sill"));
        _dupBtn = Ui.FocusBtn("Duplicate  (Ctrl+D)", () => { if (_t.SelectedOpening != null) _t.DuplicateOpening(_t.SelectedOpening); });
        _delBtn = Ui.FocusBtn("Delete  (Del)", () => { if (_t.SelectedOpening != null) _t.DeleteOpening(_t.SelectedOpening); }, "DangerButton");
        _delBtn.TooltipText = "Remove the opening; Ctrl+Z brings it back";
        _openingForm.AddChild(Ui.FieldRow("Kind", _kind));
        _openingForm.AddChild(Ui.FieldRow("Wall", _wall));
        _openingForm.AddChild(Ui.FieldRow("Along", _along));
        _openingForm.AddChild(Ui.FieldRow("Width", _width));
        _openingForm.AddChild(Ui.FieldRow("Height", _height));
        _openingForm.AddChild(Ui.FieldRow("Sill", _sill));
        var br = new HBoxContainer();
        _dupBtn.SizeFlagsHorizontal = _delBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        br.AddChild(_dupBtn);
        br.AddChild(_delBtn);
        _openingForm.AddChild(br);
        _openingForm.AddChild(Ui.Note("Click an opening in the 3D view to select it. With a row selected the arrow keys nudge it: Left and Right along the wall, Up and Down the sill, Shift five steps."));
    }

    void OpeningSet(string field, double v, string label)
    {
        if (_t.SelectedOpening is not { } id) return;
        _t.SetOpening(id, field, Math.Round(v, 2), $"{label} {v:0.##} m", true);
    }

    // ---- file dialog ----

    void OpenFileDialog()
    {
        if (_t.Current == null) return;
        if (_dialog == null)
        {
            _dialog = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true,
                Title = "Add a photo of the building", Theme = EditorTheme.Build(),
            };
            _dialog.AddFilter("*.png, *.jpg, *.jpeg, *.webp", "Images");
            _dialog.FileSelected += path => _t.AddOwnPhoto(path);
            _host.AddChild(_dialog);
        }
        _dialog.PopupCenteredRatio(0.6f);
    }

    // ---- refresh ----

    public void Refresh()
    {
        var phase = _t.Phase;
        var locked = _t.Locked;
        var hasBuilding = _t.Current != null;
        _noBuilding.Visible = !hasBuilding;
        RefreshBuildings();
        _photosSec.Visible = hasBuilding;
        _buildSec.Visible = hasBuilding;
        _reviewSec.Visible = hasBuilding && phase == PhotoBuildState.Review && _t.Result != null;
        _adjustSec.Visible = hasBuilding && !locked && (_t.Form != null || _t.FormError != null || _t.FormLoading);
        if (hasBuilding)
        {
            RefreshPhotos(locked);
            RefreshBuild(phase, locked);
            RefreshReview();
            RefreshAdjust();
        }
    }

    void CopyIds()
    {
        if (_t.Current == null) return;
        var parcel = _t.Buildings.FirstOrDefault(b => b.Id == _t.Current)?.Parcel ?? "";
        var owner = _host.Map.Parcels.TryGetValue(parcel, out var p) && p.Group != null ? p.Group : parcel;
        var ids = $"{owner} {_t.Current}".Trim();
        DisplayServer.ClipboardSet(ids);
        Ui.FlashCopied(_copyIds);
        _host.Say("copied: " + ids);
    }

    void RefreshBuildings()
    {
        var sig = string.Join("|", _t.Buildings.Select(b => b.Id)) + "#" + _t.Current + _t.Locked;
        var many = _t.Buildings.Count > 1;
        _buildingList.Visible = many;
        _buildingOne.Visible = !many && _t.Current != null;
        _copyIds.Visible = _t.Current != null;
        if (_t.Current != null && !many) _buildingOne.Text = $"{_t.Current}  on  {_t.Buildings.FirstOrDefault(b => b.Id == _t.Current)?.Parcel}";
        if (sig == _buildingSig) return;
        _buildingSig = sig;
        foreach (var c in _buildingList.GetChildren()) { _buildingList.RemoveChild(c); c.QueueFree(); }
        foreach (var b in _t.Buildings)
        {
            var captures = PhotoFiles.Captures(_t.Repo, b.Id).Count;
            var btn = new Button
            {
                Text = $"{b.Id}   {b.Parcel}\n{(captures == 0 ? "no captures" : captures == 1 ? "1 capture" : captures + " captures")}", ToggleMode = true, Alignment = HorizontalAlignment.Left, AutowrapMode = TextServer.AutowrapMode.WordSmart,
                FocusMode = Control.FocusModeEnum.All, Disabled = _t.Locked, TooltipText = $"Work on {b.Id}",
            };
            btn.SetPressedNoSignal(b.Id == _t.Current);
            var id = b.Id;
            btn.Pressed += () => { _t.SelectBuilding(id); btn.ReleaseFocus(); };
            _buildingList.AddChild(btn);
        }
    }

    static ImageTexture? Thumb(string path, int width)
    {
        var key = path + "@" + width;
        if (!Thumbs.TryGetValue(key, out var tex)) Thumbs[key] = tex = PhotoFiles.Load(path, width);
        return tex;
    }

    void RefreshPhotos(bool locked)
    {
        var empty = _t.Captures.Count == 0 && _t.Own.Count == 0;
        _photoEmpty.Visible = _copyCmd.Visible = _t.Captures.Count == 0;
        _photoNote.Visible = _t.Own.Count > 0;
        _addBtn.Disabled = locked;
        _copyCmd.Disabled = locked;
        var sig = $"{_t.Current}|{locked}|{string.Join(",", _t.Captures.Select(c => c.N + (_t.TickedCaptures.Contains(c.N) ? "+" : "-")))}|"
            + string.Join(",", _t.Own.Select(o => $"{o.Name}{o.Ticked}{o.Side}{o.Point}")) + "|" + _host.Sketches.Doc.Points.Count;
        if (sig == _photoSig) return;
        _photoSig = sig;
        foreach (var c in _photoList.GetChildren()) { _photoList.RemoveChild(c); c.QueueFree(); }
        var enlarge = _t.Captures.Select(c => (c.Png, $"Capture {c.N}, looking {c.Heading:0} degrees")).ToList();
        foreach (var (c, i) in _t.Captures.Select((c, i) => (c, i)))
        {
            var row = new HBoxContainer();
            var at = i;
            var thumb = new Button { Icon = Thumb(c.Png, 256), ExpandIcon = true, CustomMinimumSize = ThumbSize, FocusMode = Control.FocusModeEnum.All, TooltipText = "Enlarge (click, Esc closes)" };
            thumb.Pressed += () => { _t.OpenPictures(enlarge, at); thumb.ReleaseFocus(); };
            row.AddChild(thumb);
            var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            var n = c.N;
            var cb = new CheckBox { Text = $"Capture {c.N}", ButtonPressed = _t.TickedCaptures.Contains(c.N), Disabled = locked, FocusMode = Control.FocusModeEnum.All, TooltipText = "Measure this capture" };
            cb.Toggled += on => { _t.TickCapture(n, on); };
            col.AddChild(cb);
            col.AddChild(Ui.Note($"looking {c.Heading:0} degrees"));
            col.AddChild(Ui.Note($"{c.DistanceM:0} m from the wall"));
            col.AddChild(Ui.Note(c.Walls.Count == 0 ? "no wall in view" : "walls " + string.Join(", ", c.Walls)));
            row.AddChild(col);
            _photoList.AddChild(row);
        }
        foreach (var o in _t.Own) _photoList.AddChild(OwnRow(o, locked));
    }

    Control OwnRow(OwnPhoto o, bool locked)
    {
        var row = new HBoxContainer();
        var thumb = new Button { Icon = Thumb(o.Png, 256), ExpandIcon = true, CustomMinimumSize = ThumbSize, FocusMode = Control.FocusModeEnum.All, TooltipText = "Enlarge (click, Esc closes)" };
        thumb.Pressed += () => { _t.OpenPictures(new[] { (o.Png, "Your photo " + o.Name) }, 0); thumb.ReleaseFocus(); };
        row.AddChild(thumb);
        var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var cb = new CheckBox { Text = "Your photo", ButtonPressed = o.Ticked, Disabled = locked, FocusMode = Control.FocusModeEnum.All, TooltipText = o.Name };
        cb.Toggled += on => _t.TickOwn(o, on);
        col.AddChild(cb);
        col.AddChild(Ui.Note(o.Name));
        var where = new OptionButton { ClipText = true, FocusMode = Control.FocusModeEnum.All, Disabled = locked, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "Where the photo was taken: a side of the house, or a point dropped with the Sketch tool" };
        var keys = new List<(string? Side, string? Point)> { (null, null) };
        where.AddItem("Where was it taken?");
        where.SetItemDisabled(0, true);
        for (var s = 0; s < 8; s++) { where.AddItem($"From {PhotoFiles.SideNames[s]}"); keys.Add((PhotoFiles.Sides[s], null)); }
        foreach (var p in _host.Sketches.Doc.Points) { where.AddItem($"From point {p.Id}"); keys.Add((null, p.Id)); }
        var sel = keys.FindIndex(k => k.Side == o.Side && k.Point == o.Point);
        if (sel < 0 && o.Point != null) { where.AddItem($"From point {o.Point} (gone)"); keys.Add((null, o.Point)); sel = keys.Count - 1; }
        where.Select(Math.Max(sel, 0));
        where.ItemSelected += i => { if (i > 0 && i < keys.Count) _t.SetOwnPlace(o, keys[(int)i].Side, keys[(int)i].Point); where.ReleaseFocus(); };
        col.AddChild(where);
        var remove = Ui.FocusBtn("Remove", () => _host.Confirm.Show($"Remove {o.Name}?", "The copy kept with this building's photos is deleted. Your original file is not touched.", "Remove", () => _t.RemoveOwn(o)), "DangerButton");
        remove.Disabled = locked;
        col.AddChild(remove);
        row.AddChild(col);
        return row;
    }

    void RefreshBuild(PhotoBuildState phase, bool locked)
    {
        _keyBanner.Visible = !_t.ApiKey && !locked;
        var idleish = !locked && phase != PhotoBuildState.Failed;
        _buildBtn.Visible = idleish;
        _buildBtn.Text = phase == PhotoBuildState.Review ? "Build again: " + _t.BuildLabel().Replace("Build from ", "from ") : _t.BuildLabel();
        var why = idleish ? _t.BuildBlocker() : null;
        _buildBtn.Disabled = why != null;
        _blocker.Visible = why != null;
        _blocker.Text = why ?? "";
        _runBox.Visible = locked;
        _failBox.Visible = phase == PhotoBuildState.Failed;
        if (locked)
        {
            _stageLabel.Text = phase == PhotoBuildState.Cancelling ? "Stopping the build..." : _t.StageCount == 0 ? "Starting..." : $"Stage {_t.StageIndex} of {_t.StageCount}: {PhotoBuildLog.StageText(_t.Stage)}";
            _bar.MaxValue = Math.Max(1, _t.StageCount);
            _bar.Value = _t.StageCount == 0 ? 0 : Math.Max(0, _t.StageIndex - 1) + 0.5;
            _logLabel.Text = _t.LogTail.Count > 0 ? _t.LogTail[^1] : "";
            _cancelBtn.Disabled = phase == PhotoBuildState.Cancelling;
            UpdateTimer();
        }
        if (phase == PhotoBuildState.Failed)
        {
            var key = _t.KeyMissing;
            _failText.Text = _t.Error ?? "";
            _failLog.Text = string.Join("\n", _t.LogTail.TakeLast(4));
            _failLog.Visible = !key && _t.LogTail.Count > 0;
            var already = (_t.Error ?? "").Contains("already has an entry", StringComparison.Ordinal);
            _replaceBtn.Visible = already;
            _tryAgain.Visible = !already;
            _tryAgain.Disabled = _t.BuildBlocker() != null;
            if (key) _failText.Text += "\nPut ANTHROPIC_API_KEY=... in the repo-root .env, run uv sync --extra reader once, then Try again.";
        }
    }

    public void UpdateTimer()
    {
        if (_t.Locked) _timerLabel.Text = $"{_t.RunSeconds:0} s";
    }

    void RefreshReview()
    {
        if (!_reviewSec.Visible) return;
        var r = _t.Result!;
        _reviewNote.Text = _t.ResultFromDisk ? "The last build of this building, from its run folder. Problems and the made-up list are those of that run." : "Look at the overlay (the model drawn over the photo: wall ends, foot, top and windows must sit on the photo's) and the comparison (the two houses must look alike). Click a picture to enlarge it.";
        _reviewStats.Text = r.Roof == "" ? "" : $"{char.ToUpperInvariant(r.Roof[0])}{r.Roof[1..]} roof, wall top {r.EaveM:0.##} m, ridge {r.TopM:0.##} m" + (r.Openings > 0 ? $", {r.Openings} opening{(r.Openings == 1 ? "" : "s")}" : "");
        var pics = r.Overlays.Select(p => (p, "Overlay: " + System.IO.Path.GetFileNameWithoutExtension(p).Replace("overlay_", "capture ")))
            .Concat(r.Compares.Select(p => (p, "Comparison: " + System.IO.Path.GetFileNameWithoutExtension(p).Replace("compare_", "capture ")))).ToList();
        var sig = $"{r.Run}|{pics.Count}|{string.Join("#", _t.Problems)}|{string.Join("#", _t.Invented)}";
        var canEnds = r.CanRedoEnds && r.Method != "own";
        _askEnds.Disabled = !canEnds || !_t.ApiKey;
        _askEnds.Visible = r.Method != "own";
        _askWalls.Visible = r.Method != "own";
        _askWalls.Disabled = !_t.ApiKey;
        _askEnds.TooltipText = canEnds ? "The outline of a wall ends beside the house's corner: ask the model again where each wall stops, then the rest follows" : "The wall ends were marked by hand at capture: there is nothing to ask again";
        if (sig == _reviewSig) return;
        _reviewSig = sig;
        foreach (var c in _reviewList.GetChildren()) { _reviewList.RemoveChild(c); c.QueueFree(); }
        if (pics.Count > 0)
        {
            var flow = new HFlowContainer();
            foreach (var (p, i) in pics.Select((p, i) => (p, i)))
            {
                var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                var at = i;
                var b = new Button { Icon = Thumb(p.Item1, 512), ExpandIcon = true, CustomMinimumSize = ReviewSize, FocusMode = Control.FocusModeEnum.All, TooltipText = p.Item2 + " (click to enlarge, Esc closes)" };
                b.Pressed += () => { _t.OpenPictures(pics, at); b.ReleaseFocus(); };
                col.AddChild(b);
                col.AddChild(Ui.Note(p.Item2));
                flow.AddChild(col);
            }
            _reviewList.AddChild(flow);
        }
        _reviewList.AddChild(new Label { Text = $"Problems ({_t.Problems.Count})", ThemeTypeVariation = "Header" });
        if (_t.Problems.Count == 0) _reviewList.AddChild(Ui.Note("None reported."));
        foreach (var p in _t.Problems) _reviewList.AddChild(Ui.Note("- " + p, "Warn"));
        _reviewList.AddChild(new Label { Text = $"Made up by rule ({_t.Invented.Count})", ThemeTypeVariation = "Header" });
        if (_t.Invented.Count == 0) _reviewList.AddChild(Ui.Note("Nothing: every part was seen in a photo."));
        else _reviewList.AddChild(Ui.Note("Not seen in a photo; the rules filled it in (windows on unseen walls are marked low confidence):"));
        foreach (var p in _t.Invented) _reviewList.AddChild(Ui.Note("- " + p));
    }

    // ---- the form ----

    void FillColour(OptionButton ob, string palette, string? current)
    {
        var colours = _t.Form!.Palettes.TryGetValue(palette, out var p) ? p : new();
        var sig = palette + "|" + current + "|" + colours.Count;
        if (ob.HasMeta("sig") && ob.GetMeta("sig").AsString() == sig) return;
        ob.SetMeta("sig", sig);
        ob.Clear();
        var hexes = _hexes[ob];
        hexes.Clear();
        var sel = -1;
        if (current == null) { ob.AddItem("(the rules' colour)"); ob.SetItemDisabled(0, true); hexes.Add(""); sel = 0; }
        foreach (var (name, hex) in colours)
        {
            ob.AddIconItem(Ui.Swatch(hex), $"{char.ToUpperInvariant(name[0])}{name[1..]}  {hex}");
            hexes.Add(hex);
            if (string.Equals(hex, current, StringComparison.OrdinalIgnoreCase)) sel = hexes.Count - 1;
        }
        if (current != null && sel < 0) { ob.AddIconItem(Ui.Swatch(current), $"Custom  {current}"); hexes.Add(current); sel = hexes.Count - 1; }
        ob.Select(sel);
    }

    void RefreshAdjust()
    {
        _refreshing = true;
        try { RefreshAdjustFields(); }
        finally { _refreshing = false; }
    }

    void RefreshAdjustFields()
    {
        if (!_adjustSec.Visible) return;
        var f = _t.Form;
        _adjustState.Visible = f == null;
        _retryForm.Visible = f == null && !_t.FormLoading;
        _adjustState.Text = _t.FormLoading ? "Reading the building's files..." : _t.FormError ?? "";
        _adjustState.ThemeTypeVariation = _t.FormError != null ? "Danger" : "Dim";
        _formBox.Visible = f != null;
        if (f == null) return;
        _noEntry.Visible = !f.HasOverride && !f.HasDetail;
        _detailError.Visible = f.DetailError != "";
        _detailError.Text = $"The detail file cannot be read: {f.DetailError}. Fix it by hand, or Reset to measured.";
        if (_t.Applying) return; // an older answer must not overwrite what was just typed
        var d = f.Detail;
        _roof.Select(Math.Max(0, Array.FindIndex(Roofs, r => r.Value == f.Roof)));
        _wallTop.SetValueNoSignal(f.WallTop);
        _top.SetValueNoSignal(f.Top);
        _top.Editable = _wallTop.Editable = true;
        _overhang.SetValueNoSignal(d?.Overhang ?? 0);
        _plinthOn.SetPressedNoSignal(d?.PlinthHeight != null);
        _plinth.SetValueNoSignal(d?.PlinthHeight ?? 0.3);
        _plinth.Editable = d?.PlinthHeight != null;
        FillColour(_wallColour, "wall", f.WallColour);
        FillColour(_roofColour, "roof", f.RoofColour);
        FillColour(_frameColour, "frame", d?.Frame is { Length: > 0 } fr ? fr : "#f2f0ea");
        FillColour(_surroundColour, "surround", d?.Surround is { Length: > 0 } su ? su : "#f2f0ea");
        _ridgeCaps.SetPressedNoSignal(d?.RidgeCaps ?? false);
        _roofTiles.SetPressedNoSignal(d?.RoofTiles ?? false);
        _gutter.SetPressedNoSignal(d?.Gutter ?? false);
        _corners.SetPressedNoSignal(d?.Corners ?? false);
        _bands.SetPressedNoSignal(d?.Bands ?? false);
        _interior.SetPressedNoSignal(d?.Interior ?? false);
        _resetBtn.Disabled = !f.Measured;
        _measuredNote.Visible = !f.Measured;
        _measuredNote.Text = "Nothing measured to go back to: build from photos first.";
        RefreshOpenings(f);
    }

    void RefreshOpenings(BuildingForm f)
    {
        var list = f.Detail?.Openings ?? new List<OpeningRow>();
        _openingHead.Text = $"Openings ({list.Count})";
        _openingsEmpty.Visible = list.Count == 0;
        var sig = string.Join("|", list.Select(o => $"{o.Id}{o.Kind}{o.Edge}{o.At}{o.Width}{o.Height}{o.Sill}")) + "#" + _t.SelectedOpening;
        if (sig != _openingSig)
        {
            _openingSig = sig;
            foreach (var c in _openingList.GetChildren()) { _openingList.RemoveChild(c); c.QueueFree(); }
            foreach (var o in list)
            {
                var edge = f.Edges.FirstOrDefault(e => e.Index == o.Edge);
                var side = edge.Length > 0 ? BuildingForm.SideOf(edge) : "?";
                var b = new Button
                {
                    Text = $"{o.Id}   {o.Kind.Replace('_', ' ')}" + (o.Confidence == "low" ? "   (guessed)" : "") + $"\nwall {o.Edge} {side}, {o.At:0.00} m along\n{o.Width:0.0} x {o.Height:0.0} m, sill {o.Sill:0.0} m",
                    Alignment = HorizontalAlignment.Left, ToggleMode = true, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.All, TooltipText = $"Select {o.Id}: it is outlined in the 3D view",
                };
                b.SetPressedNoSignal(o.Id == _t.SelectedOpening);
                var id = o.Id;
                b.Pressed += () => { _t.SelectOpening(id == _t.SelectedOpening ? null : id); b.ReleaseFocus(); };
                _openingList.AddChild(b);
            }
        }
        var sel = list.FirstOrDefault(o => o.Id == _t.SelectedOpening);
        _openingForm.Visible = sel != null;
        if (sel == null) return;
        var ei = f.Edges.FindIndex(e => e.Index == sel.Edge);
        if (_wall.ItemCount != f.Edges.Count)
        {
            _wall.Clear();
            foreach (var e in f.Edges) _wall.AddItem($"Wall {e.Index}  {BuildingForm.SideOf(e)}  {e.Length:0.0} m");
        }
        _wall.Select(Math.Max(0, ei));
        var k = Array.FindIndex(Kinds, x => x.Value == sel.Kind);
        if (k < 0 && _kind.ItemCount == Kinds.Length) _kind.AddItem(sel.Kind.Replace('_', ' '));
        if (k >= 0 && _kind.ItemCount > Kinds.Length) _kind.RemoveItem(Kinds.Length);
        _kind.Select(k >= 0 ? k : Kinds.Length);
        _along.MaxValue = Math.Max(0.5, (ei >= 0 ? f.Edges[ei].Length : 60) - sel.Width);
        _along.SetValueNoSignal(sel.At);
        _width.SetValueNoSignal(sel.Width);
        _height.SetValueNoSignal(sel.Height);
        _sill.SetValueNoSignal(sel.Sill);
    }
}
