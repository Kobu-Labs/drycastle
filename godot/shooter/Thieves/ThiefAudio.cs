using Godot;
using Drycastle.Shooter.Assets;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Churches;
using Drycastle.ShooterCore.Thieves;

namespace Drycastle.Shooter.Thieves;

/// <summary>
/// The Thieves' sound on the client (docs/events/thieves/09), all from the private folders <c>assets-private/audio/thieves/voice</c> (the baked voice lines, <c>make thief-voices</c>) and
/// <c>.../songs</c> (the songs we put there): voice lines the server decides, the speaker some members carry (drawn, a song loops from it until it is gone) and the van's song (ticket 03).
/// Like <see cref="ChurchChant"/> it owns its <see cref="AudioStreamPlayer3D"/>s (the 10 voices of <c>Sfx.LoopVoice</c> are not for this) and never names a file in sounds.json. A folder
/// that is missing or empty is silence and one log line; a file that cannot be read is skipped the same way.
/// </summary>
public sealed partial class ThiefAudio : Node
{
    const string VoiceDir = "audio/thieves/voice", SongDir = "audio/thieves/songs", VanSongNode = "VanSong";
    const string SfxBus = "SFX", MusicBus = "Music";
    const float VoiceHeightM = 1.6f, VoiceDb = 4f;
    static readonly Vector3 HipAt = new(0.24f, 1.0f, 0.04f);   // where the song plays from on a member (the box's place; a mounted speaker follows the hip bone, the song stays on the body)

    sealed class Speaker { public Node3D View = null!; public Node3D Prop = null!; public AudioStreamPlayer3D? Song; }

    readonly ThievesAudioConfig _cfg;
    readonly HashSet<string> _warned = new();
    readonly Dictionary<string, AudioStreamOggVorbis?> _streams = new();
    readonly Dictionary<ushort, Speaker> _speakers = new();
    string[]? _voices, _songs;

    public ThiefAudio(ThievesAudioConfig cfg) { _cfg = cfg; Name = "ThiefAudio"; }

    /// <summary>Voice lines found on disk (scanned on first use).</summary>
    public int VoiceCount => (_voices ??= Scan(VoiceDir)).Length;
    /// <summary>Songs found on disk (scanned on first use).</summary>
    public int SongCount => (_songs ??= Scan(SongDir)).Length;

    // ---- voice lines ----

    /// <summary>The Gang member <paramref name="at"/> says line <paramref name="line"/> (modulo the files found): the player follows it, heard to <c>audio.voice_range_m</c>.</summary>
    public void PlayVoice(Node3D at, int line)
    {
        if (!Sfx.Enabled || VoiceCount == 0 || !IsInstanceValid(at)) return;
        if (Load(_voices![line % _voices.Length], false) is not { } stream) return;
        var p = Player(stream, SfxBus, _cfg.VoiceRangeM, 3f, VoiceDb);
        p.Position = new Vector3(0, VoiceHeightM, 0);
        at.AddChild(p);
        p.Finished += p.QueueFree;
        p.Play();
    }

    // ---- the speaker ----

    /// <summary>
    /// Keeps the speaker of one Gang member right: <paramref name="on"/> draws it on the member and loops a song from it (song number = <paramref name="id"/>, the world entity id,
    /// so every client picks the same one); off removes both (the member died). Cheap to call every frame.
    /// </summary>
    public void SetSpeaker(ushort id, Node3D view, bool on)
    {
        if (_speakers.TryGetValue(id, out var cur) && (cur.View != view || !IsInstanceValid(cur.View))) { _speakers.Remove(id); cur = null; } // the id belongs to a new member now
        if (!on)
        {
            if (cur == null) return;
            _speakers.Remove(id);
            if (IsInstanceValid(cur.Prop)) cur.Prop.QueueFree();
            if (cur.Song != null && IsInstanceValid(cur.Song)) cur.Song.QueueFree();
            return;
        }
        if (cur != null) return;
        var mounted = view is GangMemberView body ? body.MountSpeaker() : null; // the pack's speaker on the hip bone (ticket 08), else the box
        var s = new Speaker { View = view, Prop = mounted ?? MakeProp() };
        if (mounted == null) view.AddChild(s.Prop);
        if (Sfx.Enabled && SongCount > 0 && Load(_songs![id % _songs.Length], true) is { } song)
        {
            s.Song = Player(song, MusicBus, _cfg.SpeakerRangeM, 4f, 0f);
            s.Song.Position = mounted == null ? s.Prop.Position : HipAt;
            view.AddChild(s.Song);
            s.Song.Play((float)(GD.Randf() * song.GetLength() * 0.5)); // not every speaker from the first bar
        }
        _speakers[id] = s;
    }

