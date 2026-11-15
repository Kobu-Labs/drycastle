using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The map editor's machine specs (godot/viewer EditorMode): the paths the viewer takes are declared, and the ones it must never take are not.</summary>
public class EditorMachinesTests
{
    static StateMachine<EditorShapeState> Shape() => new(EditorMachines.ShapeSpec);
    static StateMachine<EditorSketchState> Sketch() => new(EditorMachines.SketchSpec);

    [Fact]
    public void Specs_are_listed_in_the_catalog_under_their_names()
    {
        var names = StateMachineCatalog.All.Select(s => s.Name).ToList();
        Assert.Contains("Editor.Shape", names);
        Assert.Contains("Editor.Sketch", names);
    }

    [Fact]
    public void Shape_draws_a_water_patch_and_moves_a_corner()
    {
        var m = Shape();
        m.Enter(EditorShapeState.Drawing);   // N or the first corner
        m.Enter(EditorShapeState.Level);     // closed, asks for the water level
        m.Enter(EditorShapeState.Drawing);   // Esc: back to the corners
        m.Enter(EditorShapeState.Level);
        m.Enter(EditorShapeState.Selected);  // created
        m.Enter(EditorShapeState.Dragging);  // corner grabbed
        m.Enter(EditorShapeState.Selected);  // released
        m.Enter(EditorShapeState.Idle);      // deselected
        Assert.True(m.Is(EditorShapeState.Idle));
    }

    [Fact]
    public void Shape_drag_ends_in_idle_when_the_shape_is_gone()
    {
        var m = Shape();
        m.Enter(EditorShapeState.Selected);
        m.Enter(EditorShapeState.Dragging);
        m.Enter(EditorShapeState.Idle);
        Assert.True(m.Is(EditorShapeState.Idle));
    }

    [Theory]
    [InlineData(EditorShapeState.Idle, EditorShapeState.Dragging)]   // a drag needs a selected shape
    [InlineData(EditorShapeState.Idle, EditorShapeState.Level)]      // the water level step follows closing a drawn shape
    [InlineData(EditorShapeState.Selected, EditorShapeState.Level)]
    [InlineData(EditorShapeState.Dragging, EditorShapeState.Drawing)] // a gesture ends before the next begins
    [InlineData(EditorShapeState.Dragging, EditorShapeState.Level)]
    [InlineData(EditorShapeState.Drawing, EditorShapeState.Dragging)]
    public void Shape_refuses_undeclared_edges(EditorShapeState from, EditorShapeState to)
    {
        Assert.False(EditorMachines.ShapeSpec.Allows(from, to));
        var m = Shape();
        m.Reset(from);
        var seen = new List<string>();
        using (StateMachineDiagnostics.Capture(seen)) m.Enter(to);
        Assert.Single(seen);
    }

    [Fact]
    public void Sketch_draws_a_line_and_selects_a_mark()
    {
        var m = Sketch();
        m.Enter(EditorSketchState.Drawing);   // first corner
        m.Enter(EditorSketchState.Selected);  // line finished while a mark was selected
        m.Enter(EditorSketchState.Drawing);
        m.Enter(EditorSketchState.Idle);      // cancelled
        m.Enter(EditorSketchState.Selected);
        m.Enter(EditorSketchState.Idle);
        Assert.True(m.Is(EditorSketchState.Idle));
    }

    [Fact]
    public void Every_editor_state_is_reachable()
    {
        Assert.Empty(EditorMachines.ShapeSpec.Unreachable);
        Assert.Empty(EditorMachines.SketchSpec.Unreachable);
    }
}
