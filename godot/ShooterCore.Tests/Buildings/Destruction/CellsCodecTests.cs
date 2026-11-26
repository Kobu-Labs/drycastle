using Drycastle.ShooterCore.Buildings.Destruction;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Buildings.Destruction;

public class CellsCodecTests
{
    [Fact]
    public void Carve_settings_and_resync_round_trip()
    {
        var m = new HouseCarveMsg(513, 65535, new CarveOp(-12, 300, short.MaxValue, 224), 0xDEADBEEF);
        var b = HouseCodec.EncodeCarve(m);
        Assert.Equal(16, b.Length);
        Assert.Equal(m, HouseCodec.DecodeCarve(b));
        Assert.Null(HouseCodec.DecodeCarve(b[..^1]));

        foreach (var s in new[] { new HouseSettingsMsg(true, false), new HouseSettingsMsg(false, true) })
            Assert.Equal(s, HouseCodec.DecodeSettings(HouseCodec.EncodeSettings(s)));
        Assert.Equal([1, 0], HouseCodec.EncodeSettings(new HouseSettingsMsg(true, false)));
        Assert.Null(HouseCodec.DecodeSettings([1]));

        Assert.Equal((ushort)777, HouseCodec.DecodeResync(HouseCodec.EncodeResync(777)));
        Assert.Null(HouseCodec.DecodeResync([1, 2, 3]));
    }

    static HouseCells Shredded()
    {
        // every third non-foundation solid cell removed: many runs, so the mask needs several messages
        var c = new HouseCells(TestHouses.Box(30, 24, 20, index: 5));
        var k = 0;
        for (var i = 0; i < c.Grid.Count; i++)
            if (c.Grid.Original(i) && c.Grid.Material(i) != CellMaterial.Foundation && k++ % 3 == 0) c.Remove(i);
        c.SetOpSeq(42);
        return c;
    }

    [Fact]
    public void Cells_split_into_200_byte_messages_and_join_back()
    {
        var src = Shredded();
        var msgs = HouseCodec.EncodeCells(src);
        Assert.True(msgs.Count > 3);
        Assert.All(msgs, b => Assert.InRange(b.Length, 13, 200));
        var dec = msgs.Select(b => HouseCodec.DecodeCells(b)!).ToList();
        Assert.Equal(0u, dec[0].FirstRun);
        for (var i = 1; i < dec.Count; i++) Assert.Equal(dec[i - 1].FirstRun + (uint)dec[i - 1].RunCount, dec[i].FirstRun);
        Assert.Equal(src.EncodeRemoved(), dec.SelectMany(d => d.Runs).ToArray());
        Assert.All(dec, d => Assert.Equal((5, 42, src.Hash()), (d.House, d.OpSeq, d.Hash)));

        var dst = new HouseCells(src.Grid);
        var join = new HouseCellsCatchUp();
        for (var i = 0; i < dec.Count; i++)
            Assert.Equal(i == dec.Count - 1 ? CatchUpStep.Done : CatchUpStep.Pending, join.Add(dec[i], dst));
        Assert.Equal(src.Hash(), dst.Hash());
        Assert.Equal(42, dst.OpSeq);

        // a missing message breaks the set
        var again = new HouseCellsCatchUp();
        Assert.Equal(CatchUpStep.Pending, again.Add(dec[0], dst));
        Assert.Equal(CatchUpStep.Broken, again.Add(dec[2], dst));
    }

    [Fact]
    public void Undamaged_house_is_one_empty_message()
    {
        var c = new HouseCells(TestHouses.Box(10, 10, 10));
        var msgs = HouseCodec.EncodeCells(c);
        Assert.Single(msgs);
        Assert.Equal(12, msgs[0].Length);
        var d = HouseCodec.DecodeCells(msgs[0])!;
        Assert.Equal(0, d.RunCount);
        var dst = new HouseCells(c.Grid);
        dst.Remove(c.Grid.IndexOf(1, 3, 3));
        Assert.Equal(CatchUpStep.Done, new HouseCellsCatchUp().Add(d, dst));
        Assert.False(dst.Damaged);
        Assert.Null(HouseCodec.DecodeCells(new byte[11]));
        Assert.Null(HouseCodec.DecodeCells([.. new byte[12], 0x80]));   // cut varint
    }
}
