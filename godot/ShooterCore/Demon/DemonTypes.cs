using System.Numerics;
using Drycastle.ShooterCore.Fsm;

namespace Drycastle.ShooterCore.Demon;

public enum DemonHitKind : byte { Bullet, Explosive, Vehicle }

public enum DemonHitResult : byte { Ignored, Hit, Killed }

public enum DemonPhase : byte
{
    /// <summary>Still, at its anchor: before the first player comes in, and after a wipe until the delay is over and a player is inside.</summary>
    Waiting,
    /// <summary>Resting on an anchor, facing its target.</summary>
    Dwelling,
    /// <summary>Crawling along a link.</summary>
    Crawling,
    /// <summary>The wind-up of a fireball (<see cref="DemonTelegraph"/> was sent).</summary>
    Telegraphing,
    /// <summary>Stopped on its anchor, roaring (aggressive roar).</summary>
    Roaring,
    /// <summary>Crouched on its anchor before a leap to the opposite wall.</summary>
    LeapWind,
    /// <summary>In the air: a scripted arc to the anchor across the nave.</summary>
    Leaping,
    /// <summary>Breathing fire at its target (the windup first, then the stream).</summary>
    Breathing,
    /// <summary>Crouched and facing its victim before the pounce; the victim is marked.</summary>
    PounceWind,
    /// <summary>In the air: a homing arc to the victim's feet.</summary>
    Pouncing,
    /// <summary>Standing over the pinned victim, biting.</summary>
    Pinning,
    Dead,
}

/// <summary>Whether the demon is up and fighting: it sleeps until the first player is inside after a wipe (<see cref="DemonBrain"/> keeps still, bar a move it began), and fights until everyone inside is dead.</summary>
public enum DemonWake : byte
{
    /// <summary>Before the first player comes in, and after a wipe until the rearm delay is over and a player is inside. Rests as <see cref="DemonPhase.Waiting"/>.</summary>
    Asleep,
    /// <summary>Fighting: its health only goes up with the players inside, the roar clock and the leap clock run.</summary>
    Awake,
}

/// <summary>Where a pounce stands for the brain. Its stages on the wire (<see cref="PounceStage"/>) are the edges: None to Marked is Wind, Marked to Pinned is Pinned, Pinned to None is Released, Marked or Pinned to None otherwise is Aborted.</summary>
public enum PounceHold : byte
{
    /// <summary>No victim marked.</summary>
    None,
    /// <summary>A victim is marked: the crouch (<see cref="DemonPhase.PounceWind"/>) and the flight (<see cref="DemonPhase.Pouncing"/>).</summary>
    Marked,
    /// <summary>The victim lies flat and is held (<see cref="DemonPhase.Pinning"/>).</summary>
    Pinned,
}

/// <summary>The specs of the demon's state machines (docs/state-machines.md is generated from them).</summary>
public static class DemonMachines
{
    /// <summary>What the demon does now. Transitions read off <see cref="DemonBrain"/>; a wipe and a death are the two that cut across.</summary>
    public static readonly StateMachineSpec<DemonPhase> PhaseSpec = StateMachineSpec<DemonPhase>.Define("Demon.Phase",
            "The demon's mind (Holy Mass): rests on an anchor, and from there roars, leaps, breathes fire, throws a fireball, pounces or crawls; a wipe sends it back to waiting, hits kill it.", DemonPhase.Waiting)
        .Allow(DemonPhase.Waiting, DemonPhase.Dwelling, DemonPhase.Roaring)
        .Allow(DemonPhase.Dwelling, DemonPhase.Roaring, DemonPhase.LeapWind, DemonPhase.Breathing, DemonPhase.Telegraphing, DemonPhase.PounceWind, DemonPhase.Crawling, DemonPhase.Waiting)
        .Allow(DemonPhase.Crawling, DemonPhase.Dwelling, DemonPhase.Waiting)
        .Allow(DemonPhase.Telegraphing, DemonPhase.Dwelling, DemonPhase.Waiting)
        .Allow(DemonPhase.Roaring, DemonPhase.Dwelling, DemonPhase.Waiting)
        .Allow(DemonPhase.Breathing, DemonPhase.Dwelling, DemonPhase.Waiting)
        .Allow(DemonPhase.LeapWind, DemonPhase.Leaping, DemonPhase.Waiting)
        .Allow(DemonPhase.Leaping, DemonPhase.Dwelling, DemonPhase.Waiting)
        .Allow(DemonPhase.PounceWind, DemonPhase.Pouncing, DemonPhase.Dwelling, DemonPhase.Waiting)
        .Allow(DemonPhase.Pouncing, DemonPhase.Pinning, DemonPhase.LeapWind)
        .Allow(DemonPhase.Pinning, DemonPhase.LeapWind, DemonPhase.Waiting)
        .FromAnywhere(DemonPhase.Dead)
        .Note(DemonPhase.Waiting, "Still at its anchor: nobody has come in yet, or a wipe happened and the rearm delay is not over. A wipe in a crawl, leap or pounce lets that finish first (it waits where it gets to).")
        .Note(DemonPhase.Dwelling, "Resting on an anchor facing its target: picks the next move when the dwell runs out (a due leap, flame and fireball come first).")
        .Note(DemonPhase.Telegraphing, "The wind-up of a fireball; the telegraph event warns the clients. Back to Dwelling with the dwell it had.")
        .Note(DemonPhase.Roaring, "Stopped on its anchor, roaring: when it wakes (if the data says so), at a health share, or on the roar clock.")
        .Note(DemonPhase.LeapWind, "Crouched before a leap; also the crouch of the leap away that ends every pounce.")
        .Note(DemonPhase.PounceWind, "Crouched and facing its victim, who is marked. Aborts (to Dwelling or Waiting) when the victim is gone.")
        .Note(DemonPhase.Pouncing, "A homing arc to the victim's feet. Lands as Pinning, or, when the victim is gone, leaps away (LeapWind).")
        .Note(DemonPhase.Pinning, "Standing over the pinned victim for the pin time, then leaps away. A wipe here leaves it standing on the floor until the rearm delay is over.")
        .Note(DemonPhase.Dead, "Hits reached the hits needed. Final: a dead demon ignores hits and steps; Reset starts a new one.")
        .Build();

