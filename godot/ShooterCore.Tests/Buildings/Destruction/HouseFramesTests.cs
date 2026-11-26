using System.Numerics;
using Drycastle.ShooterCore.Buildings.Destruction;
using Drycastle.ShooterCore.Glass;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Buildings.Destruction;

public class HouseFramesTests
{
    /// <summary>A pane in the box house's door opening (x 8..11, z 2..9 of the wall at y = 1), in Godot map local metres.</summary>
    static (HouseGrid G, GlassPaneRect R) DoorPane()
    {
        var g = TestHouses.Box(20, 16, 20, yaw: 0.4f, origin: new Vector3(5, -3, 2));
        var centre = HouseFrame.GridToGodot(g, new Vector3(10, 1.5f, 6));
        var normal = HouseFrame.GridToGodot(g, new Vector3(10, 0.5f, 6)) - centre; // out of the house (-y)
        return (g, GlassPaneRect.Make(centre, normal, 1.0f, 2.0f));
    }

    [Fact]
    public void The_frame_is_the_wall_around_the_opening()
    {
        var (g, r) = DoorPane();
        var frame = HouseFrames.FrameCells(g, r);
        Assert.NotEmpty(frame);
        Assert.Contains(g.IndexOf(7, 1, 6), frame);   // beside the opening
        Assert.Contains(g.IndexOf(10, 1, 10), frame); // over it
        Assert.DoesNotContain(g.IndexOf(3, 1, 6), frame);
        Assert.DoesNotContain(g.IndexOf(10, 14, 6), frame); // the far wall
    }

    [Fact]
    public void Only_a_carve_into_the_frame_unframes_the_pane()
    {
        var (g, r) = DoorPane();
        var frame = HouseFrames.FrameCells(g, r);
        var c = new HouseCells(g);
        Assert.False(HouseFrames.Unframed(c, frame));
        c.Remove(g.IndexOf(3, 1, 6));
        Assert.False(HouseFrames.Unframed(c, frame));
        c.Remove(g.IndexOf(7, 1, 6));
        Assert.True(HouseFrames.Unframed(c, frame));
    }

    [Fact]
    public void A_doorway_is_framed_by_the_wall_around_it()
    {
        var g = TestHouses.Box(20, 16, 20);
        var foot = HouseFrame.GridToGodot(g, new Vector3(10, 1.5f, 2));
        var outward = HouseFrame.GridToGodot(g, new Vector3(10, 0.5f, 2)) - foot;
        var yaw = MathF.Atan2(-outward.X, -outward.Z); // Doorway.Out = (-sin yaw, 0, -cos yaw)
        var way = new Drycastle.ShooterCore.Buildings.Doorway(foot, yaw, 1.0f, 2.0f);
        Assert.True(Vector3.Distance(way.Out, Vector3.Normalize(outward)) < 1e-4f);
        var frame = HouseFrames.FrameCells(g, HouseFrames.Rect(way));
        Assert.Contains(g.IndexOf(7, 1, 6), frame);
        Assert.Contains(g.IndexOf(10, 1, 10), frame);
        Assert.DoesNotContain(g.IndexOf(3, 1, 6), frame);
    }

    [Fact]
    public void A_blown_door_cannot_be_used_or_reached_and_is_hung_again_shut()
    {
        var way = new Drycastle.ShooterCore.Buildings.Doorway(Vector3.Zero, 0, 1f, 2f);
        var doors = new Drycastle.ShooterCore.Buildings.HouseDoors([new(way, Drycastle.ShooterCore.Buildings.HouseDoorKind.Hinged, "#000", "h")]);
        Assert.True(doors.Use(0, false));
        Assert.True(doors.Blow(0));
        Assert.False(doors.Blow(0));
        Assert.False(doors.IsOpen(0));
        Assert.False(doors.Use(0, false));
        Assert.Equal(-1, doors.Nearest(new Vector3(0, 0, -0.5f)));
        Assert.True(doors.Rehang(0));
        Assert.Equal(Drycastle.ShooterCore.Buildings.HouseDoorState.Shut, doors.StateOf(0));
        Assert.Equal(0, doors.Nearest(new Vector3(0, 0, -0.5f)));
    }

    [Fact]
    public void Windows_json_gives_the_panes_building()
    {
        var panes = MapGlass.Parse("""{"version":1,"windows":[{"building":"w1","centre":[1,2,3],"normal":[1,0],"width_m":1,"height_m":1,"node":"window_w1_a"},{"centre":[1,2,3],"normal":[1,0],"width_m":1,"height_m":1,"node":"b"}]}""");
        Assert.Equal("w1", panes.Single(p => p.Node == "window_w1_a").Building);
        Assert.Equal("", panes.Single(p => p.Node == "b").Building);
    }
}
