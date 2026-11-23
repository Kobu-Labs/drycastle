namespace Drycastle.ShooterCore.RozhlasFeed;

/// <summary>
/// The input timeline of the live feed: 16 kHz mono 16-bit, pulled in fixed blocks at a steady rate by whoever owns the clock (the feed's writer thread). Two kinds of input,
/// both added into whatever block is pulled next: the voice track (frames written in order, e.g. a player's mic, queued and played back to back) and one-shot samples
/// (a gunshot) that start at the next block and may overlap. A block with nothing in it is silence, so the stream the pipeline sees is continuous.
/// Thread-safe: the game thread writes, the writer thread pulls.
/// </summary>
public sealed class RozhlasFeedMixer
{
    public const int Rate = RozhlasVoice.FeedRate;

    sealed class Shot { public short[] Data = null!; public int Pos; public float Gain; }

    readonly object _lock = new();
    readonly Queue<short[]> _voice = new();
    int _voicePos, _voiceQueued;
    readonly List<Shot> _shots = new();
    readonly int _maxVoiceSamples;
    readonly int _maxShots;

    /// <summary>Most one-shots playing at once; a new one past it takes the place of the oldest (automatic fire stays audible, a flood cannot pile up).</summary>
    public const int DefaultMaxShots = 16;

    /// <param name="maxVoiceSeconds">The voice queue never holds more than this: a burst of frames that arrive faster than they play drops the oldest (it would only add delay).</param>
    /// <param name="maxShots">The cap on one-shots playing at once.</param>
    public RozhlasFeedMixer(double maxVoiceSeconds = 0.5, int maxShots = DefaultMaxShots)
    {
        _maxVoiceSamples = (int)(maxVoiceSeconds * Rate);
        _maxShots = Math.Max(1, maxShots);
    }


    /// <summary>One-shots playing now.</summary>
    public int ActiveShots { get { lock (_lock) return _shots.Count; } }

    /// <summary>Samples of the voice track dropped because the queue was full.</summary>
    public long DroppedVoiceSamples { get; private set; }

    /// <summary>Appends voice samples (16 kHz mono) after what is already queued.</summary>
    public void Write(ReadOnlySpan<short> pcm)
    {
        if (pcm.IsEmpty) return;
        var copy = pcm.ToArray();
        lock (_lock)
        {
            _voice.Enqueue(copy);
            _voiceQueued += copy.Length;
            while (_voiceQueued > _maxVoiceSamples && _voice.Count > 0)
            {
                var head = _voice.Peek();
                var left = head.Length - _voicePos;
                var drop = Math.Min(left, _voiceQueued - _maxVoiceSamples);
                _voicePos += drop; _voiceQueued -= drop; DroppedVoiceSamples += drop;
                if (_voicePos >= head.Length) { _voice.Dequeue(); _voicePos = 0; }
            }
        }
    }

    /// <summary>Appends voice samples as floats in -1..1.</summary>
    public void Write(ReadOnlySpan<float> pcm)
    {
        var s = new short[pcm.Length];
        for (var i = 0; i < s.Length; i++) s[i] = ToShort(pcm[i]);
        Write(s);
    }

    /// <summary>Starts a one-shot sample (16 kHz mono) at the next block, on top of everything else.</summary>
    public void Mix(ReadOnlySpan<short> sample, float gain = 1f)
    {
        if (sample.IsEmpty) return;
        var copy = sample.ToArray();
        lock (_lock)
        {
            if (_shots.Count >= _maxShots) _shots.RemoveAt(0); // the oldest (the list keeps start order)
            _shots.Add(new Shot { Data = copy, Gain = gain });
        }
    }

    /// <summary>True when nothing is queued and no one-shot is still playing: the next blocks are silence.</summary>
    public bool IsIdle { get { lock (_lock) return _voiceQueued == 0 && _shots.Count == 0; } }

    /// <summary>Samples of voice waiting.</summary>
    public int QueuedVoiceSamples { get { lock (_lock) return _voiceQueued; } }

    /// <summary>Fills <paramref name="dst"/> with the next block: voice and one-shots summed (clipped to 16 bit), silence where there is nothing.</summary>
    public void Pull(Span<short> dst)
    {
        Span<int> acc = dst.Length <= 2048 ? stackalloc int[dst.Length] : new int[dst.Length];
        lock (_lock)
        {
            var i = 0;
            while (i < dst.Length && _voice.Count > 0)
            {
                var head = _voice.Peek();
                var n = Math.Min(dst.Length - i, head.Length - _voicePos);
                for (var k = 0; k < n; k++) acc[i + k] += head[_voicePos + k];
                i += n; _voicePos += n; _voiceQueued -= n;
                if (_voicePos >= head.Length) { _voice.Dequeue(); _voicePos = 0; }
            }
            for (var s = _shots.Count - 1; s >= 0; s--)
            {
                var sh = _shots[s];
                var n = Math.Min(dst.Length, sh.Data.Length - sh.Pos);
                for (var k = 0; k < n; k++) acc[k] += (int)MathF.Round(sh.Data[sh.Pos + k] * sh.Gain);
                sh.Pos += n;
                if (sh.Pos >= sh.Data.Length) _shots.RemoveAt(s);
            }
        }
        for (var k = 0; k < dst.Length; k++) dst[k] = (short)Math.Clamp(acc[k], short.MinValue, short.MaxValue);
    }

    /// <summary>Forgets everything queued (a phase that ends hard).</summary>
    public void Clear()
    {
        lock (_lock) { _voice.Clear(); _voicePos = 0; _voiceQueued = 0; _shots.Clear(); }
    }

    static short ToShort(float f) => (short)Math.Clamp((int)MathF.Round(f * 32767f), short.MinValue, short.MaxValue);

    /// <summary>Linear resample of mono PCM to <see cref="Rate"/> (for a one-shot recorded at another rate; the chain band-limits to 2.2 kHz anyway).</summary>
    public static short[] Resample(ReadOnlySpan<short> src, int srcRate)
    {
        if (srcRate == Rate) return src.ToArray();
        var n = (int)((long)src.Length * Rate / srcRate);
        var dst = new short[n];
        for (var i = 0; i < n; i++)
        {
            var pos = (double)i * srcRate / Rate;
            var i0 = (int)pos;
            var i1 = Math.Min(i0 + 1, src.Length - 1);
            var f = pos - i0;
            dst[i] = (short)Math.Round(src[i0] * (1 - f) + src[i1] * f);
        }
        return dst;
    }
}
