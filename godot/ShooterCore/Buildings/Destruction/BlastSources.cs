using Drycastle.ShooterCore.Flight;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>Which <see cref="BlastSource"/> a server blast is, for the blast paths that serve several (the rocket sim, the gunship rounds). The other call sites name theirs. WP-E.</summary>
public static class BlastSources
{
    /// <summary>A blast of the rocket sim: a thrown grenade, a gunship's rocket (it has a blast of its own, killstreaks.json), the rpg, or any other launcher (<see cref="BlastSource.Other"/>).</summary>
    public static BlastSource OfRocket(WeaponDef weapon, bool gunshipRocket) =>
        weapon.Grenade != null ? BlastSource.Grenade
        : gunshipRocket ? BlastSource.GunshipRocket
        : weapon.Id == "rpg" ? BlastSource.Rpg
        : BlastSource.Other;

    /// <summary>A gunship round's blast: a flak shell that met the ground, a shell (the 105 mm) or a cannon round with a blast (25 and 40 mm, the helicopter's cannon).</summary>
    public static BlastSource OfGunshipRound(GunshipWeaponKind kind, bool flak) =>
        flak ? BlastSource.Flak
        : kind == GunshipWeaponKind.Shell ? BlastSource.GunshipCannon
        : BlastSource.Cannon;
}
