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

    /// <summary>One line in the status bar (and the log); errors stay longer and are red.</summary>
    void Say(string message, bool error = false);

    /// <summary>Draw a named set of ground segments draped on the terrain; an empty list clears it.</summary>
    void SetOutline(string name, List<(Vector2 a, Vector2 b)> segs, Color colour, float lift, float width);

    /// <summary>Read the groups again from the file on disk (after every save, and when another writer changed it).</summary>
    void ReloadGroups();

    /// <summary>The sidebar is rebuilt on the next frame.</summary>
    void MarkPanelDirty();

    /// <summary>Write the remembered settings (every part's SaveConfig) to editor_ui.cfg.</summary>
    void SaveUiConfig();
}
