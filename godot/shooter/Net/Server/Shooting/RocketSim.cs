using Godot;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Net.Server.Panes;
using Drycastle.Shooter.Net.Server.Rewind;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Buildings.Destruction;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Shooting;

/// <summary>
/// The server-owned rockets and grenades: a rocket is a projectile entity (ids from 0x8000 up, player ids count up from 1) that flies a step each tick, swept against the world, the vehicles where its shooter's screen showed them
/// (B-02) and the live players (not the owner), and explodes on impact or when its life runs out (<see cref="Explode"/>). A thrown grenade bounces and explodes when its fuse runs out.
/// </summary>
internal sealed class RocketSim
{
    /// <summary>A rocket in flight. Speed and Life are per launch when set (a gunship's, killstreaks.json); zero keeps the weapon's own. FromCar is the vehicle its owner fired it from, in an armed seat (null on foot): while the owner still rides it, the rocket passes its hull and spares its riders.</summary>
    sealed class Rocket
    {
        public ushort Id, Owner;
        public byte Weapon;
        public Vector3 Pos, Dir;
        public float Age;
        public SplashDef? Blast;
        public float Speed, Life;
        public ServerCar? FromCar;
        public int ViewBack = -1;
        public uint ViewLife;
        /// <summary>A thrown grenade (weapons.json grenade): its velocity and fuse; Dir is then the velocity's direction.</summary>
        public GrenadeDef? Grenade;
        public Vector3 Vel;
        public Countdown Fuse;
    }

    readonly ServerContext _ctx;
    readonly PoseRewind _rewind;
    readonly ShotWorld _shot;
    readonly WorldBlasts _blasts;
    readonly CarDamage _carDamage;
    readonly TargetBook _targets;
    readonly DealDamage _deal;
    readonly DeathPush _deathPush;
    readonly Splash _splash;
    readonly ImpactFx _impacts;
    readonly RocketIds _ids;
    readonly WeaponsFile _weapons;
    readonly SmokeStats _smoke;
    readonly List<Rocket> _rockets = new();

    public RocketSim(ServerContext ctx, PoseRewind rewind, ShotWorld shot, WorldBlasts blasts, CarDamage carDamage, TargetBook targets, DealDamage deal, DeathPush deathPush, Splash splash, ImpactFx impacts,
        RocketIds ids, WeaponsFile weapons, SmokeStats smoke)
    {
        _ctx = ctx; _rewind = rewind; _shot = shot; _blasts = blasts; _carDamage = carDamage; _targets = targets; _deal = deal; _deathPush = deathPush; _splash = splash; _impacts = impacts;
        _ids = ids; _weapons = weapons; _smoke = smoke;
    }

    /// <summary>How many rockets and grenades fly.</summary>
    public int Count => _rockets.Count;

    // ---- launching ----

    /// <summary>A rocket or a grenade leaves <paramref name="c"/>'s hands at <paramref name="eye"/> along <paramref name="dir"/>; it rewinds vehicles by the shooter's lag (<paramref name="back"/> ticks) at each step (B-02).</summary>
    public void Launch(ServerClient c, FireOutcome shot, Vector3 eye, NVec3 dir, ServerCar? seatCar, int back)
    {
        var w = shot.Weapon;
        var rocket = new Rocket { Id = _ids.Next(), Owner = c.Id, Weapon = (byte)shot.WeaponIndex, Pos = eye, Dir = new Vector3(dir.X, dir.Y, dir.Z), FromCar = seatCar };
        if (w.Grenade is { } gren) { rocket.Grenade = gren; rocket.Vel = rocket.Dir * w.ProjectileSpeed; rocket.Fuse.Start(gren.FuseSeconds); }
        rocket.ViewBack = back; rocket.ViewLife = c.LifeStart; // a rocket rewinds vehicles by the shooter's lag at each step (B-02)
        _rockets.Add(rocket);
        _smoke.RocketsFired++;
        _ctx.Broadcast(EventKind.Launch, WeaponCodec.Encode(new LaunchMsg(rocket.Id, c.Id, rocket.Weapon, eye.ToNumerics(), dir * w.ProjectileSpeed)));
    }

