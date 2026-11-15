namespace Drycastle.ShooterCore;

/// <summary>The polygon tools of the map editor (Ground patches, Zones): what the pointer and the keys do next. See <see cref="EditorMachines.ShapeSpec"/>.</summary>
public enum EditorShapeState { Idle, Drawing, Level, Selected, Dragging }

/// <summary>The sketch tool of the map editor: a line being drawn, a mark selected, or neither. See <see cref="EditorMachines.SketchSpec"/>.</summary>
public enum EditorSketchState { Idle, Drawing, Selected }

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
}
