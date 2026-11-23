using Godot;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.RozhlasFeed;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// Test aid for the headless bot: <c>--bot-chat "3:/event rozhlas;6:/rozhlas feedtest"</c> sends each chat line at its second (admin commands need a
/// dev server, --admin-all), and the bot decodes the rozhlas live feed it receives and prints, when it ends, how many frames came and when the feed was loud
/// (half-second windows), so a script can check the path server → ffmpeg → Opus → client without a sound card.
/// </summary>
public partial class NetClient
{
    public string BotChat = "";
    List<(double At, string Text)>? _botChat;
    RozhlasOpus.Decoder? _botFeedDec;
    readonly short[] _botFeedPcm = new short[RozhlasFeedSchedule.FrameSamples];
    readonly SortedDictionary<int, (double Sum, int N)> _botFeedLoud = new();
    int _botFeedFrames;
    bool _botStand;

    void BotChatTick()
    {
        if (!Bot || BotChat == "") return;
        if (_botChat == null)
        {
            _botChat = new();
            foreach (var part in BotChat.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var c = part.IndexOf(':');
                if (c > 0 && double.TryParse(part[..c], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var at)) _botChat.Add((at, part[(c + 1)..]));
            }
        }
        for (var i = 0; i < _botChat.Count; i++)
        {
            if (_botChat[i].At > _elapsed) continue;
            GD.Print($"client {PlayerName}: t={_elapsed:0.0} chat: {_botChat[i].Text}");
            SendChat(_botChat[i].Text);
            if (_botChat[i].Text.Contains("rozhlas goto")) _botStand = true; else if (_botChat[i].Text.Contains("rozhlas release")) _botStand = false;
            _botChat.RemoveAt(i--);
        }
    }

    void BotFeedFrame(byte[] opus)
    {
        if (!Bot) return;
        _botFeedDec ??= new RozhlasOpus.Decoder();
        _botFeedDec.Decode(opus, _botFeedPcm);
        double sum = 0;
        foreach (var s in _botFeedPcm) sum += (double)s * s;
        var w = (int)(_elapsed * 2);
        var (a, n) = _botFeedLoud.GetValueOrDefault(w);
        _botFeedLoud[w] = (a + sum, n + _botFeedPcm.Length);
        _botFeedFrames++;
    }

    RozhlasOpus.Encoder? _botMicEnc;
    short[]? _botMicVoice;
    int _botMicSent;
    double _botMicSince = -1;

    /// <summary>The scripted bot has no microphone: while it holds the set it "talks" the test voice, 50 frames a second, as a real client's capture would.</summary>
    void BotMicTick(bool holder)
    {
        if (!Bot || BotChat == "" || _peer == null) return;
        if (!holder) { _botMicSince = -1; return; }
        if (_botMicSince < 0) { _botMicSince = _elapsed; _botMicSent = 0; GD.Print($"client {PlayerName}: t={_elapsed:0.0} holds the set, talking"); }
        _botMicEnc ??= new RozhlasOpus.Encoder();
        _botMicVoice ??= RozhlasFeedTestSignal.Voice(4.0);
        var n = RozhlasFeedSchedule.FrameSamples;
        var want = (int)((_elapsed - _botMicSince) * RozhlasVoice.FeedRate / n);
        for (; _botMicSent < want && (_botMicSent + 1) * n <= _botMicVoice.Length; _botMicSent++)
            Tx(_peer, Wire.ChanRozhlasFeed, RozhlasMicWire.Encode((ushort)_botMicSent, _botMicEnc.Encode(_botMicVoice.AsSpan(_botMicSent * n, n))), 0);
    }

    void BotFeedReport()
    {
        if (!Bot || BotChat == "") return;
        var loud = string.Join(" ", _botFeedLoud.Select(kv => $"{kv.Key / 2.0:0.0}:{Math.Sqrt(kv.Value.Sum / Math.Max(1, kv.Value.N)):0}"));
        GD.Print($"client {PlayerName}: rozhlas feed frames {_botFeedFrames}, rms by half second (t:rms) {loud}");
    }
}
