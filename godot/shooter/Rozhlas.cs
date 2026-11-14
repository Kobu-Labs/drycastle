using Godot;
using Drycastle.ShooterCore;
using Num = System.Numerics;

namespace Drycastle.Shooter;

/// <summary>
/// The village loudspeaker (rozhlas): a baked broadcast (rozhlas.json) heard across the village. It is one shared recording, not one per speaker.
/// The SERVER owns the schedule and the phase (<see cref="RozhlasBroadcast"/>): it tells every client when a detour broadcast starts, which variant and how far
/// into it (<see cref="EventKind.RozhlasState"/>, <see cref="Apply"/>), so a client that joins mid-broadcast plays from the right position, and nothing here reads
/// a wall clock. When the announcer is killed (Hijacked) the announcement is cut at once (<see cref="OnHijackCut"/>). Only the distance from the player to the nearest
/// megaphone pole sets how loud (<see cref="RozhlasRules.GainDb"/>) and how dull (<see cref="RozhlasRules.CutoffHz"/>, a low-pass on the player's own bus) it is.
/// An intel or event announcement over the megaphones (<see cref="PlayAnnouncement"/>) is a separate, client-side one-shot on its own player: it does not move the
/// phase, plays over a hijack and does not end it. The audio is optional: with no file the game stays silent. Client only, and silent on headless runs.
/// </summary>
public partial class Rozhlas : Node
{
    const string DataPath = "res://data/rozhlas.json";
    const string BusName = "Rozhlas";

    /// <summary>Where the speakers hang (Godot axes); the horns of the megaphone poles.</summary>
    public IReadOnlyList<Vector3> Speakers { get; init; } = Array.Empty<Vector3>();

    /// <summary>The loudspeaker of this client, for the network layer (null before the map is loaded and on headless runs).</summary>
    public static Rozhlas? Current { get; private set; }

    // The server's last word, kept apart from the node so one that arrives before the map is loaded (a joiner's catch-up) is not lost.
    static RozhlasMsg _told = new(RozhlasPhase.Idle, -1, 0, 0);
    static ulong _toldAtMs;
    static int _toldSeq;

    /// <summary>The server told us the loudspeaker's phase (<see cref="EventKind.RozhlasState"/>): the broadcast plays from <paramref name="m"/>'s offset, counted from now.</summary>
    public static void Apply(RozhlasMsg m)
    {
        _told = m; _toldAtMs = Time.GetTicksMsec(); _toldSeq++;
    }

    /// <summary>Forgets the server's word (a new connection starts Idle).</summary>
    public static void Reset() { _told = new(RozhlasPhase.Idle, -1, 0, 0); _toldSeq++; }

    RozhlasFile _cfg = null!;
    Num.Vector3[] _spk = Array.Empty<Num.Vector3>();
    AudioStream?[] _streams = Array.Empty<AudioStream?>();
    AudioStreamPlayer _player = null!;   // the detour broadcast the server says is on air
    AudioStreamPlayer _overlay = null!;  // an intel / event announcement (client-side one-shot)
    int _bus = -1;
    int _seen = -1;
    RozhlasPhase _phase;

    public override void _Ready()
    {
        var json = Godot.FileAccess.Open(DataPath, Godot.FileAccess.ModeFlags.Read)?.GetAsText()
            ?? throw new DataFileException($"rozhlas.json: cannot open ({Godot.FileAccess.GetOpenError()})");
        _cfg = RozhlasFile.Parse(json); // a broken table fails a smoke run even headless
        if (!Sfx.Enabled || Speakers.Count == 0) { SetProcess(false); return; }
        _streams = _cfg.Files.Select(LoadStream).ToArray();
        if (_streams.All(s => s == null)) { GD.Print("rozhlas: no audio yet (run scripts/make_rozhlas.py); staying silent"); SetProcess(false); return; }
        Current = this;
        _spk = Speakers.Select(s => new Num.Vector3(s.X, s.Y, s.Z)).ToArray();
        // its own bus so the low-pass can follow the distance; it feeds the SFX bus (so the effects volume still applies)
        AudioServer.AddBus();
        _bus = AudioServer.BusCount - 1;
        AudioServer.SetBusName(_bus, BusName);
        AudioServer.SetBusSend(_bus, "SFX");
        AudioServer.AddBusEffect(_bus, new AudioEffectLowPassFilter { CutoffHz = _cfg.NearCutoffHz, Db = AudioEffectFilter.FilterDB.Filter24Db });
        _player = new AudioStreamPlayer { Bus = BusName };
        _overlay = new AudioStreamPlayer { Bus = BusName };
        AddChild(_player);
        AddChild(_overlay);
    }

