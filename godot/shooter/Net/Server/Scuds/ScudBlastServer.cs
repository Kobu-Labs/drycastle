using Godot;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Net.Server.HuntLog;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Bunkers;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Scud;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Scuds;

/// <summary>
/// THE Scud blast: the whole of what a Scud missile's impact does, at a point on the ground, credited to the player who placed the lock (the rocket cart's shooter when a loaded cart is shot). Everything inside the lethal
/// radius dies, in a vehicle or not and spawn protected or not, except what a blast shelter covers (the same rule as every blast); beyond it the damage falls off to the damage radius and then walls, a hull and shelters work as for
/// any blast (<see cref="Splash.Share"/>). Vehicles whose hull is in the destroy radius go up, those beyond it take hit points. Every glass pane within the glass radius shatters, the clients draw the biggest blast wave and dust the
/// game has, shake the camera map-wide and leave a scorch. Kills are ordinary kills (friendly fire as the match mode has it). Aircraft are not touched.
/// </summary>
internal sealed class ScudBlastServer
{
    /// <summary>The kills of a Scud blast are the placer's ordinary kills with this weapon byte.</summary>
    const byte ScudWeapon = SpecialWeapons.Scud;

    /// <summary>How hard a blast throws a body that dies in it, metres a second at the blast's centre, falling off to nothing at the damage radius.</summary>
    const float ScudKnockMps = 28f;

    readonly ServerContext _ctx;
    readonly ScudBlastData _blast;
    readonly TargetBook _targets;
    readonly Splash _splash;
    readonly DeathPush _deathPush;
    readonly DealDamage _deal;
    readonly HuntLogServer _hunts;
    readonly Fleet _fleet;
    readonly Func<Shelters> _shelters;
    readonly Func<ushort, byte, DamageResult> _dealBigfoot;
    readonly Action<Vector3, float, float?> _blastGlass;
    readonly Action<ServerCar, float, ushort, byte, float> _damageVehicle;
    readonly Func<ServerCar, NVec3, float> _hullDistance;

    /// <param name="dealBigfoot">The Bigfoot takes the blast as an explosive hit, not as a kill (<c>NetServer.DealBigfoot</c>).</param>
    /// <param name="blastGlass">Every pane within a radius takes a damage (<c>NetServer.BlastGlass</c>).</param>
    /// <param name="damageVehicle">Hurts a vehicle, its blast scheduled after a delay at 0 hit points (<c>NetServer.DamageVehicle</c>).</param>
    /// <param name="hullDistance">The distance from a point to a vehicle's hull where it stands now (<c>NetServer.HullDistanceNow</c>).</param>
    public ScudBlastServer(ServerContext ctx, ScudBlastData blast, TargetBook targets, Splash splash, DeathPush deathPush, DealDamage deal, HuntLogServer hunts, Fleet fleet, Func<Shelters> shelters,
        Func<ushort, byte, DamageResult> dealBigfoot, Action<Vector3, float, float?> blastGlass, Action<ServerCar, float, ushort, byte, float> damageVehicle, Func<ServerCar, NVec3, float> hullDistance)
    {
        _ctx = ctx; _blast = blast; _targets = targets; _splash = splash; _deathPush = deathPush; _deal = deal; _hunts = hunts; _fleet = fleet; _shelters = shelters;
        _dealBigfoot = dealBigfoot; _blastGlass = blastGlass; _damageVehicle = damageVehicle; _hullDistance = hullDistance;
    }

    /// <param name="missileId">The world entity that flew here (0 for a blast with none); the clients end its picture with it.</param>
    public void Blast(NVec3 position, ushort attackerId, ushort missileId = 0)
    {
        var b = _blast;
        _ctx.Broadcast(EventKind.ScudImpact, ScudCodec.Encode(new ScudImpactMsg(missileId, attackerId, position)));
        _blastGlass(new Vector3(position.X, position.Y, position.Z) + Vector3.Up * 1f, (float)b.GlassRadiusM, 1_000_000f); // every pane in reach shatters: the centre damage is far above any pane's hit points
        var (killed, hurt) = HurtPlayers(position, attackerId);
        var destroyed = HurtVehicles(position, attackerId);
        GD.Print($"server: Scud blast at ({position.X:F0},{position.Y:F0},{position.Z:F0}) by #{attackerId}: {killed} killed, {hurt} hurt, {destroyed} vehicles destroyed");
    }

