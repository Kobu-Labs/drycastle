using Godot;
using Drycastle.Shooter.Assets;
using Drycastle.ShooterCore.Cartel;

namespace Drycastle.Shooter.Cartel;

/// <summary>
/// The cartel's Contact on the client (docs/cartel.md "Contact", contact.json <c>view</c>): a Gang Warfare character standing idle where the map's Contact marker stands, facing
/// the way it says, with the marker itself hidden. Without the private pack a capsule with a nose on the side he faces. A look only: nothing collides with it or is tested
/// against it. Static map data (no entity, nothing on the wire): every client reads the same map. A map without a marker gets none.
/// </summary>
public sealed partial class ContactView : Node3D
{
    ContactLook _look = new();

    /// <summary>Adds the Contact of <paramref name="map"/> under <paramref name="parent"/> and hides the markers; null (nothing added) on a map without one.</summary>
    public static ContactView? AddTo(Node parent, Drycastle.MapLoader.MapLoader map)
    {
        var cfg = ContactMarker.LoadConfig();
        if (ContactMarker.PoseIn(map, cfg) is not { } pose) return null;
        foreach (var (id, _) in map.ObjectsOf(CartelZones.ContactAsset))
            if (map.ObjectNodes.TryGetValue(id, out var marker) && IsInstanceValid(marker)) marker.Visible = false; // he is drawn instead
        var view = new ContactView
        {
            Name = "CartelContact", _look = cfg.View,
            Position = new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z),
            RotationDegrees = new Vector3(0, pose.YawDeg, 0),
        };
        view.Build();
        parent.AddChild(view);
        return view;
    }

    void Build()
    {
        var pivot = new Node3D { Name = "Pivot", Scale = Vector3.One * _look.Scale };
        AddChild(pivot);
        if (SyntyLib.Instance(_look.Model, _look.Atlas) is { } model)
        {
            model.RotationDegrees = new Vector3(0, 180, 0); // the pack faces +Z, the node's forward is -Z
            pivot.AddChild(model);
            if (_look.Idle == "" || SyntyLib.Clip(_look.Idle) is not { } clip) return;
            var lib = new AnimationLibrary();
            lib.AddAnimation(_look.Idle, clip);
            var anim = new AnimationPlayer { Name = "Clips" };
            model.AddChild(anim); // root node "..": the tracks are Skeleton3D:Bone under the model
            anim.AddAnimationLibrary("", lib);
            anim.Play(_look.Idle);
            return;
        }
        var h = _look.CapsuleHeightM;
        var colour = Color.FromHtml(_look.CapsuleColour);
        pivot.AddChild(new MeshInstance3D
        {
            Name = "Capsule", Mesh = new CapsuleMesh { Radius = 0.3f, Height = h },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = colour, Roughness = 0.9f }, Position = new Vector3(0, h / 2, 0),
        });
        pivot.AddChild(new MeshInstance3D
        {
            Name = "Nose", Mesh = new BoxMesh { Size = new Vector3(0.16f, 0.1f, 0.2f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = colour.Darkened(0.55f), Roughness = 0.9f }, Position = new Vector3(0, h * 0.82f, -0.3f),
        });
    }
}
