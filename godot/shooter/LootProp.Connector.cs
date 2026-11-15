using Godot;

namespace Drycastle.Shooter;

// Ticket 07 (poles): the connector, a pole's insulator fitting (a brown ceramic insulator with two rings on a steel bracket and a hook), small, in one hand, and lying on the ground. Drawn from primitives.
public static partial class LootProp
{
    static StandardMaterial3D ConnectorMat(Color c, float rough = 0.7f, float metal = 0f) => new() { AlbedoColor = c, Roughness = rough, Metallic = metal };

    /// <summary>The fitting upright, its foot (the bracket) on the origin: about 0.16 m tall.</summary>
    static Node3D ConnectorModel()
    {
        var root = new Node3D { Name = "Connector" };
        var ceramic = ConnectorMat(new Color(0.45f, 0.26f, 0.15f), 0.35f);
        var steel = ConnectorMat(new Color(0.5f, 0.5f, 0.54f), 0.5f, 0.6f);
        void Part(Mesh m, Material mat, Vector3 at) => root.AddChild(new MeshInstance3D { Mesh = m, MaterialOverride = mat, Position = at });
        Part(new BoxMesh { Size = new Vector3(0.13f, 0.02f, 0.035f) }, steel, new Vector3(0, 0.01f, 0));                                                  // the bracket
        Part(new CylinderMesh { BottomRadius = 0.034f, TopRadius = 0.024f, Height = 0.1f, RadialSegments = 12 }, ceramic, new Vector3(0, 0.07f, 0));   // the insulator
        Part(new CylinderMesh { BottomRadius = 0.046f, TopRadius = 0.046f, Height = 0.014f, RadialSegments = 12 }, ceramic, new Vector3(0, 0.055f, 0)); // its two rings
        Part(new CylinderMesh { BottomRadius = 0.042f, TopRadius = 0.042f, Height = 0.014f, RadialSegments = 12 }, ceramic, new Vector3(0, 0.09f, 0));
        Part(new CylinderMesh { BottomRadius = 0.008f, TopRadius = 0.008f, Height = 0.05f, RadialSegments = 6 }, steel, new Vector3(0, 0.145f, 0));       // the hook's stem
        return root;
    }

    /// <summary>Hangs from one hand (the right: the Thief faces -Z, so +X), lower than the chest where both hands would hold a tire.</summary>
    static Node3D CarriedConnector()
    {
        var root = new Node3D { Name = "CarriedConnector" };
        var c = ConnectorModel();
        c.Position = new Vector3(0.26f, -0.42f, 0.14f);
        root.AddChild(c);
        return root;
    }

    /// <summary>On its side on the ground.</summary>
    static Node3D LyingConnector()
    {
        var root = new Node3D { Name = "LyingConnector" };
        var c = ConnectorModel();
        c.RotationDegrees = new Vector3(90, 0, 0); // lies along Z, the bracket stays level
        c.Position = new Vector3(0, 0.046f, -0.07f);
        root.AddChild(c);
        return root;
    }
}
