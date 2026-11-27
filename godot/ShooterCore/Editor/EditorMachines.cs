using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Editor;

/// <summary>The polygon tools of the map editor (Ground patches, Zones): what the pointer and the keys do next. See <see cref="EditorMachines.ShapeSpec"/>.</summary>
public enum EditorShapeState { Idle, Drawing, Level, Selected, Dragging }

/// <summary>The sketch tool of the map editor: a line being drawn, a mark selected, or neither. See <see cref="EditorMachines.SketchSpec"/>.</summary>
public enum EditorSketchState { Idle, Drawing, Selected }

/// <summary>The viewer's building worker (<c>building-serve</c>): see <see cref="EditorMachines.BuildingWorkerSpec"/>.</summary>
public enum BuildingWorkerState { Stopped, Starting, Idle, Running, Queued, Down }

/// <summary>
/// Specs of the map editor's state machines (godot/viewer, EditorMode). Pure so the catalog and docs/state-machines.md list them; the viewer owns the
/// <see cref="StateMachine{TState}"/> instances and the payload (corner list, selected id) next to them.
/// </summary>
public static class EditorMachines
{
    /// <summary>
    /// Idle and Selected follow the selection (they change only when a shape is selected or deselected, or the tool changes) and only while no gesture is
    /// under way; Drawing, Level and Dragging are gestures that hold their state until they end, whatever the selection does meanwhile.
    /// </summary>
    public static readonly StateMachineSpec<EditorShapeState> ShapeSpec = StateMachineSpec<EditorShapeState>.Define(
            "Editor.Shape", "The shape tools of the map editor (ground patches, zones): draw a polygon, ask for the water level, select a shape, drag one of its corners.", EditorShapeState.Idle)
        .Allow(EditorShapeState.Idle, EditorShapeState.Selected, EditorShapeState.Drawing)
        .Allow(EditorShapeState.Selected, EditorShapeState.Idle, EditorShapeState.Drawing, EditorShapeState.Dragging)
        .Allow(EditorShapeState.Drawing, EditorShapeState.Level, EditorShapeState.Idle, EditorShapeState.Selected)
        .Allow(EditorShapeState.Level, EditorShapeState.Drawing, EditorShapeState.Idle, EditorShapeState.Selected)
        .Allow(EditorShapeState.Dragging, EditorShapeState.Idle, EditorShapeState.Selected)
        .Note(EditorShapeState.Idle, "No shape selected, nothing being drawn.")
        .Note(EditorShapeState.Drawing, "Corners placed (or N pressed and waiting for the first one); nothing saved. Esc cancels.")
        .Note(EditorShapeState.Level, "A patch of a water type is closed and asks for its water level; still nothing saved. Esc goes back to the corners.")
        .Note(EditorShapeState.Selected, "A saved shape is selected: its corners can be dragged or nudged, Del deletes it.")
        .Note(EditorShapeState.Dragging, "A corner follows the cursor; the file changes on release, Esc cancels.")
        .Build();

    /// <summary>The naming row (new / rename a sketch) is a text-entry sub-mode of the panel, not a state of this machine: Esc closes it before this machine sees the key.</summary>
    public static readonly StateMachineSpec<EditorSketchState> SketchSpec = StateMachineSpec<EditorSketchState>.Define(
            "Editor.Sketch", "The sketch tool of the map editor: corners of a line being drawn, a selected mark, or neither.", EditorSketchState.Idle)
        .Allow(EditorSketchState.Idle, EditorSketchState.Drawing, EditorSketchState.Selected)
        .Allow(EditorSketchState.Drawing, EditorSketchState.Idle, EditorSketchState.Selected)
        .Allow(EditorSketchState.Selected, EditorSketchState.Idle, EditorSketchState.Drawing)
        .Note(EditorSketchState.Idle, "A click marks a point or starts a line.")
        .Note(EditorSketchState.Drawing, "Corners of an unfinished line are placed; nothing is part of the sketch yet. Enter finishes, Esc cancels.")
        .Note(EditorSketchState.Selected, "One mark of the sketch is selected: Del deletes it, [ ] steps to the next.")
        .Build();

