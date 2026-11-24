using Godot;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Fsm;

namespace Drycastle.Shooter.Net.Client.BigfootEvent;

/// <summary>The figure of one Bigfoot: the node at its feet, the pivot the procedural motion turns and scales, and the model with its clips.</summary>
internal sealed class BigfootView
{
    public Node3D Node = null!, Pivot = null!;
    public AnimationPlayer? Anim;
    /// <summary>The clip name the model uses for each plain clip name ("Attack" is "EnemyArmature|EnemyArmature|EnemyArmature|Attack").</summary>
    public readonly Dictionary<string, string> Clips = new();
    /// <summary>The act whose clip is playing, as the byte of its <see cref="BigfootAct"/>. A change means the clip is due; <c>Reset</c> after a hit reaction makes it due again.</summary>
    public Change<byte> Drawn;
    /// <summary>The replicated hit bit last seen: a flip is a hit that did not kill.</summary>
    public Change<bool> HitBit;
    /// <summary>An act was drawn at least once: a giant already dead when first seen lies in its last pose, one that dies in front of us falls.</summary>
    public bool Introduced;
    /// <summary>Client time (s) the shown act began, and until which a hit reaction holds the act's clip back.</summary>
    public double Since, HitUntil;
    /// <summary>The skeleton and the bone of its right hand (-1 without one): the log it winds up with rides that hand.</summary>
    public Skeleton3D? Skel;
    public int HandR = -1;
    /// <summary>Where it was drawn last frame and the ground speed measured from that (m/s, smoothed): Walk and Run play at it.</summary>
    public Vector3 LastAt;
    public bool HasLastAt;
    public float Speed;
    /// <summary>The act drawn before the one that plays now: a launch out of a hold is the one-handed throw of a player, any other the two-handed one.</summary>
    public BigfootAct Before;
    public BigfootAct Shown;
    /// <summary>The log on its shoulder while it winds a throw up (the thrown one is a world entity of its own).</summary>
    public Node3D? HeldLog;
    /// <summary>The player in its hand while the act is Hold (0 otherwise): when the act ends, a Launch makes the body fly, anything else lets it drop (#360).</summary>
    public ushort HeldId;
}
