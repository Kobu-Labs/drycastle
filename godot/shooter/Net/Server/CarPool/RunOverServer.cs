using Godot;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.Shooter.Net.Server.CarPool;

/// <summary>
/// Run-over from the rule table (ADR 0028): what a mover that does not stop for a body hurts is whatever its row says <see cref="Rule.RunOver"/> for the
/// body's <see cref="ObstacleClass"/>, over every live target, not a list each kind of victim opts into. A ground vehicle, a low aircraft and the Raid van
/// all come here; they differ only in their row (<see cref="MoverKind"/>) and in the footprint they sweep. What they hit is the victim's Body footprint scaled
/// to 85% (<see cref="RunOverFootprint"/>): an animal's Body box, any other body's capsule circle.
/// </summary>
internal sealed class RunOverServer
{
    /// <summary>A body is hurt by the same mover at most this often (seconds).</summary>
    public const float Cooldown = 0.8f;

    readonly ServerContext _ctx;
    readonly Fleet _fleet;
    readonly TargetBook _targets;
    readonly DealDamage _deal;
    readonly DeathPush _deathPush;
    readonly WildlifeKeeper _wild;
    readonly PetKeeper _pets;
    readonly AnimalsFile _animalData;
    readonly VehiclesFile _vehicles;
    readonly SmokeStats _smoke;
    readonly Action<ushort> _roadkillCrew;

    /// <param name="roadkillCrew">A Raid van ran over a Gang member: killed outright, with no player to credit.</param>
    public RunOverServer(ServerContext ctx, Fleet fleet, TargetBook targets, DealDamage deal, DeathPush deathPush, WildlifeKeeper wild, PetKeeper pets, AnimalsFile animalData, VehiclesFile vehicles, SmokeStats smoke, Action<ushort> roadkillCrew)
    {
        _ctx = ctx; _fleet = fleet; _targets = targets; _deal = deal; _deathPush = deathPush; _wild = wild; _pets = pets; _animalData = animalData; _vehicles = vehicles; _smoke = smoke; _roadkillCrew = roadkillCrew;
    }

    /// <summary>The row a vehicle is asked about: aircraft or ground vehicle (an aircraft counts only when it is low, which its caller checks).</summary>
    static MoverKind MoverOf(ServerCar car) => car.Flies ? MoverKind.Aircraft : MoverKind.GroundVehicle;

    /// <summary>
    /// A vehicle that moves (<see cref="RunOver.Contacts"/>), driven, on land or skimming it: its row of the rule table runs over every live target it sweeps (<see cref="Sweep"/>).
    /// Below the speed that hurts the damage is 0: the body is only pushed out of its way (<see cref="Hit"/>).
    /// </summary>
    public void Vehicle(ServerCar car, ServerClient driver)
    {
        if (!RunOver.Contacts(car.State.Speed)) return;
        var dmg = RunOver.Damage(_vehicles, car.State.Speed, _vehicles.RunOverScale(car.Def));
        Sweep(new RunOverSweep(MoverOf(car), car.PrevPos.ToNumerics(), car.GlobalPosition.ToNumerics(), car.State.Yaw, car.HalfWidth, car.HalfLength, car.Forward.ToNumerics(), car.State.Speed, dmg, driver.Id, car.HitCooldown));
    }

    /// <summary>
    /// The road kill of a trailer's box (the rule table's ground vehicle row, as <see cref="Vehicle"/>): swept from where it stood at the end of the last tick, hurt by its axle speed and its
    /// type's run-over mass, credited to whoever drives the tractor (nobody when it is loose or the tractor has no driver). Approximation: the swing of its tail in a turn is in the sweep's path, not in the speed.
    /// </summary>
    public void Trailer(ServerCar car, float speed)
    {
        var credit = car.Tractor is { } tractor ? _fleet.OccupantAt(tractor, 0)?.Id ?? (ushort)0 : (ushort)0;
        var dmg = RunOver.Damage(_vehicles, speed, _vehicles.RunOverScale(car.Def));
        Sweep(new RunOverSweep(MoverKind.GroundVehicle, car.PrevPos.ToNumerics(), car.GlobalPosition.ToNumerics(), car.State.Yaw, car.HalfWidth, car.HalfLength, car.Forward.ToNumerics(), speed, dmg, credit, car.HitCooldown));
    }

    /// <summary>
    /// Sweeps <paramref name="m"/>'s footprint over every live target whose class its row runs over, and hurts each one it touches (once per
    /// <see cref="Cooldown"/>). <paramref name="spare"/>: targets that are not run over (the driver, who rides the vehicle; the van's own crew).
    /// A player aboard any vehicle is part of it and is never run over.
    /// </summary>
    public void Sweep(in RunOverSweep m, Func<ushort, bool>? spare = null)
    {
        var targets = _targets.Live();
        try
        {
            foreach (var t in targets)
            {
                if (CollisionRules.ClassOfHitId(t.Id) is not { } cls || CollisionRules.Of(m.Mover, cls) != Rule.RunOver) continue;
                if (spare != null && spare(t.Id)) continue;
                if (cls == ObstacleClass.Player && (!_ctx.Players.TryGetValue(t.Id, out var rider) || rider.Car != null)) continue;
                var animal = cls == ObstacleClass.Wildlife ? _wild.AnimalOf(t.Id) : cls == ObstacleClass.Pet ? _pets.AnimalOf(t.Id) : null;
                if ((cls is ObstacleClass.Wildlife or ObstacleClass.Pet) && (animal == null || animal.State == AnimalState.Carried || animal.IsPhoenix)) continue; // a carried pet is in somebody's hands; a Phoenix is shot, never run over
                var footprint = animal != null ? RunOverFootprint.OfBody(animal.Body, animal.Position.Y) : RunOverFootprint.OfCapsule(t.Feet);
                if (!footprint.Hits(m.Prev, m.Now, m.Yaw, m.HalfWidth, m.HalfLength)) continue;
                if (m.Cooldown.TryGetValue(t.Id, out var until) && _ctx.Now < until) continue;
                m.Cooldown[t.Id] = _ctx.Now + Cooldown;
                Hit(m, cls, t, animal);
            }
        }
        finally { _targets.Return(targets); }
    }

