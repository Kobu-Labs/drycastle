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
    /// <summary>The throw a killing blow gave its victim (a blast's knockback, a car's run-over), set before the damage and used by <see cref="SpawnCorpse"/>. The corpse flies off with it.</summary>
    readonly Dictionary<ushort, NVec3> _deathPush = new();

    /// <summary>Records the throw for a victim about to take damage; <see cref="ClearPush"/> drops it when the hit did not kill.</summary>
    void PushOnDeath(ushort victim, NVec3 velocity) => _deathPush[victim] = velocity;

    void ClearPush(ushort victim, DamageResult result) { if (result != DamageResult.Killed) _deathPush.Remove(victim); }

    /// <summary>Called at a player's death, before the respawn: the corpse keeps the weapon the player had in the primary position.</summary>
    void SpawnCorpse(ushort victim)
    {
        if (!_byId.TryGetValue(victim, out var c) || c.W == null) return;
        var weapon = c.W.HotbarWeapon(CorpseRules.PrimaryPos);
        var corpse = new Corpse(victim, N(c.Body.GlobalPosition), c.Body.Yaw, weapon, c.W.RoundsOf(weapon));
        if (_deathPush.Remove(victim, out var push)) corpse.Launch(push);
        _corpses.Add(corpse);
    }

    /// <summary>Flies the thrown corpses one tick; they stop against the world (not the cars: a run-over body starts inside one).</summary>
    void StepCorpses(float dt)
    {
        foreach (var body in _corpses.Bodies)
        {
            if (!body.Airborne) continue;
            body.Fly(dt, (from, dir, len) =>
            {
                var a = new Vector3(from.X, from.Y, from.Z);
                var q = PhysicsRayQueryParameters3D.Create(a, a + new Vector3(dir.X, dir.Y, dir.Z) * len);
                q.CollisionMask = CollisionLayers.World;
                var r = World.GetWorld3D().DirectSpaceState.IntersectRay(q);
                return r.Count == 0 ? null : a.DistanceTo(r["position"].AsVector3());
            });
        }
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