    /// <summary>A gunship's rocket joins the rocket sim at its own speed and lives out its whole range (the rpg's 40 m/s would burst it ~150 m out).</summary>
    public void LaunchFromGunship(ushort owner, byte weapon, SplashDef? blast, NVec3 origin, NVec3 direction, float speed, float life)
    {
        var rocket = new Rocket
        {
            Id = _ids.Next(), Owner = owner, Weapon = weapon, Blast = blast,
            Pos = new Vector3(origin.X, origin.Y, origin.Z), Dir = new Vector3(direction.X, direction.Y, direction.Z), Speed = speed, Life = life,
        };
        _rockets.Add(rocket);
        _smoke.RocketsFired++;
        _ctx.Broadcast(EventKind.Launch, WeaponCodec.Encode(new LaunchMsg(rocket.Id, owner, rocket.Weapon, origin, direction * rocket.Speed)));
    }

    /// <summary>The snapshot entries of the rockets and grenades in flight; a grenade's speed rides the health byte (the rocket has none).</summary>
    public void AddRecords(List<EntityRecord> ents)
    {
        foreach (var r in _rockets)
            ents.Add(EntityRecord.Make(r.Id, EntityType.Projectile, r.Pos.ToNumerics(),
                MathF.Atan2(-r.Dir.X, -r.Dir.Z), MathF.Asin(Math.Clamp(r.Dir.Y, -1f, 1f)), r.Grenade != null ? r.Vel.Length() * NetServer.GrenadeSpeedScale : 0, EntityState.Alive, r.Weapon));
    }

    // ---- flying ----

