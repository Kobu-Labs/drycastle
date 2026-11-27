using Xunit;
using Drycastle.ShooterCore.Editor;
using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Tests.Editor;

/// <summary>The Photo to building tool's pure parts: the build process's progress lines, the geometry of openings on walls, the machine.</summary>
public class PhotoBuildTests
{
    [Fact]
    public void Stage_lines_give_name_and_position()
    {
        var l = PhotoBuildLog.Parse("STAGE: read ends 2/5");
        Assert.Equal(PhotoLineKind.Stage, l.Kind);
        Assert.Equal("read ends", l.Stage);
        Assert.Equal((2, 5), (l.Index, l.Count));
        Assert.Equal("Asking the model where each wall stops", PhotoBuildLog.StageText(l.Stage));
    }

    [Fact]
    public void Problem_made_up_and_error_lines_are_told_apart()
    {
        Assert.Equal("no ridge", PhotoBuildLog.Parse("PROBLEM: no ridge").Text);
        Assert.Equal(PhotoLineKind.Made, PhotoBuildLog.Parse("MADE UP BY RULE (not seen in a capture): windows on edge 2; a chimney").Kind);
        Assert.Equal("windows on edge 2; a chimney", PhotoBuildLog.Parse("MADE UP BY RULE (not seen in a capture): windows on edge 2; a chimney").Text);
        Assert.Equal(PhotoLineKind.Error, PhotoBuildLog.Parse("ERROR: ANTHROPIC_API_KEY is not set").Kind);
        Assert.Equal(PhotoLineKind.Other, PhotoBuildLog.Parse("capture 0: 412 straight segments").Kind);
    }

    [Fact]
    public void Result_line_is_read()
    {
        var l = PhotoBuildLog.Parse("RESULT: {\"building\":\"w1\",\"parcel\":\"P-0001\",\"method\":\"captures\",\"run\":\"/x/run\",\"overlays\":[\"/x/run/overlay_0.png\"],\"compares\":[],\"problems\":[\"a\"],\"invented\":[\"b\",\"c\"],\"roof\":\"gabled\",\"eave_m\":3.35,\"top_m\":6.09,\"openings\":4,\"can_redo_ends\":true,\"replaced\":false}");
        Assert.Equal(PhotoLineKind.Result, l.Kind);
        var r = l.Result!;
        Assert.Equal(("w1", "P-0001", "gabled", 4), (r.Building, r.Parcel, r.Roof, r.Openings));
        Assert.Equal(2, r.Invented.Count);
        Assert.True(r.CanRedoEnds);
        Assert.Single(r.Overlays);
    }

    [Fact]
    public void A_broken_result_line_is_an_error_not_a_crash()
        => Assert.Equal(PhotoLineKind.Error, PhotoBuildLog.Parse("RESULT: {oops").Kind);

    // a wall along +x from (0,0) to (10,0), outward normal -y (counter-clockwise ring seen from above: the south wall)
    static readonly WallEdge South = new(0, 0, 0, 10, 0, 0, -1);

    [Fact]
    public void Opening_corners_stand_on_the_wall_at_ground_plus_sill()
    {
        var c = BuildingGeom.Corners(South, new OpeningSpec("w1", 0, 2.0, 1.2, 1.3, 1.0), baseZ: 100.0, lift: 0.1);
        Assert.Equal((2.0, -0.1, 101.0), c[0]);
        Assert.Equal((3.2, -0.1, 101.0), c[1]);
        Assert.Equal((3.2, -0.1, 102.3), c[2]);
    }

    [Fact]
    public void A_ray_from_outside_picks_the_opening_it_goes_through_and_the_nearest_wins()
    {
        var ops = new[] { new OpeningSpec("w1", 0, 2.0, 1.2, 1.3, 1.0), new OpeningSpec("d1", 0, 6.0, 1.0, 2.1, 0.0) };
        Assert.Equal("w1", BuildingGeom.Pick(new[] { South }, ops, 0, (2.6, -8, 1.6), (0, 1, 0)));
        Assert.Equal("d1", BuildingGeom.Pick(new[] { South }, ops, 0, (6.5, -8, 1.0), (0, 1, 0)));
        Assert.Null(BuildingGeom.Pick(new[] { South }, ops, 0, (4.5, -8, 1.6), (0, 1, 0)));          // wall between them
        Assert.Null(BuildingGeom.Pick(new[] { South }, ops, 0, (2.6, 8, 1.6), (0, -1, 0)));          // from inside the house
        Assert.Equal("w1", BuildingGeom.Pick(new[] { South }, ops, 0, (2.6, -8, 1.6), (0.0, 1, 0), margin: 0.0));
    }

    [Fact]
    public void Picking_allows_a_little_slack_around_the_opening()
    {
        var ops = new[] { new OpeningSpec("w1", 0, 2.0, 1.2, 1.3, 1.0) };
        Assert.Equal("w1", BuildingGeom.Pick(new[] { South }, ops, 0, (1.95, -8, 1.6), (0, 1, 0)));
        Assert.Null(BuildingGeom.Pick(new[] { South }, ops, 0, (1.5, -8, 1.6), (0, 1, 0)));
    }

    [Fact]
    public void Next_id_follows_the_pipelines_naming()
    {
        Assert.Equal("w3", Nudge.NextId("window", new[] { "w1", "w2", "d1" }));
        Assert.Equal("d2", Nudge.NextId("door", new[] { "d1" }));
        Assert.Equal("g1", Nudge.NextId("garage_door", new[] { "w1" }));
        Assert.Equal(0.25, Nudge.Step(0.05, true), 6);
    }

    [Fact]
    public void The_machine_is_listed_in_the_catalog()
        => Assert.Contains("Editor.PhotoBuild", StateMachineCatalog.All.Select(s => s.Name).ToList());

    [Fact]
    public void Photo_build_machine_paths()
    {
        var m = new StateMachine<PhotoBuildState>(EditorMachines.PhotoBuildSpec);
        m.Enter(PhotoBuildState.Building);
        m.Enter(PhotoBuildState.Cancelling);
        m.Enter(PhotoBuildState.Idle);
        m.Enter(PhotoBuildState.Review);      // the last run found on disk
        m.Enter(PhotoBuildState.Building);    // re-ask
        m.Enter(PhotoBuildState.Failed);
        m.Enter(PhotoBuildState.Building);    // try again
        m.Enter(PhotoBuildState.Review);
        m.Enter(PhotoBuildState.Idle);
        Assert.True(m.Is(PhotoBuildState.Idle));
        Assert.Empty(EditorMachines.PhotoBuildSpec.Unreachable);
        Assert.False(EditorMachines.PhotoBuildSpec.Allows(PhotoBuildState.Cancelling, PhotoBuildState.Review)); // a stopped build is not a result
    }
}
