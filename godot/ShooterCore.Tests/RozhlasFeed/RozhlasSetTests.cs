using System.Numerics;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Menu;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.RozhlasFeed;
using Xunit;

namespace Drycastle.ShooterCore.Tests.RozhlasFeed;

/// <summary>Slice 5: the set holder's rules, the upstream mic frame and capture, the server's receiver, and the V binding shared with the vehicle view.</summary>
public class RozhlasSetTests
{
    // ---- the set ----

    [Fact]
    public void TakingNeedsAHijackAndOneHolderAtATime()
    {
        var s = new RozhlasSet();
        var changes = new List<ushort>();
        s.HolderChanged += changes.Add;
        Assert.Equal(RozhlasTake.NotHijacked, s.Take(1, hijacked: false));
        Assert.Equal(RozhlasTake.Taken, s.Take(1, hijacked: true));
        Assert.Equal(RozhlasTake.AlreadyYours, s.Take(1, true));
        Assert.Equal(RozhlasTake.Occupied, s.Take(2, true));
        Assert.False(s.Release(2)); // not theirs to let go
        Assert.Equal((ushort)1, s.Holder);
        Assert.True(s.Release(1));
        Assert.Equal(RozhlasTake.Taken, s.Take(2, true)); // the next one can take it at once
        Assert.Equal(new ushort[] { 1, 0, 2 }, changes);
    }

    [Fact]
    public void NotFromAVehicle() => Assert.Equal(RozhlasTake.InVehicle, new RozhlasSet().Take(1, true, inVehicle: true));

    [Fact]
    public void WithATableTheHolderMustBeWithinTwoMetres()
    {
        var s = new RozhlasSet { Table = new Vector3(10, 0, 10) };
        Assert.Equal(RozhlasTake.TooFar, s.Take(1, true)); // position unknown
        Assert.Equal(RozhlasTake.TooFar, s.Take(1, true, new Vector3(13, 0, 10)));
        Assert.Equal(RozhlasTake.Taken, s.Take(1, true, new Vector3(11.5f, 0, 10)));
        Assert.False(s.Check(true, new Vector3(11.9f, 0, 10), false));
        Assert.True(s.IsHeld);
        Assert.True(s.Check(true, new Vector3(12.5f, 0, 10), false)); // walked off
        Assert.False(s.IsHeld);
    }

    [Fact]
    public void WithoutATableOnlyHijackEndAndVehiclesEndTheHold()
    {
        var s = new RozhlasSet();
        s.Take(1, true);
        Assert.False(s.Check(true, new Vector3(999, 0, 0), false)); // no table: distance means nothing
        Assert.True(s.Check(true, null, holderInVehicle: true));
        s.Take(1, true);
        Assert.True(s.Check(hijacked: false, null, false));
        s.Take(2, true);
        Assert.True(s.Clear());
        Assert.False(s.Clear());
    }

    [Fact]
    public void SetMachineIsWellFormedAndCodecRoundTrips()
    {
        Assert.Empty(RozhlasSet.Spec.Unreachable);
        Assert.Equal(((ushort)513, (Vector3?)null), RozhlasSetCodec.Decode(RozhlasSetCodec.Encode(513)));
        var table = new Vector3(12.5f, -3f, 400.25f);
        Assert.Equal((RozhlasSet.None, (Vector3?)table), RozhlasSetCodec.Decode(RozhlasSetCodec.Encode(RozhlasSet.None, table)));
        Assert.Null(RozhlasSetCodec.Decode(new byte[3]));
    }

    [Fact]
    public void HoldCountsOnlyAtAFreeSetDuringAHijackOnFoot()
    {
        var t = new Vector3(10, 0, 10);
        var near = t + new Vector3(1.5f, 0, 0);
        Assert.True(RozhlasSet.CanTakeHere(true, false, t, near, false));
        Assert.False(RozhlasSet.CanTakeHere(false, false, t, near, false)); // not hijacked
        Assert.False(RozhlasSet.CanTakeHere(true, true, t, near, false)); // somebody holds it
        Assert.False(RozhlasSet.CanTakeHere(true, false, null, near, false)); // no table
        Assert.False(RozhlasSet.CanTakeHere(true, false, t, t + new Vector3(2.5f, 0, 0), false)); // out of reach
        Assert.False(RozhlasSet.CanTakeHere(true, false, t, near, true)); // in a vehicle
        var h = new InteractHold();
        var taken = 0;
        for (var i = 0; i < 60; i++) if (h.Tick(true, true, 1.0 / 30, RozhlasSet.TakeHoldSeconds)) taken++;
        Assert.Equal(1, taken); // one take per hold, after the hold time
    }

