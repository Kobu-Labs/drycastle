using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Events;

/// <summary>
/// Where the Bigfoot is in its own life: fighting, healed and waiting after a wipe, or dead. Orthogonal to <see cref="BigfootAct"/> (what it does this
/// moment): a waiting Bigfoot is always Idle, a dead one always <see cref="BigfootAct.Dead"/>. The one-way counters (phase, enrage, the hit count it needs)
/// are not states: they only ever go up while it fights and start over with a wipe.
/// </summary>
public enum BigfootLife : byte { Fighting, Waiting, Dead }

/// <summary>
/// How far the server's Bigfoot event has come (NetServer.Bigfoot.cs): none, a living Bigfoot, or the one that fell and lies out its death clip. After
/// <see cref="Dying"/> the body becomes the reward corpse (a separate thing the server does not track) and the stage is <see cref="None"/> again.
/// </summary>
public enum BigfootStage : byte { None, Alive, Dying }

/// <summary>The specs of the Bigfoot's act and life (see <see cref="Bigfoot"/>).</summary>
public static class BigfootSpecs
{
    static readonly BigfootAct[] AliveActs =
    {
        BigfootAct.Idle, BigfootAct.Walk, BigfootAct.Run, BigfootAct.TrunkWindup, BigfootAct.StompWindup, BigfootAct.Stomp,
        BigfootAct.VehicleWindup, BigfootAct.GrabReach, BigfootAct.Hold, BigfootAct.Launch, BigfootAct.Roar,
    };

    /// <summary>The replicated act (the low four bits of the state byte). Decide picks the next attack, heaviest first: vehicle, grab, stomp, trunk, else chase.</summary>
    public static readonly StateMachineSpec<BigfootAct> Act = StateMachineSpec<BigfootAct>
        .Define("Bigfoot.Act", "What the Bigfoot is doing this moment: chasing, winding up or landing one attack, or holding a player; it ends dead.", BigfootAct.Idle)
        // Decide runs only from these three; it moves, or starts the heaviest attack that applies
        .Allow(BigfootAct.Idle, BigfootAct.Walk, BigfootAct.Run, BigfootAct.TrunkWindup, BigfootAct.StompWindup, BigfootAct.VehicleWindup, BigfootAct.GrabReach, BigfootAct.Roar)
        .Allow(BigfootAct.Walk, BigfootAct.Idle, BigfootAct.Run, BigfootAct.TrunkWindup, BigfootAct.StompWindup, BigfootAct.VehicleWindup, BigfootAct.GrabReach, BigfootAct.Roar)
        .Allow(BigfootAct.Run, BigfootAct.Idle, BigfootAct.Walk, BigfootAct.TrunkWindup, BigfootAct.StompWindup, BigfootAct.VehicleWindup, BigfootAct.GrabReach, BigfootAct.Roar)
        // windup -> the blow -> back to the chase
        .Allow(BigfootAct.StompWindup, BigfootAct.Stomp)
        .Allow(BigfootAct.VehicleWindup, BigfootAct.Launch)
        .Allow(BigfootAct.GrabReach, BigfootAct.Hold)
        .Allow(BigfootAct.Hold, BigfootAct.Launch)
        // every timed act ends at rest (the trunk's release too); a wipe sends any of them there at once, the two windups only that way
        .AllowAll(AliveActs.Where(a => a != BigfootAct.Idle), BigfootAct.Idle)
        .FromAnywhere(BigfootAct.Dead)
        .Note(BigfootAct.Idle, "Standing: nobody in reach, a stop distance reached, or waiting after a wipe. Every ended act comes back here.")
        .Note(BigfootAct.TrunkWindup, "Log on the shoulder, turning to its target; the log flies when the wind-up ends (back to Idle).")
        .Note(BigfootAct.StompWindup, "The telegraph; the shockwave starts when it ends. A wipe may cut it short.")
        .Note(BigfootAct.Stomp, "The shockwave runs (BigfootRules.StompSeconds of pose).")
        .Note(BigfootAct.VehicleWindup, "The vehicle is lifted overhead; it is thrown when the wind-up ends. A wipe or death drops it.")
        .Note(BigfootAct.GrabReach, "Reaching for the nearest player on foot; holds them if still in reach, else back to Idle.")
        .Note(BigfootAct.Hold, "Holds one player in its hand until the hold time runs out (launch) or the player dies or leaves (Idle).")
        .Note(BigfootAct.Launch, "The short swing after a throw or a launch.")
        .Note(BigfootAct.Roar, "A phase began or it enraged: it stands and roars (BigfootRules.RoarSeconds), facing its target. Owed while it is busy or has an attack to make, paid the first tick it would only chase.")
        .Note(BigfootAct.Dead, "Reachable from every act (a killing hit, or an admin ending the event).")
        .Build();

