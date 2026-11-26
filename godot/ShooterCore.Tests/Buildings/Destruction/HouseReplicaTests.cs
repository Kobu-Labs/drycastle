using System.Buffers.Binary;
using System.Numerics;
using Drycastle.ShooterCore.Buildings.Destruction;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Buildings.Destruction;

public class HouseReplicaTests
{
    static byte[] Carve(ushort house, ushort seq, short cx, short cy, short cz, ushort r, uint hash)
    {
        var b = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(b, house);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), seq);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(4), cx);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(6), cy);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(8), cz);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), r);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), hash);
        return b;
    }

    static HouseReplicaWire.CellsPart Part(ushort seq, uint hash, uint first, params uint[] runs)
    {
        var o = new List<byte>();
        foreach (var r in runs) Varint.Write(o, r);
        return new HouseReplicaWire.CellsPart(0, seq, hash, first, o.ToArray());
    }

    [Fact]
    public void Wire_decodes_carve_cells_and_settings()
    {
        var c = HouseReplicaWire.DecodeCarve(Carve(3, 7, -5, 12, 40, 32, 0xDEADBEEF))!.Value;
        Assert.Equal(new HouseReplicaWire.Carve(3, 7, new CarveOp(-5, 12, 40, 32), 0xDEADBEEF), c);
        Assert.Null(HouseReplicaWire.DecodeCarve(new byte[15]));

        var p = new byte[14];
        BinaryPrimitives.WriteUInt16LittleEndian(p, 9);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), 77);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), 2);
        p[12] = 5; p[13] = 1;
        var cells = HouseReplicaWire.DecodeCells(p)!.Value;
        Assert.Equal((9, 4, 77u, 2u), (cells.House, cells.OpSeq, cells.Hash, cells.FirstRun));
        Assert.Equal(new byte[] { 5, 1 }, cells.Runs.ToArray());

        Assert.Equal(new HouseReplicaWire.Settings(true, false), HouseReplicaWire.DecodeSettings([1, 0]));
        Assert.Equal(new HouseReplicaWire.Settings(false, true), HouseReplicaWire.DecodeSettings([0, 1]));
        Assert.Equal(new byte[] { 0x34, 0x12 }, HouseReplicaWire.EncodeResync(0x1234));
    }

    [Fact]
    public void A_gap_in_op_seq_is_a_desync_and_later_ops_wait_for_the_cells()
    {
        var r = new HouseReplica(TestHouses.Box(8, 8, 8));
        var cfg = DestructionConfig.Default;
        var (o, res) = r.OnCarve(new HouseReplicaWire.Carve(0, 2, new CarveOp(8, 8, 8, 4), 0), cfg); // op 1 never came
        Assert.Equal(HouseReplica.CarveOutcome.Desync, o);
        Assert.Null(res);
        Assert.True(r.AwaitingResync);
        Assert.Equal(HouseReplica.CarveOutcome.Ignored, r.OnCarve(new HouseReplicaWire.Carve(0, 3, new CarveOp(8, 8, 8, 4), 0), cfg).Outcome);
        r.Reset();
        Assert.False(r.AwaitingResync);
        Assert.Equal(HouseReplica.CarveOutcome.Ignored, r.OnCarve(new HouseReplicaWire.Carve(0, 0, new CarveOp(8, 8, 8, 4), 0), cfg).Outcome); // stale
    }

    [Fact]
    public void Cell_parts_must_continue_the_list()
    {
        var r = new HouseReplica(TestHouses.Box(8, 8, 8));
        Assert.True(r.SolidCount > 10);
        Assert.Equal(HouseReplica.CellsOutcome.Partial, r.OnCells(Part(5, 9, 0, 3, 2)));
        Assert.Equal(HouseReplica.CellsOutcome.Dropped, r.OnCells(Part(5, 9, 3, 1))); // the next part starts at run 2
        Assert.Equal(HouseReplica.CellsOutcome.Dropped, r.OnCells(Part(5, 9, 2, 1))); // dropped: waits for a new start
        Assert.Equal(HouseReplica.CellsOutcome.Partial, r.OnCells(Part(5, 9, 0, 3, 2)));
        Assert.Equal(HouseReplica.CellsOutcome.Dropped, r.OnCells(Part(6, 9, 2, 1))); // another op seq
        Assert.Equal(HouseReplica.CellsOutcome.Dropped, r.OnCells(Part(5, 9, 0, (uint)r.SolidCount + 1))); // more cells than the house has
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
