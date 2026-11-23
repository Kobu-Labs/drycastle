using Drycastle.MapLoader;
using Drycastle.Viewer.Edits;
using Godot;
using System.Collections.Generic;

namespace Drycastle.Viewer.Editor;

/// <summary>What an editor part may use of the rest of the editor. <see cref="EditorMode"/> implements it; parts never see its fields.</summary>
public interface IEditorHost
{
    Drycastle.MapLoader.MapLoader Map { get; }
    EditsWatcher? Watcher { get; }
    SortedSet<string> Selection { get; }
    Node3D OutlineRoot { get; }
    ConfirmDialog Confirm { get; }
    string EditsPath { get; }
    EditStore Edits { get; }
    EditorTool Tool { get; }
    Camera3D FlyCam { get; }
    EditorCamera Camera { get; }
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
    bool CheatSheetShown { get; }
    /// <summary>One sphere for every corner handle, and the material of the hot one.</summary>
    SphereMesh HandleMesh { get; }
    StandardMaterial3D HandleHot { get; }
    Viewport GetViewport();

    /// <summary>One line in the status bar (and the log); errors stay longer and are red.</summary>
    void Say(string message, bool error = false);

    /// <summary>Draw a named set of ground segments draped on the terrain; an empty list clears it.</summary>
    void SetOutline(string name, List<(Vector2 a, Vector2 b)> segs, Color colour, float lift, float width);

    /// <summary>The mouse is over the sidebar, the rail, the status bar or a dialog.</summary>
    bool OverUi(Vector2 mouse);

    /// <summary>Ground point under a ray (the terrain, not buildings); false when the ray hits none.</summary>
    bool RayToGround(Vector3 origin, Vector3 dir, out Vector3 point);

    /// <summary>The nearest corner of a ring (absolute metres) within reach of a screen position; -1 when none.</summary>
    int PickCorner(Vector2 screen, IReadOnlyList<(double x, double y)> ring);

    /// <summary>An absolute-metres point on the terrain, a little above it.</summary>
    Vector3 GroundPoint((double x, double y) p);

    /// <summary>The ground segments of a ring or a line (absolute metres).</summary>
    List<(Vector2, Vector2)> RingSegs(IReadOnlyList<(double x, double y)> pts, bool closed);

    void SelectTool(EditorTool tool, bool save = true);

    /// <summary>Read the groups again from the file on disk (after every save, and when another writer changed it).</summary>
    void ReloadGroups();

    /// <summary>The sidebar is rebuilt on the next frame.</summary>
    void MarkPanelDirty();

    /// <summary>Write the remembered settings (every part's SaveConfig) to editor_ui.cfg.</summary>
    void SaveUiConfig();
}
