using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The sound of the Holy Mass: the chant, a looped positional sound in the church (with a reverb of its own for the echo) that plays while the Mass
/// progresses and pauses and resumes with the pastor; and the church bells, played once, village-wide, when the event starts. Neither sound exists yet
/// (holy_mass.json names the files): a file that is missing is skipped and logged once, the rest of the event is unaffected.
/// </summary>
public sealed partial class ChurchChant : Node3D
{
    const string BusName = "Church";

    readonly MassConfig _cfg;
    readonly HashSet<string> _warned = new();
    AudioStreamPlayer3D? _chant;
    bool _chantLoaded;

    public ChurchChant(MassConfig cfg) { _cfg = cfg; Name = "ChurchChant"; }

    /// <summary>The chant plays (resuming where it paused) while <paramref name="on"/>; it stops when the Mass is not on. <paramref name="at"/> is where it comes from.</summary>
    public void SetChant(bool on, bool mass, Vector3 at)
    {
        if (!Sfx.Enabled) return;
        if (!on && !mass) { _chant?.Stop(); return; }
        if (!_chantLoaded) { _chantLoaded = true; _chant = MakeChant(); }
        if (_chant == null) return;
        _chant.GlobalPosition = at;
        if (!_chant.Playing) _chant.Play();
        _chant.StreamPaused = !on;
    }

    /// <summary>The bells, heard everywhere (no position).</summary>
    public void RingBells()
    {
        if (!Sfx.Enabled || Load(_cfg.BellsSound, false) is not { } stream) return;
        var p = new AudioStreamPlayer { Stream = stream, Bus = "SFX" };
        AddChild(p);
        p.Finished += p.QueueFree;
        p.Play();
    }

    AudioStreamPlayer3D? MakeChant()
    {
        if (Load(_cfg.ChantSound, true) is not { } stream) return null;
        var p = new AudioStreamPlayer3D
        {
            Stream = stream, Bus = ChurchBus(), AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
            UnitSize = 6f, MaxDistance = _cfg.ChantRangeM,
        };
        AddChild(p);
        return p;
    }

    /// <summary>A bus with a reverb that sends to SFX: the church's echo. Falls back to SFX when the bus cannot be made.</summary>
    static string ChurchBus()
    {
        if (AudioServer.GetBusIndex(BusName) >= 0) return BusName;
        AudioServer.AddBus();
        var i = AudioServer.BusCount - 1;
        AudioServer.SetBusName(i, BusName);
        AudioServer.SetBusSend(i, "SFX");
        AudioServer.AddBusEffect(i, new AudioEffectReverb { RoomSize = 0.9f, Damping = 0.35f, Wet = 0.4f, Dry = 0.8f });
        return BusName;
    }

    AudioStream? Load(string rel, bool loop)
    {
        // the private pack first (the chant is not ours to publish), then the asset library
        var path = System.IO.Path.Combine(GamePaths.PrivateAssets(), rel);
        if (!System.IO.File.Exists(path)) path = ModelLib.AssetPath(rel);
        if (!System.IO.File.Exists(path)) { Warn(rel, $"holy mass: sound not found: {path} (skipped)"); return null; }
        var stream = AudioStreamOggVorbis.LoadFromFile(path);
        if (stream == null) { Warn(rel, $"holy mass: sound failed to load: {path} (skipped)"); return null; }
        stream.Loop = loop;
        return stream;
    }

    void Warn(string key, string text)
    {
        if (_warned.Add(key)) GD.PrintErr(text);
    }
}
