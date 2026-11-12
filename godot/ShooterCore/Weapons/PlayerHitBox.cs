namespace Drycastle.ShooterCore;

/// <summary>
/// The hit volume of a player body and of a target dummy, derived from the drawn character model. One place for the numbers, so the server
/// hit test, the projectiles, melee and the client's drawn model cannot drift apart again.
/// </summary>
/// <remarks>
/// Measured from the <c>assets/animated-characters</c> bodies (all share one rig and the same unhelmeted height): the skinned bind pose is a T-pose
/// 3.147 units high (feet at y 0), with the head (face plate centre) 2.6 units up. Standing, the model is scaled to the 1.8 m stand height.
/// Crouch squashes the model to the crouch height. Prone rolls it onto its front (see <see cref="ProneHeadOffset"/>), so its hit volume is a
/// horizontal capsule along the facing direction. The T-pose arms are not the drawn width (the arms hang or hold a weapon), so the radius is a
/// fixed 0.56 m, the same as the earlier KayKit body, to keep the hit volume unchanged when the model was swapped.
/// </remarks>
public static class PlayerHitBox
{
    /// <summary>Native height of the model in glTF units (scale 1), without headgear.</summary>
    public const float ModelHeight = 3.147f;
    /// <summary>Height of the head (face plate centre) in glTF units.</summary>
    public const float ModelHeadHeight = 2.6f;

    /// <summary>Standing height of the hit volume and of the drawn figure, metres.</summary>
    public const float StandHeight = MoveConsts.CapsuleHeight;

    /// <summary>Uniform scale that makes the model <see cref="StandHeight"/> tall.</summary>
    public const float DrawScale = StandHeight / ModelHeight;

    /// <summary>Radius of the upright hit capsule and of the prone hit capsule: the drawn torso and arms, 0.56 m (wider than the 0.35 m movement capsule).</summary>
    public const float Radius = 0.56f;

    /// <summary>Head hit sphere radius. The sphere sits at the top of the body, so it covers the drawn head (the top 0.5 m of the model).</summary>
    public const float HeadRadius = 0.25f;

    /// <summary>
    /// Prone: how far the head sits forward of the body centre, metres: the head centre height less half the model height, scaled by
    /// <see cref="DrawScale"/>.
    /// </summary>
    public const float ProneHeadOffset = (ModelHeadHeight - ModelHeight / 2f) * DrawScale;

    /// <summary>Prone: the lift that rests the rolled figure on the ground (its half thickness, equal to <see cref="Radius"/>).</summary>
    public const float ProneLift = Radius;

    /// <summary>Prone: the drawn body is shifted forward by half its length, so the snapshot position is the body centre.</summary>
    public const float ProneCentreShift = StandHeight / 2f;
}
