namespace Drycastle.ShooterCore.Buildings.Destruction;

/// <summary>What made a blast (house_destruction.json carve.by_source keys, snake_case). <see cref="Other"/> is any blast without its own entry: it carves splash radius * default_share.</summary>
public enum BlastSource : byte { Rpg, Grenade, Cannon, Flak, GunshipRocket, GunshipCannon, Missile, Car, Scud, Moab, Demon, Other }
