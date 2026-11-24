namespace Drycastle.ShooterCore.RozhlasFeed;

/// <summary>
/// When each output frame of the live feed goes to the clients: a FIXED delay after the moment its input was due, whatever ffmpeg's own latency was.
/// The input is pulled at a steady 50 blocks a second from the feed's start, and ffmpeg is sample-synchronous, so output frame n carries the audio of input block n
/// (shifted only by the chain's algorithmic delay, <paramref name="pipelineS"/>, a constant). The frame is therefore released at
/// <c>start + delay - pipelineS + n * 20 ms</c> on the server's clock; ffmpeg's pipe and process latency only has to stay below the delay (it is ~tens of ms against 2 s),
/// and a slow frame is sent late, never earlier than its time. Frames arrive in order from the reader; <see cref="TakeDue"/> hands out those whose time has come.
/// Thread-safe (the reader enqueues, the game thread takes).
/// </summary>
public sealed class RozhlasFeedSchedule
{
    public const int FrameSamples = 320; // 20 ms at 16 kHz
    public const double FrameS = FrameSamples / (double)RozhlasVoice.FeedRate;
    /// <summary>Total delay between a sound entering the feed and being broadcast (design: 2 s).</summary>
    public const double DelayS = 2.0;
    /// <summary>A frame later than this past its time is dropped rather than sent (its moment is gone, and a burst of stale audio is worse than a gap).</summary>
    public const double MaxLateS = 0.5;

    readonly object _lock = new();
    readonly Queue<(long Frame, byte[] Data)> _q = new();
    readonly double _start, _delay, _pipeline;

    public RozhlasFeedSchedule(double startS, double delayS = DelayS, double pipelineS = 0)
    {
        _start = startS; _delay = delayS; _pipeline = pipelineS;
    }

    /// <summary>Frames dropped for arriving more than <see cref="MaxLateS"/> late.</summary>
    public long DroppedLate { get; private set; }

    /// <summary>The server clock time frame <paramref name="frame"/> is due to go out.</summary>
    public double ReleaseAt(long frame) => _start + _delay - _pipeline + frame * FrameS;

    public void Enqueue(long frame, byte[] data)
    {
        lock (_lock) _q.Enqueue((frame, data));
    }

    public int Count { get { lock (_lock) return _q.Count; } }

    /// <summary>Moves the frames due at <paramref name="nowS"/> into <paramref name="into"/>, oldest first. Returns how many.</summary>
    public int TakeDue(double nowS, List<(long Frame, byte[] Data)> into)
    {
        var n = 0;
        lock (_lock)
        {
            while (_q.Count > 0)
            {
                var (frame, data) = _q.Peek();
                var at = ReleaseAt(frame);
                if (at > nowS) break;
                _q.Dequeue();
                if (nowS - at > MaxLateS) { DroppedLate++; continue; }
                into.Add((frame, data)); n++;
            }
        }
        return n;
    }

    public void Clear() { lock (_lock) _q.Clear(); }
}
