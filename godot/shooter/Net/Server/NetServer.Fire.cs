using Godot;
using Drycastle.Shooter.Net.Server.HuntLog;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Demon;
using Drycastle.ShooterCore.Diagnostics;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Status;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server;

/// <summary>
/// Shots and blasts: one shot or swing resolved against the targets and walls (<see cref="FireWeapon"/>: hitscan, projectile, melee), the damage it deals (<see cref="Deal"/>), and the server-owned rockets and grenades
/// (<see cref="StepRockets"/>, <see cref="Explode"/>).
/// </summary>
public partial class NetServer
{
    // rockets: server-owned projectile entities, ids from 0x8000 up (player ids count up from 1)
    /// <summary>A rocket in flight. Speed and Life are per launch when set (a gunship's, killstreaks.json); zero keeps the weapon's own.</summary>
    /// <summary>FromCar is the vehicle its owner fired it from, in an armed seat (null on foot): while the owner still rides it, the rocket passes its hull and spares its riders.</summary>
    sealed class Rocket { public ushort Id, Owner; public byte Weapon; public Vector3 Pos, Dir; public float Age; public SplashDef? Blast; public float Speed, Life; public ServerCar? FromCar; public int ViewBack = -1; public uint ViewLife; /* a thrown grenade (weapons.json grenade): its velocity and fuse; Dir is then the velocity's direction */ public GrenadeDef? Grenade; public Vector3 Vel; public Countdown Fuse; }
    readonly List<Rocket> _rockets = new();
    /// <summary>A grenade's speed in the health byte of its snapshot record, per m/s (the client divides it back out).</summary>
    public const float GrenadeSpeedScale = 8f;
    ushort _nextRocket = 0x8000;

    const float MaxShotRange = 300f;

    /// <summary>
    /// Resolves one shot or swing against the current positions (no lag compensation) and walls, applies damage, sends events.
    /// <paramref name="seatCar"/> is the vehicle the shooter rides in an armed seat (ADR 0011), null on foot. From a seat the shot leaves the
    /// seated eye, its aim is held inside the seat's arc, the vehicle's speed widens the spread as the shooter's own does on foot, the vehicle's
    /// own hull is not in the way, and its riders are not targets: riders of one vehicle never hit each other. Everything else is the same shot,
    /// so its kills are ordinary kills with that weapon.
    /// </summary>
    void FireWeapon(ServerClient c, in NetInput i, FireOutcome shot, ServerCar? seatCar = null)
    {
        List<HitTarget>? pooled = null;
        try { FireWeaponWith(c, i, shot, seatCar, ref pooled); }
        finally { if (pooled != null) _targets.Return(pooled); } // the shot is resolved: its target list goes back for the next one (it was a new list of every target per shot)
    }

    void FireWeaponWith(ServerClient c, in NetInput i, FireOutcome shot, ServerCar? seatCar, ref List<HitTarget>? pooled)
    {
        var w = shot.Weapon;
        var back = VehicleBackOf(c); // the vehicles this shot rewinds (B-02): the same view tick as the players (ADR 0013)
        _vehicleView = ViewOf(back, c.LifeStart);
        var seat = seatCar?.Def.Seats[c.CarSeat];
        var feet = seatCar != null ? SeatPos(seatCar, c.CarSeat) : c.Body.GlobalPosition;
        var eye = feet + Vector3.Up * (seat != null ? SeatAimRules.EyeHeight(seat) : MovementStep.EyeHeight(c.Body.State.Stance));
        var yaw = Quant.Yaw(i.Yaw); var pitch = Quant.Pitch(i.Pitch);
        if (seatCar != null)
        {
            var (carYaw, carPitch, carRoll) = AttitudeOf(seatCar);
            var held = SeatAimRules.Aim(seat!.Aim, carYaw, carPitch, carRoll, yaw, pitch);
            yaw = held.ViewYaw; pitch = held.ViewPitch;
        }
        var targetsT = Prof.Begin(Prof.S.ShotTargets);
        var all = ShotTargets(c); // players where the shooter's screen showed them (ADR 0013)
        pooled = all;
        var targets = seatCar != null ? RiderRules.NotAboard(all, seatCar.Seat) : all; // (NotAboard copies its own)
        Prof.End(Prof.S.ShotTargets, targetsT);
        _smoke.ShotsFired++;
        var aiming = (i.Buttons & (ushort)MoveButtons.Aim) != 0;
        var body = c.Body.State;
        float Spread(float degrees) => seat != null ? SeatAimRules.ShotSpread(degrees, w, aiming, seatCar!.State.Speed, seat) : HitMath.ShotSpread(degrees, w, body, aiming);
        var stance = seat != null ? Stance.Stand : body.Stance; // a seat steadies the recoil no more than standing does
        var aim = new ShotAim(seatCar, eye, feet, yaw, pitch, targets, aiming, stance, Spread);
        if (w.Kind == WeaponKind.Hitscan && w.Pellets > 1) { var pelT = Prof.Begin(Prof.S.FirePellets); FirePellets(c, eye, yaw, pitch, w, (byte)shot.WeaponIndex, Spread(w.PelletSpread), targets, seatCar); Prof.End(Prof.S.FirePellets, pelT); }
        else if (w.Kind == WeaponKind.Hitscan) FireHitscan(c, shot, aim);
        else if (w.Kind == WeaponKind.Projectile) FireProjectile(c, shot, aim, back);
        else if (w.Kind == WeaponKind.Melee) FireMelee(c, shot, aim);
        _vehicleView = null; // the blast and everything after the shot see the vehicles where they stand
    }

