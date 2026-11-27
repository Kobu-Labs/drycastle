using Godot;
using Drycastle.Shooter.Net.Server.Fences;
using Drycastle.Shooter.Net.Server.Houses;
using Drycastle.Shooter.Net.Server.Panes;
using Drycastle.ShooterCore.Buildings.Destruction;

namespace Drycastle.Shooter.Net.Server.Combat;

/// <summary>
/// What a server blast does to the world itself (docs/house-destruction.md WP-E): the glass panes in its radius take damage (<see cref="GlassServer.Blast"/>) and the hollow houses it reaches
/// are carved by its source's radius (<see cref="HouseDestructionServer.Blast"/>). Every blast path calls it: the rocket sim, a vehicle's explosion, the attack missile, the demon's fireball and the Scud
/// (<see cref="Blast"/>); the gunship's and the flak's rounds, which never broke glass, only carve (<see cref="Carve"/>). The damage to bodies and vehicles stays each path's own.
/// </summary>
internal sealed class WorldBlasts
{
    readonly GlassServer _glass;
    readonly HouseDestructionServer _houses;
    readonly FenceDestructionServer _fences;

    public WorldBlasts(GlassServer glass, HouseDestructionServer houses, FenceDestructionServer fences)
    {
        _glass = glass; _houses = houses; _fences = fences;
    }

    /// <summary>The panes, the houses and the thin fences: a blast at <paramref name="at"/> (Godot world) of <paramref name="splashRadius"/>; <paramref name="glassDamage"/> is the pane damage at its centre (glass.json's when null).</summary>
    public void Blast(Vector3 at, float splashRadius, BlastSource source, float? glassDamage = null)
    {
        _glass.Blast(at, splashRadius, glassDamage);
        _houses.Blast(at, splashRadius, source);
        _fences.Blast(at, splashRadius);
    }

    /// <summary>The houses and the thin fences, for a blast that leaves glass alone.</summary>
    public void Carve(Vector3 at, float splashRadius, BlastSource source)
    {
        _houses.Blast(at, splashRadius, source);
        _fences.Blast(at, splashRadius);
    }
}
