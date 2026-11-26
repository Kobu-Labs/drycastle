using System.Numerics;
using Drycastle.ShooterCore.Buildings.Destruction;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Buildings.Destruction;

/// <summary>Server cells -> HouseCodec -> client replica, as the game wires it (no Godot): ops, a late joiner's 243 catch-up, round reset.</summary>
public class EndToEndTests
{
    static readonly DestructionConfig Cfg = DestructionConfig.Default;

    static void AssertSame(HouseCells server, HouseCells client)
    {
        Assert.Equal(server.OpSeq, client.OpSeq);
        Assert.Equal(server.Hash(), client.Hash());
        for (var i = 0; i < server.Grid.Count; i++) Assert.Equal(server.Solid(i), client.Solid(i));
    }

    [Fact]
    public void Ops_catch_up_and_reset_keep_server_and_clients_equal()
    {
        var g = TestHouses.Box(24, 20, 16);
        var server = new HouseCells(g);
        var early = new HouseReplica(g);
        var blasts = new (Vector3 Grid, float R)[] { (new(1.5f, 10, 5), 1.5f), (new(12, 1.5f, 6), 1f), (new(22.5f, 6, 8), 2f), (new(12, 10, 12), 1.25f), (new(1.5f, 10, 5), 0.5f) };
        foreach (var (at, r) in blasts)
        {
            var op = CarveOp.From(g, g.ToMap(at), r)!.Value;
            var res = server.Apply(op, Cfg);
            var wire = HouseCodec.EncodeCarve(new HouseCarveMsg(g.Index, server.OpSeq, op, res.Hash));
            var (outcome, got) = early.OnCarve(HouseCodec.DecodeCarve(wire)!.Value, Cfg);
            Assert.Equal(HouseReplica.CarveOutcome.Applied, outcome);
            Assert.Equal(res.Hash, got!.Hash);
            AssertSame(server, early.Cells);
        }
        Assert.True(server.Damaged);

        // a late joiner: 244 then the 243 set, cut into several messages
        var late = new HouseReplica(g);
        var settings = HouseCodec.DecodeSettings(HouseCodec.EncodeSettings(new HouseSettingsMsg(true, false)))!.Value;
        Assert.False(settings.Reset);
        var msgs = HouseCodec.EncodeCells(server, 24);
        Assert.True(msgs.Count > 2, $"{msgs.Count} messages");
        for (var i = 0; i < msgs.Count; i++)
            Assert.Equal(i == msgs.Count - 1 ? CatchUpStep.Done : CatchUpStep.Pending, late.OnCells(HouseCodec.DecodeCells(msgs[i])!));
        AssertSame(server, late.Cells);

        // both follow the next op
        var next = CarveOp.From(g, g.ToMap(new Vector3(12, 18.5f, 6)), 1f)!.Value;
        var nr = server.Apply(next, Cfg);
        var m = HouseCodec.DecodeCarve(HouseCodec.EncodeCarve(new HouseCarveMsg(g.Index, server.OpSeq, next, nr.Hash)))!.Value;
        Assert.Equal(HouseReplica.CarveOutcome.Applied, early.OnCarve(m, Cfg).Outcome);
        Assert.Equal(HouseReplica.CarveOutcome.Applied, late.OnCarve(m, Cfg).Outcome);
        AssertSame(server, early.Cells);
        AssertSame(server, late.Cells);

        // round reset: 244 reset
        server.Restore();
        var reset = HouseCodec.DecodeSettings(HouseCodec.EncodeSettings(new HouseSettingsMsg(false, true)))!.Value;
        Assert.True(reset.Reset);
        early.Reset(); late.Reset();
        Assert.False(early.Cells.Damaged);
        AssertSame(server, early.Cells);
        AssertSame(server, late.Cells);
        Assert.Equal(new HouseCells(g).Hash(), server.Hash());
    }
}
