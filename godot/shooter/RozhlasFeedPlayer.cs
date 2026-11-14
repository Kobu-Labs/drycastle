using Godot;
using Drycastle.ShooterCore;
using Num = System.Numerics;

namespace Drycastle.Shooter;

/// <summary>
/// The client end of the rozhlas live feed (slice 3). The server sends the already degraded voice as Opus frames on <see cref="Wire.ChanRozhlasFeed"/>; this node puts them through a
/// small jitter buffer (<see cref="RozhlasFeedJitter"/>, ~100 ms), decodes them (<see cref="RozhlasOpus"/>) and plays them from an <see cref="AudioStreamGenerator"/> on its own bus,
/// which follows the announcement's megaphone rules by calling the same functions: the level from the distance to the nearest pole (<see cref="RozhlasRules.GainDb"/>) and a low-pass
/// that gets duller with distance (<see cref="RozhlasRules.CutoffHz"/>), both from rozhlas.json. No DSP of its own: all the badness was done on the server. Heard by everyone, anywhere
/// in range of a pole. Client only; silent on headless runs. It is a node of its own (not part of <see cref="Rozhlas"/>) so it works whether or not a broadcast file is baked.
/// </summary>
public partial class RozhlasFeedPlayer : Node
{
    const string DataPath = "res://data/rozhlas.json";
    const string BusName = "RozhlasFeed";
    const int Frame = RozhlasFeedSchedule.FrameSamples;
    /// <summary>The generator is kept about this full (two frames, 40 ms); the jitter buffer holds the rest of the delay.</summary>
    const int KeepFrames = 2 * Frame;

    /// <summary>Where the speakers hang (Godot axes); the horns of the megaphone poles.</summary>
    public IReadOnlyList<Vector3> Speakers { get; init; } = Array.Empty<Vector3>();

    /// <summary>This client's feed player for the network layer to push frames into (null before the map is loaded and on headless runs).</summary>
    public static RozhlasFeedPlayer? Current { get; private set; }

    RozhlasFile _cfg = null!;
    Num.Vector3[] _spk = Array.Empty<Num.Vector3>();
    readonly RozhlasFeedJitter _jitter = new();
    readonly RozhlasOpus.Decoder _dec = new();
    readonly short[] _pcm = new short[Frame];
    readonly Vector2[] _out = new Vector2[Frame];
    AudioStreamPlayer _player = null!;
    AudioStreamGeneratorPlayback? _pb;
    int _cap, _bus = -1;
    Vector3? _listener;

    public override void _Ready()
    {
        var json = Godot.FileAccess.Open(DataPath, Godot.FileAccess.ModeFlags.Read)?.GetAsText()
            ?? throw new DataFileException($"rozhlas.json: cannot open ({Godot.FileAccess.GetOpenError()})");
        _cfg = RozhlasFile.Parse(json);
        if (!Sfx.Enabled || Speakers.Count == 0) { SetProcess(false); return; }
        Current = this;
        _spk = Speakers.Select(s => new Num.Vector3(s.X, s.Y, s.Z)).ToArray();
        AudioServer.AddBus();
        _bus = AudioServer.BusCount - 1;
        AudioServer.SetBusName(_bus, BusName);
        AudioServer.SetBusSend(_bus, "SFX"); // like the announcement's bus: the effects volume still applies
        AudioServer.AddBusEffect(_bus, new AudioEffectLowPassFilter { CutoffHz = _cfg.NearCutoffHz, Db = AudioEffectFilter.FilterDB.Filter24Db });
        var gen = new AudioStreamGenerator { MixRate = RozhlasVoice.FeedRate, BufferLength = 0.25f };
        _cap = (int)(gen.BufferLength * gen.MixRate);
        _player = new AudioStreamPlayer { Bus = BusName, Stream = gen, VolumeDb = RozhlasRules.SilentDb };
        AddChild(_player);
        _player.Play();
        _pb = _player.GetStreamPlayback() as AudioStreamGeneratorPlayback;
    }

    /// <summary>Where the player is (their body), set every frame by the network client, like <see cref="Rozhlas.SetListener"/>; the camera is the fallback.</summary>
    public void SetListener(Vector3 at) => _listener = at;

    /// <summary>One frame from the server (from the network layer, on the game thread).</summary>
    public void Push(ushort seq, byte[] opus)
    {
        if (_pb != null) _jitter.Push(seq, opus);
    }

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
        if (AudioServer.GetBusIndex(BusName) is var i and >= 0) AudioServer.RemoveBus(i);
    }

    public override void _Process(double delta)
    {
        if (_pb == null) return;
        var cam = GetViewport().GetCamera3D();
        if (_listener == null && cam == null) return;
        var p = _listener ?? cam!.GlobalPosition;
        var distance = RozhlasRules.NearestDistance(_spk, new Num.Vector3(p.X, p.Y, p.Z));
        _player.VolumeDb = distance >= _cfg.MaxDistance ? RozhlasRules.SilentDb
            : _cfg.VolumeDb + RozhlasRules.GainDb(distance, _cfg.ReferenceDistance, _cfg.MaxDistance, _cfg.RolloffDb);
        if (AudioServer.GetBusEffect(_bus, 0) is AudioEffectLowPassFilter f)
            f.CutoffHz = RozhlasRules.CutoffHz(distance, _cfg.ReferenceDistance, _cfg.MaxDistance, _cfg.NearCutoffHz, _cfg.FarCutoffHz);

        var queued = _cap - _pb.GetFramesAvailable();
        while (queued < KeepFrames)
        {
            var r = _jitter.Pop(out var data);
            if (r == JitterPop.Empty) break; // still filling, or the feed is off: the generator plays silence
            _dec.Decode(r == JitterPop.Frame ? data : null, _pcm);
            for (var i = 0; i < Frame; i++) { var s = _pcm[i] / 32768f; _out[i] = new Vector2(s, s); }
            _pb.PushBuffer(_out);
            queued += Frame;
        }
    }
}
