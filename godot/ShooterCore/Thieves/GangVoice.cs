using System.Buffers.Binary;

namespace Drycastle.ShooterCore;

/// <summary>
/// thieves.json section <c>audio</c> (docs/events/thieves/09): the numbers of the voice lines, the speaker and the songs. Lengths are metres, times seconds.
/// The server uses the <c>voice_*</c> numbers and <see cref="SpeakerChance"/>; the client the ranges.
/// </summary>
public sealed class ThievesAudioConfig
{
    /// <summary>A living member with a human player this near rolls for a line.</summary>
    public float VoiceNearM { get; set; } = 8;
    /// <summary>Chance a roll says a line, and how often a member that has a player near rolls.</summary>
    public float VoiceChance { get; set; } = 0.3f;
    public float VoiceRollS { get; set; } = 2;
    /// <summary>A member that spoke does not roll again for this long.</summary>
    public float VoiceCooldownS { get; set; } = 15;
    /// <summary>After a line no other member of the Raid speaks for this long (the server does not know how long a line is).</summary>
    public float VoiceBusyS { get; set; } = 3;
    /// <summary>How many lines the server picks from (the recordings: 15). The client plays line number modulo the files it finds.</summary>
    public int VoiceLines { get; set; } = 15;
    /// <summary>Client: how far a line is heard.</summary>
    public float VoiceRangeM { get; set; } = 25;
    /// <summary>Chance a member carries a speaker, rolled once when the server first sees it.</summary>
    public float SpeakerChance { get; set; } = 0.05f;
    /// <summary>Client: how far a speaker's song is heard.</summary>
    public float SpeakerRangeM { get; set; } = 30;
    /// <summary>Client: how far a van's song is heard (<c>ThiefAudio.PlayVanSong</c>, ticket 03).</summary>
    public float VanRangeM { get; set; } = 120;
    /// <summary>Client: the loudness of a van's song, dB (+6 is twice the amplitude).</summary>
    public float VanVolumeDb { get; set; } = 8;

    public void Validate(string file)
    {
        void Positive(float v, string what) { if (!(v > 0) || !float.IsFinite(v)) throw new DataFileException($"{file}: audio.{what} must be positive"); }
        void Chance(float v, string what) { if (!(v >= 0 && v <= 1)) throw new DataFileException($"{file}: audio.{what} is between 0 and 1"); }
        Positive(VoiceNearM, "voice_near_m"); Positive(VoiceRollS, "voice_roll_s"); Positive(VoiceRangeM, "voice_range_m");
        Positive(SpeakerRangeM, "speaker_range_m"); Positive(VanRangeM, "van_range_m");
        if (!float.IsFinite(VanVolumeDb)) throw new DataFileException($"{file}: audio.van_volume_db must be a number");
        if (!(VoiceCooldownS >= 0) || !(VoiceBusyS >= 0)) throw new DataFileException($"{file}: audio.voice_cooldown_s and voice_busy_s must not be negative");
        Chance(VoiceChance, "voice_chance"); Chance(SpeakerChance, "speaker_chance");
        if (VoiceLines is < 1 or > 255) throw new DataFileException($"{file}: audio.voice_lines is between 1 and 255");
    }
}

/// <summary>Event payload of <see cref="EventKind.ThiefVoice"/>: u16 world entity id of the member, u8 line number (the client plays it modulo the files it has).</summary>
public static class GangVoiceCodec
{
    public const int Bytes = 3;

    public static byte[] Encode(ushort entity, byte line)
    {
        var b = new byte[Bytes];
        BinaryPrimitives.WriteUInt16LittleEndian(b, entity);
        b[2] = line;
        return b;
    }

    public static bool TryDecode(ReadOnlySpan<byte> d, out ushort entity, out byte line)
    {
        entity = 0; line = 0;
        if (d.Length != Bytes) return false;
        entity = BinaryPrimitives.ReadUInt16LittleEndian(d);
        line = d[2];
        return true;
    }
}

/// <summary>
/// The voice lines and the speaker roll of the Gang members, pure (docs/events/thieves/09). The server calls <see cref="Update"/> once a tick with the humans' positions.
/// <para>
/// A member that is alive, not in a windup or swing or electrocuted (<see cref="CanSpeakIn"/>), with a listener within <see cref="ThievesAudioConfig.VoiceNearM"/>, rolls <see cref="ThievesAudioConfig.VoiceChance"/> every
/// <see cref="ThievesAudioConfig.VoiceRollS"/> (the clock only runs while a listener is near); a member that spoke waits out <see cref="ThievesAudioConfig.VoiceCooldownS"/>; and a Raid
/// lets one line at a time through: after one, its other members are silent for <see cref="ThievesAudioConfig.VoiceBusyS"/>.
/// </para>
/// <para>A member seen for the first time rolls <see cref="ThievesAudioConfig.SpeakerChance"/> for a speaker (<see cref="GangMember.Speaker"/>), with this object's seeded generator.</para>
/// </summary>
public sealed class GangAudio
{
    sealed class Slot { public float RollIn; public float Cooldown; public int Seen; }