    /// <summary>A JBL-like box on the member's hip (the fallback of <see cref="GangMemberView.MountSpeaker"/>): dark body, a lighter grille on the front (-Z), a small accent.</summary>
    static Node3D MakeProp()
    {
        var root = new Node3D { Name = "Speaker", Position = new Vector3(0.24f, 1.0f, 0.04f) };
        root.AddChild(Box(new Vector3(0.12f, 0.2f, 0.09f), new Color(0.12f, 0.12f, 0.13f), Vector3.Zero));
        root.AddChild(Box(new Vector3(0.1f, 0.14f, 0.01f), new Color(0.32f, 0.33f, 0.35f), new Vector3(0, -0.01f, -0.046f)));
        root.AddChild(Box(new Vector3(0.12f, 0.02f, 0.09f), new Color(0.9f, 0.45f, 0.1f), new Vector3(0, 0.11f, 0)));
        return root;
    }

    static MeshInstance3D Box(Vector3 size, Color color, Vector3 at) => new()
    {
        Mesh = new BoxMesh { Size = size },
        MaterialOverride = new StandardMaterial3D { AlbedoColor = color, Roughness = 0.8f },
        Position = at,
    };

    // ---- the van's song (ticket 03 calls these) ----

    /// <summary>
    /// Loops song <paramref name="songIndex"/> (modulo the songs found) at <paramref name="van"/>, heard to <c>audio.van_range_m</c> at <c>audio.van_volume_db</c>, starting <paramref name="startOffsetSeconds"/> into
    /// it (modulo the song's length: the caller passes the server clock since the van's song began, so every client is in step). The player is a child of the node and follows it, and goes
    /// with it when the node is freed. Playing again on the same node restarts it. Null when there are no songs (silence).
    /// </summary>
    public AudioStreamPlayer3D? PlayVanSong(Node3D van, int songIndex, double startOffsetSeconds)
    {
        StopVanSong(van);
        if (!Sfx.Enabled || SongCount == 0 || !IsInstanceValid(van)) return null;
        if (Load(_songs![((songIndex % _songs.Length) + _songs.Length) % _songs.Length], true) is not { } song) return null;
        var p = Player(song, MusicBus, _cfg.VanRangeM, 8f, _cfg.VanVolumeDb);
        p.Name = VanSongNode;
        van.AddChild(p);
        var len = song.GetLength();
        p.Play(len > 0 ? (float)(Math.Max(0, startOffsetSeconds) % len) : 0f);
        return p;
    }

    /// <summary>Stops the song started on <paramref name="van"/> (the van is destroyed or gone).</summary>
    public void StopVanSong(Node3D van)
    {
        if (!IsInstanceValid(van) || van.GetNodeOrNull<AudioStreamPlayer3D>(VanSongNode) is not { } p) return;
        p.Stop();
        van.RemoveChild(p);
        p.QueueFree();
    }

    // ---- files ----

    static AudioStreamPlayer3D Player(AudioStream stream, string bus, float rangeM, float unitSize, float db) => new()
    {
        Stream = stream, Bus = bus, AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
        UnitSize = unitSize, MaxDistance = rangeM, VolumeDb = db,
    };

    /// <summary>The .ogg files of a private folder, by name (the same order on every client), or none. A folder that is missing or empty is logged once.</summary>
    string[] Scan(string rel)
    {
        var dir = System.IO.Path.Combine(GamePaths.PrivateAssets(), rel);
        var files = System.IO.Directory.Exists(dir) ? System.IO.Directory.GetFiles(dir, "*.ogg") : Array.Empty<string>();
        Array.Sort(files, StringComparer.Ordinal);
        if (files.Length == 0) Warn(rel, $"thieves audio: no .ogg files in {dir} (silent)");
        return files;
    }

    AudioStreamOggVorbis? Load(string path, bool loop)
    {
        if (_streams.TryGetValue(path, out var cached)) return cached;
        var stream = AudioStreamOggVorbis.LoadFromFile(path);
        if (stream == null) Warn(path, $"thieves audio: failed to load {path} (skipped)");
        else stream.Loop = loop;
        _streams[path] = stream;
        return stream;
    }

    void Warn(string key, string text)
    {
        if (_warned.Add(key)) GD.PrintErr(text);
    }
}
