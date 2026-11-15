using Concentus;
using Concentus.Enums;

namespace Drycastle.ShooterCore;

/// <summary>
/// One live-feed frame on <see cref="Wire.ChanRozhlasFeed"/> (server to every client, unreliable): u8 <see cref="Wire.TRozhlasFeed"/>, u16 sequence (little endian,
/// counts up by one per 20 ms frame and wraps), then one Opus packet (16 kHz mono, 20 ms). There is no start or stop message: frames are sent only while the feed
/// carries sound (a silent mic sends nothing; the sequence does not skip over the pause) and a client that stops hearing frames goes quiet by itself.
/// </summary>
public static class RozhlasFeedWire
{
    public const int HeaderBytes = 3;

    public static byte[] Encode(ushort seq, ReadOnlySpan<byte> opus)
    {
        var b = new byte[HeaderBytes + opus.Length];
        b[0] = Wire.TRozhlasFeed; b[1] = (byte)seq; b[2] = (byte)(seq >> 8);
        opus.CopyTo(b.AsSpan(HeaderBytes));
        return b;
    }

    public static bool TryDecode(byte[] d, out ushort seq, out byte[] opus)
    {
        seq = 0; opus = Array.Empty<byte>();
        if (d.Length <= HeaderBytes || d[0] != Wire.TRozhlasFeed) return false;
        seq = (ushort)(d[1] | d[2] << 8);
        opus = d.AsSpan(HeaderBytes).ToArray();
        return true;
    }
}

/// <summary>The feed's Opus settings and the two ends of the codec (pure C#, Concentus). Each instance keeps codec state: one per stream, one thread.</summary>
public static class RozhlasOpus
{
    public const int Bitrate = 40000;
    const int MaxPacket = 400;

    /// <summary>The managed codec on every platform (the same bytes on server and client). Both ends are created through here: a nested class alone would not run this switch.</summary>
    static IOpusEncoder NewEncoder() { OpusCodecFactory.AttemptToUseNativeLibrary = false; return OpusCodecFactory.CreateEncoder(RozhlasVoice.FeedRate, 1, OpusApplication.OPUS_APPLICATION_VOIP); }
    static IOpusDecoder NewDecoder() { OpusCodecFactory.AttemptToUseNativeLibrary = false; return OpusCodecFactory.CreateDecoder(RozhlasVoice.FeedRate, 1); }

    public sealed class Encoder
    {
        readonly IOpusEncoder _enc = NewEncoder();
        readonly byte[] _buf = new byte[MaxPacket];

        public Encoder() { _enc.Bitrate = Bitrate; _enc.UseVBR = true; _enc.Complexity = 5; }

        /// <summary>One 20 ms frame (<see cref="RozhlasFeedSchedule.FrameSamples"/> samples) to its Opus packet.</summary>
        public byte[] Encode(ReadOnlySpan<short> frame)
        {
            var n = _enc.Encode(frame, RozhlasFeedSchedule.FrameSamples, _buf, _buf.Length);
            return _buf.AsSpan(0, n).ToArray();
        }
    }

    public sealed class Decoder
    {
        readonly IOpusDecoder _dec = NewDecoder();

        /// <summary>Decodes one packet into <paramref name="pcm"/> (320 samples); null conceals a lost one.</summary>
        public void Decode(byte[]? opus, Span<short> pcm) =>
            _dec.Decode(opus is null ? ReadOnlySpan<byte>.Empty : opus, pcm, RozhlasFeedSchedule.FrameSamples, false);
    }
}

/// <summary>A synthetic stand-in for a voice and a gunshot, for the admin's <c>/rozhlas feedtest</c> (no mic or sound library needed on the server).</summary>
public static class RozhlasFeedTestSignal
{
    /// <summary>"Speech": a buzzing voice that glides in pitch, in ~4 syllables a second, with gaps. Inside the voice chain's band (500-2200 Hz) through its harmonics.</summary>
    public static short[] Voice(double seconds, int rate = RozhlasVoice.FeedRate)
    {
        var n = (int)(seconds * rate);
        var s = new short[n];
        double phase = 0;
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)rate;
            var f0 = 140 + 40 * Math.Sin(2 * Math.PI * 0.7 * t) + 25 * Math.Sin(2 * Math.PI * 4.3 * t);
            phase += 2 * Math.PI * f0 / rate;
            var syll = Math.Max(0, Math.Sin(2 * Math.PI * 3.7 * t));
            var env = syll * syll * (Math.Sin(2 * Math.PI * 0.45 * t) > -0.7 ? 1 : 0.15);
            double v = 0;
            for (var h = 1; h <= 14; h++) v += Math.Sin(h * phase) / Math.Pow(h, 0.8);
            s[i] = (short)(v * env * 7000);
        }
        return s;
    }

    /// <summary>A "gunshot": a noise burst with a fast decay.</summary>
    public static short[] Shot(double seconds = 0.35, int rate = RozhlasVoice.FeedRate)
    {
        var n = (int)(seconds * rate);
        var s = new short[n];
        var rng = new Random(5);
        for (var i = 0; i < n; i++) s[i] = (short)((rng.NextDouble() * 2 - 1) * 26000 * Math.Exp(-i / (0.06 * rate)));
        return s;
    }
}
