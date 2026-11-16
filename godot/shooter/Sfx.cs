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

    /// <summary><paramref name="exact"/>: play at exactly the volume asked, see <see cref="Sfx.SetExactLevel"/>.</summary>
    public void Update(Vector3 position, float pitch = 1f, float dbOffset = 0f, bool exact = false)
    {
        if (Player == null) return;
        Player.GlobalPosition = position;
        Player.PitchScale = Mathf.Max(0.05f, pitch * Def.Pitch);
        Player.VolumeDb = BaseDb + dbOffset;
        Sfx.SetExactLevel(Player, exact);
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
    const int Pool2D = 32, Pool3D = 64, PoolUi = 4, PoolLoop = 24;

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
                if (!d.Optional && ModelLib.Find(f) == null) throw new DataFileException($"sounds.json: {d.Id}: file not found {ModelLib.AssetPath(f)}");
        if (!Enabled) return;
        foreach (var d in _table.Sounds)
        {
            var list = new List<AudioStream>();
            foreach (var f in d.Files)
            {
                if (ModelLib.Find(f) is not { } path) continue; // an optional sound whose file is not there
                var st = AudioStreamOggVorbis.LoadFromFile(path);
                if (st == null) { GD.PrintErr($"sound failed to load: {f}"); continue; }
                st.Loop = d.Loop;
                if (d.Loop && d.LoopStart > 0) st.LoopOffset = d.LoopStart;
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
    /// <summary>True when the sound started; false when it was skipped or has no voice, with the reason in <see cref="LastNote"/>.</summary>
    public static bool Play3D(string id, Vector3 at, float dbOffset = 0f, bool exact = false) => Instance?.Do3D(id, at, dbOffset, exact) ?? false;

    /// <summary>
    /// Godot's 3D player clamps its level (volume plus the nearness boost of the attenuation) to max_db, 3 dB, so a sound close to the listener plays at +3 dB whatever its
    /// volume_db is (fades, ducking and the mix levels do nothing, and several together clip), and below 0 dB it low-passes the sound at 5 kHz in proportion (muffled). A
    /// vehicle's own engine, heard from the inside, wants neither: <paramref name="exact"/> makes the level the player's own volume near the source (max_db = volume) and
    /// turns the filter off. Pooled players are put back when it is false.
    /// </summary>
    public static void SetExactLevel(AudioStreamPlayer3D p, bool exact)
    {
        p.MaxDb = exact ? p.VolumeDb : 3f;
        p.AttenuationFilterCutoffHz = exact ? 20500f : 5000f;
        // A sound right at the listener (the car you sit in) has no direction: its offset from the camera is a frame of
        // driving old, so left/right would flip and the level wobble. Centre it.
        p.PanningStrength = exact && ListenerDistance(p.GlobalPosition) < 3f ? 0f : 1f;
    }
    public static string LastNote { get; private set; } = "";
    public static void Ui(string id) => Instance?.DoUi(id);
    /// <summary>A looping voice; <paramref name="startFraction"/> starts it that share of the way into its file (0 to 1).</summary>
    public static LoopVoice? Loop(string id, Vector3 at, float startFraction = 0f) => Instance?.DoLoop(id, at, startFraction);
    /// <summary>Distance from the listener (the current camera) to a point; 0 when there is no camera yet.</summary>
    /// <summary>Where the listener (the current camera) is, or <paramref name="fallback"/> when there is no camera yet: a sound placed here is heard at full volume, centred.</summary>
    public static Vector3 ListenerPosition(Vector3 fallback) => Instance?.GetViewport().GetCamera3D() is { } c ? c.GlobalPosition : fallback;
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

    bool Do3D(string id, Vector3 at, float dbOffset, bool exact)
    {
        if (!Enabled || _p3.Length == 0) { LastNote = "audio disabled"; return false; }
        if (Def(id) is not { } d) { LastNote = $"no sounds.json entry {id}"; return false; }
        if (!d.Positional) { Do2D(id, dbOffset); LastNote = "played 2D"; return true; }
        var cam = GetViewport().GetCamera3D();
        var dist = cam != null ? cam.GlobalPosition.DistanceTo(at) : 0f;
        if (cam != null && !SoundRules.Audible(d, dist)) { LastNote = $"out of range ({dist:F0} m of {d.MaxDistance:F0})"; return false; }
        if (!GapOk(d)) { LastNote = "min gap"; return false; }
        var i = SoundRules.PickVoice(_p3.Length, k => _p3[k].Playing, k => _pr3[k], k => _s3[k], d.Priority);
        if (i < 0) { LastNote = $"no voice: every playing voice outranks priority {d.Priority}"; return false; }
        if (Pick(d) is not { } st) { LastNote = "no stream loaded (file missing or failed to decode)"; return false; }
        var stolen = _p3[i].Playing;
        _pr3[i] = d.Priority;
        var p = _p3[i];
        p.Stream = st; p.Bus = d.Bus; p.VolumeDb = d.VolumeDb + dbOffset;
        p.PitchScale = SoundRules.PitchFor(d, _rng.Randf());
        p.MaxDistance = d.MaxDistance; p.UnitSize = SoundRules.UnitSize(d.MaxDistance);
        p.GlobalPosition = at;
        SetExactLevel(p, exact);
        p.Play();
        _s3[i] = ++_i3;
        LastNote = $"played on voice {i}{(stolen ? " (stole a playing voice)" : "")}, {d.VolumeDb + dbOffset:F1} dB, pitch {p.PitchScale:F2}, listener {dist:F1} m, length {st.GetLength():F2}s, bus {p.Bus}";
        return true;
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

    LoopVoice? DoLoop(string id, Vector3 at, float startFraction)
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
            SetExactLevel(p, false); // the loop's own Update says it again each frame
            p.Play(startFraction > 0 ? (float)(Math.Clamp(startFraction, 0f, 0.98f) * st.GetLength()) : 0f);
            return new LoopVoice { Player = p, Def = d, BaseDb = d.VolumeDb, Owner = this, Slot = i };
        }
        return null;
    }

    internal void FreeLoopSlot(int i) => _loopTaken[i] = false;
    /// <summary>How many looping voices are held right now, by anything (engines, rockets): for the engine sound trace.</summary>
    public static int LoopVoicesInUse => Instance?._loopTaken.Count(t => t) ?? 0;

    AudioStreamPlayer? _fight;
    Tween? _fightFade;
    string? _fightFile;
    bool _fightOn;

    /// <summary>
    /// Music of a fight (the Bigfoot's): a looped .ogg or .mp3 of the private asset folder, faded in while <paramref name="on"/> and out after. The
    /// file is not part of the repository, so a machine without it plays nothing. <paramref name="file"/> is its path there without the extension.
    /// </summary>
    public static void FightMusic(bool on, string file, float volumeDb)
    {
        if (Instance is not { } s || !Enabled || s._fightOn == on) return;
        s._fightOn = on;
        s._fightFade?.Kill();
        if (on)
        {
            if (s._fight == null) { s._fight = new AudioStreamPlayer { Bus = "Music" }; s.AddChild(s._fight); }
            if (s._fightFile != file) { s._fightFile = file; s._fight.Stream = LoadPrivateMusic(file); }
            if (s._fight.Stream == null) return;
            if (!s._fight.Playing) { s._fight.VolumeDb = -40f; s._fight.Play(); }
            s._fightFade = s.CreateTween();
            s._fightFade.TweenProperty(s._fight, "volume_db", volumeDb, 1.5f);
        }
        else if (s._fight is { Playing: true } p)
        {
            s._fightFade = s.CreateTween();
            s._fightFade.TweenProperty(p, "volume_db", -60f, 3f);
            s._fightFade.TweenCallback(Callable.From(p.Stop));
        }
    }

    static AudioStream? LoadPrivateMusic(string file)
    {
        if (string.IsNullOrEmpty(file)) return null;
        var env = OS.GetEnvironment("DRYCASTLE_ASSETS_PRIVATE");
        var dir = !string.IsNullOrEmpty(env) ? env : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(GamePaths.Assets().TrimEnd('/', '\\')) ?? "", "assets-private");
        var ogg = System.IO.Path.Combine(dir, file + ".ogg");
        if (System.IO.File.Exists(ogg) && AudioStreamOggVorbis.LoadFromFile(ogg) is { } o) { o.Loop = true; return o; }
        var mp3 = System.IO.Path.Combine(dir, file + ".mp3");
        if (System.IO.File.Exists(mp3)) return new AudioStreamMP3 { Data = System.IO.File.ReadAllBytes(mp3), Loop = true };
        return null;
    }

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