    /// <summary>Everything alive in reach: the lethal radius kills, beyond it the damage falls off through walls and hulls. Returns how many died and how many were only hurt.</summary>
    (int Killed, int Hurt) HurtPlayers(NVec3 position, ushort attackerId)
    {
        var b = _blast;
        var killed = 0; var hurt = 0;
        var targets = _targets.Live();
        foreach (var t in targets)
        {
            var centre = HitMath.Centre(t);
            var dist = HitMath.DistanceToBody(position, t);
            var hit = ScudBlastRules.PlayerHit(b, dist);
            if (!hit.Hurts || !_shelters().Reaches(position, centre)) continue; // a bunker room or the church shelters those inside
            var away = centre - position; away.Y = MathF.Max(away.Y, 2f);
            var push = away == NVec3.Zero ? NVec3.UnitY : NVec3.Normalize(away);
            var k = push * (ScudKnockMps * Math.Clamp(1f - dist / (float)b.DamageRadiusM, 0.1f, 1f));
            if (hit.Lethal)
            {
                _deathPush.Record(t.Id, CorpseRules.ThrowFromBlast(k)); // the body is thrown
                if (t.Id == BigfootRules.TargetId) _dealBigfoot(attackerId, ScudWeapon); // the Bigfoot takes it as an explosive hit, not as a kill (issue #264)
                else
                {
                    _hunts.Pending = new WildHit(ScudWeapon, KillCause.Standard, null, null); // a wild animal in the blast is a hunt with the Scud
                    var res = _ctx.Match.ApplyDamage(t.Id, attackerId, ScudWeapon, hit.Damage, ignoreProtection: true);
                    _hunts.Pending = null;
                    _deathPush.Clear(t.Id, res);
                    if (res == DamageResult.Killed) killed++;
                }
                continue;
            }
            var share = _splash.Share(position, t); // walls, a hull over a covered seat
            if (share <= 0) continue;
            var dmg = hit.Damage * share;
            if (_ctx.Players.TryGetValue(t.Id, out var victimClient)) dmg = MathF.Max(1f, MathF.Round(dmg * victimClient.Perks.ExplosiveDamageTaken)); // Flak Jacket
            _deathPush.Record(t.Id, CorpseRules.ThrowFromBlast(k));
            var dealt = _deal(attackerId, ScudWeapon, t.Id, dmg, false, false);
            _deathPush.Clear(t.Id, dealt);
            if (dealt == DamageResult.Ignored) continue; // spawn protected
            if (dealt == DamageResult.Killed) killed++; else hurt++;
            if (dealt != DamageResult.Killed && _ctx.Players.TryGetValue(t.Id, out var v)) v.Body.AddImpulse(new Vector3(k.X, k.Y, k.Z) * 0.3f);
        }
        _targets.Return(targets);
        return (killed, hurt);
    }

    /// <summary>The vehicles: a hull in the destroy radius goes up, the rest take hit points; the nearest go first, the wave reaching the farther ones a moment later. Returns how many were destroyed.</summary>
    int HurtVehicles(NVec3 position, ushort attackerId)
    {
        var b = _blast;
        var destroyed = 0;
        foreach (var car in _fleet.Cars.ToList())
        {
            if (car.Destroyed) continue;
            var dist = _hullDistance(car, position);
            var dmg = ScudBlastRules.VehicleDamage(b, dist, car.Hp);
            if (dmg <= 0) continue;
            var delay = dist <= b.VehicleDestroyRadiusM ? MathF.Min(1.5f, ScudBlastRules.ArrivalSeconds(b, dist)) : 0f;
            if (car.Cargo?.Detonates(car.Def) == true) delay = ScudBlastRules.ChainDelay(b, dist); // a loaded rocket cart goes up in its turn, once (a doomed vehicle keeps its first date)
            _damageVehicle(car, dmg, attackerId, ScudWeapon, delay);
            if (dist <= b.VehicleDestroyRadiusM) destroyed++;
        }
        return destroyed;
    }
}
