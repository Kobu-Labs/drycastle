using Godot;
using Drycastle.ShooterCore;
using Num = System.Numerics;

namespace Drycastle.Shooter;

/// <summary>
/// The village loudspeaker (rozhlas): a baked broadcast (rozhlas.json) played from the megaphone poles. Which broadcast is
/// on the air and how far into it comes from the clock alone (<see cref="RozhlasRules.OnAir"/>), so every client hears the
/// same moment without any message. A few voices follow the speakers nearest to the camera; each one's volume comes from
/// its own distance (<see cref="RozhlasRules.GainDb"/>), so walking toward a pole brings the announcement up and away from
/// it takes it down, and duller too: each voice has its own bus with a low-pass filter set from the distance
/// (<see cref="RozhlasRules.CutoffHz"/>). The audio is optional: with no file the game stays silent. Client only, and silent on headless runs.
/// </summary>
public partial class Rozhlas : Node
{
    const float RefreshSeconds = 0.25f;
    const string DataPath = "res://data/rozhlas.json";

    /// <summary>Where the speakers hang (Godot axes); the horns of the megaphone poles.</summary>
    public IReadOnlyList<Vector3> Speakers { get; init; } = Array.Empty<Vector3>();

    RozhlasFile _cfg = null!;
    Num.Vector3[] _spk = Array.Empty<Num.Vector3>();
    AudioStream?[] _streams = Array.Empty<AudioStream?>();
    AudioStreamPlayer3D[] _voices = Array.Empty<AudioStreamPlayer3D>();
    string[] _buses = Array.Empty<string>();
    int[] _held = Array.Empty<int>();
    long _slot = long.MinValue;
    int _variant = -1;
    double _refreshIn;

    public override void _Ready()
    {
        var json = Godot.FileAccess.Open(DataPath, Godot.FileAccess.ModeFlags.Read)?.GetAsText()
            ?? throw new DataFileException($"rozhlas.json: cannot open ({Godot.FileAccess.GetOpenError()})");
        _cfg = RozhlasFile.Parse(json); // a broken table fails a smoke run even headless
        if (!Sfx.Enabled || Speakers.Count == 0) { SetProcess(false); return; }
        _streams = _cfg.Files.Select(LoadStream).ToArray();
        if (_streams.All(s => s == null)) { GD.Print("rozhlas: no audio yet (run scripts/make_rozhlas.py); staying silent"); SetProcess(false); return; }
        _spk = Speakers.Select(s => new Num.Vector3(s.X, s.Y, s.Z)).ToArray();
        _voices = new AudioStreamPlayer3D[_cfg.Voices];
        _held = new int[_cfg.Voices];
        _buses = new string[_voices.Length];
        for (var i = 0; i < _voices.Length; i++)
        {
            // one bus per voice so each can have its own low-pass; it feeds the SFX bus (so the effects volume still applies)
            _buses[i] = $"Rozhlas{i}";
            AudioServer.AddBus();
            var bus = AudioServer.BusCount - 1;
            AudioServer.SetBusName(bus, _buses[i]);
            AudioServer.SetBusSend(bus, "SFX");
            AudioServer.AddBusEffect(bus, new AudioEffectLowPassFilter { CutoffHz = _cfg.NearCutoffHz, Db = AudioEffectFilter.FilterDB.Filter24Db });
            // distance is handled here (GainDb, CutoffHz), so Godot adds no attenuation of its own; the node still pans it
            _voices[i] = new AudioStreamPlayer3D { Bus = _buses[i], AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled, MaxDistance = 0f };
            AddChild(_voices[i]);
            _held[i] = -1;
        }
    }

    public override void _ExitTree()
    {
        foreach (var name in _buses)
            if (AudioServer.GetBusIndex(name) is var i and >= 0) AudioServer.RemoveBus(i);
    }

    void SetCutoff(int voice, float hz)
    {
        var bus = AudioServer.GetBusIndex(_buses[voice]);
        if (bus >= 0 && AudioServer.GetBusEffect(bus, 0) is AudioEffectLowPassFilter f) f.CutoffHz = hz;
    }

    static AudioStream? LoadStream(string rel)
    {
        var path = ModelLib.AssetPath(rel);
        if (!System.IO.File.Exists(path)) return null;
        var st = AudioStreamOggVorbis.LoadFromFile(path);
        if (st == null) GD.PrintErr($"rozhlas: failed to load {rel}");
        return st;
    }

    public override void _Process(double delta)
    {
        var cam = GetViewport().GetCamera3D();
        if (cam == null) return;
        var length = _streams.Where(s => s != null).Select(s => s!.GetLength()).DefaultIfEmpty(0).Max();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        if (RozhlasRules.OnAir(now, _cfg.PeriodS, length, _streams.Length) is not { } air || _streams[air.Variant] is not { } stream)
        {
            if (_slot != long.MinValue) StopAll();
            return;
        }
        if (air.Offset >= stream.GetLength()) { if (_slot != long.MinValue) StopAll(); return; } // this variant is shorter than the slot's window
        var p = cam.GlobalPosition;
        var listener = new Num.Vector3(p.X, p.Y, p.Z);
        if (air.Slot != _slot || air.Variant != _variant) { StopAll(); _slot = air.Slot; _variant = air.Variant; }

        _refreshIn -= delta;
        if (_refreshIn <= 0)
        {
            _refreshIn = RefreshSeconds;
            _held = RozhlasRules.Assign(_held, RozhlasRules.Nearest(_spk, listener, _voices.Length, _cfg.MaxDistance));
        }
        for (var v = 0; v < _voices.Length; v++)
        {
            var player = _voices[v];
            if (_held[v] < 0) { if (player.Playing) player.Stop(); continue; }
            var at = Speakers[_held[v]];
            player.GlobalPosition = at;
            var distance = at.DistanceTo(cam.GlobalPosition);
            player.VolumeDb = _cfg.VolumeDb + RozhlasRules.GainDb(distance, _cfg.ReferenceDistance, _cfg.MaxDistance);
            SetCutoff(v, RozhlasRules.CutoffHz(distance, _cfg.ReferenceDistance, _cfg.MaxDistance, _cfg.NearCutoffHz, _cfg.FarCutoffHz));
            if (player.Playing && player.Stream == stream) continue;
            player.Stream = stream;
            player.Play((float)air.Offset); // join mid-broadcast: every speaker is at the same point of the same recording
        }
    }

    void StopAll()
    {
        foreach (var v in _voices) v.Stop();
        for (var i = 0; i < _held.Length; i++) _held[i] = -1;
        _slot = long.MinValue; _variant = -1;
    }
}