    Vector3? _listener;

    /// <summary>Where the player is (their body, riding in a vehicle too), set every frame by the network client. A vehicle's chase or orbit camera can be far from the player, so the distance to the poles is taken from here; the camera is only the fallback when nothing sets it (offline runs).</summary>
    public void SetListener(Vector3 at) => _listener = at;

    /// <summary>
    /// An intel / event announcement over the megaphones: the baked broadcast from its beginning, now, on its own player. It is not a detour broadcast: the server's phase is
    /// untouched, and it plays over a hijack without ending it. A detour broadcast on air is silent while it plays (the same single recording, as before).
    /// </summary>
    public void PlayAnnouncement()
    {
        if (!IsProcessing()) return; // no audio here
        _overlay.Stream = _streams.FirstOrDefault(s => s != null);
        _overlayAtMs = Time.GetTicksMsec();
        if (_overlay.Playing) _overlay.Stop(); // starts over (from its beginning) as soon as the next frame sees it
    }
    ulong? _overlayAtMs; // when the announcement began, while it is still playing out

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
        if (AudioServer.GetBusIndex(BusName) is var i and >= 0) AudioServer.RemoveBus(i);
    }

    static AudioStream? LoadStream(string rel)
    {
        var path = ModelLib.AssetPath(rel);
        if (!System.IO.File.Exists(path)) return null;
        var st = AudioStreamOggVorbis.LoadFromFile(path);
        if (st == null) GD.PrintErr($"rozhlas: failed to load {rel}");
        return st;
    }

    /// <summary>
    /// HOOK, not built yet: the announcer was killed and the announcement was just cut. The scream, the mic thump and the feedback over the megaphones go here (a later slice).
    /// </summary>
    void OnHijackCut() { }

    public override void _Process(double delta)
    {
        var cam = GetViewport().GetCamera3D();
        if (_seen != _toldSeq)
        {
            _seen = _toldSeq;
            var was = _phase; _phase = _told.Phase;
            Stop(); // a new word from the server: replay from its offset
            if (was == RozhlasPhase.OnAir && _phase == RozhlasPhase.Hijacked) OnHijackCut(); // the announcement stops at once, nothing else of the broadcast comes back
        }
        var p = _listener ?? cam?.GlobalPosition;
        if (p is not { } at) return;
        var distance = RozhlasRules.NearestDistance(_spk, new Num.Vector3(at.X, at.Y, at.Z));
        var inRange = distance < _cfg.MaxDistance;
        var gain = _cfg.VolumeDb + RozhlasRules.GainDb(distance, _cfg.ReferenceDistance, _cfg.MaxDistance, _cfg.RolloffDb);
        if (AudioServer.GetBusEffect(_bus, 0) is AudioEffectLowPassFilter f)
            f.CutoffHz = RozhlasRules.CutoffHz(distance, _cfg.ReferenceDistance, _cfg.MaxDistance, _cfg.NearCutoffHz, _cfg.FarCutoffHz);

        // the announcement over the megaphones: a one-shot on its own player, not tied to the phase; out of range and back it rejoins at the right moment
        if (_overlayAtMs is { } t0 && _overlay.Stream is { } ann)
        {
            var at0 = (Time.GetTicksMsec() - t0) / 1000.0;
            if (at0 >= ann.GetLength()) { _overlayAtMs = null; if (_overlay.Playing) _overlay.Stop(); }
            else if (!inRange) { if (_overlay.Playing) _overlay.Stop(); }
            else { _overlay.VolumeDb = gain; if (!_overlay.Playing) _overlay.Play((float)at0); }
        }

        // the detour broadcast: the server's variant and offset (OnAir only; Hijacked and Idle play nothing)
        if (_phase != RozhlasPhase.OnAir || !inRange || _overlayAtMs != null || _told.Variant < 0 || _told.Variant >= _streams.Length || _streams[_told.Variant] is not { } stream)
        {
            if (_player.Playing) _player.Stop();
            return;
        }
        var offset = _told.OffsetS + (Time.GetTicksMsec() - _toldAtMs) / 1000.0;
        if (offset >= stream.GetLength()) { if (_player.Playing) _player.Stop(); return; } // this variant is shorter than the slot's window
        _player.VolumeDb = gain;
        if (_player.Playing && _player.Stream == stream) return;
        _player.Stream = stream;
        _player.Play((float)offset); // the server says where in the recording the village is; out of range and back rejoins at the right moment
    }

    void Stop()
    {
        if (_player.Playing) _player.Stop();
    }
}
