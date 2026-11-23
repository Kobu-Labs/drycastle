using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Thieves;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Thieves;

namespace Drycastle.Shooter.Net.Client;

/// <summary>The Thieves' sound on the client (docs/events/thieves/09): the voice lines the server sends and the speakers some Gang members carry. The files and players are <see cref="ThiefAudio"/>.</summary>
public partial class NetClient
{
    ThiefAudio? _thiefAudio;

    /// <summary>The Thieves' sound (made on first use). The van ticket calls <see cref="ThiefAudio.PlayVanSong"/> and <see cref="ThiefAudio.StopVanSong"/> on it.</summary>
    public ThiefAudio ThiefSounds
    {
        get
        {
            if (_thiefAudio != null) return _thiefAudio;
            _thiefAudio = new ThiefAudio(ThievesData.Audio);
            Owned.AddChild(_thiefAudio);
            return _thiefAudio;
        }
    }

    /// <summary>
    /// The song of a Raid van (the number the server picked at its birth, in the van's owner field): looped at the van to <c>audio.van_range_m</c> from the server's clock (seconds of ticks) modulo the
    /// song's length, so every client, a joiner too, hears the same bar. Silence without songs (or on a client with no sound).
    /// </summary>
    void StartVanSong(Node3D van, int song)
    {
        if (!Sfx.Enabled) return;
        ThiefSounds.PlayVanSong(van, song, _lastTick / (double)Wire.TickRate);
    }

    /// <summary>A Gang member says a line: it is played at the member (the entity id names it), if its body is here.</summary>
    void OnThiefAudioEvent(EventKind kind, byte[] payload)
    {
        if (kind != EventKind.ThiefVoice || !GangVoiceCodec.TryDecode(payload, out var id, out var line)) return;
        if (_gang.TryGetValue(id, out var g)) ThiefSounds.PlayVoice(g.View, line);
    }

    /// <summary>Every frame per member: the speaker on it (hp byte bit 6) is drawn with its song, or taken away when the flag is gone.</summary>
    void UpdateGangSpeaker(ushort id, GangNode g, byte hp)
    {
        var on = GangRules.SpeakerOf(hp) && GangRules.AliveOf(hp);
        if (on || _thiefAudio != null) ThiefSounds.SetSpeaker(id, g.View, on);
    }
}