    /// <summary>
    /// The Photo to building tool (godot/viewer, PhotoTool): one build at a time. Building runs <c>facade_pipeline auto</c> as a subprocess (stages, the model's answers, the
    /// files); Cancelling waits for the process to be gone; Review shows the result (or the last one found on disk) and the adjustments; Failed says what went wrong.
    /// </summary>
    public static readonly StateMachineSpec<PhotoBuildState> PhotoBuildSpec = StateMachineSpec<PhotoBuildState>.Define(
            "Editor.PhotoBuild", "The Photo to building tool of the map editor: idle, a build process running, stopping it, the result under review, a failed build.", PhotoBuildState.Idle)
        .Allow(PhotoBuildState.Idle, PhotoBuildState.Building, PhotoBuildState.Review)
        .Allow(PhotoBuildState.Building, PhotoBuildState.Review, PhotoBuildState.Failed, PhotoBuildState.Cancelling)
        .Allow(PhotoBuildState.Cancelling, PhotoBuildState.Idle)
        .Allow(PhotoBuildState.Review, PhotoBuildState.Building, PhotoBuildState.Idle)
        .Allow(PhotoBuildState.Failed, PhotoBuildState.Building, PhotoBuildState.Idle)
        .Note(PhotoBuildState.Idle, "Nothing running: pick the photos and press Build, or adjust the building as it is.")
        .Note(PhotoBuildState.Building, "The build process runs (stage lines, model calls, then the two files); the panel shows the stage. Cancel or Esc asks, then stops it; the files are written only at its end.")
        .Note(PhotoBuildState.Cancelling, "The process was told to stop; the panel waits for it to be gone, then goes back to Idle.")
        .Note(PhotoBuildState.Review, "A build finished (or an earlier run of the building was found on disk): its overlay and comparison pictures, the problems and the made-up list; re-ask a model question or adjust live.")
        .Note(PhotoBuildState.Failed, "The build stopped with an error (a missing API key, a bad answer, no usable wall): the message says what to do; Try again or go back.")
        .Build();

    /// <summary>
    /// The viewer's live loop for buildings (godot/viewer, EditsWatcher.Buildings.cs): one long-lived <c>building-serve</c> process, one request on the
    /// wire at a time. A changed overrides or building file waits in a set (the payload, next to the machine); the set goes out as one request when the
    /// worker is Idle, or after the answer. A worker that dies is Down for a moment, then started again once there is work. The viewer closing stops it.
    /// </summary>
    public static readonly StateMachineSpec<BuildingWorkerState> BuildingWorkerSpec = StateMachineSpec<BuildingWorkerState>.Define(
            "Editor.BuildingWorker", "The viewer's building worker: started once, one rebuild request at a time, edits that arrive meanwhile join the next request, a dead worker is started again when there is work.", BuildingWorkerState.Stopped)
        .Allow(BuildingWorkerState.Stopped, BuildingWorkerState.Starting)
        .Allow(BuildingWorkerState.Starting, BuildingWorkerState.Idle, BuildingWorkerState.Down)
        .Allow(BuildingWorkerState.Idle, BuildingWorkerState.Running, BuildingWorkerState.Down)
        .Allow(BuildingWorkerState.Running, BuildingWorkerState.Idle, BuildingWorkerState.Queued, BuildingWorkerState.Down)
        .Allow(BuildingWorkerState.Queued, BuildingWorkerState.Running, BuildingWorkerState.Down)
        .Allow(BuildingWorkerState.Down, BuildingWorkerState.Starting)
        .FromAnywhere(BuildingWorkerState.Stopped)
        .Note(BuildingWorkerState.Stopped, "No process: before the first start, or the viewer is closing.")
        .Note(BuildingWorkerState.Starting, "The process is loading the map's work files (and warming up); no request goes before its ready line. Times out.")
        .Note(BuildingWorkerState.Idle, "Ready and nothing on the wire; changed files go out at once.")
        .Note(BuildingWorkerState.Running, "One request is on the wire (times out); its files are no longer pending.")
        .Note(BuildingWorkerState.Queued, "A request is on the wire and more files changed meanwhile: they go together in the next one after the answer.")
        .Note(BuildingWorkerState.Down, "The process died, did not start or timed out: killed, and started again after a short wait when files are pending.")
        .Build();
}
