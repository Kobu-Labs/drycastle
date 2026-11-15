using System.Numerics;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class AnimalShapeTests
{
    static readonly AnimalShapes Shipped = AnimalShapes.Load(Path.Combine(AppContext.BaseDirectory, "data", "animal_shapes.json"));

    static AnimalShape Deer(AnimalPose pose = AnimalPose.Idle, float seconds = 0) => Shipped.At("deer", pose, seconds)!.Value.Shape;

    /// <summary>The middle of the head cells of a shape, in the animal's frame (z ahead, y up).</summary>
    static (float Z, float Y) Head(AnimalShape s)
    {
        float z = 0, y = 0; var n = 0;
        for (var r = 0; r < s.Rows; r++)
            for (var c = 0; c < s.Cols; c++)
                if (s.IsHead(c, r)) { z += s.Z0 + (c + 0.5f) * s.Cell; y += (r + 0.5f) * s.Cell; n++; }
        Assert.True(n > 0);
        return (z / n, y / n);
    }

    /// <summary>A deer at the origin facing -Z, shot from its right side straight across at a point of its side plane (z metres ahead, y up).</summary>
    static RayHit? Broadside(AnimalShape s, float ahead, float up)
    {
        var t = new HitTarget(WildlifeRules.FirstHitId, Vector3.Zero, Stance.Stand, 0, HeadGuarded: true, Box: new Vector3(0.48f, 1.41f, 1.45f), BoxAhead: 0.19f, Shape: s);
        return HitMath.Raycast(new Vector3(40, up, -ahead), -Vector3.UnitX, 200, float.PositiveInfinity, new[] { t }, shooter: 1);
    }

    [Fact]
    public void The_shipped_file_has_the_free_species_in_every_pose()
    {
        foreach (var id in new[] { "deer", "stag", "wolf", "fox" })
            foreach (var pose in Enum.GetValues<AnimalPose>())
                Assert.NotNull(Shipped.Of(id, pose));
        Assert.Null(Shipped.Of("unicorn", AnimalPose.Idle));
        Assert.True(Shipped.Of("deer", AnimalPose.Eat)!.Frames.Count > 10);
    }

    [Fact]
    public void A_standing_deer_has_its_head_up_at_the_front()
    {
        var s = Deer();
        var (z, y) = Head(s);
        Assert.InRange(z, 0.6f, 0.95f);   // the nose is 0.91 m ahead of the origin
        Assert.InRange(y, 1.05f, 1.4f);
    }

    [Fact]
    public void A_bullet_hits_the_body_and_the_head_and_misses_the_air_inside_the_old_box()
    {
        var s = Deer();
        var (hz, hy) = Head(s);
        var body = Broadside(s, 0.1f, 0.85f);
        Assert.NotNull(body);
        Assert.False(body!.Value.ShapeHead);
        Assert.False(body.Value.Head); // never a headshot's damage
        var head = Broadside(s, hz, hy);
        Assert.True(head!.Value.ShapeHead);
        Assert.False(head.Value.Head);
        // all three are inside the box a bullet was tested against before
        Assert.Null(Broadside(s, -0.2f, 1.3f));  // over the back
        Assert.Null(Broadside(s, 0.15f, 0.25f)); // under the belly, between the legs
        Assert.Null(Broadside(s, 0.8f, 0.6f));   // under the head, ahead of the chest
    }

    [Fact]
    public void The_hit_is_on_the_near_flank_at_the_animals_half_width()
    {
        var hit = Broadside(Deer(), 0.1f, 0.85f)!.Value;
        Assert.InRange(40 - hit.Distance, 0.1f, 0.3f); // the body is 0.48 m wide
    }

    [Fact]
    public void A_grazing_deer_is_hit_at_the_head_where_it_is_and_not_where_it_was()
    {
        var up = Head(Deer());
        var down = Head(Deer(AnimalPose.Eat, 3f));
        Assert.True(down.Y < 0.7f && up.Y > 1.05f);
        var grazing = Deer(AnimalPose.Eat, 3f);
        Assert.True(Broadside(grazing, down.Z, down.Y)!.Value.ShapeHead);
        Assert.Null(Broadside(grazing, up.Z, up.Y));
        // and the head is back up when the clip comes round
        Assert.True(Head(Deer(AnimalPose.Eat, 0f)).Y > 1.05f);
    }

    [Fact]
    public void The_eat_frame_follows_the_time_and_loops_and_a_single_shape_ignores_it()
    {
        var eat = Shipped.Of("deer", AnimalPose.Eat)!;
        Assert.Equal(0, eat.FrameAt(0));
        Assert.Equal(15, eat.FrameAt(3f));
        Assert.Equal(eat.FrameAt(1f), eat.FrameAt(1f + eat.ClipSeconds));
        Assert.Equal(eat.FrameAt(eat.ClipSeconds - 0.5f), eat.FrameAt(-0.5f));
        Assert.Equal(0, Shipped.Of("deer", AnimalPose.Run)!.FrameAt(12.3f));
    }

    [Fact]
    public void A_running_shape_reaches_farther_than_a_standing_one()
    {
        var stand = Deer(); var run = Deer(AnimalPose.Run);
        Assert.True(run.Z0 < stand.Z0 - 0.2f);
        Assert.True(run.Z0 + run.Length > stand.Z0 + stand.Length);
    }

    [Fact]
    public void The_shape_turns_with_the_animal_and_a_wall_still_stops_the_bullet()
    {
        var s = Deer();
        var (hz, hy) = Head(s);
        // facing +X (a quarter turn to the right): the head is hz metres along +X
        var t = new HitTarget(WildlifeRules.FirstHitId, new Vector3(10, 0, 5), Stance.Stand, -MathF.PI / 2, Shape: s);
        var from = new Vector3(10 + hz, hy, 45);
        var hit = HitMath.Raycast(from, -Vector3.UnitZ, 200, float.PositiveInfinity, new[] { t }, 1);
        Assert.True(hit!.Value.ShapeHead);
        Assert.Null(HitMath.Raycast(from, -Vector3.UnitZ, 200, 20, new[] { t }, 1));
    }

    [Fact]
    public void A_state_maps_to_the_pose_a_client_plays_and_a_lying_animal_has_none()
    {
        Assert.Equal(AnimalPose.Idle, WildlifeKeeper.PoseOf(AnimalState.Idle));
        Assert.Equal(AnimalPose.Eat, WildlifeKeeper.PoseOf(AnimalState.Eat));
        Assert.Equal(AnimalPose.Walk, WildlifeKeeper.PoseOf(AnimalState.Walk));
        Assert.Equal(AnimalPose.Run, WildlifeKeeper.PoseOf(AnimalState.Flee));
        Assert.Null(WildlifeKeeper.PoseOf(AnimalState.Down));
        Assert.Null(WildlifeKeeper.PoseOf(AnimalState.Thrown));
    }

    [Fact]
    public void The_diagram_is_told_the_shape_and_where_on_its_grid_the_bullet_entered()
    {
        var s = Deer(AnimalPose.Eat, 3f);
        var (hz, hy) = Head(s);
        var t = new HitTarget(WildlifeRules.FirstHitId, Vector3.Zero, Stance.Stand, 0, Shape: s, ShapePose: AnimalPose.Eat, ShapeFrame: 15);
        var hit = HitMath.Raycast(new Vector3(40, hy, -hz), -Vector3.UnitX, 200, float.PositiveInfinity, new[] { t }, 1)!.Value;
        var m = HuntShot.From(t, new Vector3(40 - hit.Distance, hy, -hz), species: 3, distance: 140, weapon: 9, hit.ShapeHead);
        Assert.Equal((byte)AnimalPose.Eat, m.Pose);
        Assert.Equal(15, m.Frame);
        Assert.True(m.Head);
        Assert.True(m.RightFlank);
        Assert.Equal((hz - s.Z0) / s.Length, m.Along, 2);
        Assert.Equal(hy / s.Height, m.Height, 2);
        var back = HuntShot.Decode(HuntShot.Encode(m))!.Value;
        Assert.Equal(m.Pose, back.Pose);
        Assert.Equal(m.Frame, back.Frame);
        Assert.True(back.Head);
        Assert.Same(s, Shipped.Frame("deer", (AnimalPose)back.Pose, back.Frame));
    }

    [Fact]
    public void A_file_without_an_idle_shape_or_with_a_wrong_cell_count_is_refused()
    {
        Assert.Throws<DataFileException>(() => AnimalShapes.Parse("""{"version":1,"cell":0.05,"species":{"x":{"run":{"frames":[{"z0":0,"cols":1,"rows":1,"cells":"AQ=="}]}}}}"""));
        Assert.Throws<DataFileException>(() => AnimalShapes.Parse("""{"version":1,"cell":0.05,"species":{"x":{"idle":{"frames":[{"z0":0,"cols":2,"rows":1,"cells":"AQ=="}]}}}}"""));
        Assert.Throws<DataFileException>(() => AnimalShapes.Parse("""{"version":2,"cell":0.05,"species":{}}"""));
        Assert.Equal(1, AnimalShapes.Parse("""{"version":1,"cell":0.05,"species":{"x":{"idle":{"frames":[{"z0":0,"cols":1,"rows":1,"cells":"AQ=="}]}}}}""").Count);
    }
}

