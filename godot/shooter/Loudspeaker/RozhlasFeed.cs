using System.Diagnostics;
using System.Text;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.RozhlasFeed;

namespace Drycastle.Shooter.Loudspeaker;

/// <summary>
/// The server's live rozhlas feed: whatever is written into it (voice frames, one-shot samples; 16 kHz mono) is piped through ONE ffmpeg subprocess per phase that runs the same
/// filter chain as the baked announcement (<see cref="RozhlasVoice"/>, rozhlas_voice.json), encoded as Opus and handed to <c>send</c> 2.0 s after it went in (<see cref="RozhlasFeedSchedule"/>),
/// 50 frames a second, so the network layer only has to broadcast the packets. Only frames that carry sound go out: while nothing is written (a mic nobody talks into) the process
/// keeps running on silence but nothing is encoded or sent. No Godot types: the owner (NetServer) calls <see cref="Pump"/> every frame on the game thread and supplies the sender.
/// <code>
/// feed.Start();                 // mic live: spawns ffmpeg (echo taps picked now), input is accepted from here on
/// feed.Write(pcm);              // a voice frame, any size; or feed.Mix(shot) for a one-shot on top
/// feed.Stop();                  // mic over: what was written still plays out, then the echo tail, then silence
/// </code>
/// ffmpeg missing, dying or stalling is logged ONCE for the phase and the feed stays silent until the next <see cref="Start"/>: unprocessed audio is never sent, the server never throws.
/// Threads: the game thread owns Start/Stop/Write/Mix/Pump; each session has a writer thread (steady 20 ms blocks into ffmpeg's stdin, silence when nothing was written), a reader
/// thread (ffmpeg's stdout into Opus frames) and a stderr drain. The state is <see cref="RozhlasFeedMachine"/>.
/// </summary>
public sealed class RozhlasFeed : IDisposable
{
    const int FrameSamples = RozhlasFeedSchedule.FrameSamples;
    /// <summary>How long after the last input the stdin stays open, to let the chain's echo (taps up to 700 ms) ring out.</summary>
    public const double TailS = 0.9;
    /// <summary>ffmpeg gets this long to produce its first frame (process start, filter init).</summary>
    const double FirstOutputS = 4.0;
    /// <summary>Output frames this long after the last input block with sound are still sent (the echo tail and the chain's delay); after that the feed is silent and sends nothing.</summary>
    static readonly int QuietAfterBlocks = (int)Math.Ceiling((TailS + 0.1) / RozhlasFeedSchedule.FrameS);
    /// <summary>Once it produced, a gap in its output longer than this means it stalled.</summary>
    const double StallS = 1.5;

    /// <summary>The chain's own algorithmic delay (alimiter look-ahead, resampler), measured with a tone burst through <see cref="RozhlasVoice.FilterGraph"/> (10 ms: 160 samples); the schedule subtracts it so the total is the 2.0 s.</summary>
    public static double PipelineDelayS = 0.010;

    readonly RozhlasVoice _voice;
    readonly Action<byte[]> _send;
    readonly Action<string> _log;
    readonly string _ffmpeg;
    readonly Func<double> _now;
    readonly Random _rng;
    readonly StateMachine<RozhlasFeedState> _fsm = new(RozhlasFeedMachine.Spec);
    readonly List<(long Frame, byte[] Data)> _due = new();
    Session? _s;
    ushort _seq;
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <param name="send">Takes the finished packet (<see cref="RozhlasFeedWire"/>) of one frame; broadcasts it to every client.</param>
    /// <param name="log">One line of the server log.</param>
    /// <param name="ffmpeg">The executable; null uses $DRYCASTLE_FFMPEG or "ffmpeg" from the PATH.</param>
    public RozhlasFeed(RozhlasVoice voice, Action<byte[]> send, Action<string> log, string? ffmpeg = null, Func<double>? clock = null, Random? rng = null)
    {
        _voice = voice; _send = send; _log = log;
        _ffmpeg = ffmpeg ?? Environment.GetEnvironmentVariable("DRYCASTLE_FFMPEG") ?? "ffmpeg";
        _now = clock ?? (() => Clock.Elapsed.TotalSeconds);
        _rng = rng ?? new Random();
    }

