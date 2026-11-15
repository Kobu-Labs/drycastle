namespace Drycastle.ShooterCore;

/// <summary>Which hit target ids are scenery-like beings and never an enemy for an automatic gun. The Gang members of the Thieves event are not in it: turrets and killstreak weapons shoot them.</summary>
public static class NonEnemyTargets
{
    /// <summary>True for a citizen, a pet or the demon: automatic guns (turrets, the helicopter's helpers) leave them alone.</summary>
    public static bool IsNonEnemy(ushort hitId) => CitizenRules.IsHitId(hitId) || PetRules.IsHitId(hitId) || WildlifeRules.IsHitId(hitId) || DemonRules.IsHitId(hitId);
}
