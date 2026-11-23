using Drycastle.MapLoader;
using Drycastle.Viewer.Edits;
using Drycastle.Viewer.Editor.Tools;
using Godot;
using System.Collections.Generic;

namespace Drycastle.Viewer.Editor;

/// <summary>What an editor part may use of the rest of the editor. <see cref="EditorMode"/> implements it; parts never see its fields.</summary>
public interface IEditorHost
{
    Drycastle.MapLoader.MapLoader Map { get; }
    EditsWatcher? Watcher { get; }
    SortedSet<string> Selection { get; }
    ConfirmDialog Confirm { get; }
    string EditsPath { get; }
    EditStore Edits { get; }
    EditorTool Tool { get; }
    Camera3D FlyCam { get; }
    EditorCamera Camera { get; }
    ObjectsTool Objects { get; }
    PolesTool Poles { get; }
    FenceTool Fence { get; }
    ShapeEditor Shapes { get; }
    GroundTool Ground { get; }
    ZonesTool Zones { get; }
    EditorChrome Chrome { get; }
    OutlineLayer Outlines { get; }
    ParcelGroups Groups { get; }
    GroundPicker Picker { get; }
    /// <summary>The walk-mode player (frozen and hidden while the editor is open).</summary>
    Player Player { get; }
    /// <summary>The editor is open.</summary>
    bool Active { get; }
    /// <summary>Parent of the editor's markers in the 3D view (visible only while the editor is).</summary>
    Node3D MarkerRoot { get; }
    /// <summary>A confirm or the command palette is open: the keys and the mouse belong to it.</summary>
    bool Modal { get; }
    /// <summary>The right mouse button is held: the camera is looking around.</summary>
    bool Looking { get; }
    Viewport GetViewport();
    void AddChild(Node node, bool forceReadableName = false, Node.InternalMode @internal = Node.InternalMode.Disabled);

    /// <summary>One line in the status bar (and the log); errors stay longer and are red.</summary>
    void Say(string message, bool error = false);

    void SelectTool(EditorTool tool, bool save = true);

    /// <summary>Click semantics of the Select tool: plain click replaces the selection, shift toggles one parcel.</summary>
    void Click(string? parcelId, bool shift);

    /// <summary>The sidebar is rebuilt on the next frame.</summary>
    void MarkPanelDirty();

    /// <summary>Write the remembered settings (every part's SaveConfig) to editor_ui.cfg.</summary>
    void SaveUiConfig();
}
