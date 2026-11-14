using Godot;

namespace Drycastle.Shooter;

// Ticket 07 (poles): the connector, small, in one hand, and lying on the ground. Stubs until then.
public static partial class LootProp
{
    static Node3D CarriedConnector() => Stub(new Color(0.55f, 0.55f, 0.6f), 0.15f);
    static Node3D LyingConnector() => Stub(new Color(0.55f, 0.55f, 0.6f), 0.15f);
}
