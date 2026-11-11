using Godot;

namespace Suchohrad.Shooter;

/// <summary>
/// Held weapon visuals. Models come from the Quaternius Ultimate Gun Pack: pistol = Pistol_1, ak47 = AssaultRifle_1,
/// knife = Bayonet (stand-in, the pack has no knife). The pack has no launcher, so the bazooka is a code-built tube.
/// Every model is returned with its muzzle toward -Z and its origin at the grip end.
/// </summary>
public static class WeaponFx
{
    /// <summary>File, longest edge in metres and the model-space offset of the grip, per weapon id.</summary>
    static readonly Dictionary<string, (string File, float Length)> Models = new()
    {
        ["pistol"] = ("quaternius-ultimate-gun-pack/pistol-1.glb", 0.26f),
        ["ak47"] = ("quaternius-ultimate-gun-pack/assault-rifle-1.glb", 0.8f),
        ["sniper"] = ("quaternius-ultimate-gun-pack/sniper-rifle.glb", 1.15f),
        ["knife"] = ("quaternius-ultimate-gun-pack/bayonet.glb", 0.32f),
    };

    /// <summary>The weapon node for <paramref name="weaponId"/>, or null when headless / unknown id.</summary>
    public static Node3D? Make(string weaponId)
    {
        if (!ShotFx.Enabled) return null;
        var root = new Node3D { Name = "Held_" + weaponId };
        if (Models.TryGetValue(weaponId, out var m) && ModelLib.Instance(m.File) is { } model)
        {
            var box = Bounds(model);
            var s = m.Length / Mathf.Max(box.Size.X, 0.001f);
            // barrel along +X in the model; centre it on the grip (rear third), rotate so the muzzle faces -Z
            var holder = new Node3D { Scale = Vector3.One * s, RotationDegrees = new Vector3(0, 90, 0) };
            model.Position = new Vector3(-(box.Position.X + box.Size.X * 0.3f), -(box.Position.Y + box.Size.Y * 0.8f), -(box.Position.Z + box.Size.Z * 0.5f));
            holder.AddChild(model);
            root.AddChild(holder);
        }
        else if (weaponId == "bazooka") BuildTube(root);
        else return null;
        // muzzle marker: the barrel tip (0.7 of the length in front of the grip; the tube ends at -0.81)
        var z = weaponId == "bazooka" ? -0.82f : Models.TryGetValue(weaponId, out var mm) ? -mm.Length * 0.7f : -0.3f;
        var muzzle = new Node3D { Name = "Muzzle", Position = new Vector3(0, 0.02f, z) };
        if (weaponId != "knife") muzzle.AddChild(new MuzzleFlash());
        root.AddChild(muzzle);
        return root;
    }

    /// <summary>Stand-in bazooka: dark tube with a flared rear, laid along -Z.</summary>
    static void BuildTube(Node3D root)
    {
        var dark = new StandardMaterial3D { AlbedoColor = new Color(0.22f, 0.25f, 0.2f), Roughness = 0.8f };
        var tip = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.12f, 0.1f), Roughness = 0.8f };
        root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.05f, Height = 0.8f, Material = dark }, Position = new Vector3(0, 0, -0.25f), RotationDegrees = new Vector3(90, 0, 0) });
        root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.08f, Height = 0.12f, Material = dark }, Position = new Vector3(0, 0, 0.18f), RotationDegrees = new Vector3(90, 0, 0) });
        root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.0f, BottomRadius = 0.045f, Height = 0.16f, Material = tip }, Position = new Vector3(0, 0, -0.73f), RotationDegrees = new Vector3(-90, 0, 0) });
        root.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.05f, 0.14f, 0.06f), Material = dark }, Position = new Vector3(0, -0.12f, -0.1f) });
    }

    static Aabb Bounds(Node n)
    {
        var box = new Aabb();
        var first = true;
        foreach (var c in n.FindChildren("*", "MeshInstance3D", true, false))
        {
            var mi = (MeshInstance3D)c;
            var b = mi.Transform * mi.GetAabb();
            box = first ? b : box.Merge(b);
            first = false;
        }
        return box;
    }
}
