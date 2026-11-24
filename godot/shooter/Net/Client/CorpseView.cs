using Godot;
using Drycastle.Shooter.Characters;
using Drycastle.ShooterCore.Events;

namespace Drycastle.Shooter.Net.Client;

/// <summary>A corpse as drawn: the node, the character that lies there (or the giant's model, or a bale), and what the render has decided about it.</summary>
internal sealed class CorpseView
{
    public Node3D Node = null!;
    /// <summary>The character that lies there; null for the giant (<see cref="Giant"/>).</summary>
    public CharacterView? View;
    /// <summary>The Bigfoot's corpse (issue #264): the giant's model in its death pose, no character, no weapon to take.</summary>
    public bool Giant;
    /// <summary>Where the corpse is looted from when that is not where it lies (the giant's head); null: its <see cref="Feet"/>.</summary>
    public Vector3? ReachFrom;
    /// <summary>A hay bale (#331): a corpse owned by <see cref="HarvesterRules.BaleOwner"/>, drawn as a bale; only its Items to take.</summary>
    public bool Bale;
    /// <summary>A bale's drawing state (smoothed position, roll, the body inside it, #351); null for anything else.</summary>
    public BaleMotion? Motion;
    public string Weapon = "";
    /// <summary>The ragdoll: the spin, limb flail and settling of a body thrown by a blast or a car.</summary>
    public readonly RagdollTumble Tumble = new();
    /// <summary>The feet, from the last render (the node sits at the body's turning point).</summary>
    public Vector3 Feet;
    /// <summary>When the entity first came (client clock), and whether the collapse question is settled (<see cref="CorpsesClient"/>).</summary>
    public double Born;
    public bool Decided;
}

/// <summary>How a bale corpse is drawn (#351): its model hangs under <see cref="Spin"/>; <see cref="Shown"/> is the smoothed position, <see cref="Angle"/> the turn about its own axis, <see cref="Body"/> the caught body inside it.</summary>
internal sealed class BaleMotion
{
    public Node3D? Spin;
    /// <summary>The way it leans with the ground (<see cref="Up"/> eases to <see cref="UpTarget"/>; <see cref="Leaned"/>: looked up at rest), and the heading of its axis (<see cref="Yaw"/>: across the way it last moved).</summary>
    public Vector3 Shown, Up = Vector3.Up, UpTarget = Vector3.Up;
    public float Angle, Yaw;
    public bool Leaned;
    public bool Seen;
    public CharacterView? Body;
    /// <summary>Whether <see cref="Body"/>'s limbs fly (the bale moves) or hang still; null before the first frame.</summary>
    public bool? BodyLoose;
}