    public RozhlasFeedState State => _fsm.Current;
    /// <summary>The feed takes input (Live).</summary>
    public bool Accepting => _fsm.Is(RozhlasFeedState.Live);
    /// <summary>Frames broadcast so far (all phases).</summary>
    public long FramesSent { get; private set; }

    /// <summary>The mic is live: starts the ffmpeg process for this phase. A phase already Live is left alone (false); the tail of a previous one is cut short. Never throws; a feed that cannot run says so once and stays silent.</summary>
    public bool Start()
    {
        if (_fsm.Is(RozhlasFeedState.Live)) return false;
        _s?.Kill(); _s = null;
        var taps = _voice.PickTaps(_rng);
        var args = RozhlasVoice.LiveArgs(RozhlasVoice.FilterGraph(_voice.BuildChain(taps)));
        try
        {
            _s = new Session(_ffmpeg, args, _now);
        }
        catch (Exception e)
        {
            _log($"rozhlas feed: cannot start ffmpeg ('{_ffmpeg}'): {e.Message}. The live feed is silent for this phase (install ffmpeg on the server).");
            if (!_fsm.Is(RozhlasFeedState.Failed)) _fsm.Enter(RozhlasFeedState.Failed); // a retry after a failure fails the same way: it stays Failed
            return true;
        }
        _fsm.Enter(RozhlasFeedState.Live);
        _log($"rozhlas feed: live (echo taps {string.Join("|", taps)} ms, delay {RozhlasFeedSchedule.DelayS:0.0} s)");
        return true;
    }

    /// <summary>The mic is over: no more input; what was written still comes out at its time, then the echo tail, then the process ends (<see cref="Pump"/> sees it).</summary>
    public void Stop()
    {
        if (_fsm.Is(RozhlasFeedState.Failed)) { _fsm.Enter(RozhlasFeedState.Idle); return; }
        if (!_fsm.Is(RozhlasFeedState.Live)) return;
        _s!.RequestStop();
        _fsm.Enter(RozhlasFeedState.Draining);
    }

    /// <summary>Voice samples (16 kHz mono) appended to the voice track. Ignored unless Live.</summary>
    public void Write(ReadOnlySpan<short> pcm) { if (Accepting) _s!.Mixer.Write(pcm); }
    public void Write(ReadOnlySpan<float> pcm) { if (Accepting) _s!.Mixer.Write(pcm); }
    /// <summary>A one-shot sample (16 kHz mono) on top of everything, starting with the next block. Ignored unless Live.</summary>
    public void Mix(ReadOnlySpan<short> sample, float gain = 1f) { if (Accepting) _s!.Mixer.Mix(sample, gain); }


    /// <summary>Game thread, every frame: sends the frames that are due, notices a dead or stalled ffmpeg, finishes a drain.</summary>
    public void Pump()
    {
        var s = _s;
        if (s == null || !_fsm.IsAny(RozhlasFeedState.Live, RozhlasFeedState.Draining)) return;
        var now = _now();
        var why = s.FailedReason ?? s.Watchdog(now);
        if (why != null)
        {
            _log($"rozhlas feed: {why}. The live feed is silent for this phase.");
            s.Kill(); _s = null;
            _fsm.Enter(RozhlasFeedState.Failed);
            return;
        }
        _due.Clear();
        s.Schedule.TakeDue(now, _due);
        foreach (var (_, data) in _due)
        {
            _send(RozhlasFeedWire.Encode(_seq++, data));
            FramesSent++;
        }
        if (_fsm.Is(RozhlasFeedState.Draining) && s.Finished)
        {
            s.Kill(); _s = null;
            _fsm.Enter(RozhlasFeedState.Idle);
            _log($"rozhlas feed: drained, idle ({FramesSent} frames sent so far)");
        }
    }

