using Godot;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Net.Server.Fauna;
using Drycastle.Shooter.Net.Server.Panes;
using Drycastle.Shooter.Net.Server.Rewind;
using Drycastle.Shooter.Net.Server.Scenery;
using Drycastle.Shooter.Net.Server.Sentries;
using Drycastle.Shooter.Net.Server.Streaks;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Diagnostics;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Shooting;

/// <summary>
/// One shot or swing resolved against the targets as the shooter saw them and the walls: hitscan (with bullet drop), projectile (handed to the <see cref="RocketSim"/>, or to the Lock-on launcher) and melee; a shotgun's fan is the
/// <see cref="PelletFan"/>'s. The damage goes through <see cref="DealDamage"/>, so its kills are ordinary kills with that weapon. Hot path: one call per shot, no list per shot beyond the pooled targets.
/// </summary>
internal sealed class ShotServer
{
    /// <summary>The farthest a shot goes when its weapon has no range of its own.</summary>
    public const float MaxRange = 300f;

    readonly ServerContext _ctx;
    readonly PoseRewind _rewind;
    readonly ShotWorld _shot;
    readonly GlassServer _glass;
    readonly CarDamage _carDamage;
    readonly TargetBook _targets;
    readonly DealDamage _deal;
    readonly SceneryServer _scenery;
    readonly SentryWorld _sentries;
    readonly WildlifeServer _wildlife;
    readonly LockOnServer _lockOn;
    readonly RocketSim _rockets;
    readonly PelletFan _pellets;
    readonly ImpactFx _impacts;
    readonly SmokeStats _smoke;
    readonly VehiclesFile _vehicles;
    readonly Random _rng;
    readonly float _knifeDamage;
    readonly bool _weaponsSmoke;
    readonly Action<NVec3, NVec3, float> _baleShot;
    readonly Action<ServerClient, Vector3, Vector3, float, ushort?> _perfShot;

    /// <param name="knifeDamage">What a knife does to a sentry turret (turret.json).</param>
    /// <param name="baleShot">A shot along a line stopped after a length: any bale it passes catches fire (#331).</param>
    /// <param name="perfShot">The lag-compensation log of a shot (a perf run only).</param>
    public ShotServer(ServerContext ctx, PoseRewind rewind, ShotWorld shot, GlassServer glass, CarDamage carDamage, TargetBook targets, DealDamage deal, SceneryServer scenery, SentryWorld sentries,
        WildlifeServer wildlife, LockOnServer lockOn, RocketSim rockets, PelletFan pellets, ImpactFx impacts, SmokeStats smoke, VehiclesFile vehicles, Random rng, float knifeDamage, bool weaponsSmoke,
        Action<NVec3, NVec3, float> baleShot, Action<ServerClient, Vector3, Vector3, float, ushort?> perfShot)
    {
        _ctx = ctx; _rewind = rewind; _shot = shot; _glass = glass; _carDamage = carDamage; _targets = targets; _deal = deal; _scenery = scenery; _sentries = sentries; _wildlife = wildlife;
        _lockOn = lockOn; _rockets = rockets; _pellets = pellets; _impacts = impacts; _smoke = smoke; _vehicles = vehicles; _rng = rng; _knifeDamage = knifeDamage; _weaponsSmoke = weaponsSmoke;
        _baleShot = baleShot; _perfShot = perfShot;
    }

    /// <summary>
    /// Resolves one shot or swing against the current positions (no lag compensation) and walls, applies damage, sends events.
    /// <paramref name="seatCar"/> is the vehicle the shooter rides in an armed seat (ADR 0011), null on foot. From a seat the shot leaves the
    /// seated eye, its aim is held inside the seat's arc, the vehicle's speed widens the spread as the shooter's own does on foot, the vehicle's
    /// own hull is not in the way, and its riders are not targets: riders of one vehicle never hit each other. Everything else is the same shot,
    /// so its kills are ordinary kills with that weapon.
    /// </summary>
    public void Fire(ServerClient c, in NetInput i, FireOutcome shot, ServerCar? seatCar = null)
    {
        List<HitTarget>? pooled = null;
        try { Resolve(c, i, shot, seatCar, ref pooled); }
        finally { if (pooled != null) _targets.Return(pooled); } // the shot is resolved: its target list goes back for the next one (it was a new list of every target per shot)
    }

