using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>A looping 3D voice (rocket flight, car engine) borrowed from <see cref="Sfx"/>; give it back with <see cref="Release"/>.</summary>
public sealed class LoopVoice
{
    internal AudioStreamPlayer3D? Player;
    internal SoundDef Def = null!;
    internal float BaseDb;
    internal Sfx Owner = null!;
    internal int Slot;

    public bool Active => Player != null;

    public void Update(Vector3 position, float pitch = 1f, float dbOffset = 0f)
    {
        if (Player == null) return;
        Player.GlobalPosition = position;
        Player.PitchScale = Mathf.Max(0.05f, pitch * Def.Pitch);
        Player.VolumeDb = BaseDb + dbOffset;
    }

    /// <summary>Stops the loop and returns the voice to the pool.</summary>
    public void Release()
    {
        if (Player == null) return;
        Player.Stop();
        Player = null;
        Owner.FreeLoopSlot(Slot);
    }
}

/// <summary>
/// The audio system: event id to sound through sounds.json, played on the SFX, UI or Music bus. Own sounds are
/// non-positional (<see cref="Play2D"/>); other players' events are 3D positional with the per-event range
/// (<see cref="Play3D"/>, no occlusion). All players are created once at start and reused (round robin, the oldest
/// is stolen), so playing a shot allocates nothing. Everything is a no-op on headless runs, where only the table
/// and its files are validated.
/// </summary>
public partial class Sfx : Node
{
    const int Pool2D = 32, Pool3D = 64, PoolUi = 4, PoolLoop = 10;

    public static Sfx? Instance { get; private set; }
    public static bool Enabled => ShotFx.Enabled;

    SoundsFile _table = null!;
    readonly Dictionary<string, AudioStream[]> _streams = new();
    readonly Dictionary<string, int> _last = new();
    readonly RandomNumberGenerator _rng = new();
    AudioStreamPlayer[] _p2 = Array.Empty<AudioStreamPlayer>(), _pUi = Array.Empty<AudioStreamPlayer>();
    AudioStreamPlayer3D[] _p3 = Array.Empty<AudioStreamPlayer3D>();
    AudioStreamPlayer3D[] _pLoop = Array.Empty<AudioStreamPlayer3D>();
    readonly bool[] _loopTaken = new bool[PoolLoop];
    ulong[] _s2 = Array.Empty<ulong>(), _s3 = Array.Empty<ulong>(), _sUi = Array.Empty<ulong>();
    ulong _i2, _i3, _iUi;
    readonly Dictionary<string, double> _lastPlay = new();
    int[] _pr2 = Array.Empty<int>(), _pr3 = Array.Empty<int>();
    ulong _uiFrame; int _uiFrameSlot = -1;
    AudioStreamPlayer? _music;
    Tween? _musicFade;
    public int SoundCount => _table.Sounds.Count;

    /// <summary>Loads the table and checks that every file exists (throws when not, so a smoke run fails); builds the player pools unless headless.</summary>
    public static Sfx Init(Node parent)
    {
        if (Instance != null && IsInstanceValid(Instance)) return Instance;
        var s = new Sfx { Name = "Sfx" };
        s.Load();
        parent.AddChild(s);
        Instance = s;
        return s;
    }

    void Load()
    {
        var json = Godot.FileAccess.Open("res://data/sounds.json", Godot.FileAccess.ModeFlags.Read)?.GetAsText()
            ?? throw new DataFileException($"sounds.json: cannot open ({Godot.FileAccess.GetOpenError()})");
        _table = SoundsFile.Parse(json);
        foreach (var d in _table.Sounds)
            foreach (var f in d.Files)
                if (!System.IO.File.Exists(ModelLib.AssetPath(f))) throw new DataFileException($"sounds.json: {d.Id}: file not found {ModelLib.AssetPath(f)}");
        if (!Enabled) return;
        foreach (var d in _table.Sounds)
        {
            var list = new List<AudioStream>();
            foreach (var f in d.Files)
            {
                var st = AudioStreamOggVorbis.LoadFromFile(ModelLib.AssetPath(f));
                if (st == null) { GD.PrintErr($"sound failed to load: {f}"); continue; }
                st.Loop = d.Loop;
                list.Add(st);
            }
            _streams[d.Id] = list.ToArray();
        }
    }