    [Fact]
    public void TalkingNeedsEveryCondition()
    {
        Assert.True(RozhlasMicRules.Transmitting(true, true, false, false, true));
        Assert.False(RozhlasMicRules.Transmitting(false, true, false, false, true)); // not the holder
        Assert.False(RozhlasMicRules.Transmitting(true, false, false, false, true)); // key not held
        Assert.False(RozhlasMicRules.Transmitting(true, true, true, false, true));   // dead
        Assert.False(RozhlasMicRules.Transmitting(true, true, false, true, true));   // V is the vehicle view there
        Assert.False(RozhlasMicRules.Transmitting(true, true, false, false, false)); // a menu or the chat has the keys
    }

    // ---- the upstream frame ----

    [Fact]
    public void MicWireRoundTripsAndRejectsStrangers()
    {
        var pkt = RozhlasMicWire.Encode(65535, new byte[] { 1, 2, 3 });
        Assert.Equal(Wire.TRozhlasMic, pkt[0]);
        Assert.True(RozhlasMicWire.TryDecode(pkt, out var seq, out var opus));
        Assert.Equal((ushort)65535, seq); Assert.Equal(new byte[] { 1, 2, 3 }, opus);
        Assert.False(RozhlasMicWire.TryDecode(new byte[] { Wire.TRozhlasMic, 0, 0 }, out _, out _));   // no payload
        Assert.False(RozhlasMicWire.TryDecode(new byte[] { Wire.TRozhlasFeed, 0, 0, 9 }, out _, out _)); // the downstream's byte
        Assert.False(RozhlasMicWire.TryDecode(new byte[RozhlasMicWire.HeaderBytes + RozhlasMicWire.MaxOpusBytes + 1].Select((_, i) => i == 0 ? Wire.TRozhlasMic : (byte)1).ToArray(), out _, out _));
    }

    // ---- capture: resample and frame ----

