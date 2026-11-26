using Godot;
using Drycastle.ShooterCore.Buildings.Destruction;
using Drycastle.ShooterCore.Fsm;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Buildings.Destruction;

/// <summary>
/// One piece of house debris (WP-G) and its <see cref="DebrisPhase"/> machine: a rigid body while it falls, breaks and rests; a plain mesh when it is fading after it was
/// evicted from the stay rubble (<see cref="Body"/> null). Its cells are house-grid indices; its local frame is <see cref="DebrisRules.LocalOf"/> minus <see cref="CentroidGrid"/>.
/// </summary>
public sealed class DebrisPiece
{
    public required long Id { get; init; }
    public required HouseGrid Grid { get; init; }
    public required int[] Cells { get; init; }
    /// <summary>0: a whole fallen component; 1: a piece of its first split; 2: of the second.</summary>
    public int Generation { get; init; }
    public uint Seed { get; init; }
    public NVec3 CentroidGrid { get; init; }
    /// <summary>What moves and fades: the body, or the mesh of a ghost.</summary>
    public required Node3D Root { get; init; }
    public RigidBody3D? Body { get; init; }
    public required GeometryInstance3D Look { get; init; }
    /// <summary>The triangles of a body (local frame), kept for the stay rubble. Null on a ghost.</summary>
    public DebrisArrays? Arrays { get; init; }

    public readonly StateMachine<DebrisPhase> Phase = new(DebrisMachine.Spec);
    /// <summary>The body's velocity at the last physics tick: the speed change of one tick is the impact.</summary>
    public Vector3 PrevVelocity;
    /// <summary>Seconds the body has been slower than <see cref="DebrisRules.StillSpeed"/>.</summary>
    public float StillFor;
    /// <summary>Where the fade started and how far it sinks (metres).</summary>
    public Vector3 FadeFrom;
    public float SinkM;
    /// <summary>Stay mode: waiting to be merged into its house's rubble mesh.</summary>
    public bool Pending;

    /// <summary>A body that is still in the world's physics (falling, broken or resting): what debris.max_bodies counts.</summary>
    public bool Counts => Body != null && !Phase.Is(DebrisPhase.Fading);
}
