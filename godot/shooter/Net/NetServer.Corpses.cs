using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// Corpses on the server (ticket #130): a killed player leaves a corpse carrying the weapon in their primary position and its
/// rounds; a living player within reach takes it with a <see cref="EventKind.PickUpCorpse"/> event. The rules and the
/// cap live in <see cref="CorpseBook"/>; this file only spawns the corpse at death and answers the pickup.
/// </summary>
public partial class NetServer
{
    CorpseBook _corpses = null!;

    /// <summary>Called at a player's death, before the respawn: the corpse keeps the weapon the player had in the primary position.</summary>
    void SpawnCorpse(ushort victim)
    {
        if (!_byId.TryGetValue(victim, out var c) || c.W == null) return;
        var weapon = c.W.HotbarWeapon(CorpseRules.PrimaryPos);
        _corpses.Add(new Corpse(victim, N(c.Body.GlobalPosition), c.Body.Yaw, weapon, c.W.RoundsOf(weapon)));
    }

    void OnPickUpCorpse(Client c, byte[] payload)
    {
        if (!CorpseCodec.TryDecodePickUp(payload, out var corpse, out var pos)) return;
        if (!c.Joined || c.W == null || c.Car != null || _match.Get(c.Id)?.Life != PlayerLife.Alive) return;
        var result = _corpses.TryPickUp(corpse, N(c.Body.GlobalPosition), c.W, Weapons, pos);
        if (result != PickUpResult.Ok)
        {
            GD.Print($"server: {c.Name} corpse pickup refused: {result}");
            return;
        }
        var hotbar = Enumerable.Range(0, c.W.HotbarCount).Select(c.W.HotbarWeapon).ToList();
        Send(c, new EventMsg((byte)EventKind.Hotbar, CorpseCodec.EncodeHotbar(hotbar)));
        SendAmmo(c);
        GD.Print($"server: {c.Name} took a corpse weapon into hotbar {pos}");
    }
}
