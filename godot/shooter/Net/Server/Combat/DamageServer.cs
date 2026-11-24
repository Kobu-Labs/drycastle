using Drycastle.Shooter.Net.Server.BotRoster;
using Drycastle.Shooter.Net.Server.Careers;
using Drycastle.Shooter.Net.Server.Fauna;
using Drycastle.Shooter.Net.Server.HuntLog;
using Drycastle.Shooter.Net.Server.Parish;
using Drycastle.Shooter.Net.Server.Statuses;
using Drycastle.Shooter.Net.Server.Streaks;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Demon;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Status;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.Shooter.Net.Server.Combat;

/// <summary>
/// Where every hit lands (<see cref="Deal"/>): the match applies the damage and the hit event goes to the shooter and the victim. The few that have no hit points (the Bigfoot, the demon, a pilot at the gun)
/// are answered on the way in; a citizen, a pet, a wild animal, a Gang member or a Raid van gets the hit marker and nothing else; a player's hit feeds the statuses, the assist window, the bots, the counters and the hunter's log.
/// <see cref="Deal"/> is also the <see cref="DealDamage"/> a feature that hurts a player is given.
/// </summary>
internal sealed class DamageServer
{
    readonly ServerContext _ctx;
    readonly AnimalWorld _animals;
    readonly ProgressionServer _progress;
    readonly BotServer _bots;
    readonly SmokeStats _smoke;
    readonly Func<StreakServer> _streaks;
    readonly Func<DemonServer> _demon;
    readonly Func<HuntLogServer> _hunts;
    readonly Func<StatusServer> _status;
    readonly Func<GunshipServer> _gunships;
    readonly Func<ushort, byte, DamageResult> _dealBigfoot;
    readonly Action<ushort, ushort, bool> _lossHit;

    /// <param name="dealBigfoot">A hit on the Bigfoot is a hit count, not hit points (issue #264).</param>
    /// <param name="lossHit">The loss test's count of the hits that landed (attacker, victim, killed).</param>
    public DamageServer(ServerContext ctx, AnimalWorld animals, ProgressionServer progress, BotServer bots, SmokeStats smoke, Func<StreakServer> streaks, Func<DemonServer> demon, Func<HuntLogServer> hunts,
        Func<StatusServer> status, Func<GunshipServer> gunships, Func<ushort, byte, DamageResult> dealBigfoot, Action<ushort, ushort, bool> lossHit)
    {
        _ctx = ctx; _animals = animals; _progress = progress; _bots = bots; _smoke = smoke; _streaks = streaks; _demon = demon; _hunts = hunts; _status = status; _gunships = gunships;
        _dealBigfoot = dealBigfoot; _lossHit = lossHit;
    }

    /// <summary>Applies damage through the match and sends the hit event; returns the result (Ignored when absorbed).</summary>
    /// <param name="shot">Where the bullet entered a wild animal it hit (the hit diagram), and <paramref name="shotDistance"/> how far it flew: for the hunter's log (<see cref="HuntLogServer.OnWildKilled"/>).</param>
    public DamageResult Deal(ushort attackerId, byte weaponIdx, ushort victim, float damage, bool head, bool backstab, KillCause cause = KillCause.Standard, HuntShotMsg? shot = null, float? shotDistance = null)
    {
        _animals.PlayerHurt(); // a life may end here: the animals' list of players is asked anew
        if (victim == BigfootRules.TargetId) return _dealBigfoot(attackerId, weaponIdx); // a hit count, not hit points (issue #264)
        if (_streaks().Rules.ControlOf(victim) is { ShieldsBody: true }) return DamageResult.Ignored; // a pilot at the gun is not a sitting target (attack helicopter)
        if (DemonRules.IsHitId(victim)) return _demon().Hit(attackerId, weaponIdx, damage); // the demon: a hit count, no hit points
        var hunts = _hunts();
        if (WildlifeRules.IsHitId(victim)) hunts.Pending = new WildHit(weaponIdx, cause, shotDistance, shot); // read by the keeper's Killed event, inside this call
        var r = _ctx.Match.ApplyDamage(victim, attackerId, weaponIdx, damage, cause: head ? KillCause.Headshot : cause);
        hunts.Pending = null;
        if (r == DamageResult.Ignored) return r; // dead, spawn protected or round not running: the shot is absorbed
        if (CitizenRules.IsHitId(victim) || PetRules.IsHitId(victim) || WildlifeRules.IsHitId(victim) || GangRules.IsHitId(victim) || RaidVanRules.IsHitId(victim)) // a citizen, a pet, a wild animal, a Gang member or a Raid van: the hit marker, but no assist, streak, refill or kill feed
        {
            if (_ctx.Players.TryGetValue(attackerId, out var shooter)) _ctx.SendEvent(shooter, EventKind.Hit, WeaponCodec.Encode(new HitMsg(victim, attackerId, weaponIdx, (ushort)damage, head, backstab, r == DamageResult.Killed)));
            return r;
        }
        Landed(r, attackerId, weaponIdx, victim, damage, head, backstab, shotDistance);
        return r;
    }

    /// <summary>A player's hit: what the statuses, the assist window, the bots and the counters make of it, the hit marker for both sides, and the refill a kill earns.</summary>
    void Landed(DamageResult r, ushort attackerId, byte weaponIdx, ushort victim, float damage, bool head, bool backstab, float? shotDistance)
    {
        if (r == DamageResult.Saved) _ctx.Broadcast(EventKind.BlessingSaved, StatusCodec.EncodeSaved(victim)); // the blessing is used up; the mask follows with the flush
        else if (r == DamageResult.Damaged) _status().OnHit(attackerId, weaponIdx, victim);
        if (r == DamageResult.Damaged) _progress.NoteDamage(victim, attackerId); // an assist if the victim dies within the window
        _bots.OnHit(victim, attackerId);
        _smoke.HitsLanded++;
        _lossHit(attackerId, victim, r == DamageResult.Killed);
        if (head) _smoke.Headshots++;
        if (backstab) _smoke.Backstabs++;
        var kill = r == DamageResult.Killed;
        if (kill && shotDistance is { } far) _hunts().RecordPlayerKill(attackerId, victim, weaponIdx, far); // the longest player kill of a hitscan shot (#430)
        var msg = new EventMsg((byte)EventKind.Hit, WeaponCodec.Encode(new HitMsg(victim, attackerId, weaponIdx, (ushort)damage, head, backstab, kill)));
        _ctx.Players.TryGetValue(attackerId, out var attacker);
        if (!_gunships().NoteHit(attackerId, head, kill) && attacker != null) _ctx.Send(attacker, msg); // a gunner's hit marker rides the tick's gunship blasts (#159)
        if (_ctx.Players.TryGetValue(victim, out var v) && v != attacker) _ctx.Send(v, msg);
        if (kill && victim != attackerId && attacker != null) { attacker.W.RefillReserves(); _smoke.Refills++; }
    }
}