    /// <summary>Ends the process now, discarding what is buffered (server shutdown).</summary>
    public void Dispose()
    {
        _s?.Kill(); _s = null;
        if (!_fsm.Is(RozhlasFeedState.Idle)) _fsm.Reset(RozhlasFeedState.Idle);
    }

    /// <summary>One ffmpeg process and its three threads.</summary>
    sealed class Session
    {
        readonly Process _p;
        readonly Stream _stdin, _stdout;
        readonly Thread _writer, _reader, _err;
        readonly StringBuilder _stderr = new();
        readonly double _t0;
        volatile bool _stop, _kill, _writerDone, _readerDone;
        volatile string? _failed;
        long _lastOutputTicks, _frames;
        /// <summary>The index of the last input block that was not silence (written by the writer before the block goes in, read by the reader); far in the past at the start.</summary>
        long _lastLoudBlock = long.MinValue / 2;
        readonly Func<double> _clock;

        public readonly RozhlasFeedMixer Mixer = new();
        public readonly RozhlasFeedSchedule Schedule;

        public Session(string exe, IReadOnlyList<string> args, Func<double> clock)
        {
            _clock = clock;
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            _p = Process.Start(psi) ?? throw new InvalidOperationException("the process did not start");
            _stdin = _p.StandardInput.BaseStream; _stdout = _p.StandardOutput.BaseStream;
            _t0 = _clock();
            Volatile.Write(ref _lastOutputTicks, Stopwatch.GetTimestamp());
            Schedule = new RozhlasFeedSchedule(_t0, RozhlasFeedSchedule.DelayS, PipelineDelayS);
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "rozhlas-feed-writer" };
            _reader = new Thread(ReaderLoop) { IsBackground = true, Name = "rozhlas-feed-reader" };
            _err = new Thread(() => { try { string? l; while ((l = _p.StandardError.ReadLine()) != null) lock (_stderr) { if (_stderr.Length < 2000) _stderr.AppendLine(l); } } catch { } }) { IsBackground = true, Name = "rozhlas-feed-stderr" };
            _writer.Start(); _reader.Start(); _err.Start();
        }

        public string? FailedReason => _failed;
        /// <summary>The reader hit the end of ffmpeg's output and every frame is out.</summary>
        public bool Finished => _readerDone && _failed == null && Schedule.Count == 0;
        public void RequestStop() => _stop = true;

        void Fail(string why) { _failed ??= why; }

        string StderrTail() { lock (_stderr) return _stderr.Length == 0 ? "" : " (ffmpeg said: " + _stderr.ToString().Trim().Replace('\n', ' ') + ")"; }

        /// <summary>A reason when ffmpeg stopped producing: nothing for a long time since the start, or a silence in its output while it should be running.</summary>
        public string? Watchdog(double now)
        {
            var sinceOut = (Stopwatch.GetTimestamp() - Volatile.Read(ref _lastOutputTicks)) / (double)Stopwatch.Frequency;
            if (Volatile.Read(ref _frames) == 0 && now - _t0 > FirstOutputS) return "ffmpeg produced no audio" + StderrTail();
            if (!_writerDone && Volatile.Read(ref _frames) > 0 && sinceOut > StallS) return "ffmpeg stalled" + StderrTail();
            if (_writerDone && !_readerDone && sinceOut > 3.0) return "ffmpeg did not finish after its input closed" + StderrTail();
            return null;
        }