    /// <summary>
    /// What running over one body does. A body that can die as a ragdoll (a player, a dummy) is thrown ahead, and a player the hit does not kill is thrown ahead alive at the same velocity, knocked off their feet (PlayerFlung: tumbling, then getting up, #411); an animal is thrown and loses part of its hit points; any other NPC dies outright.
    /// A vehicle too slow to hurt (<see cref="RunOverSweep.Damage"/> 0, above <see cref="RunOver.ContactMinSpeed"/>) only moves the body out of its way: a player is pushed ahead, an animal is
    /// knocked over unhurt, the rest are left alone.
    /// </summary>
    void Hit(in RunOverSweep m, ObstacleClass cls, HitTarget t, Animal? animal)
    {
        var id = t.Id;
        if (m.Damage <= 0) { Push(m, cls, id, animal); return; }
        switch (cls)
        {
            case ObstacleClass.Player:
            case ObstacleClass.TargetDummy:
                Flatten(m, cls, id);
                break;
            case ObstacleClass.Wildlife:
            case ObstacleClass.Pet:
                if (_ctx.Match.Phase != MatchPhase.Running) break;
                animal!.Throw(CorpseRules.ThrowFromCar(m.Forward, m.Speed), m.Prev); // the animal is thrown ahead and takes a share of its hit points: the first time it gets up running, the second kills it
                _deal(m.Attacker, SpecialWeapons.Vehicle, id, animal.MaxHp * _animalData.RunOverHpFraction, false, false, KillCause.Roadkill);
                break;
            case ObstacleClass.Gang when m.Mover == MoverKind.RaidVan: // a van has no player to credit: the member is simply killed (a roadkill kills outright)
                _roadkillCrew(id);
                break;
            default: // gang, citizen, worker: the amount only has to be above 0, a roadkill kills
                _deal(m.Attacker, SpecialWeapons.Vehicle, id, 1, false, false, KillCause.Roadkill);
                break;
        }
    }

    /// <summary>A vehicle too slow to hurt: a player is pushed ahead, an animal knocked over unhurt, the rest are left alone.</summary>
    void Push(in RunOverSweep m, ObstacleClass cls, ushort id, Animal? animal)
    {
        var push = CorpseRules.ThrowFromCar(m.Forward, m.Speed);
        if (cls == ObstacleClass.Player && _ctx.Players.TryGetValue(id, out var pushed) && _ctx.IsAlive(id)) pushed.Body.AddImpulse(new Vector3(push.X, push.Y, push.Z));
        else if (cls is ObstacleClass.Wildlife or ObstacleClass.Pet && _ctx.Match.Phase == MatchPhase.Running) animal!.Throw(push, m.Prev);
    }

    /// <summary>A player or a dummy run over: the roadkill, the body thrown ahead (the ragdoll), and a player who lives through it leaves the bumper the same way, tumbling.</summary>
    void Flatten(in RunOverSweep m, ObstacleClass cls, ushort id)
    {
        var thrown = CorpseRules.ThrowFromCar(m.Forward, m.Speed);
        _deathPush.Record(id, thrown, DeathFlight.RunOver); // a roadkill is thrown ahead of the vehicle (the ragdoll)
        var r = _deal(m.Attacker, SpecialWeapons.Vehicle, id, m.Damage, false, false, KillCause.Roadkill);
        _deathPush.Clear(id, r);
        // one who lives through it (hurt, or spared: a teammate) leaves the bumper the same way, as the slow push does: no vehicle drives through a standing body (#411)
        if (cls == ObstacleClass.Player && r != DamageResult.Killed && _ctx.Players.TryGetValue(id, out var hurt) && _ctx.IsAlive(id))
        {
            hurt.Body.AddImpulse(new Vector3(thrown.X, thrown.Y, thrown.Z));
            hurt.LaunchGraceUntil = _ctx.Now + RunOver.ThrownGraceSeconds; hurt.LaunchedAt = _ctx.Now; // knocked off their feet, as by the Bigfoot's throw: no shooting in the air, no fall damage from this landing
            _ctx.Broadcast(EventKind.PlayerFlung, PlayerFlungCodec.Encode(id)); // every client draws the body tumbling and getting up; the driver's has guessed it already
        }
        if (cls != ObstacleClass.Player || r == DamageResult.Ignored) return;
        _smoke.RunOvers++;
        if (r == DamageResult.Killed) _smoke.RunOverKills++;
        GD.Print($"server: {m.Mover} ran over #{id} at {m.Speed:F1} m/s for {m.Damage:F0}");
    }
}
