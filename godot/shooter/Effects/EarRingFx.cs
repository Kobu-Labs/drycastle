using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.ShooterCore.Effects;

namespace Drycastle.Shooter.Effects;

/// <summary>
/// The ear ringing of the local player (#481): a high tone (sounds.json <c>ear.ring</c>, a looping 2D voice) that comes up when a blast goes off within its damage radius and fades out
/// over seconds. Levels and fade are <see cref="EarRing"/>; this node only drives the voice. Heard by the local player alone, never sent. Silent when the file is missing.
/// </summary>
public partial class EarRingFx : Node
{
    const string Id = "ear.ring";
    EarRing _ring;
    AudioStreamPlayer? _voice;

    /// <summary>A blast at <paramref name="at"/> with <paramref name="damageRadius"/> metres of reach, the player at <paramref name="me"/>.</summary>
    public void Blast(Vector3 at, Vector3 me, float damageRadius)
    {
        if (!_ring.Hit(EarRingRules.Level(me.DistanceTo(at), damageRadius))) return;
        _voice ??= Sfx.OwnVoice(Id);
        if (_voice != null && !_voice.Playing) _voice.Play();
    }

    public override void _Process(double delta)
    {
        if (!_ring.Active) return;
        _ring.Tick((float)delta);
        if (_voice == null) return;
        if (!_ring.Active) { _voice.Stop(); return; }
        _voice.VolumeDb = Sfx.BaseDb(Id) + EarRingRules.Db(_ring.Level);
    }

    /// <summary>Silences the ring at once (a new round, leaving the match).</summary>
    public void Clear()
    {
        _ring.Clear();
        _voice?.Stop();
    }
}