    readonly ThievesAudioConfig _cfg;
    readonly Random _rng;
    readonly Dictionary<GangMember, Slot> _slots = new();
    readonly Dictionary<ThiefGang, float> _busy = new();
    readonly Dictionary<ThiefGang, int> _lastLine = new();
    readonly List<GangMember> _stale = new();
    int _pass;

    public GangAudio(ThievesAudioConfig cfg, int seed = 0) { _cfg = cfg; _rng = new Random(seed); }

    /// <summary>True with probability <paramref name="chance"/>: whether a new member carries a speaker.</summary>
    public static bool RollSpeaker(Random rng, float chance) => rng.NextDouble() < chance;

    /// <summary>Members the audio knows (tests).</summary>
    public int Tracked => _slots.Count;

    /// <summary>Whether this member can say something now: alive, not mid-blow and not electrocuted (shocked, falling, lying, getting up).</summary>
    public static bool CanSpeak(GangMember m) => m.Alive && CanSpeakIn(m.State);

    /// <summary>Whether a member in this state can speak: not in a windup or swing, not Shocked, Falling, Down or GetUp (ticket 07's open point).</summary>
    public static bool CanSpeakIn(GangMemberState state) => state is not (GangMemberState.Windup or GangMemberState.Swing or GangMemberState.Shocked or GangMemberState.Falling or GangMemberState.Down or GangMemberState.GetUp);

    /// <summary>Advances every clock by <paramref name="dt"/>; <paramref name="say"/> is called with the member and a line number below <see cref="ThievesAudioConfig.VoiceLines"/> for each line said.</summary>
    public void Update(float dt, IReadOnlyList<ThiefGang> gangs, IReadOnlyList<GangPlayer> listeners, Action<GangMember, byte> say)
    {
        _pass++;
        foreach (var gang in gangs)
        {
            _busy[gang] = _busy.TryGetValue(gang, out var b) ? MathF.Max(0, b - dt) : 0;
            foreach (var m in gang.Members)
            {
                if (!_slots.TryGetValue(m, out var s))
                {
                    s = new Slot { RollIn = (float)_rng.NextDouble() * _cfg.VoiceRollS };
                    _slots[m] = s;
                    m.Speaker = RollSpeaker(_rng, _cfg.SpeakerChance);
                }
                s.Seen = _pass;
                s.Cooldown = MathF.Max(0, s.Cooldown - dt);
                if (!CanSpeak(m) || !Near(m, listeners)) continue;
                s.RollIn -= dt;
                if (s.RollIn > 0) continue;
                s.RollIn += _cfg.VoiceRollS;
                if (s.Cooldown > 0 || _busy[gang] > 0 || _rng.NextDouble() >= _cfg.VoiceChance) continue;
                var line = PickLine(gang);
                s.Cooldown = _cfg.VoiceCooldownS;
                _busy[gang] = _cfg.VoiceBusyS;
                say(m, line);
            }
        }
        Sweep(gangs);
    }

    bool Near(GangMember m, IReadOnlyList<GangPlayer> listeners)
    {
        var r2 = _cfg.VoiceNearM * _cfg.VoiceNearM;
        foreach (var p in listeners)
        {
            var dx = p.Feet.X - m.Position.X; var dy = p.Feet.Y - m.Position.Y; var dz = p.Feet.Z - m.Position.Z;
            if (dx * dx + dy * dy + dz * dz <= r2) return true;
        }
        return false;
    }

    /// <summary>A random line that is not the one this Raid said last.</summary>
    byte PickLine(ThiefGang gang)
    {
        var n = Math.Clamp(_cfg.VoiceLines, 1, 255);
        var line = _rng.Next(n);
        if (n > 1 && _lastLine.TryGetValue(gang, out var last) && line == last) line = (line + 1 + _rng.Next(n - 1)) % n;
        _lastLine[gang] = line;
        return (byte)line;
    }

    /// <summary>Forgets members and Raids that are gone.</summary>
    void Sweep(IReadOnlyList<ThiefGang> gangs)
    {
        if (_slots.Count > 0)
        {
            _stale.Clear();
            foreach (var (m, s) in _slots) if (s.Seen != _pass) _stale.Add(m);
            foreach (var m in _stale) _slots.Remove(m);
        }
        if (_busy.Count > gangs.Count)
            foreach (var g in _busy.Keys.Where(g => !gangs.Contains(g)).ToList()) { _busy.Remove(g); _lastLine.Remove(g); }
    }
}