    static float[] Tone(int rate, double hz, int n) => Enumerable.Range(0, n).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / rate))).ToArray();

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void CaptureMakesTwentyMillisecondFramesAt16k(int rate)
    {
        var sent = new List<byte[]>();
        var cap = new RozhlasMicCapture(rate, sent.Add);
        var tone = Tone(rate, 700, rate); // one second, pushed in uneven chunks like a frame-rate pump
        for (var i = 0; i < tone.Length;) { var n = Math.Min(731, tone.Length - i); cap.Push(tone.AsSpan(i, n)); i += n; }
        Assert.InRange(sent.Count, 49, 50);
        for (var i = 0; i < sent.Count; i++)
        {
            Assert.True(RozhlasMicWire.TryDecode(sent[i], out var seq, out _));
            Assert.Equal((ushort)i, seq);
        }
        // decoded, the tone is still a tone of the right pitch and level (upward zero crossings)
        var recv = new RozhlasMicReceiver();
        var pcm = new List<short>();
        foreach (var p in sent) { RozhlasMicWire.TryDecode(p, out var seq, out var opus); recv.Receive(seq, opus, b => pcm.AddRange(b.ToArray())); }
        var mid = pcm.Skip(4000).Take(8000).ToArray(); // 0.25 s in, past the codec's settling
        Assert.InRange(mid.Max(x => (int)x), 6000, 20000);
        var crossings = 0;
        for (var i = 1; i < mid.Length; i++) if (mid[i - 1] < 0 && mid[i] >= 0) crossings++;
        Assert.InRange(crossings, 345, 355); // 700 Hz over 0.5 s = 350 cycles
    }

    [Fact]
    public void ResetDropsThePartialFrameAndKeepsCounting()
    {
        var sent = new List<byte[]>();
        var cap = new RozhlasMicCapture(48000, sent.Add);
        cap.Push(Tone(48000, 500, 48000 / 50 * 3 / 2)); // 1.5 frames
        Assert.Single(sent);
        cap.Reset();
        cap.Push(Tone(48000, 500, 48000 / 50 / 2)); // the half frame that was left is gone, so this half does not complete one
        Assert.Single(sent);
        cap.Push(Tone(48000, 500, 48000 / 50 / 2));
        Assert.Equal(2, sent.Count);
        Assert.True(RozhlasMicWire.TryDecode(sent[1], out var seq, out _));
        Assert.Equal((ushort)1, seq);
    }

    // ---- the server's receiver ----

    static byte[] Frame(RozhlasOpus.Encoder e, short amp) => e.Encode(Enumerable.Range(0, 320).Select(i => (short)(amp * Math.Sin(2 * Math.PI * 800 * i / 16000))).ToArray());

    [Fact]
    public void ReceiverDropsDuplicatesConcealsSmallGapsAndSurvivesGarbage()
    {
        var enc = new RozhlasOpus.Encoder();
        var r = new RozhlasMicReceiver();
        var blocks = 0;
        PcmSink count = p => { Assert.Equal(320, p.Length); blocks++; };
        Assert.Equal(1, r.Receive(10, Frame(enc, 8000), count));
        Assert.Equal(0, r.Receive(10, Frame(enc, 8000), count)); // a duplicate
        Assert.Equal(0, r.Receive(9, Frame(enc, 8000), count));  // an older one
        Assert.Equal(3, r.Receive(13, Frame(enc, 8000), count)); // two lost: concealed, then the frame
        Assert.Equal(1, r.Receive(500, Frame(enc, 8000), count)); // a long pause: just continues
        Assert.Equal(0, r.Receive(501, new byte[] { 0xFF, 0xFF, 0xFF, 1, 2, 3, 4 }, count)); // not Opus: dropped (or decoded as noise, never thrown)
        r.Receive(502, Array.Empty<byte>(), count);
        Assert.True(r.Dropped >= 3);
        Assert.Equal(5, blocks);
    }

    // ---- V is shared by the vehicle view and the talk key ----

    static BindingMap Map() => new(new[] { ("view", Binding.Key(1)), ("talk", Binding.Key(1)), ("map", Binding.Key(2)), ("fire", Binding.Key(3)) }, new[] { ("view", "talk") });

    [Fact]
    public void ContextPairMayShareAKeyWithoutASwap()
    {
        var m = Map();
        Assert.Empty(m.Assign("talk", Binding.Key(1))); // already there, no conflict
        Assert.Equal(Binding.Key(1), m.Get("view")); Assert.Equal(Binding.Key(1), m.Get("talk"));
        Assert.Empty(m.Assign("view", Binding.Key(9)));
        Assert.Empty(m.Assign("talk", Binding.Key(9))); // both on 9 now: still no conflict
    }

    [Fact]
    public void ConflictWithAThirdActionStillSwapsAndNeverLeavesAClash()
    {
        var m = Map();
        Assert.Equal(new[] { "map" }, m.Assign("talk", Binding.Key(2))); // talk takes map's key; map must not end up beside "view" on key 1
        Assert.Equal(Binding.Key(2), m.Get("talk"));
        Assert.Equal(Binding.Key(2), m.Get("view")); // the pair moved together
        Assert.Equal(Binding.Key(1), m.Get("map"));
        Assert.Equal(new[] { "talk", "view" }, m.Assign("fire", Binding.Key(2)).OrderBy(a => a)); // both holders of 2 are displaced onto fire's old key, and both are reported
        Assert.Equal(Binding.Key(2), m.Get("fire")); Assert.Equal(Binding.Key(3), m.Get("view")); Assert.Equal(Binding.Key(3), m.Get("talk"));
    }

    [Fact]
    public void LoadAllKeepsTheSharedKeyAndStillRejectsRealDuplicates()
    {
        var m = Map();
        m.LoadAll(new Dictionary<string, string?> { ["view"] = Binding.Key(7).ToString(), ["talk"] = Binding.Key(7).ToString(), ["map"] = Binding.Key(7).ToString() });
        Assert.Equal(Binding.Key(7), m.Get("view")); Assert.Equal(Binding.Key(7), m.Get("talk"));
        Assert.Equal(Binding.Key(2), m.Get("map")); // map may not share it: default stays
    }
}