    /// <summary>The Bigfoot's life: the wipe heals it and puts it to sleep, a player coming near (or a shot) wakes it, death is final.</summary>
    public static readonly StateMachineSpec<BigfootLife> Life = StateMachineSpec<BigfootLife>
        .Define("Bigfoot.Life", "Fighting, healed and waiting after a wipe, or dead.", BigfootLife.Fighting)
        .Allow(BigfootLife.Fighting, BigfootLife.Waiting)
        .Allow(BigfootLife.Waiting, BigfootLife.Fighting)
        .FromAnywhere(BigfootLife.Dead)
        .Note(BigfootLife.Waiting, "Wiped: healed to full, standing where it was, until a living player comes within the wake radius or a shot lands.")
        .Note(BigfootLife.Dead, "Reachable from Fighting and Waiting (a shot can also kill a waiting one).")
        .Build();
}

/// <summary>
/// The server's lifecycle of one Bigfoot event, kept beside <see cref="EventBook"/>: the book says whether an event runs (it stops running the moment the
/// Bigfoot dies, when the win is booked), this says whether the body is still lying there. Both must agree before a new Bigfoot may start
/// (<see cref="WhyNotStart"/>), so an admin always gets the one reason that applies.
/// </summary>
public sealed class BigfootLifecycle
{
    /// <summary>None to Alive (it appeared); Alive to Dying (it fell: the win is booked) or back to None (despawned: no reward); Dying to None (the body became the reward).</summary>
    public static readonly StateMachineSpec<BigfootStage> Spec = StateMachineSpec<BigfootStage>
        .Define("Bigfoot.Event", "The server's Bigfoot event: none, a living Bigfoot, or the dead one lying out its death clip before it becomes the reward.", BigfootStage.None)
        .Allow(BigfootStage.None, BigfootStage.Alive)
        .Allow(BigfootStage.Alive, BigfootStage.Dying, BigfootStage.None)
        .Allow(BigfootStage.Dying, BigfootStage.None)
        .Note(BigfootStage.Dying, "The event is already won (the book has no running event) but a new Bigfoot may not start until the body is gone.")
        .Build();

    readonly StateMachine<BigfootStage> _fsm = new(Spec);

    public BigfootStage Stage => _fsm.Current;

    /// <summary>It was spawned.</summary>
    public void Appeared() => _fsm.Enter(BigfootStage.Alive);

    /// <summary>It died: the event is won, the body lies.</summary>
    public void Fell() => _fsm.Enter(BigfootStage.Dying);

    /// <summary>The entity left the world. True when it had died first (its reward is due), false when the event was simply over.</summary>
    public bool Gone()
    {
        var won = _fsm.Is(BigfootStage.Dying);
        _fsm.Enter(BigfootStage.None);
        return won;
    }

    /// <summary>The refusal an admin gets for "/event bigfoot" now (one sentence, saying why), or null when one may start.</summary>
    public string? WhyNotStart(EventBook events)
    {
        if (events.WhyNotStart(EventBook.Bigfoot) is { } running) return running;
        if (Stage == BigfootStage.Dying) return "The last Bigfoot is still lying where it fell. Try again in a few seconds.";
        if (Stage == BigfootStage.Alive) return "A Bigfoot is already alive."; // unreachable while the book and the stage agree; a guard, not a path
        return null;
    }
}
