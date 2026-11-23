using Godot;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Corpses;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server;

/// <summary>
/// Run-over from the rule table (ADR 0028): what a mover that does not stop for a body hurts is whatever its row says <see cref="Rule.RunOver"/> for the
/// body's <see cref="ObstacleClass"/>, over every live target, not a list each kind of victim opts into. A ground vehicle, a low aircraft and the Raid van
/// all come here; they differ only in their row (<see cref="MoverKind"/>) and in the footprint they sweep. What they hit is the victim's Body footprint scaled
/// to 85% (<see cref="RunOverFootprint"/>): an animal's Body box, any other body's capsule circle.
/// </summary>
public partial class NetServer
{
    /// <summary>What one sweep of a run-over mover needs: where it was and is, its box, how hard it hits, and who is credited.</summary>
    readonly record struct RunOverSweep(MoverKind Mover, NVec3 Prev, NVec3 Now, float Yaw, float HalfWidth, float HalfLength, NVec3 Forward, float Speed, float Damage, ushort Attacker, Dictionary<ushort, double> Cooldown);

    /// <summary>The row a vehicle is asked about: aircraft or ground vehicle (an aircraft counts only when it is low, which its caller checks).</summary>
    static MoverKind RunOverMoverOf(ServerCar car) => car.Flies ? MoverKind.Aircraft : MoverKind.GroundVehicle;

    /// <summary>
    /// Sweeps <paramref name="m"/>'s footprint over every live target whose class its row runs over, and hurts each one it touches (once per
    /// <see cref="RunOverCooldown"/>). <paramref name="spare"/>: targets that are not run over (the driver, who rides the vehicle; the van's own crew).
    /// A player aboard any vehicle is part of it and is never run over.
    /// </summary>
    void RunOverTargets(in RunOverSweep m, Func<ushort, bool>? spare = null)
    {
        var targets = LiveTargets();
        try
        {
            foreach (var t in targets)
            {
                if (ObstacleClassOf(t.Id) is not { } cls || CollisionRules.Of(m.Mover, cls) != Rule.RunOver) continue;
                if (spare != null && spare(t.Id)) continue;
                if (cls == ObstacleClass.Player && (!_byId.TryGetValue(t.Id, out var rider) || rider.Car != null)) continue;
                var animal = cls == ObstacleClass.Wildlife ? _wildlife?.AnimalOf(t.Id) : cls == ObstacleClass.Pet ? _pets?.AnimalOf(t.Id) : null;
                if ((cls is ObstacleClass.Wildlife or ObstacleClass.Pet) && (animal == null || animal.State == AnimalState.Carried || animal.IsPhoenix)) continue; // a carried pet is in somebody's hands; a Phoenix is shot, never run over
                var footprint = animal != null ? RunOverFootprint.OfBody(animal.Body, animal.Position.Y) : RunOverFootprint.OfCapsule(t.Feet);
                if (!footprint.Hits(m.Prev, m.Now, m.Yaw, m.HalfWidth, m.HalfLength)) continue;
                if (m.Cooldown.TryGetValue(t.Id, out var until) && _elapsed < until) continue;
                m.Cooldown[t.Id] = _elapsed + RunOverCooldown;
                RunOverOne(m, cls, t, animal);
            }
        }
        finally { ReturnTargets(targets); }
    }

    /// <summary>The class of a live target's hit id, null for the Bigfoot and what has no row (<see cref="CollisionRules.ClassOfHitId"/>).</summary>
    static ObstacleClass? ObstacleClassOf(ushort hitId) => CollisionRules.ClassOfHitId(hitId);

    /// <summary>
    /// What running over one body does. A body that can die as a ragdoll (a player, a dummy) is thrown ahead, and a player the hit does not kill is thrown ahead alive at the same velocity, knocked off their feet (PlayerFlung: tumbling, then getting up, #411); an animal is thrown and loses part of its hit points; any other NPC dies outright.
    /// A vehicle too slow to hurt (<see cref="RunOverSweep.Damage"/> 0, above <see cref="RunOver.ContactMinSpeed"/>) only moves the body out of its way: a player is pushed ahead, an animal is
    /// knocked over unhurt, the rest are left alone.
    /// </summary>
    void RunOverOne(in RunOverSweep m, ObstacleClass cls, HitTarget t, Animal? animal)
    {
        var id = t.Id;
        if (m.Damage <= 0)
        {
            var push = CorpseRules.ThrowFromCar(m.Forward, m.Speed);
            if (cls == ObstacleClass.Player && _byId.TryGetValue(id, out var pushed) && _match.Get(id)?.Life == PlayerLife.Alive) pushed.Body.AddImpulse(new Vector3(push.X, push.Y, push.Z));
            else if (cls is ObstacleClass.Wildlife or ObstacleClass.Pet && _match.Phase == MatchPhase.Running) animal!.Throw(push, m.Prev);
            return;
        }
        switch (cls)
        {
            case ObstacleClass.Player:
            case ObstacleClass.TargetDummy:
                var thrown = CorpseRules.ThrowFromCar(m.Forward, m.Speed);
                PushOnDeath(id, thrown, DeathFlight.RunOver); // a roadkill is thrown ahead of the vehicle (the ragdoll)
                var r = Deal(m.Attacker, SpecialWeapons.Vehicle, id, m.Damage, false, false, KillCause.Roadkill);
                ClearPush(id, r);
                // one who lives through it (hurt, or spared: a teammate) leaves the bumper the same way, as the slow push does: no vehicle drives through a standing body (#411)
                if (cls == ObstacleClass.Player && r != DamageResult.Killed && _byId.TryGetValue(id, out var hurt) && _match.Get(id)?.Life == PlayerLife.Alive)
                {
                    hurt.Body.AddImpulse(new Vector3(thrown.X, thrown.Y, thrown.Z));
                    hurt.LaunchGraceUntil = _elapsed + RunOver.ThrownGraceSeconds; hurt.LaunchedAt = _elapsed; // knocked off their feet, as by the Bigfoot's throw: no shooting in the air, no fall damage from this landing
                    BroadcastEvent(EventKind.PlayerFlung, PlayerFlungCodec.Encode(id)); // every client draws the body tumbling and getting up; the driver's has guessed it already
                }
                if (cls == ObstacleClass.Player && r != DamageResult.Ignored)
                {
                    _runOvers++;
                    if (r == DamageResult.Killed) _runOverKills++;
                    GD.Print($"server: {m.Mover} ran over #{id} at {m.Speed:F1} m/s for {m.Damage:F0}");
                }
                break;
            case ObstacleClass.Wildlife:
            case ObstacleClass.Pet:
                if (_match.Phase != MatchPhase.Running) break;
                animal!.Throw(CorpseRules.ThrowFromCar(m.Forward, m.Speed), m.Prev); // the animal is thrown ahead and takes a share of its hit points: the first time it gets up running, the second kills it
                Deal(m.Attacker, SpecialWeapons.Vehicle, id, animal.MaxHp * AnimalData.RunOverHpFraction, false, false, KillCause.Roadkill);
                break;
            case ObstacleClass.Gang when m.Mover == MoverKind.RaidVan: // a van has no player to credit: the member is simply killed (a roadkill kills outright)
                foreach (var raid in _raids!.Raids) raid.Gang.Hit(id, 0, SpecialWeapons.Vehicle, 1, KillCause.Roadkill);
                break;
            default: // gang, citizen, worker: the amount only has to be above 0, a roadkill kills
                Deal(m.Attacker, SpecialWeapons.Vehicle, id, 1, false, false, KillCause.Roadkill);
                break;
        }
    }
}
