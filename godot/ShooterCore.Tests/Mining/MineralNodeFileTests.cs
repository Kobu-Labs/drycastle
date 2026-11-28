using System.Numerics;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Mining;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Mining;

public class MineralNodeFileTests
{
    const string Doc = """
        {"version": 1, "nodes": [
          {"id": "MN-0001", "mineral": "smetiskomite", "x": 236.1, "y": 659.1, "z": 193.8},
          {"id": "MN-0002", "mineral": "zombite", "x": 58.0, "y": -482.5, "z": 190.0}]}
        """;

    [Fact]
    public void ParsesLocalMetresToGodotGroundAxes()
    {
        var nodes = MineralNodeFile.Parse(Doc);
        Assert.Equal(2, nodes.Count);
        Assert.Equal(("MN-0001", "smetiskomite"), (nodes[0].Id, nodes[0].MineralId));
        Assert.Equal(new Vector3(236.1f, 193.8f, -659.1f), nodes[0].Position); // x east, y up, z = -north
        Assert.Equal(482.5f, nodes[1].Position.Z);
    }

    [Fact]
    public void NoFileIsNoNodesAndABrokenOneIsReported()
    {
        Assert.Empty(MineralNodeFile.FromJson(null));
        Assert.Empty(MineralNodeFile.FromJson(""));
        string? error = null;
        Assert.Empty(MineralNodeFile.FromJsonOrEmpty("""{"version": 2, "nodes": []}""", m => error = m));
        Assert.Contains("version 2", error);
        Assert.Throws<DataFileException>(() => MineralNodeFile.Parse("""{"version": 1, "nodes": [{"id": "MN-0001", "mineral": "opal", "x": 1}]}"""));
    }

    [Fact]
    public void BuildsStaticNodesInFileOrderAndSkipsAnUnknownMineral()
    {
        var specs = MineralNodeFile.Parse(Doc).Append(new MineralNodeSpec("MN-0003", "gold", default)).ToList();
        var set = new MineralNodeSet();
        var skipped = new List<string>();
        Assert.Equal(2, MineralNodeFile.AddTo(set, specs, skipped.Add));
        Assert.Single(skipped);
        Assert.Contains("gold", skipped[0]);
        Assert.Equal(new[] { "smetiskomite", "zombite" }, set.Nodes.Select(n => n.MineralId));
        Assert.Equal(new byte[] { 0, 1 }, set.Nodes.Select(n => n.Id));
        Assert.All(set.Nodes, n => Assert.False(MineralNodeSet.IsDynamic(n))); // a static node never goes away
        Assert.Equal(new Vector3(236.1f, 193.8f, -659.1f), set.Find(0)!.Position);
    }

    [Fact]
    public void ANodeSurvivesTheRoundAndTheClientCodec()
    {
        var set = new MineralNodeSet();
        MineralNodeFile.AddTo(set, MineralNodeFile.Parse(Doc), _ => { });
        Assert.False(set.ClearDrops());
        var back = MineralRules.DecodeNodes(MineralRules.EncodeNodes(set.Nodes.Select(n => (n, true)).ToList()))!;
        Assert.Equal(set.Nodes, back.Select(b => b.Node));
    }
}