    public override void _Ready()
    {
        _rng.Randomize();
        if (!Enabled) return;
        _p2 = MakePool(Pool2D, "SFX"); _pUi = MakePool(PoolUi, "UI");
        _s2 = new ulong[_p2.Length]; _pr2 = new int[_p2.Length]; _pr3 = new int[Pool3D]; _sUi = new ulong[_pUi.Length];
        _p3 = new AudioStreamPlayer3D[Pool3D]; _s3 = new ulong[Pool3D];
        for (var i = 0; i < Pool3D; i++) { _p3[i] = Make3D(); AddChild(_p3[i]); }
        _pLoop = new AudioStreamPlayer3D[PoolLoop];
        for (var i = 0; i < PoolLoop; i++) { _pLoop[i] = Make3D(); AddChild(_pLoop[i]); }
        _music = new AudioStreamPlayer { Bus = "Music" };
        AddChild(_music);
    }

    public override void _ExitTree() { if (Instance == this) Instance = null; }

    AudioStreamPlayer[] MakePool(int n, string bus)
    {
        var a = new AudioStreamPlayer[n];
        for (var i = 0; i < n; i++) { a[i] = new AudioStreamPlayer { Bus = bus }; AddChild(a[i]); }
        return a;
    }

    static AudioStreamPlayer3D Make3D() => new() { AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance, Bus = "SFX" };

    // ---- lookup ----

    AudioStream? Pick(SoundDef d)
    {
        if (!_streams.TryGetValue(d.Id, out var list) || list.Length == 0) return null;
        _last.TryGetValue(d.Id, out var last);
        var i = SoundRules.PickVariant(list.Length, _last.ContainsKey(d.Id) ? last : -1, _rng.Randf());
        _last[d.Id] = i;
        return list[i];
    }

    SoundDef? Def(string id)
    {
        var d = _table.Find(id);
        if (d == null) GD.PrintErr($"sfx: no sound for event {id}");
        return d;
    }

    static int Oldest(ulong[] started, AudioStreamPlayer[] p) => Oldest(started, i => p[i].Playing, p.Length);
    static int Oldest(ulong[] started, Func<int, bool> playing, int n)
    {
        var best = 0;
        for (var i = 0; i < n; i++)
        {
            if (!playing(i)) return i;
            if (started[i] < started[best]) best = i;
        }
        return best;
    }

    /// <summary>True (and records the play) unless the event played less than its min_gap ago.</summary>
    bool GapOk(SoundDef d)
    {
        if (d.MinGap <= 0) return true;
        var now = Time.GetTicksMsec() / 1000.0;
        if (_lastPlay.TryGetValue(d.Id, out var t) && now - t < d.MinGap) return false;
        _lastPlay[d.Id] = now;
        return true;
    }

    // ---- playback ----

    /// <summary>Non-positional (own weapon, hit marker, stingers).</summary>
    public static void Play2D(string id, float dbOffset = 0f) => Instance?.Do2D(id, dbOffset);
    /// <summary>3D positional at <paramref name="at"/> with the event's range; silent beyond it. Events with no range play non-positional.</summary>
    public static void Play3D(string id, Vector3 at, float dbOffset = 0f) => Instance?.Do3D(id, at, dbOffset);
    public static void Ui(string id) => Instance?.DoUi(id);
    public static LoopVoice? Loop(string id, Vector3 at) => Instance?.DoLoop(id, at);
    /// <summary>Distance from the listener (the current camera) to a point; 0 when there is no camera yet.</summary>
    public static float ListenerDistance(Vector3 at) => Instance?.GetViewport().GetCamera3D() is { } c ? c.GlobalPosition.DistanceTo(at) : 0f;
    public static void MenuMusic(bool on) => Instance?.DoMusic(on);

