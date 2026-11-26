using System.Numerics;
using Drycastle.ShooterCore.Buildings.Destruction;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Buildings.Destruction;

public class HouseReplicaTests
{
    static readonly DestructionConfig Cfg = DestructionConfig.Default;

    [Fact]
    public void A_gap_in_op_seq_is_a_desync_and_later_ops_wait_for_the_cells()
    {
        var r = new HouseReplica(TestHouses.Box(8, 8, 8));
        var (o, res) = r.OnCarve(new HouseCarveMsg(0, 2, new CarveOp(8, 8, 8, 4), 0), Cfg); // op 1 never came
        Assert.Equal(HouseReplica.CarveOutcome.Desync, o);
        Assert.Null(res);
        Assert.True(r.AwaitingResync);
        Assert.Equal(HouseReplica.CarveOutcome.Ignored, r.OnCarve(new HouseCarveMsg(0, 3, new CarveOp(8, 8, 8, 4), 0), Cfg).Outcome);
        r.Reset();
        Assert.False(r.AwaitingResync);
        Assert.Equal(HouseReplica.CarveOutcome.Ignored, r.OnCarve(new HouseCarveMsg(0, 0, new CarveOp(8, 8, 8, 4), 0), Cfg).Outcome); // stale
    }

    [Fact]
    public void Cells_from_the_codec_end_a_resync_and_a_gap_breaks_the_set()
    {
        var g = TestHouses.Box(16, 12, 10);
        var server = new HouseCells(g);
        server.Apply(CarveOp.From(g, g.ToMap(new Vector3(1.5f, 6, 4)), 1f)!.Value, Cfg);
        server.Apply(CarveOp.From(g, g.ToMap(new Vector3(8, 0.5f, 5)), 0.75f)!.Value, Cfg);
        var msgs = HouseCodec.EncodeCells(server, HouseCodec.CellsHeaderBytes + 5).Select(b => HouseCodec.DecodeCells(b)!).ToList();
        Assert.True(msgs.Count >= 3);

        var r = new HouseReplica(g);
        r.MarkAwaiting();
        Assert.Equal(CatchUpStep.Pending, r.OnCells(msgs[0]));
        Assert.Equal(CatchUpStep.Broken, r.OnCells(msgs[2])); // msgs[1] lost
        Assert.True(r.AwaitingResync);
        for (var i = 0; i < msgs.Count; i++) Assert.Equal(i == msgs.Count - 1 ? CatchUpStep.Done : CatchUpStep.Pending, r.OnCells(msgs[i]));
        Assert.False(r.AwaitingResync);
        Assert.Equal((server.OpSeq, server.Hash()), (r.Cells.OpSeq, r.Cells.Hash()));
    }

    [Fact]
    public void Removed_layer_marks_removed_solid_cells_only()
    {
        var g = TestHouses.Box(6, 5, 7);
        var r = new HouseReplica(g);
        var wall = g.IndexOf(1, 2, 3);
        Assert.True(g.Original(wall));
        r.Cells.Remove(wall);
        var layer = new byte[6 * 5];
        r.FillRemovedLayer(3, layer);
        Assert.Equal(255, layer[1 + 6 * 2]);
        Assert.Equal(1, layer.Count(b => b != 0));
        r.FillRemovedLayer(2, layer);
        Assert.All(layer, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Frame_maps_grid_to_godot_and_back()
    {
        var g = TestHouses.Box(8, 8, 8, origin: new Vector3(10, 20, 3), yaw: 0.7f);
        var p = new Vector3(2.5f, 3.25f, 4);
        var godot = HouseFrame.GridToGodot(g, p);
        var map = g.ToMap(p);
        Assert.Equal(new Vector3(map.X, map.Z, -map.Y), godot);
        Assert.True(Vector3.Distance(p, HouseFrame.GodotToGrid(g, godot)) < 1e-4f);
        var (ax, ay, az, o) = HouseFrame.Affine(g);
        Assert.True(Vector3.Distance(godot, o + ax * p.X + ay * p.Y + az * p.Z) < 1e-4f);
        Assert.True(Vector3.Distance(az, new Vector3(0, 0.25f, 0)) < 1e-6f); // grid up is Godot up
        var (centre, size, yaw) = HouseFrame.Box(g, new Vector3(1, 2, 3), new Vector3(5, 3, 7));
        Assert.True(Vector3.Distance(centre, HouseFrame.GridToGodot(g, new Vector3(3, 2.5f, 5))) < 1e-4f);
        Assert.Equal(new Vector3(1f, 1f, 0.25f), size);
        Assert.Equal(0.7f, yaw);
        // the box's own x axis (Godot basis rotated by yaw about +y) is grid x
        var bx = new Vector3(MathF.Cos(yaw), 0, -MathF.Sin(yaw));
        Assert.True(Vector3.Distance(bx * 0.25f, ax) < 1e-5f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Triangles_face_their_normal_in_godot_winding(bool swapEdges)
    {
        var g = TestHouses.Box(8, 8, 8, yaw: 0.3f);
        Vector3 u = Vector3.UnitX, v = Vector3.UnitY;
        if (swapEdges) (u, v) = (v, u);
        var quads = new List<Quad> { new(new Vector3(1, 1, 4), u, v, Vector3.UnitZ, TestHouses.PWall) };
        var pos = new List<Vector3>();
        var nrm = new List<Vector3>();
        HouseFrame.Triangles(g, quads, pos, nrm);
        Assert.Equal(6, pos.Count);
        Assert.Equal(6, nrm.Count);
        Assert.True(Vector3.Distance(nrm[0], Vector3.UnitY) < 1e-5f);
        for (var t = 0; t < 6; t += 3)
            Assert.True(Vector3.Dot(Vector3.Cross(pos[t + 1] - pos[t], pos[t + 2] - pos[t]), nrm[t]) < 0); // clockwise seen from the front
    }
}