    /// <summary>What a shot is resolved from: the seat's car (null on foot), where the eye and the feet are, the view's yaw and pitch (held inside a seat's arc), the targets as the shooter saw them, and the spread in degrees.</summary>
    readonly record struct ShotAim(ServerCar? SeatCar, Vector3 Eye, Vector3 Feet, float Yaw, float Pitch, List<HitTarget> Targets, bool Aiming, Stance Stance, Func<float, float> Spread);

    /// <summary>A hitscan weapon's bullet: recoil, breath and spread move the aim, then the bullet flies along its line (a weapon with bullet drop in straight pieces) to the first wall, hull or target; the hit, the glass, the lamps and the dust follow.</summary>
    void FireHitscan(ServerClient c, FireOutcome shot, in ShotAim aim)
    {
        var w = shot.Weapon;
        var (seatCar, eye, _, yaw, pitch, targets, aiming, stance, Spread) = aim;
        var kick = c.Recoil.Shot(w.Recoil, RecoilState.Multiplier(w.Recoil, aiming, stance));
        var sway = c.Breath.AimError(c.Breath.SwayDegrees(w));
        var d = HitMath.SpreadDirection(yaw + kick.YawRad + sway.YawRad, RecoilState.ClampPitch(pitch + kick.PitchRad + sway.PitchRad), Spread(shot.SpreadDegrees), _rng);
        var dir = new Vector3(d.X, d.Y, d.Z);
        var range = w.Range > 0 ? w.Range : MaxShotRange;
        // a weapon with bullet drop is resolved along its curve, one straight piece at a time (BulletDrop); any other, and a bot's, is the one piece of the whole range
        var drops = BulletDrop.HasDrop(w) && !c.IsBot;
        var zero = drops ? c.Zero.Of(shot.WeaponIndex, w) : 0f;
        var from = eye; var along = dir; var len = range; var before = 0f; // the piece: its start, direction, length, and the metres of line of sight before it
        List<GlassCross>? crossed; float wall; ServerCar? shotCar; RayHit? hit;
        while (true)
        {
            if (drops)
            {
                var a = BulletDrop.Point(N(eye), d, w, zero, before);
                var b = BulletDrop.Point(N(eye), d, w, zero, MathF.Min(range, before + BulletDrop.SegmentM));
                var piece = b - a;
                len = piece.Length();
                from = new Vector3(a.X, a.Y, a.Z);
                along = len > 1e-4f ? new Vector3(piece.X, piece.Y, piece.Z) / len : dir;
            }
            crossed = null;
            var partT = Prof.Begin(Prof.S.FireBulletHit);
            wall = BulletHit(from, along, len, out shotCar, ref crossed, seatCar);
            Prof.End(Prof.S.FireBulletHit, partT);
            partT = Prof.Begin(Prof.S.FireRaycast);
            var (exposedGunner, pastHull) = ExposedGunnerBehind(shotCar, from, along, len, targets, skip: seatCar);
            hit = GunnerCover.Raycast(N(from), N(along), len, wall, targets, c.Id, exposedGunner, pastHull); // a flak gunner in the open is hit through his hull
            Prof.End(Prof.S.FireRaycast, partT);
            partT = Prof.Begin(Prof.S.FireMissCheck);
            if (WeaponsSmoke && hit == null && HitMath.Raycast(N(from), N(along), len, float.PositiveInfinity, targets, c.Id) != null) _smoke.WallBlocked++; // only the weapons smoke reads the count: a second ray over every target on each miss otherwise
            Prof.End(Prof.S.FireMissCheck, partT);
            if (!drops || hit != null || wall <= len || before + BulletDrop.SegmentM >= range) break;
            var passed = before;
            DamageCrossed(crossed, float.PositiveInfinity, at => HitMath.HitscanDamage(w, passed + at, false)); // the panes of a piece the bullet flew through
            _scenery.Shot(N(from), N(along), len); // and the lamps it passed
            before += BulletDrop.SegmentM;
        }
        PerfShot(c, eye, dir, range, hit?.Id);
        var end = from + along * (hit?.Distance ?? Math.Min(wall, len));
        BroadcastEvent(EventKind.Shot, WeaponCodec.Encode(new ShotMsg(c.Id, (byte)shot.WeaponIndex, N(eye), N(end))));
        BaleShot(N(from), N(along), hit?.Distance ?? Math.Min(wall, len)); // any bale the bullet passes catches fire (#331)
        _scenery.Shot(N(from), N(along), hit?.Distance ?? Math.Min(wall, len)); // and any pole lamp it passes breaks (ADR 0037), any sign plate bends (ADR 0039)
        if (hit is { } h)
        {
            // a wild animal under any hitscan weapon: where the bullet entered its body, taken before the hit can remove it (the hunter's log keeps it; a sniper's shooter is also shown it)
            var hunt = WildlifeRules.IsHitId(h.Id) ? _wildlife.HuntShotOf(targets, h.Id, N(from + along * h.Distance), before + h.Distance, (byte)shot.WeaponIndex, h.ShapeHead) : null;
            var dealT = Prof.Begin(Prof.S.FireDeal);
            var dealt = Deal(c.Id, (byte)shot.WeaponIndex, h.Id, HitMath.HitscanDamage(w, before + h.Distance, h.Head), h.Head, false, shot: hunt, shotDistance: before + h.Distance);
            Prof.End(Prof.S.FireDeal, dealT);
            if (dealt == DamageResult.Killed && hunt is { } shown && w.Category == WeaponCategory.Sniper) Send(c, new EventMsg((byte)EventKind.HuntShot, HuntShot.Encode(shown)));
        }
        else if (shotCar != null && Vehicles != null && wall <= len)
            DamageVehicle(shotCar, HitMath.HitscanDamage(w, before + wall, false) * Vehicles.BulletDamageMultiplier, c.Id, (byte)shot.WeaponIndex, 0);
        else if (shotCar == null && wall <= len) BulletPuff(N(from + along * wall)); // dust where a bullet meets the ground or a wall (ticket #154)
        if (hit == null && wall <= len) ShotGlass(shotCar, from, along, wall, HitMath.HitscanDamage(w, before + wall, false)); // the pane the bullet crossed where it stopped (ADR 0007)
        DamageCrossed(crossed, hit?.Distance ?? float.PositiveInfinity, at => HitMath.HitscanDamage(w, before + at, false));
    
    }

