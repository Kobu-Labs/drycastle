using Godot;
using Drycastle.Shooter.Assets;

namespace Drycastle.Shooter.Thieves;

// Ticket 06 (dishes): the satellite dish in both hands, and lying on the ground, drawn with the generated model (assets/drycastle-generated-models/satellite_dish.glb: origin on the wall at the
// arm's root, +X out of the wall, the dish looks out and 20 degrees up, about 0.67 m deep, 0.7 m across). A box when the model is not there (headless, or the file missing).
public static partial class LootProp
{
    const string DishModel = "drycastle-generated-models/satellite_dish.glb";

    /// <summary>Held in front of the chest in both hands, its face turned forward (away from the Thief): the model's +X out of the wall turned to -Z, and centred between the hands.</summary>
    static Node3D CarriedDish()
    {
        var root = new Node3D { Name = "CarriedDish" };
        if (ModelLib.Instance(DishModel) is not { } model) return Stub(new Color(0.75f, 0.77f, 0.8f), 0.4f);
        model.RotationDegrees = new Vector3(0, 90, 0);
        model.Position = new Vector3(0, -0.05f, 0.35f);
        root.AddChild(model);
        return root;
    }

    /// <summary>Lying on the ground on its back (its face up), a little askew; its wall arm is below the ground line.</summary>
    static Node3D LyingDish()
    {
        var root = new Node3D { Name = "LyingDish" };
        if (ModelLib.Instance(DishModel) is not { } model) return Stub(new Color(0.75f, 0.77f, 0.8f), 0.4f);
        model.RotationDegrees = new Vector3(12, 0, 90);
        model.Position = new Vector3(0, -0.28f, 0);
        root.AddChild(model);
        return root;
    }
}
