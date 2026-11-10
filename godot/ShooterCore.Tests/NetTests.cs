using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

public class NetTests
{
    [Fact]
    public void ReliableMessagesRoundTrip()
    {
        Assert.Equal(new HelloMsg(1, "Anička"), Wire.DecodeReliable(Wire.Encode(new HelloMsg(1, "Anička"))));
        Assert.Equal(new WelcomeMsg(1, 7, 30, 123456), Wire.DecodeReliable(Wire.Encode(new WelcomeMsg(1, 7, 30, 123456))));
        Assert.Equal(new JoinMsg(300, "bob"), Wire.DecodeReliable(Wire.Encode(new JoinMsg(300, "bob"))));
        Assert.Equal(new LeaveMsg(9), Wire.DecodeReliable(Wire.Encode(new LeaveMsg(9))));
        var e = Assert.IsType<EventMsg>(Wire.DecodeReliable(Wire.Encode(new EventMsg((byte)EventKind.Hit, new byte[] { 1, 2, 3 }))));
        Assert.Equal((byte)EventKind.Hit, e.Kind);
        Assert.Equal(new byte[] { 1, 2, 3 }, e.Payload);
    }

    [Fact]
    public void MalformedReliableIsRejected()
    {
        Assert.Null(Wire.DecodeReliable(ReadOnlySpan<byte>.Empty));
        Assert.Null(Wire.DecodeReliable(new byte[] { Wire.TJoin, 1 }));
        Assert.Null(Wire.DecodeReliable(new byte[] { Wire.TEvent, 1, 200, 0 }));
        Assert.Null(Wire.DecodeReliable(new byte[] { 99 }));
    }

    [Fact]
    public void LongNameIsClamped()
    {
        var m = Assert.IsType<JoinMsg>(Wire.DecodeReliable(Wire.Encode(new JoinMsg(1, new string('x', 100)))));
        Assert.Equal(Wire.MaxNameBytes, m.Name.Length);
    }

    [Fact]
    public void InputsRoundTripAndKeepNewestThree()
    {
        var src = new List<NetInput>();
        for (ushort i = 0; i < 5; i++)
            src.Add(NetInput.From((ushort)(65534 + i), new MoveInput { MoveX = -1, MoveY = 0.5f, Yaw = -1.2f, Buttons = MoveButtons.Jump | MoveButtons.Use }, 0.4f, 3));
        var pkt = Wire.EncodeInputs(src.ToArray());
        var got = new List<NetInput>();
        Assert.True(Wire.TryDecodeInputs(pkt, got));
        Assert.Equal(3, got.Count);
        Assert.Equal(src[2].Seq, got[0].Seq);
        Assert.Equal(src[4].Seq, got[2].Seq);
        Assert.Equal(src[4], got[2]);
        var m = got[2].ToMove();
        Assert.Equal(-1f, m.MoveX);
        Assert.True(m.Has(MoveButtons.Use));
        Assert.Equal(3, got[2].Slot);
        Assert.Equal(0.4f, Quant.Pitch(got[2].Pitch), 3);
        Assert.False(Wire.TryDecodeInputs(pkt.AsSpan(0, 5), got));
    }

    [Fact]
    public void SeqComparisonWraps()
    {
        Assert.True(NetInput.Newer(2, 65535));
        Assert.False(NetInput.Newer(65535, 2));
        Assert.False(NetInput.Newer(5, 5));
    }

    [Fact]
    public void YawQuantisationWraps()
    {
        Assert.Equal(Quant.Yaw(0.1f), Quant.Yaw(0.1f + Quant.TwoPi));
        Assert.Equal(0.1f, Quant.Yaw(Quant.Yaw(0.1f)), 3);
        Assert.Equal(Quant.TwoPi - 0.5f, Quant.Yaw(Quant.Yaw(-0.5f)), 3);
    }

    static EntityRecord Ent(ushort id, float x, float y = 0, float z = 0, float yaw = 0, float hp = 100, EntityType t = EntityType.Player) =>
        EntityRecord.Make(id, t, new Vector3(x, y, z), yaw, 0.2f, hp, EntityState.Alive, 1);

    static SnapshotHeader Decode(SnapshotDecoder d, IEnumerable<byte[]> pkts)
    {
        SnapshotHeader h = default;
        foreach (var p in pkts) Assert.True(d.Apply(p, out h));
        return h;
    }

    [Fact]
    public void SnapshotKeyframeThenDeltasReproduceState()
    {
        var enc = new SnapshotEncoder(); var dec = new SnapshotDecoder();
        var h = Decode(dec, enc.Encode(100, 42, new[] { Ent(1, 10.5f, 3, -200.25f, 1f), Ent(2, 0, 0, 0, 0, 80, EntityType.Projectile) }));
        Assert.Equal(new SnapshotHeader(100, 42), h);
        Assert.Equal(2, dec.States.Count);
        Assert.Equal(EntityType.Projectile, dec.States[2].Type);
        Assert.Equal(-200.25f, dec.States[1].Position.Z);

        var cur = new[] { Ent(1, 10.9f, 3, -200f, 1.5f, 90), Ent(2, 0, 0, 0, 0, 80, EntityType.Projectile) };
        var pkts = enc.Encode(101, 43, cur);
        Assert.Equal(1, enc.DeltaEntries); // entity 2 unchanged: omitted
        Decode(dec, pkts);
        Assert.Equal(cur[0], dec.States[1]);
        Assert.Equal(cur[1], dec.States[2]);
    }