    /// <summary>A projectile weapon: a Lock-on launcher's missile homes on its Aircraft (ADR 0031); a rocket or a grenade is a server-owned entity that rewinds vehicles by the shooter's lag at each step (B-02).</summary>
    void FireProjectile(ServerClient c, FireOutcome shot, in ShotAim aim, int back)
    {
        var w = shot.Weapon;
        var (seatCar, eye, _, yaw, pitch, _, _, _, _) = aim;
        var dir = HitMath.Forward(yaw, pitch);
        if (w.Lock != null && FireHoming(c, shot, eye)) return; // a Lock-on launcher's missile homes on its Aircraft (ADR 0031)
        var rocket = new Rocket { Id = _nextRocket, Owner = c.Id, Weapon = (byte)shot.WeaponIndex, Pos = eye, Dir = new Vector3(dir.X, dir.Y, dir.Z), FromCar = seatCar };
        if (w.Grenade is { } gren) { rocket.Grenade = gren; rocket.Vel = rocket.Dir * w.ProjectileSpeed; rocket.Fuse.Start(gren.FuseSeconds); }
        _nextRocket = _nextRocket == ushort.MaxValue ? (ushort)0x8000 : (ushort)(_nextRocket + 1);
        rocket.ViewBack = back; rocket.ViewLife = c.LifeStart; // a rocket rewinds vehicles by the shooter's lag at each step (B-02)
        _rockets.Add(rocket);
        _smoke.RocketsFired++;
        BroadcastEvent(EventKind.Launch, WeaponCodec.Encode(new LaunchMsg(rocket.Id, c.Id, rocket.Weapon, N(eye), dir * w.ProjectileSpeed)));
    
    }