    void Resolve(ServerClient c, in NetInput i, FireOutcome shot, ServerCar? seatCar, ref List<HitTarget>? pooled)
    {
        var w = shot.Weapon;
        var back = _rewind.BackOf(c); // the vehicles this shot rewinds (B-02): the same view tick as the players (ADR 0013)
        _rewind.Look(back, c.LifeStart);
        var seat = seatCar?.Def.Seats[c.CarSeat];
        var feet = seatCar != null ? CarPose.SeatPos(seatCar, c.CarSeat) : c.Body.GlobalPosition;
        var eye = feet + Vector3.Up * (seat != null ? SeatAimRules.EyeHeight(seat) : MovementStep.EyeHeight(c.Body.State.Stance));
        var yaw = Quant.Yaw(i.Yaw); var pitch = Quant.Pitch(i.Pitch);
        if (seatCar != null)
        {
            var (carYaw, carPitch, carRoll) = Fleet.AttitudeOf(seatCar);
            var held = SeatAimRules.Aim(seat!.Aim, carYaw, carPitch, carRoll, yaw, pitch);
            yaw = held.ViewYaw; pitch = held.ViewPitch;
        }
        var targetsT = Prof.Begin(Prof.S.ShotTargets);
        var all = _shot.TargetsFor(c); // players where the shooter's screen showed them (ADR 0013)
        pooled = all;
        var targets = seatCar != null ? RiderRules.NotAboard(all, seatCar.Seat) : all; // (NotAboard copies its own)
        Prof.End(Prof.S.ShotTargets, targetsT);
        _smoke.ShotsFired++;
        var aiming = (i.Buttons & (ushort)MoveButtons.Aim) != 0;
        var body = c.Body.State;
        float Spread(float degrees) => seat != null ? SeatAimRules.ShotSpread(degrees, w, aiming, seatCar!.State.Speed, seat) : HitMath.ShotSpread(degrees, w, body, aiming);
        var stance = seat != null ? Stance.Stand : body.Stance; // a seat steadies the recoil no more than standing does
        var aim = new ShotAim(seatCar, eye, feet, yaw, pitch, targets, aiming, stance, Spread);
        if (w.Kind == WeaponKind.Hitscan && w.Pellets > 1) { var pelT = Prof.Begin(Prof.S.FirePellets); _pellets.Fire(c, eye, yaw, pitch, w, (byte)shot.WeaponIndex, Spread(w.PelletSpread), targets, seatCar); Prof.End(Prof.S.FirePellets, pelT); }
        else if (w.Kind == WeaponKind.Hitscan) FireHitscan(c, shot, aim);
        else if (w.Kind == WeaponKind.Projectile) FireProjectile(c, shot, aim, back);
        else if (w.Kind == WeaponKind.Melee) FireMelee(c, shot, aim);
        _rewind.LookNow(); // the blast and everything after the shot see the vehicles where they stand
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
        var range = w.Range > 0 ? w.Range : MaxRange;
        // a weapon with bullet drop is resolved along its curve, one straight piece at a time (BulletDrop); any other, and a bot's, is the one piece of the whole range
        var drops = BulletDrop.HasDrop(w) && !c.IsBot;
        var zero = drops ? c.Zero.Of(shot.WeaponIndex, w) : 0f;
        var from = eye; var along = dir; var len = range; var before = 0f; // the piece: its start, direction, length, and the metres of line of sight before it
        List<GlassCross>? crossed; float wall; ServerCar? shotCar; RayHit? hit;
        while (true)
        {
            if (drops)
            {
                var a = BulletDrop.Point(eye.ToNumerics(), d, w, zero, before);
                var b = BulletDrop.Point(eye.ToNumerics(), d, w, zero, MathF.Min(range, before + BulletDrop.SegmentM));
                var piece = b - a;
                len = piece.Length();
                from = new Vector3(a.X, a.Y, a.Z);
                along = len > 1e-4f ? new Vector3(piece.X, piece.Y, piece.Z) / len : dir;
            }
            crossed = null;
            var partT = Prof.Begin(Prof.S.FireBulletHit);
            wall = _shot.BulletHit(from, along, len, out shotCar, ref crossed, seatCar);
            Prof.End(Prof.S.FireBulletHit, partT);
            partT = Prof.Begin(Prof.S.FireRaycast);
            var (exposedGunner, pastHull) = _shot.ExposedGunnerBehind(shotCar, from, along, len, targets, skip: seatCar);
            hit = GunnerCover.Raycast(from.ToNumerics(), along.ToNumerics(), len, wall, targets, c.Id, exposedGunner, pastHull); // a flak gunner in the open is hit through his hull
            Prof.End(Prof.S.FireRaycast, partT);
            partT = Prof.Begin(Prof.S.FireMissCheck);
            if (_weaponsSmoke && hit == null && HitMath.Raycast(from.ToNumerics(), along.ToNumerics(), len, float.PositiveInfinity, targets, c.Id) != null) _smoke.WallBlocked++; // only the weapons smoke reads the count: a second ray over every target on each miss otherwise
            Prof.End(Prof.S.FireMissCheck, partT);
            if (!drops || hit != null || wall <= len || before + BulletDrop.SegmentM >= range) break;
            var passed = before;
            _glass.DamageCrossed(crossed, float.PositiveInfinity, at => HitMath.HitscanDamage(w, passed + at, false)); // the panes of a piece the bullet flew through
            _scenery.Shot(from.ToNumerics(), along.ToNumerics(), len); // and the lamps it passed
            before += BulletDrop.SegmentM;
        }
        _perfShot(c, eye, dir, range, hit?.Id);
        var end = from + along * (hit?.Distance ?? Math.Min(wall, len));
        _ctx.Broadcast(EventKind.Shot, WeaponCodec.Encode(new ShotMsg(c.Id, (byte)shot.WeaponIndex, eye.ToNumerics(), end.ToNumerics())));
        _baleShot(from.ToNumerics(), along.ToNumerics(), hit?.Distance ?? Math.Min(wall, len)); // any bale the bullet passes catches fire (#331)
        _scenery.Shot(from.ToNumerics(), along.ToNumerics(), hit?.Distance ?? Math.Min(wall, len)); // and any pole lamp it passes breaks (ADR 0037), any sign plate bends (ADR 0039)
        if (hit is { } h)
        {
            // a wild animal under any hitscan weapon: where the bullet entered its body, taken before the hit can remove it (the hunter's log keeps it; a sniper's shooter is also shown it)
            var hunt = WildlifeRules.IsHitId(h.Id) ? _wildlife.HuntShotOf(targets, h.Id, (from + along * h.Distance).ToNumerics(), before + h.Distance, (byte)shot.WeaponIndex, h.ShapeHead) : null;
            var dealT = Prof.Begin(Prof.S.FireDeal);
            var dealt = _deal(c.Id, (byte)shot.WeaponIndex, h.Id, HitMath.HitscanDamage(w, before + h.Distance, h.Head), h.Head, false, shot: hunt, shotDistance: before + h.Distance);
            Prof.End(Prof.S.FireDeal, dealT);
            if (dealt == DamageResult.Killed && hunt is { } shown && w.Category == WeaponCategory.Sniper) _ctx.SendEvent(c, EventKind.HuntShot, HuntShot.Encode(shown));
        }
        else if (shotCar != null && wall <= len)
            _carDamage.Damage(shotCar, HitMath.HitscanDamage(w, before + wall, false) * _vehicles.BulletDamageMultiplier, c.Id, (byte)shot.WeaponIndex, 0);
        else if (shotCar == null && wall <= len) _impacts.BulletPuff((from + along * wall).ToNumerics()); // dust where a bullet meets the ground or a wall (ticket #154)
        if (hit == null && wall <= len) _glass.Shot(shotCar, from, along, wall, HitMath.HitscanDamage(w, before + wall, false)); // the pane the bullet crossed where it stopped (ADR 0007)
        _glass.DamageCrossed(crossed, hit?.Distance ?? float.PositiveInfinity, at => HitMath.HitscanDamage(w, before + at, false));
    }

