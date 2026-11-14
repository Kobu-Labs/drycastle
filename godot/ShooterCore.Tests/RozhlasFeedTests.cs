using System.Diagnostics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The pure parts of the rozhlas live feed: the shared voice chain, the input mix, the 2 s schedule, the client's jitter buffer, the wire and Opus.</summary>
public class RozhlasFeedTests
{
    static readonly string VoiceJson = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "rozhlas_voice.json"));

    // what scripts/make_rozhlas.py baked before the chain moved into rozhlas_voice.json, for these taps
    const string BakedChain = "highpass=f=500,lowpass=f=2200,volume=18dB,asoftclip=type=hard:threshold=0.15,aresample=5000,acrusher=bits=6:mix=0.7,vibrato=f=6.0:d=0.15,tremolo=f=3.0:d=0.4,aecho=0.7:0.7:227|517|610:0.3|0.2|0.12,alimiter=limit=0.9:level=0,volume=-5.5dB";

    [Fact]
    public void ChainFromTheSharedFileIsTheOneTheBakeUsed()
    {
        var v = RozhlasVoice.Parse(VoiceJson);
        Assert.Equal(BakedChain, v.BuildChain(new[] { 227, 517, 610 }));
    }

    [Fact]
    public void TapsStayInTheirRanges()
    {
        var v = RozhlasVoice.Parse(VoiceJson);
        var rng = new Random(1);
        for (var i = 0; i < 200; i++)
        {
            var t = v.PickTaps(rng);
            Assert.InRange(t[0], 220, 320); Assert.InRange(t[1], 420, 520); Assert.InRange(t[2], 560, 700);
        }
    }

    [Fact]
    public void PythonBuilderAndCSharpBuilderAgree()
    {
        // the bake (scripts/make_rozhlas.py voice_chain) and the live feed must build the same text from the same file; skipped quietly where python3 is missing
        var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../scripts"));
        if (!Directory.Exists(script)) return;
        var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("import sys; sys.path.insert(0, sys.argv[1]); import make_rozhlas as m\n" +
            "class R:\n  def randint(self, a, b): return a + 7\n" +
            "print(m.voice_chain(m.load_voice(sys.argv[2]), R()))");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "data", "rozhlas_voice.json"));
        string py;
        try { using var p = Process.Start(psi)!; py = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit(); if (p.ExitCode != 0) return; }
        catch (System.ComponentModel.Win32Exception) { return; }
        Assert.Equal(py, RozhlasVoice.Parse(VoiceJson).BuildChain(new[] { 227, 427, 567 }));
    }

    [Fact]
    public void LiveGraphMirrorsTheBakesPrefixAndEndsAtTheFeedRate()
    {
        var g = RozhlasVoice.FilterGraph(BakedChain);
        Assert.StartsWith("[0:a]aformat=channel_layouts=mono,highpass=f=500", g);
        Assert.EndsWith("volume=-5.5dB[s];[s]aresample=16000[o]", g);
    }

    [Fact]
    public void HissInTheVoiceFileIsRejectedForTheLiveFeed() =>
        Assert.Throws<DataFileException>(() => RozhlasVoice.Parse(VoiceJson.Replace("\"noise\": 0.0", "\"noise\": 0.3")));

    [Fact]
    public void UnknownPlaceholderIsRejected() =>
        Assert.Throws<DataFileException>(() => RozhlasVoice.Parse(VoiceJson.Replace("\"volume={gain_db}dB\"", "\"volume={nope}dB\"")));

    [Fact]
    public void MixerSumsVoiceAndShotsAndFillsSilence()
    {
        var m = new RozhlasFeedMixer();
        var block = new short[320];
        m.Pull(block);
        Assert.All(block, s => Assert.Equal(0, s)); // nothing written: silence, the stream is continuous
        m.Write(Enumerable.Repeat((short)1000, 400).ToArray());
        m.Mix(Enumerable.Repeat((short)500, 100).ToArray());
        m.Pull(block);
        Assert.Equal(1500, block[0]); Assert.Equal(1500, block[99]); Assert.Equal(1000, block[100]); Assert.Equal(1000, block[319]);
        m.Pull(block);
        Assert.Equal(1000, block[0]); Assert.Equal(1000, block[79]); Assert.Equal(0, block[80]);
        Assert.True(m.IsIdle);
    }

    [Fact]
    public void MixerClipsAndDropsTheOldestWhenTheVoiceBacksUp()
    {
        var m = new RozhlasFeedMixer(maxVoiceSeconds: 0.1); // 1600 samples
        m.Write(Enumerable.Repeat((short)30000, 400).ToArray());
        m.Mix(Enumerable.Repeat((short)30000, 10).ToArray());
        var block = new short[320];
        m.Pull(block);
        Assert.Equal(short.MaxValue, block[0]);
        m.Clear();
        m.Write(new short[1000]); m.Write(Enumerable.Repeat((short)7, 1000).ToArray());
        Assert.Equal(1600, m.QueuedVoiceSamples);
        Assert.Equal(400, m.DroppedVoiceSamples);
    }

    [Fact]
    public void FramesGoOutTwoSecondsAfterTheirInputMinusThePipelineDelay()
    {
        var s = new RozhlasFeedSchedule(100.0, pipelineS: 0.010);
        Assert.Equal(100.0 + 2.0 - 0.010, s.ReleaseAt(0), 6);
        Assert.Equal(100.0 + 2.0 - 0.010 + 1.0, s.ReleaseAt(50), 6); // frame 50 is input second 1.0
        for (var i = 0; i < 5; i++) s.Enqueue(i, new[] { (byte)i });
        var due = new List<(long, byte[])>();
        Assert.Equal(0, s.TakeDue(101.9, due));
        Assert.Equal(2, s.TakeDue(100.0 + 2.0 - 0.010 + 0.021, due)); // frames 0 and 1
        Assert.Equal(new long[] { 0, 1 }, due.Select(d => d.Item1));
        due.Clear();
        Assert.Equal(0, s.TakeDue(110.0, due)); // the rest are 8 s late: dropped, none sent
        Assert.Empty(due);
        Assert.Equal(3, s.DroppedLate);
    }

    [Fact]
    public void JitterBufferWaitsThenPlaysInOrderAndConcealsAGap()
    {
        var j = new RozhlasFeedJitter(targetFrames: 3);
        byte[] F(int i) => new[] { (byte)i };
        j.Push(10, F(10)); j.Push(11, F(11));
        Assert.Equal(JitterPop.Empty, j.Pop(out _)); // still filling: 100 ms not reached
        j.Push(13, F(13)); j.Push(12, F(12)); // out of order
        Assert.Equal(JitterPop.Frame, j.Pop(out var a)); Assert.Equal(10, a![0]);
        Assert.Equal(JitterPop.Frame, j.Pop(out a)); Assert.Equal(11, a![0]);
        j.Push(15, F(15)); // 14 never comes
        Assert.Equal(JitterPop.Frame, j.Pop(out a)); Assert.Equal(12, a![0]);
        Assert.Equal(JitterPop.Frame, j.Pop(out a)); Assert.Equal(13, a![0]);
        Assert.Equal(JitterPop.Lost, j.Pop(out _));
        j.Push(11, F(11)); // late: dropped
        Assert.Equal(1, j.Late);
        Assert.Equal(JitterPop.Frame, j.Pop(out a)); Assert.Equal(15, a![0]);
        Assert.Equal(JitterPop.Empty, j.Pop(out _)); // ran dry
        Assert.Equal(RozhlasJitterState.Priming, j.State);
    }

    [Fact]
    public void JitterBufferHandlesSequenceWrapAndABacklog()
    {
        var j = new RozhlasFeedJitter(targetFrames: 2, maxFrames: 6);
        foreach (var s in new ushort[] { 65534, 65535, 0, 1 }) j.Push(s, new[] { (byte)s });
        Assert.Equal(JitterPop.Frame, j.Pop(out var a)); Assert.Equal(254, a![0]);
        for (ushort s = 2; s < 14; s++) j.Push(s, new[] { (byte)s }); // far more than it may hold
        Assert.True(j.Buffered <= j.MaxFrames);
        Assert.True(j.Skipped > 0);
    }

    [Fact]
    public void WireAndOpusRoundTrip()
    {
        var enc = new RozhlasOpus.Encoder();
        var tone = Enumerable.Range(0, 320).Select(i => (short)(8000 * Math.Sin(2 * Math.PI * 800 * i / 16000))).ToArray();
        byte[] opus = Array.Empty<byte>();
        for (var i = 0; i < 5; i++) opus = enc.Encode(tone); // let the encoder settle
        Assert.InRange(opus.Length, 10, 200);
        var pkt = RozhlasFeedWire.Encode(65535, opus);
        Assert.Equal(Wire.TRozhlasFeed, pkt[0]);
        Assert.True(RozhlasFeedWire.TryDecode(pkt, out var seq, out var back));
        Assert.Equal((ushort)65535, seq); Assert.Equal(opus, back);
        var dec = new RozhlasOpus.Decoder();
        var pcm = new short[320];
        for (var i = 0; i < 5; i++) dec.Decode(back, pcm);
        Assert.True(pcm.Max(x => (int)x) > 3000); // the tone came out
        dec.Decode(null, pcm); // a lost frame is concealed, not an exception
    }

    [Fact]
    public void StateMachinesAreWellFormed()
    {
        Assert.Empty(RozhlasFeedMachine.Spec.Unreachable);
        Assert.Empty(RozhlasJitterMachine.Spec.Unreachable);
    }
}
