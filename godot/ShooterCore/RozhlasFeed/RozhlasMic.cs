namespace Drycastle.ShooterCore;

/// <summary>
/// One upstream mic frame (the set holder's voice, client to server, unreliable, <see cref="Wire.ChanRozhlasFeed"/>): u8 <see cref="Wire.TRozhlasMic"/>, u16 sequence (little endian,
/// +1 per 20 ms frame, wraps, keeps counting across talk bursts), then one Opus packet (16 kHz mono, 20 ms, the same codec as the downstream: <see cref="RozhlasOpus"/>).
/// There is no start or stop message: the client sends frames only while it talks, the server takes them only from the holder.
/// </summary>
public static class RozhlasMicWire
{
    public const int HeaderBytes = 3;
    /// <summary>An Opus packet of 20 ms at 40 kbps is ~100 bytes; anything bigger is not ours.</summary>
    public const int MaxOpusBytes = 400;

    public static byte[] Encode(ushort seq, ReadOnlySpan<byte> opus)
    {
        var b = new byte[HeaderBytes + opus.Length];
        b[0] = Wire.TRozhlasMic; b[1] = (byte)seq; b[2] = (byte)(seq >> 8);
        opus.CopyTo(b.AsSpan(HeaderBytes));
        return b;
    }

    public static bool TryDecode(byte[] d, out ushort seq, out byte[] opus)
    {
        seq = 0; opus = Array.Empty<byte>();
        if (d.Length <= HeaderBytes || d.Length > HeaderBytes + MaxOpusBytes || d[0] != Wire.TRozhlasMic) return false;
        seq = (ushort)(d[1] | d[2] << 8);
        opus = d.AsSpan(HeaderBytes).ToArray();
        return true;
    }
}

/// <summary>When a client may send mic frames: the pure rule behind "holder AND holds the talk action" (and is in a state where V is not the vehicle view's).</summary>
public static class RozhlasMicRules
{
    /// <summary>
    /// True when the local player's mic goes on the wire this frame: they hold the set, hold the talk action, are alive, not in a vehicle (where the same key is the vehicle view)
    /// and have the game's mouse (no menu, chat or map open, so typing a V never transmits).
    /// </summary>
    public static bool Transmitting(bool isHolder, bool talkHeld, bool dead, bool inVehicle, bool mouseCaptured) =>
        isHolder && talkHeld && !dead && !inVehicle && mouseCaptured;

    /// <summary>The mic device is open while the local player holds the set (so a press talks at once) and not otherwise.</summary>
    public static bool DeviceOpen(bool isHolder) => isHolder;
}

/// <summary>
/// The client's mic end: raw mono float samples at the audio engine's mix rate in, <see cref="Wire.TRozhlasMic"/> packets out (16 kHz mono, 20 ms Opus frames). A box-filter resampler
/// (each output sample is the average of the input samples it spans, which is also the anti-aliasing for the usual 48 kHz to 16 kHz), a 320-sample framer and the encoder.
/// Godot-free; the game only feeds it what <c>AudioEffectCapture</c> returned. One instance per talk session is not needed: <see cref="Reset"/> starts a new burst clean.
/// </summary>
public sealed class RozhlasMicCapture
{
    const int Frame = RozhlasFeedSchedule.FrameSamples;

    readonly double _ratio; // input samples per output sample
    readonly Action<byte[]> _send;
    readonly short[] _frame = new short[Frame];
    RozhlasOpus.Encoder _enc = new();
    int _filled;
    double _acc, _accW;
    ushort _seq;

    /// <param name="srcRate">The rate of the samples pushed (the mix rate, 44100 or 48000 usually).</param>
    /// <param name="send">Gets each finished upstream packet (<see cref="RozhlasMicWire"/>).</param>
    public RozhlasMicCapture(int srcRate, Action<byte[]> send)
    {
        if (srcRate <= 0) throw new ArgumentOutOfRangeException(nameof(srcRate));
        _ratio = srcRate / (double)RozhlasVoice.FeedRate;
        _send = send;
    }

