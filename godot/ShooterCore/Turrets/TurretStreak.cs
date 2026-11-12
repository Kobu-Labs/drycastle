namespace Drycastle.ShooterCore;

/// <summary>
/// The turret killstreak (killstreaks.json id "turret", 6 kills). Using it spawns a placing ghost that follows the owner's
/// aim, and takes the owner's input through a control session. Fire or interact places it when the spot is valid; the
/// killstreak key cancels. A placement that is not confirmed within the streak's duration is removed. Once placed, the
/// turret is an ordinary world entity with its own lifetime, and the streak instance ends.
/// </summary>
public sealed class TurretStreak : IKillstreak
{
    /// <summary>killstreaks.json id.</summary>
    public const string StreakId = "turret";
    /// <summary>The weapon id of the turret's shots (its kills pay the streak award and do not advance the counter).</summary>
    public const string WeaponId = "turret_gun";

    static readonly IReadOnlySet<string> Weapons = new HashSet<string> { WeaponId };

    readonly TurretDef _def;
    readonly WorldEntityManager _entities;
    readonly ITurretWorld _world;

    public TurretStreak(TurretDef def, WorldEntityManager entities, ITurretWorld world)
    {
        _def = def;
        _entities = entities;
        _world = world;
    }

    public string Id => StreakId;
    public IReadOnlySet<string> OwnedWeapons => Weapons;

    public bool Activate(KillstreakUse use)
    {
        var pose = _world.Pose(use.Owner);
        if (pose is not { } p) return false;

        var ghost = new TurretEntity(_def, use.Owner, _world);
        ghost.Track(p.Feet, p.Eye, 0f, 0f);
        if (_entities.Spawn(ghost) is not { } entityId) return false;

        var placed = false;
        use.OnEnded(_ => { if (!placed) _entities.Despawn(entityId, WorldDespawnReason.Removed); });

        // the activating key press is still held on the first routed input, and Fire or Use may be held too: each button
        // counts as pressed until it has been released once
        var prevFire = true; var prevUse = true; var prevKey = true;
        if (!use.TakeControl(entityId, input =>
            {
                var buttons = (MoveButtons)input.Buttons;
                var fire = buttons.HasFlag(MoveButtons.Fire) && !prevFire;
                var interact = buttons.HasFlag(MoveButtons.Use) && !prevUse;
                var cancel = buttons.HasFlag(MoveButtons.Killstreak) && !prevKey;
                prevFire = buttons.HasFlag(MoveButtons.Fire);
                prevUse = buttons.HasFlag(MoveButtons.Use);
                prevKey = buttons.HasFlag(MoveButtons.Killstreak);

                if (cancel) { use.End(KillstreakEndReason.Finished); return; }
                if (_world.Pose(use.Owner) is not { } at) return;
                ghost.Track(at.Feet, at.Eye, Quant.Yaw(input.Yaw), Quant.Pitch(input.Pitch));
                if ((fire || interact) && ghost.Place())
                {
                    placed = true;
                    use.End(KillstreakEndReason.Finished);
                }
            }))
        {
            _entities.Despawn(entityId, WorldDespawnReason.Removed);
            return false;
        }
        return true;
    }
}
