using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.RozhlasFeed;

/// <summary>What <see cref="RozhlasFeedJitter.Pop"/> has for the next 20 ms.</summary>
public enum JitterPop
{
    /// <summary>The next frame in sequence: decode it.</summary>
    Frame,
    /// <summary>The next frame never came but later ones did: conceal (decode with no data).</summary>
    Lost,
    /// <summary>Nothing to play (still filling, or ran dry): play silence.</summary>
    Empty,
}

/// <summary>
/// The client's jitter buffer for the live feed (unreliable, so frames can be late, doubled, out of order or lost). Collects frames by sequence number (u16, wrapping), starts
/// playing once <see cref="TargetFrames"/> are waiting (~100 ms), then gives one frame per call in order. A frame that arrives after its turn is dropped; a gap with later
/// frames behind it comes out as <see cref="JitterPop.Lost"/>; running dry goes back to filling. A backlog beyond <see cref="MaxFrames"/> (the server's clock is faster than
/// ours) skips ahead. Pure: the caller calls <see cref="Pop"/> once per 20 ms of audio it has room for.
/// </summary>
public sealed class RozhlasFeedJitter
{
    public const double FrameS = RozhlasFeedSchedule.FrameS;
    /// <summary>A jump this far ahead of what is playing is a new stream (the feed restarted): the buffer is flushed.</summary>
    const int RestartGap = 100;

    readonly SortedDictionary<long, byte[]> _frames = new();
    readonly StateMachine<RozhlasJitterState> _fsm = new(RozhlasJitterMachine.Spec);
    long _next = -1, _highest = -1;

    public RozhlasFeedJitter(int targetFrames = 5, int maxFrames = 15)
    {
        TargetFrames = targetFrames; MaxFrames = Math.Max(maxFrames, targetFrames + 2);
    }

    public int TargetFrames { get; }
    public int MaxFrames { get; }
    public RozhlasJitterState State => _fsm.Current;
    public int Buffered => _frames.Count;
    public long Late { get; private set; }
    public long Concealed { get; private set; }
    public long Skipped { get; private set; }

    public void Push(ushort seq, byte[] data)
    {
        long ext;
        if (_highest < 0) ext = seq;
        else
        {
            ext = _highest + (short)(seq - (ushort)_highest); // unwrap relative to the newest
            if (ext > _highest + RestartGap || ext < _next - RestartGap * 4) { Reset(); ext = seq; }
        }
        if (_next >= 0 && ext < _next) { Late++; return; }
        _frames[ext] = data;
        if (ext > _highest) _highest = ext;
        if (_fsm.Is(RozhlasJitterState.Playing))
            while (_highest - _next + 1 > MaxFrames) { _frames.Remove(_next); _next++; Skipped++; }
    }

    /// <summary>The next 20 ms. <paramref name="data"/> is set for <see cref="JitterPop.Frame"/>.</summary>
    public JitterPop Pop(out byte[]? data)
    {
        data = null;
        if (_fsm.Is(RozhlasJitterState.Priming))
        {
            if (_frames.Count < TargetFrames) return JitterPop.Empty;
            _next = _frames.Keys.First(); // what was missed while filling is not played late
            _fsm.Enter(RozhlasJitterState.Playing);
        }
        if (_frames.Remove(_next, out var f)) { _next++; data = f; return JitterPop.Frame; }
        if (_next < _highest) { _next++; Concealed++; return JitterPop.Lost; }
        _fsm.Enter(RozhlasJitterState.Priming); // ran dry
        return JitterPop.Empty;
    }

    public void Reset()
    {
        _frames.Clear(); _next = -1; _highest = -1;
        if (!_fsm.Is(RozhlasJitterState.Priming)) _fsm.Enter(RozhlasJitterState.Priming);
    }
}
