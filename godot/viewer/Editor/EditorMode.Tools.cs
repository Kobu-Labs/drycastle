using Godot;
using System;

namespace Drycastle.Viewer.Editor;

/// <summary>The active tool of the rail: switching it (what each tool lets go of), its key, and the remembered settings (editor_ui.cfg).</summary>
public partial class EditorMode
{
    const string CfgPath = "user://editor_ui.cfg";

    public EditorTool Tool { get; private set; } = EditorTool.None;

    public void SelectTool(EditorTool tool, bool save = true)
    {
        if (Shapes.ShapeTool && (tool != Tool)) Shapes.CancelShapeGesture();
        if (Tool == EditorTool.Sketch && tool != EditorTool.Sketch) Sketches.CancelGesture();
        if (Objects.Dragging && tool != Tool) Objects.CancelDrag(); // an object drag must not outlive its tool (the release would still save it)
        if (tool is EditorTool.Ground or EditorTool.Zones or EditorTool.Sketch) { Objects.DisarmPlacement(false); if (Fence.Gates.Armed) Fence.Gates.Cancel(); }
        if (Tool == EditorTool.Poles && tool != EditorTool.Poles) { Poles.StopConnecting(false); Poles.Plan.Stop(false); Poles.SelectCable(null); }
        if (tool == EditorTool.Poles && Fence.Gates.Armed) Fence.Gates.Cancel();
        Tool = tool;
        Shapes.ShapeRest(); // Selected or Idle depends on which kind of shape the tool selects
        Chrome.ShowTool(tool);
        _panelDirty = true;
        if (save) SaveUiConfig();
    }

    /// <summary>Open a tool by name (screenshots, smoke tests); an unknown name closes the sidebar.</summary>
    public void SetTool(string name)
        => SelectTool(Enum.TryParse<EditorTool>(name, true, out var t) ? t : EditorTool.None);

    /// <summary>Compatibility with the old collapsible sections: opening a title opens its tool.</summary>
    public void SetSectionOpen(string title, bool open)
    {
        if (!Enum.TryParse<EditorTool>(title, true, out var t)) return;
        if (open) SelectTool(t);
        else if (Tool == t) SelectTool(EditorTool.None);
    }

    /// <summary>A number key opens the tool of that number (again: closes it). True when used.</summary>
    bool HandleToolKey(Key key)
    {
        foreach (var (t, _, _, k) in EditorChrome.Tools)
            if (k == key) { SelectTool(Tool == t ? EditorTool.None : t); return true; }
        return false;
    }

    // ---- persistence ----

    void LoadUiConfig()
    {
        try
        {
            var cfg = new ConfigFile();
            if (cfg.Load(CfgPath) != Error.Ok) return;
            if (Enum.TryParse<EditorTool>((string)cfg.GetValue("ui", "tool", "None"), true, out var t)) { Tool = t; Shapes.ShapeRest(); }
            Camera.LoadConfig(cfg);
            foreach (var part in _parts) part.LoadConfig(cfg);
        }
        catch (Exception e) { GD.PushWarning($"editor ui settings not loaded: {e.Message}"); }
    }

    /// <summary>Write the remembered settings (every part's SaveConfig) to editor_ui.cfg.</summary>
    public void SaveUiConfig()
    {
        try
        {
            var cfg = new ConfigFile();
            cfg.SetValue("ui", "tool", Tool.ToString());
            Camera.SaveConfig(cfg);
            foreach (var part in _parts) part.SaveConfig(cfg);
            cfg.Save(CfgPath);
        }
        catch (Exception e) { GD.PushWarning($"editor ui settings not saved: {e.Message}"); }
    }
}
