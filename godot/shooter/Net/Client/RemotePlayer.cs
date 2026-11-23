using Godot;
using Drycastle.Shooter.Characters;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Client;

/// <summary>Another player (or a target dummy) as this client draws them: the node and character view, the interpolation buffer, the animation and tumble state.</summary>
public sealed class RemotePlayer
{
    public Node3D Node = null!;
    public CharacterView? View;
    public readonly Interpolator Buf = new();
    public EntityState State;
    public Stance Stance;
    public byte Slot;
    public double FireUntil;
    public Vector3 LastPos;
    public double LastT;
    public float Speed, StepAcc, WadeAcc;
    public double RippleDue;
    public readonly RagdollTumble Tumble = new();
    public Vector3 TumbleLast;
    /// <summary>The span the server's positions covered (the net smoke's "remote moved" check).</summary>
    public NVec3 Min = new(float.MaxValue), Max = new(float.MinValue);

    /// <summary>The driver's own picture of this body thrown by the vehicle they drive, shown before the server's kill arrives (#411).</summary>
    public readonly ThrowGuess Guess = new();
    /// <summary>
    /// The guessed throw is drawn as the dead ragdoll (the server has said dead; until then a hit that hurts tumbles as a thrown living body, <see cref="BodyFlingTracker"/>). GuessFlung: this guess started that
    /// tumble; GuessConfirmed: the server's PlayerFlung came for it.
    /// </summary>
    public bool GuessRagdoll, GuessFlung, GuessConfirmed;
    /// <summary>The launch speed of the guess and where the server had the body last frame, to see when the server's living body has come down (#411).</summary>
    public float GuessLaunch;
    public Vector3 GuessServerLast;

    /// <summary>The yaw and pitch drawn this frame (the aim of a rider).</summary>
    public float ShownYaw, ShownPitch;
    /// <summary>The vehicle and seat this player sits in (<see cref="SeatRiders"/>).</summary>
    public ushort RideCar;
    public int RideSeat = -1;
    public bool RideStanding;
    /// <summary>The pair of binoculars held at the face, or null.</summary>
    public ItemDef? Binoculars;
    /// <summary>The squad whose name the name tag shows after the name (0: none); the tag is rebuilt when it changes (#379).</summary>
    public byte TagSquad;
}
