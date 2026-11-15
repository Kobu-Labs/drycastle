using System.Diagnostics;
using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The rozhlas live feed on the server (slice 3): <see cref="RozhlasFeed"/> turns what is written into it into the degraded, 2 s delayed Opus stream, and this file broadcasts that
/// to every client on <see cref="Wire.ChanRozhlasFeed"/>. <see cref="RozhlasLiveFeed"/> is the API the later slices call (Start/Stop when the mic goes live and dead, Write for the set
/// holder's voice, Mix for combat sounds); today its only caller is the admin's <c>/rozhlas feedtest</c>. Optional: with no rozhlas_voice.json or no ffmpeg the feed is silent (logged), the game goes on.
/// </summary>
public partial class NetServer
{
    LazyInit<RozhlasFeed> _liveFeed;

    /// <summary>The live feed (null when rozhlas_voice.json cannot be read, logged once). Start it when the mic goes live, Stop it when the mic is dead.</summary>
    public RozhlasFeed? RozhlasLiveFeed => EnsureLiveFeed();

    RozhlasFeed? EnsureLiveFeed()
    {
        if (!_liveFeed.Begin()) return _liveFeed.Value;
        var json = Godot.FileAccess.Open("res://data/rozhlas_voice.json", Godot.FileAccess.ModeFlags.Read)?.GetAsText();
        if (json == null) { GD.PrintErr($"server: rozhlas_voice.json: cannot open ({Godot.FileAccess.GetOpenError()}); no live rozhlas feed"); return null; }
        RozhlasVoice voice;
        try { voice = RozhlasVoice.Parse(json); }
        catch (DataFileException e) { GD.PrintErr($"server: {e.Message}; no live rozhlas feed"); return null; }
        var feed = new RozhlasFeed(voice, BroadcastRozhlasFeed, m => GD.Print($"server: {m}"));
        _liveFeed.Ready(feed);
        TreeExiting += () => feed.Dispose(); // ffmpeg must not outlive the server
        RozhlasPhaseChanged += OnRozhlasPhaseForFeed;
        return feed;
    }

    /// <summary>The feed runs exactly while the mic is live: it starts with a detour broadcast (OnAir), runs through a hijack, and drains when the loudspeaker goes Idle.</summary>
    void OnRozhlasPhaseForFeed(RozhlasPhase from, RozhlasPhase to)
    {
        if (_liveFeed.Value is not { } feed) return;
        RozhlasCombatPhase(from, to); // hijack thump/feedback, the end click: while the feed still takes input (NetServer.RozhlasCombat.cs)
        if (from == RozhlasPhase.Idle) feed.Start(); // already Live under a running feedtest: the phase takes it over
        else if (to == RozhlasPhase.Idle) feed.Stop();
        if (_feedTest is { } t) t.OwnsFeed = false;
    }

    bool MicLive => _rozhlasBroadcast.Value is { MicLive: true };

    /// <summary>One feed frame to every joined player (unreliable on its own channel; bots have no connection).</summary>
    void BroadcastRozhlasFeed(byte[] packet)
    {
        foreach (var c in _clients.Values)
            if (c.Joined && c.Peer is { } peer) Tx(peer, Wire.ChanRozhlasFeed, packet, 0);
    }

    /// <summary>Every frame: the frames that are due go out, and a running /rozhlas feedtest is fed.</summary>
    void RozhlasFeedPump()
    {
        if (_liveFeed.Value == null) return;
        FeedTestTick();
        _liveFeed.Value.Pump();
    }

    // ---- /rozhlas feedtest: a few seconds of synthetic voice, then a "gunshot", through the whole pipeline -----------------------------------------------------------------------------------

    const double FeedTestVoiceS = 4.0, FeedTestShotS = 4.5, FeedTestEndS = 5.5;
    sealed class FeedTest { public readonly Stopwatch Clock = Stopwatch.StartNew(); public int Written; public bool Shot; public short[] Voice = RozhlasFeedTestSignal.Voice(FeedTestVoiceS); public string Who = ""; public bool OwnsFeed; }
    FeedTest? _feedTest;

    /// <summary>"/rozhlas feedtest": starts the feed, pushes 4 s of test voice (written in real time, like a mic) and a shot into it, stops it. Everyone hears it from the megaphones about 2 s later.</summary>
    void ChatRozhlasFeedTest(Client c)
    {
        if (EnsureLiveFeed() is not { } feed) { ChatTo(c, ChatLineKind.Error, "The live feed has no data (rozhlas_voice.json could not be read)."); return; }
        if (_feedTest != null) { ChatTo(c, ChatLineKind.Error, "A feed test is already running."); return; }
        var owns = !MicLive; // while the mic is live the phase owns the feed: the test only writes into it
        if (owns) feed.Start();
        if (feed.State != RozhlasFeedState.Live) { ChatTo(c, ChatLineKind.Error, "The live feed is not running (ffmpeg missing? see the server log)."); if (owns) feed.Stop(); return; }
        _feedTest = new FeedTest { Who = c.Name, OwnsFeed = owns };
        ChatToAll(ChatLineKind.System, "", $"{c.Name} started the rozhlas feed test.");
    }

    void FeedTestTick()
    {
        if (_feedTest is not { } t || _liveFeed.Value is not { } feed) return;
        if (feed.State != RozhlasFeedState.Live) { _feedTest = null; return; } // it failed (logged by the feed)
        var el = t.Clock.Elapsed.TotalSeconds;
        var want = (int)(Math.Min(el, FeedTestVoiceS) * RozhlasVoice.FeedRate);
        if (want > t.Written) { feed.Write(t.Voice.AsSpan(t.Written, want - t.Written)); t.Written = want; }
        if (!t.Shot && el >= FeedTestShotS) { feed.Mix(RozhlasFeedTestSignal.Shot()); t.Shot = true; }
        if (el >= FeedTestEndS) { if (t.OwnsFeed) feed.Stop(); _feedTest = null; }
    }
}