    /// <summary>A melee swing: the target in reach, unless a wall stands between the attacker and the victim's chest; else a sentry turret in reach.</summary>
    void FireMelee(ServerClient c, FireOutcome shot, in ShotAim aim)
    {
        var w = shot.Weapon;
        var (seatCar, eye, feet, yaw, _, targets, _, _, _) = aim;
        var fwd = HitMath.Forward(yaw, 0);
        var hit = HitMath.Melee(N(feet), yaw, w.Reach, targets, c.Id);
        if (hit is { } h && _byId.ContainsKey(h.Id) && TargetFeet(targets, h.Id) is { } victimFeet)
        {
            // a wall between the attacker and the victim's chest blocks the stab (the chest where the victim was seen, ADR 0013)
            var chest = victimFeet + Vector3.Up * 1.0f;
            var to = chest - eye;
            if (WorldHit(eye, to.Normalized(), to.Length(), out _, seatCar) < to.Length() - 0.05f) { _smoke.WallBlocked++; hit = null; }
        }
        BroadcastEvent(EventKind.Shot, WeaponCodec.Encode(new ShotMsg(c.Id, (byte)shot.WeaponIndex, N(eye), N(eye) + fwd * w.Reach)));
        if (hit is { } m) Deal(c.Id, (byte)shot.WeaponIndex, m.Id, HitMath.MeleeDamage(w, m.Backstab), false, m.Backstab);
        else if (_sentries.Melee(N(feet), yaw, w.Reach) is { } turret) _match.Entities.Damage(turret, Turret.KnifeDamage * c.Perks.VehicleDamageDealt, c.Id);
    
    }

    /// <summary>Applies damage through the match and sends the hit event; returns the result (Ignored when absorbed).</summary>
    /// <param name="shot">Where the bullet entered a wild animal it hit (the hit diagram), and <paramref name="shotDistance"/> how far it flew: for the hunter's log (<see cref="HuntLogServer.OnWildKilled"/>).</param>
    DamageResult Deal(ushort attackerId, byte weaponIdx, ushort victim, float damage, bool head, bool backstab, KillCause cause = KillCause.Standard, HuntShotMsg? shot = null, float? shotDistance = null)
    {
        _animals.PlayerHurt(); // a life may end here: the animals' list of players is asked anew
        if (victim == BigfootRules.TargetId) return DealBigfoot(attackerId, weaponIdx); // a hit count, not hit points (issue #264)
        if (_streaks.ControlOf(victim) is { ShieldsBody: true }) return DamageResult.Ignored; // a pilot at the gun is not a sitting target (attack helicopter)
        if (DemonRules.IsHitId(victim)) return DemonHit(attackerId, weaponIdx, damage); // the demon: a hit count, no hit points
        if (WildlifeRules.IsHitId(victim)) _hunts.Pending = new WildHit(weaponIdx, cause, shotDistance, shot); // read by the keeper's Killed event, inside this call
        var r = _match.ApplyDamage(victim, attackerId, weaponIdx, damage, cause: head ? KillCause.Headshot : cause);
        _hunts.Pending = null;
        if (r == DamageResult.Ignored) return r; // dead, spawn protected or round not running: the shot is absorbed
        if (CitizenRules.IsHitId(victim) || PetRules.IsHitId(victim) || WildlifeRules.IsHitId(victim) || GangRules.IsHitId(victim) || RaidVanRules.IsHitId(victim)) // a citizen, a pet, a wild animal, a Gang member or a Raid van: the hit marker, but no assist, streak, refill or kill feed
        {
            if (_byId.TryGetValue(attackerId, out var shooter)) Send(shooter, new EventMsg((byte)EventKind.Hit, WeaponCodec.Encode(new HitMsg(victim, attackerId, weaponIdx, (ushort)damage, head, backstab, r == DamageResult.Killed))));
            return r;
        }
        if (r == DamageResult.Saved) BroadcastEvent(EventKind.BlessingSaved, StatusCodec.EncodeSaved(victim)); // the blessing is used up; the mask follows with the flush
        else if (r == DamageResult.Damaged) _status.OnHit(attackerId, weaponIdx, victim);
        if (r == DamageResult.Damaged && Scores(attackerId) && Scores(victim)) _xp.NoteDamage(victim, attackerId, _elapsed); // an assist if the victim dies within the window
        BotHit(victim, attackerId);
        _smoke.HitsLanded++;
        LossHit(attackerId, victim, r == DamageResult.Killed);
        if (head) _smoke.Headshots++;
        if (backstab) _smoke.Backstabs++;
        var kill = r == DamageResult.Killed;
        if (kill && shotDistance is { } far) _hunts.RecordPlayerKill(attackerId, victim, weaponIdx, far); // the longest player kill of a hitscan shot (#430)
        var msg = new EventMsg((byte)EventKind.Hit, WeaponCodec.Encode(new HitMsg(victim, attackerId, weaponIdx, (ushort)damage, head, backstab, kill)));
        _byId.TryGetValue(attackerId, out var attacker);
        if (attackerId == _gunFxOwner) _gunFx.Hit(attackerId, head, kill); // a gunner's hit marker rides the tick's gunship blasts (#159)
        else if (attacker != null) Send(attacker, msg);
        if (_byId.TryGetValue(victim, out var v) && v != attacker) Send(v, msg);
        if (kill && victim != attackerId && attacker != null) { attacker.W.RefillReserves(); _smoke.Refills++; }
        return r;
    }

