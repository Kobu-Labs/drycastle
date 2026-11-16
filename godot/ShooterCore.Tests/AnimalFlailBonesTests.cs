using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>The loose bones of a thrown animal are told by name across the packs' rigs (<see cref="AnimalFlailBones"/>).</summary>
public class AnimalFlailBonesTests
{
    [Theory]
    // the animated-animals pack (deer, wolf, horse)
    [InlineData("FrontUpperLeg.L", FlailBone.Leg)]
    [InlineData("BackLowerLeg.R", FlailBone.Leg)]
    [InlineData("BackLeg.L", FlailBone.Leg)]
    [InlineData("Neck2", FlailBone.Neck)]
    [InlineData("Head", FlailBone.Head)]
    [InlineData("Tail3", FlailBone.Tail)]
    [InlineData("Ear1.L", FlailBone.Ear)]
    // the pets pack (moose on the horse rig, boar on the pig rig)
    [InlineData("Horse_Rig_l_FrontLeg_HipSHJnt", FlailBone.Leg)]
    [InlineData("Horse_Rig_r_HindLeg_Knee2SHJnt", FlailBone.Leg)]
    [InlineData("Horse_Rig_Neck_TopSHJnt", FlailBone.Neck)]
    [InlineData("Horse_Rig_Head_TopSHJnt", FlailBone.Head)]
    [InlineData("PigRig2_Head_AuxSHJnt", FlailBone.Head)]
    [InlineData("PigRig2_Tail_AuxSHJnt", FlailBone.Tail)]
    [InlineData("PigRig2_l_Ears_01SHJnt", FlailBone.Ear)]
    public void A_limb_swings(string bone, FlailBone kind) => Assert.Equal(kind, AnimalFlailBones.Classify(bone));

    [Theory]
    [InlineData("Body")]
    [InlineData("Torso2")]
    [InlineData("FrontShoulder.L")]
    [InlineData("IKBackLeg.L")]
    [InlineData("PoleTargetBack.R")]
    [InlineData("FFB.L")]
    [InlineData("Horse_Rig_ROOTSHJnt")]
    [InlineData("Horse_Rig_Spine_TopSHJnt")]
    [InlineData("Horse_Rig_l_Clavicle_01_01SHJnt")]
    [InlineData("Horse_Rig_l_FrontLeg_AnkleSHJnt")]
    [InlineData("Horse_Rig_l_HindLeg_ToeSHJnt")]
    [InlineData("Horse_Rig_Head_JawEndSHJnt")]
    [InlineData("Horse_Rig_l_Eye_01SHJnt")]
    public void The_rest_keeps_the_clip(string bone) => Assert.Null(AnimalFlailBones.Classify(bone));
}
