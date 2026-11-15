using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// The demon's sounds from the private pack (not ours to publish; a missing file is skipped): the turning's scream, the roar's screams, and the fight's
/// background, two loops at one level through the church (no falloff, no panning, cut off a little beyond the nave's length) that fade in slowly from the
/// moment the turning starts and fade out when it dies. It stays where the fight began.
/// </summary>
public sealed partial class DemonAudio : Node3D
{
    const string Dir = "audio/holy_mass";
    const float FadeInS = 9f, FadeOutS = 3f, SilentDb = -50f;
    static readonly (string File, float Db)[] Loops = { ("demon_music.ogg", 14f), ("demon_ambient.ogg", 9f) };
    static readonly string[] Screams = { "scream1.ogg", "scream2.ogg" };

    readonly List<(AudioStreamPlayer3D Player, float Db)> _loops = new();
    bool _on, _loaded;
    float _k;

    public static DemonAudio Attach(Node3D owner)
    {
        var a = new DemonAudio { Name = "DemonAudio", TopLevel = true };
        owner.AddChild(a);
        a.GlobalPosition = owner.GlobalPosition;
        a.SetProcess(false);
        return a;
    }

    static AudioStreamOggVorbis? Load(string file)
    {
        var path = System.IO.Path.Combine(GamePaths.PrivateAssets(), Dir, file);
        return System.IO.File.Exists(path) ? AudioStreamOggVorbis.LoadFromFile(path) : null;
    }

    /// <summary>The background loops: on from the turning (fading in), off once the demon is dead (fading out).</summary>
    public void SetBackground(bool on)
    {
        if (!Sfx.Enabled || on == _on) return;
        _on = on;
        if (on && !_loaded)
        {
            _loaded = true;
            foreach (var (file, db) in Loops)
            {
                if (Load(file) is not { } stream) continue;
                stream.Loop = true;
                var p = new AudioStreamPlayer3D
                {
                    Stream = stream, Bus = "Music", VolumeDb = SilentDb, PanningStrength = 0f,
                    AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled, MaxDistance = 30f, MaxDb = 20f,
                };
                AddChild(p);
                _loops.Add((p, db));
            }
        }
        if (on) foreach (var (p, _) in _loops) if (!p.Playing) p.Play();
        SetProcess(_loops.Count > 0);
    }

    public override void _Process(double delta)
    {
        _k = Mathf.MoveToward(_k, _on ? 1f : 0f, (float)delta / (_on ? FadeInS : FadeOutS));
        foreach (var (p, db) in _loops) p.VolumeDb = Mathf.Lerp(SilentDb, db, Mathf.Sqrt(_k));
        if (_on ? _k >= 1f : _k <= 0f)
        {
            if (!_on) foreach (var (p, _) in _loops) p.Stop();
            SetProcess(false);
        }
    }

    /// <summary>The turning's scream, from where the pastor stood.</summary>
    public void Transform() => Play("transform.ogg", 9f);

    /// <summary>One of the roar's screams. False when the pack has none (the caller plays its stand-in).</summary>
    public bool Scream() => Play(Screams[GD.RandRange(0, Screams.Length - 1)], 10f);

    AudioStreamPlayer3D? _bite3;
    AudioStreamPlayer? _bite2;

    /// <summary>
    /// The pounce's bite (5.6 s, faded out at its end), from the landing: for the victim it is not positional and loud, for everyone else it plays at the demon (<see cref="MoveBite"/>
    /// follows it). False when the pack has no such file (nothing plays).
    /// </summary>
    public bool Bite(Vector3 at, bool victim)
    {
        StopBite();
        if (!Sfx.Enabled || Load("bite.ogg") is not { } stream) return false;
        if (victim)
        {
            _bite2 = new AudioStreamPlayer { Stream = stream, Bus = "SFX", VolumeDb = 8f };
            AddChild(_bite2);
            _bite2.Finished += () => { _bite2?.QueueFree(); _bite2 = null; };
            _bite2.Play();
        }
        else
        {
            _bite3 = new AudioStreamPlayer3D { Stream = stream, Bus = "SFX", UnitSize = 30f, MaxDistance = 120f, MaxDb = 12f, VolumeDb = 10f };
            AddChild(_bite3);
            _bite3.GlobalPosition = at;
            _bite3.Finished += () => { _bite3?.QueueFree(); _bite3 = null; };
            _bite3.Play();
        }
        return true;
    }

    /// <summary>Keeps a positional bite at the demon.</summary>
    public void MoveBite(Vector3 at)
    {
        if (_bite3 != null && GodotObject.IsInstanceValid(_bite3)) _bite3.GlobalPosition = at;
    }

    /// <summary>Cuts the bite short (the demon died).</summary>
    public void StopBite()
    {
        if (_bite3 != null && GodotObject.IsInstanceValid(_bite3)) _bite3.QueueFree();
        if (_bite2 != null && GodotObject.IsInstanceValid(_bite2)) _bite2.QueueFree();
        _bite3 = null; _bite2 = null;
    }

    bool Play(string file, float db)
    {
        if (!Sfx.Enabled || Load(file) is not { } stream) return false;
        var p = new AudioStreamPlayer3D { Stream = stream, Bus = "SFX", UnitSize = 30f, MaxDistance = 120f, MaxDb = 12f, VolumeDb = db, PitchScale = (float)GD.RandRange(0.95, 1.05) };
        AddChild(p);
        p.Finished += p.QueueFree;
        p.Play();
        return true;
    }
}
