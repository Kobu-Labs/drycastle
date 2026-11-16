namespace Drycastle.ShooterCore;

/// <summary>A part of an animal's rig that swings loose while the body is thrown (<see cref="AnimalFlailBones"/>).</summary>
public enum FlailBone : byte { Leg, Neck, Head, Tail, Ear }

/// <summary>
/// Which bones of an animal's rig hang loose when a vehicle throws it, told by name: the models come from three packs with rigs of their own (Body/Neck1/FrontUpperLeg.L,
/// Horse_Rig_l_FrontLeg_HipSHJnt, ...), and every one names its legs, neck, head, tail and ears so. A bone that is none of them (spine, shoulder, hoof, jaw, eye, an IK
/// target) keeps the pose of the clip. The client's modifier (AnimalFlail) reads this; a rig with no such bone is thrown as the rigid body it was.
/// </summary>
public static class AnimalFlailBones
{
    /// <summary>How far the bone may swing from the clip's pose (radians) and how hard it is pulled back to it (1/s²): a tail is loose, a neck stiff.</summary>
    public readonly record struct Swing(float MaxAngle, float Pull);

    public static Swing Of(FlailBone kind) => kind switch
    {
        FlailBone.Leg => new(0.8f, 70f),
        FlailBone.Neck => new(0.35f, 110f),
        FlailBone.Head => new(0.4f, 110f),
        FlailBone.Tail => new(0.9f, 40f),
        _ => new(0.5f, 150f),
    };

    /// <summary>The part <paramref name="bone"/> is, or null for one that keeps the clip's pose.</summary>
    public static FlailBone? Classify(string bone)
    {
        var n = bone.ToLowerInvariant();
        // helpers of the rig, not limbs: IK and pole targets (their names hold the limb's), and the ends of a limb that would only wobble
        if (n.StartsWith("ik") || n.StartsWith("pole") || n.StartsWith("ff") || Has(n, "jaw", "eye", "ankle", "ball", "toe", "shoulder", "clavicle", "root")) return null;
        if (n.Contains("tail")) return FlailBone.Tail;
        if (n.Contains("ear")) return FlailBone.Ear;
        if (n.Contains("neck")) return FlailBone.Neck;
        if (n.Contains("head")) return FlailBone.Head;
        if (n.Contains("leg")) return FlailBone.Leg;
        return null;
    }

    static bool Has(string n, params string[] parts) { foreach (var p in parts) if (n.Contains(p)) return true; return false; }
}
