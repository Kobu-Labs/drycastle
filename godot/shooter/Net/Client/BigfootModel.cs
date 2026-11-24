using Godot;
using Drycastle.Shooter.Assets;
using Drycastle.ShooterCore.Events;

namespace Drycastle.Shooter.Net.Client;

/// <summary>What the Bigfoot is drawn of: the giant's model with its clips and its right hand bone, and the log it throws.</summary>
internal static class BigfootModel
{
    /// <summary>Finds the skeleton of the giant and its right hand.</summary>
    public static void IndexBones(BigfootView v)
    {
        if (v.Node.FindChildren("*", "Skeleton3D", true, false).FirstOrDefault() is not Skeleton3D sk) return;
        v.Skel = sk;
        v.HandR = sk.FindBone("Hand.R");
    }

    /// <summary>The giant: the model scaled to its height and turned to face forward, or a box of its size without one (headless, or the file is missing).</summary>
    /// <param name="data">bigfoot.json: the model, its scale and yaw.</param>
    public static Node3D MakeGiant(BigfootFile data, out AnimationPlayer? anim)
    {
        anim = null;
        if (ModelLib.Instance(data.Model) is { } model)
        {
            model.Scale = Vector3.One * data.ModelScale;
            model.RotationDegrees = new Vector3(0, data.ModelYawDeg, 0);
            anim = model.FindChildren("*", "AnimationPlayer", true, false).FirstOrDefault() as AnimationPlayer;
            if (anim != null)
            {
                foreach (var name in anim.GetAnimationList())
                    anim.GetAnimation(name).LoopMode = BigfootRules.LoopedClips.Contains(name) ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
            }
            return model;
        }
        var h = data.HeightM;
        var box = new Node3D();
        box.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(h * 0.5f, h, h * 0.35f) }, Position = new Vector3(0, h / 2, 0) });
        return box;
    }

    /// <summary>Maps the plain clip names to the model's (a model exported from a tool may prefix them: "Armature|Idle").</summary>
    public static void IndexClips(Dictionary<string, string> clips, AnimationPlayer? anim)
    {
        if (anim == null) return;
        foreach (var plain in BigfootRules.Clips)
            foreach (var name in anim.GetAnimationList())
                if (name == plain || name.EndsWith("|" + plain)) { clips[plain] = name; break; }
    }

    /// <summary>A log: a thick trunk with a paler cut face at each end and two branch stubs, lying along -Z. The one it throws and the one on its shoulder.</summary>
    public static Node3D MakeLog()
    {
        const float length = 3.8f;
        var node = new Node3D();
        var bark = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.20f, 0.12f), Roughness = 1f };
        var cut = new StandardMaterial3D { AlbedoColor = new Color(0.78f, 0.62f, 0.40f), Roughness = 1f };
        node.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.36f, BottomRadius = 0.44f, Height = length, RadialSegments = 10 }, MaterialOverride = bark, RotationDegrees = new Vector3(90, 0, 0) });
        foreach (var end in new[] { -1f, 1f })
            node.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = end < 0 ? 0.33f : 0.41f, BottomRadius = end < 0 ? 0.33f : 0.41f, Height = 0.03f, RadialSegments = 10 },
                MaterialOverride = cut, RotationDegrees = new Vector3(90, 0, 0), Position = new Vector3(0, 0, end * (length / 2f + 0.005f)),
            });
        node.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.07f, BottomRadius = 0.12f, Height = 0.7f, RadialSegments = 6 }, MaterialOverride = bark, Position = new Vector3(0.45f, 0.3f, -0.7f), RotationDegrees = new Vector3(0, 0, -50) });
        node.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.06f, BottomRadius = 0.1f, Height = 0.55f, RadialSegments = 6 }, MaterialOverride = bark, Position = new Vector3(-0.4f, -0.2f, 0.8f), RotationDegrees = new Vector3(0, 0, 120) });
        return node;
    }
}
