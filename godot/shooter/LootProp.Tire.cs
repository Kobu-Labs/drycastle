using Godot;
using Drycastle.ShooterCore.Thieves;

namespace Drycastle.Shooter;

public static partial class LootProp
{
    const float TireInner = 0.17f, TireOuter = 0.35f;   // a car tire: 0.7 m across, 0.18 m of rubber (the torus' tube is (outer - inner) / 2)

    static StandardMaterial3D Rubber() => new() { AlbedoColor = new Color(0.07f, 0.07f, 0.075f), Roughness = 0.95f };

    static MeshInstance3D TireMesh() => new() { Name = "Tire", Mesh = new TorusMesh { InnerRadius = TireInner, OuterRadius = TireOuter, Rings = 24, RingSegments = 12 }, MaterialOverride = Rubber() };

    const float PackTireThickM = 0.224f;   // props/tire_01 lies flat: 0.725 across, 0.224 high, origin on its base

    /// <summary>Held upright in both hands in front of the chest, its face to the Thief, its middle on the origin: the pack's tire (stood up), else a torus.</summary>
    static Node3D CarriedTire()
    {
        var root = new Node3D { Name = "CarriedTire" };
        if (PackModel(LootKind.Tire) is { } pack)
        {
            pack.RotationDegrees = new Vector3(90, 0, 0);          // stands it up: its axis along Z
            pack.Position = new Vector3(0, 0, -PackTireThickM / 2); // and its middle on the origin
            root.AddChild(pack);
            return root;
        }
        var t = TireMesh();
        t.RotationDegrees = new Vector3(90, 0, 0); // the torus lies flat: stand it up so its hole faces along Z
        root.AddChild(t);
        return root;
    }

    /// <summary>Lying flat on the ground.</summary>
    static Node3D LyingTire()
    {
        var root = new Node3D { Name = "LyingTire" };
        if (PackModel(LootKind.Tire) is { } pack) { root.AddChild(pack); return root; } // its base is on the origin already
        var t = TireMesh();
        t.Position = new Vector3(0, (TireOuter - TireInner) / 2, 0);
        root.AddChild(t);
        return root;
    }
}