    /// <summary>A projectile weapon: a Lock-on launcher's missile homes on its Aircraft (ADR 0031); a rocket or a grenade is a server-owned entity that rewinds vehicles by the shooter's lag at each step (B-02).</summary>
    void FireProjectile(ServerClient c, FireOutcome shot, in ShotAim aim, int back)
    {
        var w = shot.Weapon;
        var (seatCar, eye, _, yaw, pitch, _, _, _, _) = aim;
        var dir = HitMath.Forward(yaw, pitch);
        if (w.Lock != null && _lockOn.Fire(c, shot, eye)) return; // a Lock-on launcher's missile homes on its Aircraft (ADR 0031)
        _rockets.Launch(c, shot, eye, dir, seatCar, back);
    }

    /// <summary>A melee swing: the target in reach, unless a wall stands between the attacker and the victim's chest; else a sentry turret in reach.</summary>
    void FireMelee(ServerClient c, FireOutcome shot, in ShotAim aim)
    {
        var w = shot.Weapon;
        var (seatCar, eye, feet, yaw, _, targets, _, _, _) = aim;
        var fwd = HitMath.Forward(yaw, 0);
        var hit = HitMath.Melee(feet.ToNumerics(), yaw, w.Reach, targets, c.Id);
        if (hit is { } h && _ctx.Players.ContainsKey(h.Id) && ShotWorld.FeetIn(targets, h.Id) is { } victimFeet)
        {
            // a wall between the attacker and the victim's chest blocks the stab (the chest where the victim was seen, ADR 0013)
            var chest = victimFeet + Vector3.Up * 1.0f;
            var to = chest - eye;
            if (_shot.WorldHit(eye, to.Normalized(), to.Length(), out _, seatCar) < to.Length() - 0.05f) { _smoke.WallBlocked++; hit = null; }
        }
        _ctx.Broadcast(EventKind.Shot, WeaponCodec.Encode(new ShotMsg(c.Id, (byte)shot.WeaponIndex, eye.ToNumerics(), eye.ToNumerics() + fwd * w.Reach)));
        if (hit is { } m) _deal(c.Id, (byte)shot.WeaponIndex, m.Id, HitMath.MeleeDamage(w, m.Backstab), false, m.Backstab);
        else if (_sentries.Melee(feet.ToNumerics(), yaw, w.Reach) is { } turret) _ctx.Match.Entities.Damage(turret, _knifeDamage * c.Perks.VehicleDamageDealt, c.Id);
    }

}