    /// <summary>Frames sent so far.</summary>
    public long FramesSent { get; private set; }

    /// <summary>The next sequence number (wraps).</summary>
    public ushort NextSeq => _seq;

    /// <summary>Resamples <paramref name="mono"/> (-1..1) to 16 kHz and sends every 20 ms frame it completes.</summary>
    public void Push(ReadOnlySpan<float> mono)
    {
        foreach (var raw in mono)
        {
            var s = Math.Clamp((double)raw, -1.0, 1.0);
            var w = 1.0; // how much of this input sample is still to be spent
            while (w > 1e-9)
            {
                var room = _ratio - _accW;
                if (w < room) { _acc += s * w; _accW += w; w = 0; }
                else
                {
                    _acc += s * room;
                    Emit((short)Math.Clamp(Math.Round(_acc / _ratio * 32767.0), short.MinValue, short.MaxValue));
                    _acc = 0; _accW = 0; w -= room;
                }
            }
        }
    }

    void Emit(short sample)
    {
        _frame[_filled++] = sample;
        if (_filled < Frame) return;
        _filled = 0;
        _send(RozhlasMicWire.Encode(_seq++, _enc.Encode(_frame)));
        FramesSent++;
    }

    /// <summary>A talk burst ended (or the holder changed): drops the partial frame, the filter state and the codec state. The sequence keeps counting.</summary>
    public void Reset()
    {
        _filled = 0; _acc = 0; _accW = 0;
        _enc = new RozhlasOpus.Encoder();
    }
}

/// <summary>Receives one block of 16 kHz mono PCM (valid only during the call).</summary>
public delegate void PcmSink(ReadOnlySpan<short> pcm);

/// <summary>
/// The server's end of the upstream: turns the holder's frames into 16 kHz PCM for <c>RozhlasFeed.Write</c>. Checks nothing about WHO sent them (the server accepts only the holder, while
/// Hijacked, before it gets here). A duplicate or late frame is dropped, a gap of up to <see cref="MaxConceal"/> frames is concealed by the codec, a longer one (the holder paused, another
/// took over) just continues; a packet that does not decode is dropped, never thrown. One instance per holder: <see cref="Reset"/> when the holder changes.
/// </summary>
public sealed class RozhlasMicReceiver
{
    public const int MaxConceal = 3;
    const int Frame = RozhlasFeedSchedule.FrameSamples;

    RozhlasOpus.Decoder _dec = new();
    readonly short[] _pcm = new short[Frame];
    bool _has;
    ushort _last;

    public long Dropped { get; private set; }

    /// <summary>Decodes one upstream frame and calls <paramref name="write"/> with each 320-sample block (the concealed ones first). Returns how many blocks were written.</summary>
    public int Receive(ushort seq, byte[] opus, PcmSink write)
    {
        if (opus.Length == 0 || opus.Length > RozhlasMicWire.MaxOpusBytes) { Dropped++; return 0; }
        var lost = 0;
        if (_has)
        {
            var delta = (ushort)(seq - _last);
            if (delta == 0 || delta >= 0x8000) { Dropped++; return 0; } // a duplicate or an older frame
            if (delta - 1 is >= 1 and <= MaxConceal) lost = delta - 1;
        }
        var n = 0;
        try
        {
            for (var i = 0; i < lost; i++) { _dec.Decode(null, _pcm); write(_pcm); n++; }
            _dec.Decode(opus, _pcm);
        }
        catch (Exception)
        {
            Dropped++;
            _dec = new RozhlasOpus.Decoder(); // a decoder that threw is in an unknown state
            return n;
        }
        _last = seq; _has = true;
        write(_pcm); n++;
        return n;
    }

    /// <summary>A new holder (or none): forget the sequence and the codec state.</summary>
    public void Reset() { _has = false; _dec = new RozhlasOpus.Decoder(); }
}