    void Do2D(string id, float dbOffset)
    {
        if (!Enabled || _p2.Length == 0 || Def(id) is not { } d || !GapOk(d)) return;
        var i = SoundRules.PickVoice(_p2.Length, k => _p2[k].Playing, k => _pr2[k], k => _s2[k], d.Priority);
        if (i < 0 || Pick(d) is not { } st) return;
        _pr2[i] = d.Priority;
        var p = _p2[i];
        p.Stream = st; p.Bus = d.Bus; p.VolumeDb = d.VolumeDb + dbOffset;
        p.PitchScale = SoundRules.PitchFor(d, _rng.Randf());
        p.Play();
        _s2[i] = ++_i2;
    }

    void Do3D(string id, Vector3 at, float dbOffset)
    {
        if (!Enabled || _p3.Length == 0 || Def(id) is not { } d) return;
        if (!d.Positional) { Do2D(id, dbOffset); return; }
        var cam = GetViewport().GetCamera3D();
        if (cam != null && !SoundRules.Audible(d, cam.GlobalPosition.DistanceTo(at))) return;
        if (!GapOk(d)) return;
        var i = SoundRules.PickVoice(_p3.Length, k => _p3[k].Playing, k => _pr3[k], k => _s3[k], d.Priority);
        if (i < 0 || Pick(d) is not { } st) return;
        _pr3[i] = d.Priority;
        var p = _p3[i];
        p.Stream = st; p.Bus = d.Bus; p.VolumeDb = d.VolumeDb + dbOffset;
        p.PitchScale = SoundRules.PitchFor(d, _rng.Randf());
        p.MaxDistance = d.MaxDistance; p.UnitSize = SoundRules.UnitSize(d.MaxDistance);
        p.GlobalPosition = at;
        p.Play();
        _s3[i] = ++_i3;
    }

    /// <summary>UI sounds: when several are asked for in the same frame (a button click that also pops the screen) the last one wins.</summary>
    void DoUi(string id)
    {
        if (!Enabled || _pUi.Length == 0 || Def(id) is not { } d || Pick(d) is not { } st) return;
        var frame = Engine.GetProcessFrames();
        if (frame == _uiFrame && _uiFrameSlot >= 0) _pUi[_uiFrameSlot].Stop();
        var i = Oldest(_sUi, _pUi);
        var p = _pUi[i];
        p.Stream = st; p.Bus = d.Bus; p.VolumeDb = d.VolumeDb; p.PitchScale = d.Pitch;
        p.Play();
        _sUi[i] = ++_iUi; _uiFrame = frame; _uiFrameSlot = i;
    }

    LoopVoice? DoLoop(string id, Vector3 at)
    {
        if (!Enabled || _pLoop.Length == 0 || Def(id) is not { } d || Pick(d) is not { } st) return null;
        for (var i = 0; i < PoolLoop; i++)
        {
            if (_loopTaken[i]) continue;
            _loopTaken[i] = true;
            var p = _pLoop[i];
            p.Stream = st; p.Bus = d.Bus; p.VolumeDb = d.VolumeDb; p.PitchScale = d.Pitch;
            p.MaxDistance = d.MaxDistance; p.UnitSize = SoundRules.UnitSize(d.MaxDistance);
            p.GlobalPosition = at;
            p.Play();
            return new LoopVoice { Player = p, Def = d, BaseDb = d.VolumeDb, Owner = this, Slot = i };
        }
        return null;
    }

    internal void FreeLoopSlot(int i) => _loopTaken[i] = false;

    void DoMusic(bool on)
    {
        if (!Enabled || _music == null) return;
        _musicFade?.Kill();
        if (on)
        {
            if (Def("music.menu") is not { } d) return;
            _music.VolumeDb = d.VolumeDb;
            if (_music.Playing) return;
            if (Pick(d) is not { } st) return;
            _music.Stream = st; _music.Play();
        }
        else if (_music.Playing)
        {
            _musicFade = CreateTween();
            _musicFade.TweenProperty(_music, "volume_db", -60f, 0.5f);
            _musicFade.TweenCallback(Callable.From(_music.Stop));
        }
    }
}
