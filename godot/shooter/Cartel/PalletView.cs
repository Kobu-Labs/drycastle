using Godot;
using Drycastle.Shooter.Assets;
using Drycastle.ShooterCore.Cartel;

namespace Drycastle.Shooter.Cartel;

/// <summary>
/// The Cartel pallet on the client (docs/cartel.md "Delivery", cartel.json <c>pallet</c>): the Gang Warfare pallet where the map's pallet marker stands, turned with it, the marker itself hidden.
/// Without the private pack a box of the pallet's size. A look only: the marker's own solid box is what players and shots meet (a low box the pallet's size, like any placed prop), so nothing
/// is tested against the drawn model. Static map data (no entity, nothing on the wire): every client reads the same map. A map without a marker gets none. The bags on it are Duffels
/// (<see cref="Net.Client.Incidents.DuffelsClient"/>).
/// </summary>
public sealed partial class PalletView : Node3D
{
    CartelPalletData _look = new();

    /// <summary>Adds the pallet of <paramref name="map"/> under <paramref name="parent"/> and hides the markers; null (nothing added) on a map without one.</summary>
    public static PalletView? AddTo(Node parent, Drycastle.MapLoader.MapLoader map, CartelPalletData look)
    {
        if (ContactMarker.PalletIn(map) is not { } pose) return null;
        foreach (var (id, _) in map.ObjectsOf(CartelZones.PalletAsset))
            if (map.ObjectNodes.TryGetValue(id, out var marker) && IsInstanceValid(marker)) marker.Visible = false; // the pallet is drawn instead
        var view = new PalletView
        {
            Name = "CartelPallet", _look = look,
            Position = new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z),
            RotationDegrees = new Vector3(0, pose.YawDeg, 0),
        };
        view.Build();
        parent.AddChild(view);
        return view;
    }

    void Build()
    {
        if (SyntyLib.Instance(_look.Model, _look.Atlas) is { } model) { AddChild(model); return; } // the glb's origin is the base's centre
        var size = new Vector3(_look.SizeM[0], _look.SizeM[1], _look.SizeM[2]);
        AddChild(new MeshInstance3D
        {
            Name = "Box", Mesh = new BoxMesh { Size = size },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromHtml(_look.Colour), Roughness = 0.95f }, Position = new Vector3(0, size.Y / 2, 0),
        });
    }
}