    /// <summary>Moves every rocket one tick: swept against the world and live players (not the owner), explodes on impact or when its life runs out.</summary>
    void StepRockets(float dt)
    {
        for (var k = _rockets.Count - 1; k >= 0; k--)
        {
            var r = _rockets[k];
            var w = Weapons.Weapons[r.Weapon];
            if (r.Grenade != null) { if (StepGrenade(r, w, dt)) _rockets.RemoveAt(k); continue; }
            var speed = r.Speed > 0 ? r.Speed : w.ProjectileSpeed;
            var life = r.Life > 0 ? r.Life : w.ProjectileLife;
            var len = speed * dt;
            // a rocket fired from an armed seat passes its own vehicle's hull and riders for as long as its owner still rides it
            var own = RocketCar(r);
            // the vehicles a rocket meets are where its shooter's screen showed them, this step (B-02); the players are where they stand (ADR 0013)
            _vehicleView = ViewOf(r.ViewBack, r.ViewLife);
            var wall = WorldHit(r.Pos, r.Dir, len, out var hitCar, own);
            var all = _targets.Live();
            var targets = own != null ? RiderRules.NotAboard(all, own.Seat) : all;
            var (exposedGunner, pastHull) = ExposedGunnerBehind(hitCar, r.Pos, r.Dir, len, targets, skip: own);
            var step = RocketFlight.Step(N(r.Pos), N(r.Dir), r.Age, speed, life, dt, wall, targets, r.Owner, exposedGunner, pastHull);
            _targets.Return(all); // the step is done with the targets (NotAboard copied its own)
            _vehicleView = null;
            switch (step.Outcome)
            {
                case RocketOutcome.Player:
                    _rockets.RemoveAt(k);
                    Explode(r, w, r.Pos + r.Dir * step.Distance, step.Victim);
                    continue;
                case RocketOutcome.World:
                    _rockets.RemoveAt(k);
                    _smoke.WorldExplosions++;
                    // pulled back a hair so the blast centre is in front of the surface, not inside it
                    Explode(r, w, r.Pos + r.Dir * MathF.Max(0, step.Distance - 0.02f), null, hitCar);
                    continue;
            }
            r.Pos += r.Dir * len;
            r.Age += dt;
            if (step.Outcome == RocketOutcome.Expired) { _rockets.RemoveAt(k); _smoke.ExpiredRockets++; Explode(r, w, r.Pos, null); }
        }
    }

