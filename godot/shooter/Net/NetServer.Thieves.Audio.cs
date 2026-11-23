using Godot;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Thieves;

namespace Drycastle.Shooter;

/// <summary>
/// The Thieves' audio on the server (docs/events/thieves/09): who says what is decided here (<see cref="GangAudio"/>: a member with a human near rolls a line now and then, one
/// line at a time per Raid) and sent as <see cref="EventKind.ThiefVoice"/> to everyone; the clients play the file. A member that carries a speaker has it in bit 6 of its hp
/// byte (rolled by <see cref="GangAudio"/> when first seen); when it is killed the speaker drops where it fell as the plain Item <see cref="SpeakerItemId"/> (bag only, silent).
/// </summary>
public partial class NetServer
{
    /// <summary>The id in items.json of the speaker a killed member drops.</summary>
    public const string SpeakerItemId = "speaker";

    GangAudio? _gangAudio;
    readonly List<GangPlayer> _audioListeners = new();
    bool _speakerItemWarned;

    /// <summary>Once a tick (from <see cref="ThievesTick"/>): the voice lines. Humans only listen: bots do not hear a thing.</summary>
    void GangAudioTick(float dt)
    {
        if (_gangs == null || _gangs.Gangs.Count == 0) return;
        _gangAudio ??= new GangAudio(ThievesData.Audio, System.Environment.TickCount);
        _audioListeners.Clear();
        foreach (var c in _byId.Values)
            if (c is { IsBot: false, Joined: true } && _match.Get(c.Id)?.Life == PlayerLife.Alive) _audioListeners.Add(new GangPlayer(c.Id, N(c.Body.GlobalPosition), c.Car == null));
        _gangAudio.Update(dt, _gangs.Gangs, _audioListeners, SayGangLine);
    }

    void SayGangLine(GangMember m, byte line)
    {
        foreach (var (id, entity) in _match.Entities.OfType(GangRules.TypeId))
            if (entity == m) { BroadcastEvent(EventKind.ThiefVoice, GangVoiceCodec.Encode(id, line)); return; }
    }

    /// <summary>A member was killed by a hit: if it carried a speaker, the speaker lies where it fell.</summary>
    void GangSpeakerDrop(GangKilled k)
    {
        if (_gangs == null || _corpses == null) return;
        GangMember? member = null;
        foreach (var g in _gangs.Gangs)
            foreach (var m in g.Members) if (m.HitId == k.HitId) member = m;
        if (member is not { Speaker: true }) return;
        var item = Items.IndexOf(SpeakerItemId);
        if (item < 0)
        {
            if (!_speakerItemWarned) GD.PrintErr($"server: items.json has no '{SpeakerItemId}': a killed member's speaker is not dropped");
            _speakerItemWarned = true;
            return;
        }
        if (_corpses.Drop(0, k.Where, new ItemStack(item)) == null) GD.PrintErr("server: the world refused the speaker item (entity cap)");
    }
}
