using Godot;
using Drycastle.ShooterCore;
using Num = System.Numerics;

namespace Drycastle.Shooter;

/// <summary>
/// The village loudspeaker (rozhlas): a baked broadcast (rozhlas.json) heard across the village. It is one shared recording,
/// not one per speaker: which broadcast is on the air and how far into it comes from the clock alone
/// (<see cref="RozhlasRules.OnAir"/>), so every client hears the same moment without any message, and only the distance from
/// the camera to the nearest megaphone pole sets how loud (<see cref="RozhlasRules.GainDb"/>) and how dull
/// (<see cref="RozhlasRules.CutoffHz"/>, a low-pass on the player's own bus) it is. Walking toward a pole brings the announcement
/// up and away from it takes it down; poles next to each other do not add up. The audio is optional: with no file the game stays
/// silent. Client only, and silent on headless runs.
/// </summary>
public partial class Rozhlas : Node
{
    const string DataPath = "res://data/rozhlas.json";
    const string BusName = "Rozhlas";

    /// <summary>Where the speakers hang (Godot axes); the horns of the megaphone poles.</summary>
    public IReadOnlyList<Vector3> Speakers { get; init; } = Array.Empty<Vector3>();

    /// <summary>The loudspeaker of this client, for the network layer to trigger (null before the map is loaded and on headless runs).</summary>
    public static Rozhlas? Current { get; private set; }

    RozhlasFile _cfg = null!;
    Num.Vector3[] _spk = Array.Empty<Num.Vector3>();
    AudioStream?[] _streams = Array.Empty<AudioStream?>();
    AudioStreamPlayer _player = null!;
    int _bus = -1;
    /// <summary>Unix seconds an admin's /event rozhlas started a broadcast at, while it is still playing; it takes the place of the scheduled one.</summary>
    double? _triggeredAt;
    const long TriggeredSlot = -1;
    long _slot = long.MinValue;
    int _variant = -1;

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
        AddChild(_player);
    }

    Vector3? _listener;

    /// <summary>Where the player is (their body, riding in a vehicle too), set every frame by the network client. A vehicle's chase or orbit camera can be far from the player, so the distance to the poles is taken from here; the camera is only the fallback when nothing sets it (offline runs).</summary>
    public void SetListener(Vector3 at) => _listener = at;

    /// <summary>Starts the broadcast now, from its beginning (a broadcast already on the air starts over).</summary>
    public void Trigger()
    {
        if (!IsProcessing()) return; // no audio here
        _triggeredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        Stop();
    }

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

    public override void _Process(double delta)
    {
        var cam = GetViewport().GetCamera3D();
        if (_listener == null && cam == null) return;
        var length = _streams.Where(s => s != null).Select(s => s!.GetLength()).DefaultIfEmpty(0).Max();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        if (_triggeredAt is { } t0 && now - t0 >= length) _triggeredAt = null; // the triggered one has played out
        var scheduled = _triggeredAt is { } t
            ? new RozhlasOnAir(TriggeredSlot, Array.FindIndex(_streams, s => s != null), now - t)
            : RozhlasRules.OnAir(now, _cfg.PeriodS, length, _streams.Length);
        if (scheduled is not { } air || _streams[air.Variant] is not { } stream
            || air.Offset >= stream.GetLength()) // this variant is shorter than the slot's window
        {
            Stop();
            return;
        }
        var p = _listener ?? cam!.GlobalPosition;
        var distance = RozhlasRules.NearestDistance(_spk, new Num.Vector3(p.X, p.Y, p.Z));
        if (distance >= _cfg.MaxDistance) { Stop(); return; } // out of range: stop, and join mid-broadcast again on the way back
        if (air.Slot != _slot || air.Variant != _variant) { Stop(); _slot = air.Slot; _variant = air.Variant; }

        _player.VolumeDb = _cfg.VolumeDb + RozhlasRules.GainDb(distance, _cfg.ReferenceDistance, _cfg.MaxDistance, _cfg.RolloffDb);
        if (AudioServer.GetBusEffect(_bus, 0) is AudioEffectLowPassFilter f)
            f.CutoffHz = RozhlasRules.CutoffHz(distance, _cfg.ReferenceDistance, _cfg.MaxDistance, _cfg.NearCutoffHz, _cfg.FarCutoffHz);
        if (_player.Playing && _player.Stream == stream) return;
        _player.Stream = stream;
        _player.Play((float)air.Offset); // the clock says where in the recording the village is
    }

    void Stop()
    {
        if (_player.Playing) _player.Stop();
        _slot = long.MinValue; _variant = -1;
    }
}
