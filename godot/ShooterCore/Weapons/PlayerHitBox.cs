namespace Suchohrad.ShooterCore;

/// <summary>
/// The hit volume of a player body and of a target dummy, derived from the drawn KayKit model. One place for the numbers, so the server
/// hit test, the projectiles, melee and the client's drawn model cannot drift apart again.
/// </summary>
/// <remarks>
/// Measured from <c>assets/kaykit-character-animations/animated-character.glb</c>: the skinned rest pose (all vertices, bind pose) spans
/// 1.899 m high (feet at y 0), 1.175 m across x and 1.118 m deep, with the head region from about 1.4 m up. The old renderer scaled the model
/// by 1.6, so a 3.04 m figure was drawn inside a 1.8 m hit capsule; that is the cause of the misses.
/// Standing, the model is scaled to the 1.8 m stand height. Crouch squashes the model to the crouch height. Prone rolls it onto its front
/// (see <see cref="ProneHeadOffset"/>), so its hit volume is a horizontal capsule along the facing direction.
/// </remarks>
public static class PlayerHitBox
{
    /// <summary>Native height of the model in metres (glTF units, scale 1).</summary>
    public const float ModelHeight = 1.899f;
    /// <summary>Native half width of the model across x in metres (scale 1). The model is about as wide as it is deep.</summary>
    public const float ModelHalfWidth = 0.588f;

    /// <summary>Standing height of the hit volume and of the drawn figure, metres.</summary>
    public const float StandHeight = MoveConsts.CapsuleHeight;

    /// <summary>Uniform scale that makes the model <see cref="StandHeight"/> tall.</summary>
    public const float DrawScale = StandHeight / ModelHeight;

    /// <summary>Radius of the upright hit capsule and of the prone hit capsule: the drawn torso and arms, about 0.56 m (wider than the 0.35 m movement capsule).</summary>
    public const float Radius = ModelHalfWidth * DrawScale;

    /// <summary>Head hit sphere radius. The sphere sits at the top of the body, so it covers the drawn head (the top 0.5 m of the model).</summary>
    public const float HeadRadius = 0.25f;

    /// <summary>
    /// Prone: how far the head sits forward of the body centre, metres. The model's head centre is at 1.65 m of 1.9 m, the body centre at
    /// half the height, so the head is 0.7 model metres from the centre, scaled by <see cref="DrawScale"/>.
    /// </summary>
    public const float ProneHeadOffset = 0.7f * DrawScale;

    /// <summary>Prone: the lift that rests the rolled figure on the ground (its half thickness, equal to <see cref="Radius"/>).</summary>
    public const float ProneLift = Radius;

    /// <summary>Prone: the drawn body is shifted forward by half its length, so the snapshot position is the body centre.</summary>
    public const float ProneCentreShift = StandHeight / 2f;
}
