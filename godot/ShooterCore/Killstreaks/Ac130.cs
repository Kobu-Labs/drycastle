using System.Buffers.Binary;
using System.Numerics;
using Suchohrad.ShooterCore;

namespace Suchohrad.ShooterCore;

/// <summary>
/// The AC-130 killstreak (ticket #128): a gunship circles the owner at altitude for its streak duration, and the owner
/// steers its turret point-of-view with three weapons (hotbar keys 1-3). The airframe is the "gunship" aircraft kind; the
/// turret is the killstreaks.json "ac130" section (its own weapons, ammo, heat and splash). Kills by any of its weapons are
/// credited to <see cref="SpecialWeapons.Ac130"/> and count as the streak's (they pay the award, not the counter).
/// </summary>
public sealed class Ac130Streak : IKillstreak
{
    public const string StreakId = "ac130";

    /// <summary>What the streak needs from the server: the entities, the match clock, and the owner's turret control.</summary>
    public interface IWorld
    {
        WorldEntityManager Entities { get; }
        /// <summary>The match clock in seconds (the turret's heat and fire interval use it).</summary>
        double Clock { get; }
        /// <summary>The owner's turret control of this airframe (its shots are resolved by the server).</summary>
        GunshipControl Turret(ushort owner, Aircraft aircraft);
    }

    readonly IWorld _world;
    readonly AircraftKindDef _kind;
    readonly int _kindIndex;

    /// <param name="kind">The airframe kind (killstreaks.json "aircraft", "gunship").</param>
    /// <param name="kindIndex">Its index in that list, which the aircraft's wire state carries.</param>
    public Ac130Streak(IWorld world, AircraftKindDef kind, int kindIndex)
    {
        _world = world; _kind = kind; _kindIndex = kindIndex;
    }

    public string Id => StreakId;

    /// <summary>Kills by the AC-130's weapons are its kills: they pay the streak award and do not advance the Streak counter.</summary>
    public IReadOnlySet<string> OwnedWeapons { get; } = new HashSet<string> { StreakId };

    /// <summary>
    /// Spawns the gunship over the owner's position (it circles there for the streak's duration) and gives the owner its turret.
    /// The owner's body stands still while they steer (<c>freezesBody</c>). The streak ends when the airframe leaves the world.
    /// </summary>
    public bool Activate(KillstreakUse use)
    {
        var entities = _world.Entities;
        var lifetime = use.Def.DurationSeconds > 0 ? (float?)use.Def.DurationSeconds : null;
        var aircraft = new Aircraft(_kind, _kindIndex, use.Position, 0f, true, use.Owner, lifetime);
        if (entities.Spawn(aircraft) is not { } id)
        {
            use.Log("no room for the gunship (entity cap or type not registered)");
            return false;
        }
        var turret = _world.Turret(use.Owner, aircraft);
        if (!use.TakeControl(id, input => turret.OnInput(input, _world.Clock), freezesBody: true))
        {
            entities.Despawn(id, WorldDespawnReason.Removed);
            return false;
        }

        // the airframe's end is the streak's end; the streak's end removes the airframe if it is still up
        void OnDespawned(WorldDespawned d)
        {
            if (d.Id == id) use.End(KillstreakEndReason.Finished);
        }
        entities.Despawned += OnDespawned;
        use.OnEnded(_ =>
        {
            entities.Despawned -= OnDespawned;
            entities.Despawn(id, WorldDespawnReason.Removed); // no-op when it already left
        });
        return true;
    }
}