public class AnimalShapeDiagramTests
{
    static readonly AnimalShapes Shipped = AnimalShapes.Load(Path.Combine(AppContext.BaseDirectory, "data", "animal_shapes.json"));

    static (int Col, int Row) CellOf(AnimalShape s, (float Along, float Height) at) => ((int)(at.Along * s.Cols), (int)(at.Height * s.Rows));

    [Fact]
    public void Only_the_standing_shape_has_the_picture()
    {
        Assert.NotNull(Shipped.Frame("deer", AnimalPose.Idle, 0)!.Picture);
        Assert.Null(Shipped.Frame("deer", AnimalPose.Eat, 15)!.Picture);
        Assert.Null(Shipped.Frame("deer", AnimalPose.Run, 0)!.Picture);
    }

    [Fact]
    public void A_hit_on_the_standing_shape_stays_where_it_is()
    {
        var s = Shipped.Frame("deer", AnimalPose.Idle, 0)!;
        Assert.Equal((0.4f, 0.6f), s.MapOnto(s, 0.4f, 0.6f, false));
    }

    [Fact]
    public void A_head_hit_while_grazing_is_shown_on_the_standing_head_and_a_body_hit_on_the_body()
    {
        var standing = Shipped.Frame("deer", AnimalPose.Idle, 0)!;
        foreach (var (pose, frame) in new[] { (AnimalPose.Eat, 15), (AnimalPose.Run, 0), (AnimalPose.Walk, 0) })
        {
            var from = Shipped.Frame("deer", pose, frame)!;
            for (var r = 0; r < from.Rows; r++)
                for (var c = 0; c < from.Cols; c++)
                {
                    if (!from.Covered(c, r)) continue;
                    var head = from.IsHead(c, r);
                    var (col, row) = CellOf(standing, from.MapOnto(standing, (c + 0.5f) / from.Cols, (r + 0.5f) / from.Rows, head));
                    Assert.True(standing.Covered(col, row), $"{pose} {c},{r} -> {col},{row}");
                    Assert.Equal(head, standing.IsHead(col, row));
                }
        }
    }

    [Fact]
    public void The_front_of_the_body_stays_at_the_front_and_the_rear_at_the_rear()
    {
        var standing = Shipped.Frame("deer", AnimalPose.Idle, 0)!;
        var run = Shipped.Frame("deer", AnimalPose.Run, 0)!;
        var rear = run.MapOnto(standing, 0.15f, 0.6f, false);
        var front = run.MapOnto(standing, 0.7f, 0.6f, false);
        Assert.True(front.Along > rear.Along + 0.3f);
    }
}
