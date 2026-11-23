using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.Shooter.Net.Server.Combat;

/// <summary>
/// Applies damage through the match and sends the hit event (<c>NetServer.Deal</c>); returns the result (Ignored when absorbed). What a feature that hurts a player (a turret, a burn, the Gang, the Bigfoot) is given,
/// so that it does not reach the rest of the server. <paramref name="shot"/> is where the bullet entered a wild animal it hit (the hit diagram), and <paramref name="shotDistance"/> how far it flew: for the hunter's log.
/// </summary>
internal delegate DamageResult DealDamage(ushort attackerId, byte weaponIdx, ushort victim, float damage, bool head, bool backstab, KillCause cause = KillCause.Standard, HuntShotMsg? shot = null, float? shotDistance = null);
