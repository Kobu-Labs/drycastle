using Godot;

namespace Drycastle.Viewer.Editor.Tools;

/// <summary>
/// One part of the editor that owns its state: a tool of the rail (sketch, ground, objects ...) or a view layer (aerial photo, floor plan).
/// <see cref="EditorMode"/> routes to its parts and builds nothing of theirs; a part reaches the rest of the editor only through <see cref="Host"/>.
/// Every hook is optional.
/// </summary>
public abstract class EditorPart
{
    protected EditorPart(IEditorHost host) => Host = host;

    protected IEditorHost Host { get; }

    /// <summary>Per frame while the editor is open.</summary>
    public virtual void Update(double delta) { }

    /// <summary>The editor opened or closed (closing drops whatever the part has half done).</summary>
    public virtual void ActiveChanged(bool on) { }

    /// <summary>Read and write the part's remembered settings (editor_ui.cfg, section "ui").</summary>
    public virtual void LoadConfig(ConfigFile cfg) { }
    public virtual void SaveConfig(ConfigFile cfg) { }
}
