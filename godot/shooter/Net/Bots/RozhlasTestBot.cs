using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Test aid for the headless bot: <c>--bot-chat "3:/event rozhlas;6:/rozhlas feedtest"</c> sends each chat line at its second (admin commands need a
/// dev server, --admin-all), and the bot decodes the rozhlas live feed it receives and prints, when it ends, how many frames came and when the feed was loud
/// (half-second windows), so a script can check the path server → ffmpeg → Opus → client without a sound card. Not a <see cref="ClientBot"/>: it runs beside
/// whichever bot plays.
/// </summary>
public sealed class RozhlasTestBot
{
    readonly IBotHost _host;
    readonly List<(double At, string Text)> _chat = new();
    RozhlasOpus.Decoder? _feedDec;
    readonly short[] _feedPcm = new short[RozhlasFeedSchedule.FrameSamples];
    readonly SortedDictionary<int, (double Sum, int N)> _feedLoud = new();
    int _feedFrames;
    RozhlasOpus.Encoder? _micEnc;
    short[]? _micVoice;
    int _micSent;
    double _micSince = -1;

    public RozhlasTestBot(IBotHost host, string script)
    {
        _host = host;
        foreach (var part in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var c = part.IndexOf(':');
            if (c > 0 && double.TryParse(part[..c], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var at)) _chat.Add((at, part[(c + 1)..]));
        }
    }

    /// <summary>At the dictation table ("/rozhlas goto"): the walker stands still and holds E until the set is taken.</summary>
    public bool Standing { get; private set; }

    string Name => _host.Ctx.PlayerName;

    /// <summary>Every physics tick: sends the chat lines that are due.</summary>
    public void Tick()
    {
        for (var i = 0; i < _chat.Count; i++)
        {
            if (_chat[i].At > _host.Elapsed) continue;
            GD.Print($"client {Name}: t={_host.Elapsed:0.0} chat: {_chat[i].Text}");
            _host.SendChat(_chat[i].Text);
            if (_chat[i].Text.Contains("rozhlas goto")) Standing = true; else if (_chat[i].Text.Contains("rozhlas release")) Standing = false;
            _chat.RemoveAt(i--);
        }
    }

    /// <summary>A frame of the live feed arrived: its loudness goes into the half-second window.</summary>
    public void OnFeedFrame(byte[] opus)
    {
        _feedDec ??= new RozhlasOpus.Decoder();
        _feedDec.Decode(opus, _feedPcm);
        double sum = 0;
        foreach (var s in _feedPcm) sum += (double)s * s;
        var w = (int)(_host.Elapsed * 2);
        var (a, n) = _feedLoud.GetValueOrDefault(w);
        _feedLoud[w] = (a + sum, n + _feedPcm.Length);
        _feedFrames++;
    }

    /// <summary>The scripted bot has no microphone: while it holds the set it "talks" the test voice, 50 frames a second, as a real client's capture would.</summary>
    public void MicTick(bool holder)
    {
        if (!holder) { _micSince = -1; return; }
        if (_micSince < 0) { _micSince = _host.Elapsed; _micSent = 0; GD.Print($"client {Name}: t={_host.Elapsed:0.0} holds the set, talking"); }
        _micEnc ??= new RozhlasOpus.Encoder();
        _micVoice ??= RozhlasFeedTestSignal.Voice(4.0);
        var n = RozhlasFeedSchedule.FrameSamples;
        var want = (int)((_host.Elapsed - _micSince) * RozhlasVoice.FeedRate / n);
        for (; _micSent < want && (_micSent + 1) * n <= _micVoice.Length; _micSent++)
            _host.SendMic(RozhlasMicWire.Encode((ushort)_micSent, _micEnc.Encode(_micVoice.AsSpan(_micSent * n, n))));
    }

    public void Report()
    {
        var loud = string.Join(" ", _feedLoud.Select(kv => $"{kv.Key / 2.0:0.0}:{Math.Sqrt(kv.Value.Sum / Math.Max(1, kv.Value.N)):0}"));
        GD.Print($"client {Name}: rozhlas feed frames {_feedFrames}, rms by half second (t:rms) {loud}");
    }
}