    /// <summary>Asleep until a player is inside (after the rearm delay), awake until everyone inside is dead.</summary>
    public static readonly StateMachineSpec<DemonWake> WakeSpec = StateMachineSpec<DemonWake>.Define("Demon.Wake",
            "Whether the demon fights: it sleeps from the start of an event and after a wipe, wakes when a player is inside and the rearm delay is over.", DemonWake.Asleep)
        .Allow(DemonWake.Asleep, DemonWake.Awake)
        .Allow(DemonWake.Awake, DemonWake.Asleep)
        .Note(DemonWake.Asleep, "Resting phases show as Waiting; the health is back to full and the pounce, flame and leap clocks are cleared.")
        .Note(DemonWake.Awake, "Fighting: the hits needed grow with the players inside and never shrink; the fight clock, the roar clock and the leap clock run.")
        .Build();

    /// <summary>The victim of a pounce: marked, then held, then let go. The wire's <see cref="PounceStage"/> is the event of each edge.</summary>
    public static readonly StateMachineSpec<PounceHold> PounceSpec = StateMachineSpec<PounceHold>.Define("Demon.Pounce",
            "The victim of a pounce: marked in the crouch (Wind), held on landing (Pinned), then let go as Released after the pin or Aborted when they leave or die, or the demon dies or is wiped.", PounceHold.None)
        .Allow(PounceHold.None, PounceHold.Marked)
        .Allow(PounceHold.Marked, PounceHold.Pinned, PounceHold.None)
        .Allow(PounceHold.Pinned, PounceHold.None)
        .Note(PounceHold.None, "Nobody marked. Every way back here sets the pounce cooldown and remembers the victim (never the same one twice while another qualifies).")
        .Note(PounceHold.Marked, "The victim is marked from the crouch to the landing; the host holds them back from health changes. Gone before the landing: Aborted, the demon finishes its leap with nobody pinned.")
        .Note(PounceHold.Pinned, "Lying flat on the floor under the demon. Pin time over: Released (the host sets their health). Gone, or the demon dies: Aborted.")
        .Build();
}

/// <summary>A player in the church as the brain sees them: feet, alive, the velocity the fireball leads with, whether they stand on the ground and whether they sit at the Mass (the pounce takes neither a jumper nor a sitter).</summary>
public readonly record struct DemonPlayer(ushort Id, Vector3 Feet, bool Alive, Vector3 Velocity = default, bool Grounded = true, bool Seated = false);

/// <summary>Where the demon is and how it is turned: <see cref="Normal"/> is its up (the surface under it), <see cref="Forward"/> the way it faces along the surface. Godot axes.</summary>
public readonly record struct DemonPose(Vector3 Position, Vector3 Normal, Vector3 Forward);

public abstract record DemonEvent;
/// <summary>A hit that counted: its weight, the total taken and the total that kills it.</summary>
public sealed record DemonHurt(ushort Shooter, int Weight, int HitsTaken, int HitsNeeded) : DemonEvent;
/// <summary>A fireball is winding up at <see cref="Target"/>: the warning for clients.</summary>
public sealed record DemonTelegraph(ushort Target, Vector3 Origin, float Seconds) : DemonEvent;
public sealed record DemonFireballLaunched(Fireball Ball) : DemonEvent;
/// <summary>It stopped to roar.</summary>
public sealed record DemonRoared : DemonEvent;
/// <summary>It began to breathe fire at <see cref="Target"/> (the windup starts now; clients aim the stream at that player).</summary>
public sealed record DemonFlameStarted(ushort Target) : DemonEvent;
/// <summary>A flame tick reached this player (in the cone, in sight): the host hurts and ignites them.</summary>
public sealed record DemonFlameHit(ushort Player) : DemonEvent;
/// <summary>
/// A pounce changed stage for <see cref="Victim"/> (<see cref="PounceStage"/>): Wind, the victim is marked; Pinned, they land flat at <see cref="Feet"/> (the floor) lying with their head towards
/// (sin <see cref="Yaw"/>, cos <see cref="Yaw"/>) in x and z; Released, the pin is over and the host sets their health; Aborted, it ended without a release (they left, died or the demon died).
/// </summary>
public sealed record DemonPounced(ushort Victim, PounceStage Stage, Vector3 Feet = default, float Yaw = 0) : DemonEvent;
/// <summary>Every player inside died: it has healed to full and waits.</summary>
public sealed record DemonWiped : DemonEvent;
/// <summary>It is dead. <see cref="Credited"/> are the players who hit it at least once, in id order: they get the devil's curse (<see cref="CurseRules.Grant"/>).</summary>
public sealed record DemonDied(IReadOnlyList<ushort> Credited) : DemonEvent;