    /// <summary>Moves every rocket one tick: swept against the world and live players (not the owner), explodes on impact or when its life runs out.</summary>
    public void Step(float dt)
    {
        for (var k = _rockets.Count - 1; k >= 0; k--)
        {
            var r = _rockets[k];
            var w = _weapons.Weapons[r.Weapon];
            if (r.Grenade != null) { if (StepGrenade(r, w, dt)) _rockets.RemoveAt(k); continue; }
            var speed = r.Speed > 0 ? r.Speed : w.ProjectileSpeed;
            var life = r.Life > 0 ? r.Life : w.ProjectileLife;
            var len = speed * dt;
            // a rocket fired from an armed seat passes its own vehicle's hull and riders for as long as its owner still rides it
            var own = CarOf(r);
            // the vehicles a rocket meets are where its shooter's screen showed them, this step (B-02); the players are where they stand (ADR 0013)
            _rewind.Look(r.ViewBack, r.ViewLife);
            var wall = _shot.WorldHit(r.Pos, r.Dir, len, out var hitCar, own);
            var all = _targets.Live();
            var targets = own != null ? RiderRules.NotAboard(all, own.Seat) : all;
            var (exposedGunner, pastHull) = _shot.ExposedGunnerBehind(hitCar, r.Pos, r.Dir, len, targets, skip: own);
            var step = RocketFlight.Step(r.Pos.ToNumerics(), r.Dir.ToNumerics(), r.Age, speed, life, dt, wall, targets, r.Owner, exposedGunner, pastHull);
            _targets.Return(all); // the step is done with the targets (NotAboard copied its own)
            _rewind.LookNow();
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
        var own = CarOf(r);
        _rewind.Look(r.ViewBack, r.ViewLife);
        var step = GrenadeFlight.Step(r.Grenade!, r.Pos.ToNumerics(), r.Vel.ToNumerics(), dt, (from, dir, len) =>
        {
            var stop = _shot.Query.Shot(new Vector3(from.X, from.Y, from.Z), new Vector3(dir.X, dir.Y, dir.Z), len, own);
            return new GrenadeStop(stop.Distance, new NVec3(stop.Normal.X, stop.Normal.Y, stop.Normal.Z));
        });
        _rewind.LookNow();
        r.Pos = new Vector3(step.Pos.X, step.Pos.Y, step.Pos.Z);
        r.Vel = new Vector3(step.Vel.X, step.Vel.Y, step.Vel.Z);
        if (r.Vel.LengthSquared() > 1e-4f) r.Dir = r.Vel.Normalized();
        r.Age += dt;
        if (!r.Fuse.Tick(dt)) return false;
        Explode(r, w, r.Pos + Vector3.Up * 0.1f, null); // a hair above where it lies: the blast centre is not in the floor
        return true;
    }

    /// <summary>The vehicle a rocket was fired from, while its owner still rides it; null otherwise (fired on foot, or the owner has got out or died).</summary>
    static ServerCar? CarOf(Rocket r) => r.FromCar is { Destroyed: false } car && car.Seat.SeatOf(r.Owner) >= 0 ? car : null;

    // ---- the blast ----

    /// <summary>
    /// Explosion: the direct-hit victim takes the full damage (and no splash on top); everyone else alive within the splash
    /// radius takes the linear falloff damage, the owner scaled by the self multiplier. A wall between the blast and a target stops it, and a covered seat's rider takes half through the hull (<see cref="Splash.Share"/>). Affected players get knockback. A rocket fired from an armed seat spares the shooter's fellow riders (riders
    /// of one vehicle never hit each other); the shooter takes their own blast as on foot, and so does the vehicle.
    /// </summary>
    void Explode(Rocket r, WeaponDef weapon, Vector3 pos, ushort? direct, ServerCar? directCar = null)
    {
        var w = ProjectileMath.WithBlast(weapon, r.Blast); // a gunship's rocket has its own blast (killstreaks.json); the rpg keeps weapons.json's
        _smoke.Explosions++;
        _carDamage.Rockets(pos, w, r.Owner, r.Weapon, directCar);
        _blasts.Blast(pos, w.Splash?.Radius ?? 0, BlastSources.OfRocket(weapon, r.Blast != null)); // the panes and the houses (a gunship rocket carves as one)
        _ctx.Broadcast(EventKind.Explosion, WeaponCodec.Encode(new ExplosionMsg(r.Id, r.Owner, r.Weapon, pos.ToNumerics(), w.Splash?.Radius ?? 0, w.Splash?.WaveSpeed ?? 0)));
        _impacts.Broadcast(pos.ToNumerics(), ImpactSize.Plume, body: direct != null && directCar == null, vehicle: directCar != null); // rockets throw up a plume (ticket #154)
        var own = CarOf(r);
        var targets = _targets.Live();
        foreach (var t in targets) HurtTarget(r, w, pos, direct, own, t);
        _targets.Return(targets);
    }

    /// <summary>What a blast at <paramref name="pos"/> does to one target: the damage by its distance behind walls, the throw if it dies, the knockback if it lives.</summary>
    void HurtTarget(Rocket r, WeaponDef w, Vector3 pos, ushort? direct, ServerCar? own, HitTarget t)
    {
        var centre = pos.ToNumerics();
        var isDirect = direct == t.Id;
        var dist = HitMath.DistanceToBody(centre, t);
        var self = t.Id == r.Owner;
        if (!self && own != null && own.Seat.SeatOf(t.Id) >= 0) return; // a fellow rider of the shooter's vehicle
        var dmg = isDirect ? w.Damage : ProjectileMath.SplashDamage(w, dist, self);
        if (dmg <= 0) return;
        var share = isDirect ? 1f : _splash.Share(centre, t); // walls, a bunker room's too, and a covered seat's hull
        if (share <= 0) return;
        dmg *= share;
        if (_ctx.Players.TryGetValue(t.Id, out var victimClient)) dmg = MathF.Max(1f, MathF.Round(dmg * victimClient.Perks.ExplosiveDamageTaken)); // Flak Jacket
        var k = ProjectileMath.Knockback(w, centre, HitMath.Centre(t), isDirect ? 1f : share * ProjectileMath.SplashFraction(w, dist));
        _deathPush.Record(t.Id, CorpseRules.ThrowFromBlast(k)); // a kill throws the body (the ragdoll)
        var res = _deal(r.Owner, r.Weapon, t.Id, dmg, false, false);
        _deathPush.Clear(t.Id, res);
        if (res == DamageResult.Ignored) return; // spawn protected: neither damage nor knockback
        if (isDirect) _smoke.DirectHits++; else if (self) _smoke.SelfHits++; else _smoke.SplashHits++;
        if (res != DamageResult.Killed && _ctx.Players.TryGetValue(t.Id, out var v) && k != NVec3.Zero) { v.Body.AddImpulse(new Vector3(k.X, k.Y, k.Z)); _smoke.Knocked++; }
    }
}