    [Fact]
    public void UnchangedSnapshotIsHeaderOnlyAndLargeJumpUsesAbsolute()
    {
        var enc = new SnapshotEncoder(); var dec = new SnapshotDecoder();
        Decode(dec, enc.Encode(1, 0, new[] { Ent(1, 0) }));
        var same = enc.Encode(2, 5, new[] { Ent(1, 0) });
        Assert.Equal(8, Assert.Single(same).Length);
        var far = new[] { Ent(1, 1500, 80, -900) };
        Decode(dec, enc.Encode(3, 6, far));
        Assert.Equal(far[0], dec.States[1]);
    }

    [Fact]
    public void KeyframeAtLeastEveryThirtyTicks()
    {
        long maxGap = 0;
        // per entity: count keyframes over 300 ticks of a single moving entity
        var e2 = new SnapshotEncoder();
        long last = 0; var full = 0L;
        for (uint tick = 1; tick <= 300; tick++)
        {
            var f0 = e2.FullEntries;
            e2.Encode(tick, 0, new[] { Ent(5, tick * 0.01f) });
            if (e2.FullEntries > f0) { if (full > 0) maxGap = Math.Max(maxGap, tick - last); last = tick; full++; }
        }
        Assert.True(full >= 10);
        Assert.Equal(30, maxGap);
    }

    [Fact]
    public void LostDeltaIsRepairedByNextKeyframe()
    {
        var enc = new SnapshotEncoder(); var dec = new SnapshotDecoder();
        Decode(dec, enc.Encode(1, 0, new[] { Ent(1, 0) }));
        enc.Encode(2, 0, new[] { Ent(1, 1) }); // lost
        var final = Ent(1, 2);
        for (uint t = 3; t <= 40; t++) { var p = enc.Encode(t, 0, new[] { Ent(1, 2) }); if (t > 3) Decode(dec, p); }
        Assert.Equal(final, dec.States[1]);
    }

    [Fact]
    public void ManyEntitiesSplitIntoPacketsUnderMtu()
    {
        var enc = new SnapshotEncoder(); var dec = new SnapshotDecoder();
        var ents = Enumerable.Range(1, 100).Select(i => Ent((ushort)i, i, 1, 2)).ToArray();
        var pkts = enc.Encode(5, 9, ents);
        Assert.Equal(3, pkts.Count);
        Assert.All(pkts, p => Assert.True(p.Length < 1400));
        Decode(dec, pkts);
        Assert.Equal(100, dec.States.Count);
    }

    [Fact]
    public void RemovedEntityGetsKeyframeWhenItReturns()
    {
        var enc = new SnapshotEncoder(); var dec = new SnapshotDecoder();
        Decode(dec, enc.Encode(1, 0, new[] { Ent(1, 0), Ent(2, 0) }));
        enc.Encode(2, 0, new[] { Ent(1, 0) });
        var dec2 = new SnapshotDecoder();
        Decode(dec2, enc.Encode(3, 0, new[] { Ent(1, 0), Ent(2, 7) }));
        Assert.Equal(7f, dec2.States[2].Position.X); // fresh decoder resolves it from the keyframe alone
    }

    [Fact]
    public void TruncatedSnapshotIsRejectedAndChangesNothing()
    {
        var enc = new SnapshotEncoder(); var dec = new SnapshotDecoder();
        var p = Assert.Single(enc.Encode(1, 0, new[] { Ent(1, 3) }));
        Assert.False(dec.Apply(p.AsSpan(0, p.Length - 2), out _));
        Assert.Empty(dec.States);
    }

    [Fact]
    public void InterpolatorBlendsAndClamps()
    {
        var ip = new Interpolator();
        ip.Push(10, new Vector3(0, 0, 0), 0f, 0f);
        ip.Push(13, new Vector3(3, 0, 0), 0f, 0f);
        Assert.Equal(1.5f, ip.At(11.5)!.Value.Pos.X, 3);
        Assert.Equal(0f, ip.At(5)!.Value.Pos.X);
        Assert.Equal(3f, ip.At(99)!.Value.Pos.X);
        ip.Push(12, Vector3.Zero, 0, 0); // stale, ignored
        Assert.Equal(2, ip.Count);
        ip.Push(13, new Vector3(4, 0, 0), 0, 0); // same tick replaces
        Assert.Equal(2, ip.Count);
        Assert.Equal(4f, ip.At(13)!.Value.Pos.X);
    }

    [Fact]
    public void InterpolatorTakesShortestWayAroundYaw()
    {
        var ip = new Interpolator();
        ip.Push(0, Vector3.Zero, 6.0f, 0);
        ip.Push(2, Vector3.Zero, 0.3f, 0);
        var y = ip.At(1)!.Value.Yaw;
        Assert.InRange(y, 6.0f, 6.0f + 0.6f); // passes through 2*pi, not back through 3
    }

    [Fact]
    public void DecoderDropsEntitiesThatStopArriving()
    {
        var enc = new SnapshotEncoder(); var dec = new SnapshotDecoder();
        Decode(dec, enc.Encode(1, 0, new[] { Ent(1, 0), Ent(2, 0) }));
        Decode(dec, enc.Encode(100, 0, new[] { Ent(1, 0) }));
        Assert.Equal(new ushort[] { 2 }, dec.RemoveStale(100, 90).ToArray());
        Assert.Single(dec.States);
    }
}