    /// <summary>
    /// Moves a thrown grenade one tick (<see cref="GrenadeFlight"/>): it bounces off the world and vehicles (as they stood where its thrower's screen showed them, B-02) and
    /// flies through players; its fuse runs out into the rpg's own <see cref="Explode"/>. True when it blew up (the caller drops it).
    /// </summary>
    bool StepGrenade(Rocket r, WeaponDef w, float dt)
    {
        var own = RocketCar(r);
        _vehicleView = ViewOf(r.ViewBack, r.ViewLife);
        var step = GrenadeFlight.Step(r.Grenade!, N(r.Pos), N(r.Vel), dt, (from, dir, len) =>
        {
            var stop = Query.Shot(new Vector3(from.X, from.Y, from.Z), new Vector3(dir.X, dir.Y, dir.Z), len, own);
            return new GrenadeStop(stop.Distance, new NVec3(stop.Normal.X, stop.Normal.Y, stop.Normal.Z));
        });
        _vehicleView = null;
        r.Pos = new Vector3(step.Pos.X, step.Pos.Y, step.Pos.Z);
        r.Vel = new Vector3(step.Vel.X, step.Vel.Y, step.Vel.Z);
        if (r.Vel.LengthSquared() > 1e-4f) r.Dir = r.Vel.Normalized();
        r.Age += dt;
        if (!r.Fuse.Tick(dt)) return false;
        Explode(r, w, r.Pos + Vector3.Up * 0.1f, null); // a hair above where it lies: the blast centre is not in the floor
        return true;
    }

    /// <summary>The vehicle a rocket was fired from, while its owner still rides it; null otherwise (fired on foot, or the owner has got out or died).</summary>
    ServerCar? RocketCar(Rocket r) => r.FromCar is { Destroyed: false } car && car.Seat.SeatOf(r.Owner) >= 0 ? car : null;

    /// <summary>
    /// Explosion: the direct-hit victim takes the full damage (and no splash on top); everyone else alive within the splash
    /// radius takes the linear falloff damage, the owner scaled by the self multiplier. A wall between the blast and a target stops it, and a covered seat's rider takes half through the hull (<see cref="SplashShare"/>). Affected players get knockback. A rocket fired from an armed seat spares the shooter's fellow riders (riders
    /// of one vehicle never hit each other); the shooter takes their own blast as on foot, and so does the vehicle.
    /// </summary>
    void Explode(Rocket r, WeaponDef weapon, Vector3 pos, ushort? direct, ServerCar? directCar = null)
    {
        var w = ProjectileMath.WithBlast(weapon, r.Blast); // a gunship's rocket has its own blast (killstreaks.json); the rpg keeps weapons.json's
        _smoke.Explosions++;
        if (Vehicles != null) RocketVsVehicles(pos, w, r.Owner, r.Weapon, directCar);
        BlastGlass(pos, w.Splash?.Radius ?? 0);
        BroadcastEvent(EventKind.Explosion, WeaponCodec.Encode(new ExplosionMsg(r.Id, r.Owner, r.Weapon, N(pos), w.Splash?.Radius ?? 0, w.Splash?.WaveSpeed ?? 0)));
        BroadcastImpact(N(pos), ImpactSize.Plume, body: direct != null && directCar == null, vehicle: directCar != null); // rockets throw up a plume (ticket #154)
        var own = RocketCar(r);
        var targets = _targets.Live();
        foreach (var t in targets)
        {
            var isDirect = direct == t.Id;
            var dist = HitMath.DistanceToBody(N(pos), t);
            var self = t.Id == r.Owner;
            if (!self && own != null && own.Seat.SeatOf(t.Id) >= 0) continue; // a fellow rider of the shooter's vehicle
            var dmg = isDirect ? w.Damage : ProjectileMath.SplashDamage(w, dist, self);
            if (dmg <= 0) continue;
            var share = isDirect ? 1f : SplashShare(N(pos), t); // walls, a bunker room's too, and a covered seat's hull
            if (share <= 0) continue;
            dmg *= share;
            if (_byId.TryGetValue(t.Id, out var victimClient)) dmg = MathF.Max(1f, MathF.Round(dmg * victimClient.Perks.ExplosiveDamageTaken)); // Flak Jacket
            var k = ProjectileMath.Knockback(w, N(pos), HitMath.Centre(t), isDirect ? 1f : share * ProjectileMath.SplashFraction(w, dist));
            PushOnDeath(t.Id, CorpseRules.ThrowFromBlast(k)); // a kill throws the body (the ragdoll)
            var res = Deal(r.Owner, r.Weapon, t.Id, dmg, false, false);
            ClearPush(t.Id, res);
            if (res == DamageResult.Ignored) continue; // spawn protected: neither damage nor knockback
            if (isDirect) _smoke.DirectHits++; else if (self) _smoke.SelfHits++; else _smoke.SplashHits++;
            if (res != DamageResult.Killed && _byId.TryGetValue(t.Id, out var v) && k != NVec3.Zero) { v.Body.AddImpulse(new Vector3(k.X, k.Y, k.Z)); _smoke.Knocked++; }
        }
        _targets.Return(targets);
    }
}
