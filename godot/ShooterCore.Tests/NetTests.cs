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
        Assert.True(Wire.TryDecodeInputs(pkt, got, out _));
        Assert.Equal(3, got.Count);
        Assert.Equal(src[2].Seq, got[0].Seq);
        Assert.Equal(src[4].Seq, got[2].Seq);
        Assert.Equal(src[4], got[2]);
        var m = got[2].ToMove();
        Assert.Equal(-1f, m.MoveX);
        Assert.True(m.Has(MoveButtons.Use));
        Assert.Equal(3, got[2].Slot);
        Assert.Equal(0.4f, Quant.Pitch(got[2].Pitch), 3);
        Assert.False(Wire.TryDecodeInputs(pkt.AsSpan(0, 5), got, out _));
    }

    [Fact]
    public void InputsCarryTheSnapshotTickTheyAcknowledge()
    {
        var pkt = Wire.EncodeInputs(new[] { NetInput.From(7, new MoveInput { SpeedScale = 1 }, 0f, 0) }, 4321u);
        Assert.True(Wire.TryDecodeInputs(pkt, new List<NetInput>(), out var snap));
        Assert.Equal(4321u, snap);
        Assert.True(Wire.TryDecodeInputs(Wire.EncodeInputs(new[] { NetInput.From(8, new MoveInput(), 0f, 0) }), new List<NetInput>(), out var none));
        Assert.Equal(Wire.NoTick, none);
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
        var committed = false;
        foreach (var p in pkts) committed |= d.Apply(p, out h);
        Assert.True(committed, "the tick did not complete");
        return h;
    }

    [Fact]
    public void SnapshotKeyframeThenDeltasReproduceState()
    {
        var enc = new SnapshotEncoder(); var dec = new SnapshotDecoder();
        var h = Decode(dec, enc.Encode(100, 42, Wire.NoTick, new[] { Ent(1, 10.5f, 3, -200.25f, 1f), Ent(2, 0, 0, 0, 0, 80, EntityType.Projectile) }));
        Assert.Equal(new SnapshotHeader(100, 42), h);
        Assert.Equal(2, dec.States.Count);
        Assert.Equal(EntityType.Projectile, dec.States[2].Type);
        Assert.Equal(-200.25f, dec.States[1].Position.Z);

        var cur = new[] { Ent(1, 10.9f, 3, -200f, 1.5f, 90), Ent(2, 0, 0, 0, 0, 80, EntityType.Projectile) };
        var pkts = enc.Encode(101, 43, 100, cur); // against the tick the client acknowledged
        Assert.Equal(1, enc.DeltaEntries); // entity 2 unchanged since the baseline: omitted
        Decode(dec, pkts);
        Assert.Equal(cur[0], dec.States[1]);
        Assert.Equal(cur[1], dec.States[2]);
        Assert.Equal(101u, dec.AckTick);
    }

    [Fact]
    public void UnchangedSnapshotIsHeaderOnlyAndLargeJumpUsesAbsolute()
    {
        var enc = new SnapshotEncoder(); var dec = new SnapshotDecoder();
        Decode(dec, enc.Encode(1, 0, Wire.NoTick, new[] { Ent(1, 0) }));
        var same = enc.Encode(2, 5, 1, new[] { Ent(1, 0) });
        Assert.Equal(SnapshotEncoder.HeaderBytes, Assert.Single(same).Length);
        Decode(dec, same);
        var far = new[] { Ent(1, 1500, 80, -900) };
        Decode(dec, enc.Encode(3, 6, 2, far));
        Assert.Equal(far[0], dec.States[1]);
    }

    [Fact]
    public void KeyframeOnlyForEntitiesNewToTheBaseline()
    {
        var enc = new SnapshotEncoder(); var dec = new SnapshotDecoder();
        Decode(dec, enc.Encode(1, 0, Wire.NoTick, new[] { Ent(5, 0) }));
        var full = enc.FullEntries;
        for (uint tick = 2; tick <= 300; tick++)
        {
            var moved = Ent(5, tick * 0.01f);
            Decode(dec, enc.Encode(tick, 0, tick - 1, new[] { moved }));
            Assert.Equal(moved, dec.States[5]);
        }
        Assert.Equal(full, enc.FullEntries); // no periodic keyframes: the baseline repairs everything
    }

    [Fact]
    public void LostPacketIsRepairedByTheNextTickAgainstTheSameBaseline()
    {
        var enc = new SnapshotEncoder(); var dec = new SnapshotDecoder();
        Decode(dec, enc.Encode(1, 0, Wire.NoTick, new[] { Ent(1, 0) }));
        enc.Encode(2, 0, 1, new[] { Ent(1, 1) }); // lost: the client never commits tick 2
        Assert.Equal(1u, dec.AckTick);
        var final = Ent(1, 2);
        Decode(dec, enc.Encode(3, 0, 1, new[] { final })); // still relative to tick 1, which the client has
        Assert.Equal(final, dec.States[1]);
    }

    [Fact]
    public void ATickIsCommittedOnlyWhenAllItsPacketsArrive()
    {
        var enc = new SnapshotEncoder(); var dec = new SnapshotDecoder();
        var ents = Enumerable.Range(1, 60).Select(i => Ent((ushort)i, i, 1, 2)).ToArray();
        var pkts = enc.Encode(5, 0, Wire.NoTick, ents);
        Assert.Equal(2, pkts.Count);
        Assert.False(dec.Apply(pkts[1], out _)); // the second packet alone completes nothing
        Assert.False(dec.HasState);
        Assert.True(dec.Apply(pkts[0], out _));
        Assert.Equal(60, dec.States.Count);
    }

    [Fact]
    public void BaselineTheClientNoLongerKeepsIsNotDecodable()
    {
        var enc = new SnapshotEncoder(); var first = new SnapshotDecoder();
        Decode(first, enc.Encode(1, 0, Wire.NoTick, new[] { Ent(1, 0) }));
        var delta = enc.Encode(2, 0, 1, new[] { Ent(1, 3) });
        var fresh = new SnapshotDecoder(); // knows no tick 1
        Assert.False(fresh.Apply(Assert.Single(delta), out _));
        Assert.False(fresh.HasState);
    }

    [Fact]
    public void AnAckOlderThanTheHistoryGetsAFullSet()
    {
        var enc = new SnapshotEncoder(); var dec = new SnapshotDecoder();
        Decode(dec, enc.Encode(1, 0, Wire.NoTick, new[] { Ent(1, 0), Ent(2, 0) }));
        var late = enc.Encode(1 + (uint)SnapshotHistory.Depth + 5, 0, 1, new[] { Ent(1, 4) }); // tick 1 has aged out
        Assert.True(dec.Apply(Assert.Single(late), out _)); // a full set: the client replaces its state
        Assert.Single(dec.States);
        Assert.Equal(new[] { (ushort)2 }, dec.Removed.ToArray());
    }

    [Fact]
    public void UnderPacketLossEveryCommittedTickMatchesTheServer()
    {
        // 30% of the packets are dropped; the client acknowledges what it committed one tick ago. Whatever it commits must be
        // exactly the server's state for that tick, and the stream keeps committing (the next tick repairs a lost one).
        var rng = new Random(7);
        var enc = new SnapshotEncoder(); var dec = new SnapshotDecoder();
        var ack = Wire.NoTick;
        var committed = 0;
        for (uint tick = 1; tick <= 600; tick++)
        {
            var now = new List<EntityRecord>();
            for (ushort id = 1; id <= 40; id++)
            {
                if (id == 7 && tick is >= 100 and <= 130) continue; // leaves, then comes back
                var moving = (tick + id) % 3 != 0;
                now.Add(Ent(id, id + (moving ? tick * 0.05f : 0), 0, 0, 0.1f * (tick % 7), 100 - tick / 50));
            }
            var got = false;
            foreach (var p in enc.Encode(tick, 0, ack, now))
                if (rng.NextDouble() >= 0.3 && dec.Apply(p, out _)) got = true;
            if (!got) continue;
            committed++;
            Assert.Equal(now.Count, dec.States.Count);
            foreach (var r in now) Assert.Equal(r, dec.States[r.Id]);
            ack = dec.AckTick;
        }
        Assert.True(committed > 400, $"only {committed} of 600 ticks committed");
    }

    [Fact]
    public void ManyEntitiesSplitIntoPacketsUnderMtu()
    {
        var enc = new SnapshotEncoder(); var dec = new SnapshotDecoder();
        var ents = Enumerable.Range(1, 100).Select(i => Ent((ushort)i, i, 1, 2)).ToArray();
        var pkts = enc.Encode(5, 9, Wire.NoTick, ents);
        Assert.Equal(3, pkts.Count);
        Assert.All(pkts, p => Assert.True(p.Length < 1400));
        Decode(dec, pkts);
        Assert.Equal(100, dec.States.Count);
    }

    [Fact]
    public void AnEntityThatLeftIsRemovedAgainstTheBaseline()
    {
        var enc = new SnapshotEncoder(); var dec = new SnapshotDecoder();
        Decode(dec, enc.Encode(1, 0, Wire.NoTick, new[] { Ent(1, 0), Ent(2, 0) }));
        Decode(dec, enc.Encode(2, 0, 1, new[] { Ent(1, 0) }));
        Assert.Equal(new[] { (ushort)2 }, dec.Removed.ToArray());
        Assert.False(dec.States.ContainsKey(2));
        // it comes back later: a fresh decoder resolves it from the keyframe alone
        var dec2 = new SnapshotDecoder();
        Decode(dec2, enc.Encode(3, 0, Wire.NoTick, new[] { Ent(1, 0), Ent(2, 7) }));
        Assert.Equal(7f, dec2.States[2].Position.X);
    }

    [Fact]
    public void TruncatedSnapshotIsRejectedAndChangesNothing()
    {
        var enc = new SnapshotEncoder(); var dec = new SnapshotDecoder();
        var p = Assert.Single(enc.Encode(1, 0, Wire.NoTick, new[] { Ent(1, 3) }));
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
        Decode(dec, enc.Encode(1, 0, Wire.NoTick, new[] { Ent(1, 0), Ent(2, 0) }));
        Decode(dec, enc.Encode(100, 0, 1, new[] { Ent(1, 0) }));
        Assert.Equal(new ushort[] { 2 }, dec.Removed.ToArray());
        Assert.Single(dec.States);
    }
}