        /// <summary>The input: a steady block every 20 ms from the start (silence when nothing was written), so ffmpeg sees a continuous stream; after Stop, until the mixer has run dry and the echo tail has passed.</summary>
        void WriterLoop()
        {
            try
            {
                var block = new short[FrameSamples];
                var bytes = new byte[FrameSamples * 2];
                var tailBlocks = (int)Math.Ceiling(TailS / RozhlasFeedSchedule.FrameS);
                long n = 0; var idleAfterStop = 0;
                while (!_kill)
                {
                    SleepUntil(_t0 + n * RozhlasFeedSchedule.FrameS);
                    if (_kill) break;
                    var stopping = _stop;
                    Mixer.Pull(block);
                    if (block.AsSpan().IndexOfAnyExcept((short)0) >= 0) Volatile.Write(ref _lastLoudBlock, n);
                    Buffer.BlockCopy(block, 0, bytes, 0, bytes.Length); // little endian on every platform the server runs on
                    _stdin.Write(bytes, 0, bytes.Length);
                    _stdin.Flush();
                    n++;
                    if (stopping && Mixer.IsIdle && ++idleAfterStop >= tailBlocks) break;
                }
                _stdin.Close(); // EOF: ffmpeg flushes what it holds and ends
            }
            catch (Exception e) when (!_kill) { Fail($"ffmpeg input broke: {e.Message}{StderrTail()}"); }
            catch { }
            finally { _writerDone = true; }
        }

        void SleepUntil(double t)
        {
            for (; ; )
            {
                var d = t - _clock();
                if (d <= 0 || _kill) return;
                Thread.Sleep(d > 0.004 ? (int)((d - 0.002) * 1000) : 1);
            }
        }

        /// <summary>The output: ffmpeg's raw PCM cut into 20 ms frames, each to Opus and onto the schedule under its index.</summary>
        void ReaderLoop()
        {
            try
            {
                var enc = new RozhlasOpus.Encoder();
                var buf = new byte[4096];
                var frameBytes = new byte[FrameSamples * 2];
                var frame = new short[FrameSamples];
                var have = 0; long idx = 0;
                int n;
                while ((n = _stdout.Read(buf, 0, buf.Length)) > 0)
                {
                    Volatile.Write(ref _lastOutputTicks, Stopwatch.GetTimestamp());
                    var off = 0;
                    while (off < n)
                    {
                        var take = Math.Min(n - off, frameBytes.Length - have);
                        Buffer.BlockCopy(buf, off, frameBytes, have, take);
                        have += take; off += take;
                        if (have < frameBytes.Length) continue;
                        Buffer.BlockCopy(frameBytes, 0, frame, 0, frameBytes.Length);
                        // output frame idx answers input block idx: silence in, silence out (the chain adds no noise), so it is not worth a packet
                        if (idx - Volatile.Read(ref _lastLoudBlock) <= QuietAfterBlocks) Schedule.Enqueue(idx, enc.Encode(frame));
                        idx++;
                        Volatile.Write(ref _frames, idx);
                        have = 0;
                    }
                }
                if (have > 0) // the last, short frame: padded with silence
                {
                    Array.Clear(frameBytes, have, frameBytes.Length - have);
                    Buffer.BlockCopy(frameBytes, 0, frame, 0, frameBytes.Length);
                    if (idx - Volatile.Read(ref _lastLoudBlock) <= QuietAfterBlocks) Schedule.Enqueue(idx, enc.Encode(frame));
                }
                if (!_kill && !(_stop && _writerDone)) Fail($"ffmpeg ended unexpectedly{ExitText()}{StderrTail()}");
            }
            catch (Exception e) when (!_kill) { Fail($"ffmpeg output broke: {e.Message}{StderrTail()}"); }
            catch { }
            finally { _readerDone = true; }
        }

        string ExitText() { try { if (_p.WaitForExit(500)) return $", exit code {_p.ExitCode}"; } catch { } return ""; }

        public void Kill()
        {
            _kill = true;
            try { if (!_p.HasExited) _p.Kill(true); } catch { }
            try { _p.Dispose(); } catch { }
        }
    }
}
