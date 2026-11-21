using Godot;

namespace Drycastle.Shooter;

/// <summary>
/// Held weapon visuals. Models come from the Quaternius Ultimate Gun Pack: pistol = Pistol_1, ak47 = AssaultRifle_1,
/// knife = Bayonet (stand-in, the pack has no knife). The pack has no launcher: the RPG and the Lock-on launcher are the paid pack's models, or code-built ones without the pack.
/// The shooter v2 weapons map to the other OBJ models of the same pack (see assets/README.md, "Shooter v2 weapons").
/// Every model is returned with its muzzle toward -Z and its origin at the grip end.
/// </summary>
public static class WeaponFx
{
    /// <summary>File, longest edge in metres and the model-space offset of the grip, per weapon id.</summary>
    /// <summary>The model file of every held weapon, for warming them on the loading screen (#170, T2-18).</summary>
    public static IEnumerable<string> ModelFiles => Models.Values.Select(m => m.File);

    static readonly Dictionary<string, (string File, float Length)> Models = new()
    {
        ["pistol"] = ("quaternius-ultimate-gun-pack/pistol-1.glb", 0.26f),
        ["ak47"] = ("quaternius-ultimate-gun-pack/assault-rifle-1.glb", 0.8f),
        ["sniper"] = ("quaternius-ultimate-gun-pack/sniper-rifle.glb", 1.15f),
        ["knife"] = ("quaternius-ultimate-gun-pack/bayonet.glb", 0.32f),
        // shooter v2 weapons, all from the same pack (the full OBJ set, converted one-to-one); the pack has no
        // machine guns, so those use an assault rifle model
        ["m4a1"] = ("quaternius-ultimate-gun-pack/assault-rifle-2.glb", 0.86f),
        ["rpk"] = ("quaternius-ultimate-gun-pack/assault-rifle-3.glb", 1.0f),
        ["pkm"] = ("quaternius-ultimate-gun-pack/assault-rifle-4.glb", 1.1f),
        ["m249"] = ("quaternius-ultimate-gun-pack/assault-rifle-5.glb", 1.04f),
        ["g36"] = ("quaternius-ultimate-gun-pack/assault-rifle2-1.glb", 0.99f),
        ["m60"] = ("quaternius-ultimate-gun-pack/assault-rifle2-3.glb", 1.1f),
        ["aug"] = ("quaternius-ultimate-gun-pack/bullpup-1.glb", 0.73f),
        ["p90"] = ("quaternius-ultimate-gun-pack/bullpup-2.glb", 0.5f),
        ["mp5"] = ("quaternius-ultimate-gun-pack/submachine-gun-1.glb", 0.6f),
        ["ump45"] = ("quaternius-ultimate-gun-pack/submachine-gun-2.glb", 0.66f),
        ["remington870"] = ("quaternius-ultimate-gun-pack/shotgun-1.glb", 1.0f),
        ["spas12"] = ("quaternius-ultimate-gun-pack/shotgun-2.glb", 0.9f),
        ["benelli_m4"] = ("quaternius-ultimate-gun-pack/shotgun-3.glb", 0.95f),
        ["aa12"] = ("quaternius-ultimate-gun-pack/shotgun-4.glb", 0.9f),
        ["glock17"] = ("quaternius-ultimate-gun-pack/pistol-2.glb", 0.22f),
        ["deagle"] = ("quaternius-ultimate-gun-pack/pistol-3.glb", 0.3f),
        ["m82"] = ("quaternius-ultimate-gun-pack/sniper-rifle-2.glb", 1.3f),
        ["awm"] = ("quaternius-ultimate-gun-pack/sniper-rifle-3.glb", 1.2f),
        ["svd"] = ("quaternius-ultimate-gun-pack/sniper-rifle-4.glb", 1.2f),
        ["hunting_rifle"] = ("quaternius-ultimate-gun-pack/sniper-rifle-4.glb", 1.25f), // placeholder: SM_Wep_HuntingRifle_Clean_01 (Synty Apocalypse) is not a runtime GLB yet (#473)
    };

    /// <summary>Node meta: height of the weapon's top (iron sight) above its origin, in metres; the viewmodel lowers the gun by this much to aim down sights.</summary>
    public const string SightHeightMeta = "sight_height";

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
            root.SetMeta(SightHeightMeta, box.Size.Y * 0.2f * s); // top of the model (its iron sight) above the grip origin
        }
        else if (weaponId == "rpg")
        {
            var pack = AddPackRpg(root);
            if (!pack) BuildTube(root);
            root.SetMeta(SightHeightMeta, pack ? PackRpgSight : 0.05f);
        }
        else if (weaponId == "grenade")
        {
            // the pack's grenade (SM_Wep_Grenade_01, 0.15 m tall), held upright in the hand: nothing to aim, so the muzzle marker sits at its top
            var nade = SyntyLib.Instance("props/grenade_01", "glb/atlas/military_weapons_01.png") ?? new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.05f, Height = 0.1f } };
            if (nade is Node3D n3) n3.Scale = Vector3.One * 1.3f;
            root.AddChild(nade);
            root.SetMeta(SightHeightMeta, 0f);
        }
        else if (weaponId == "strela")
        {
            if (!AddPackLauncher(root)) BuildLockOnLauncher(root);
            root.SetMeta(SightHeightMeta, 0.11f);
        }
        else return null;
        // muzzle marker: the barrel tip (0.7 of the length in front of the grip; the tube ends at -0.81)
        var z = weaponId == "rpg" ? (root.HasNode(PackRpgNode) ? -PackRpgMouth : -0.82f) : weaponId == "strela" ? (root.HasNode(PackLauncherNode) ? -PackLauncherMuzzle : -0.9f) : Models.TryGetValue(weaponId, out var mm) ? -mm.Length * 0.7f : -0.3f;
        var muzzle = new Node3D { Name = "Muzzle", Position = new Vector3(0, root.HasNode(PackRpgNode) ? PackRpgAxis : 0.02f, z) };
        if (weaponId is not ("knife" or "grenade")) muzzle.AddChild(new MuzzleFlash());
        root.AddChild(muzzle);
        return root;
    }

    const string PackRpgNode = "PackRpg";
    /// <summary>The loaded rocket head inside the pack's RPG (a child mesh), hidden while the tube is empty (<see cref="SetLoaded"/>).</summary>
    const string PackRpgRocket = "SM_Wep_RPG_Rocket_01";
    /// <summary>The paid pack's RPG is drawn at this share of its authored size (1.27 m of tube, 1.84 m with the rocket in).</summary>
    const float PackRpgScale = 0.75f;
    /// <summary>
    /// The pack's RPG has sights that do not look along its tube: the rear sight's notch is at authored y 0.211 (0.22 m behind the grip), the front
    /// sight's post ends at y 0.201 (0.21 m in front of it), so the line over them drops 0.010 m in 0.43 m. The model is held with its mouth raised by
    /// that angle, which lays the line of sight level, 0.206 m (authored) above the grip: aimed down the sights, the post's tip in the notch is the
    /// point the rocket flies to (the screen's centre).
    /// </summary>
    const float PackRpgSightTilt = 0.0233f;
    /// <summary>Scaled distances of the pack's RPG from its pistol grip (the model's origin): the tube's axis above it, the tube's mouth in front of it, and its line of sight above it.</summary>
    const float PackRpgAxis = 0.088f * PackRpgScale, PackRpgMouth = 0.27f * PackRpgScale, PackRpgSight = 0.206f * PackRpgScale;
    /// <summary>How far behind the grip a held launcher's rear opening is, metres (the backblast leaves there).</summary>
    public const float LauncherRear = 0.75f;

    /// <summary>
    /// The RPG of the paid pack (SM_Wep_RPG_01: tube, loaded rocket head, two sights and trigger, converted to glb/props/rpg_01.glb by the pack's
    /// tools/export_multi_glb.gd; atlas Weapons_01). Faces +Z with the grip under its origin: turned half round, and its mouth raised so its sights look level (<see cref="PackRpgSightTilt"/>). False when the pack is not there.
    /// </summary>
    static bool AddPackRpg(Node3D root)
    {
        if (SyntyLib.Instance("props/rpg_01", "glb/atlas/military_weapons_01.png") is not { } model) return false;
        model.Name = PackRpgNode;
        model.Transform = new Transform3D((new Basis(Vector3.Right, PackRpgSightTilt) * new Basis(Vector3.Up, Mathf.Pi)).Scaled(Vector3.One * PackRpgScale), Vector3.Zero);
        root.AddChild(model);
        return true;
    }

    /// <summary>Shows or hides the rocket head in a held RPG's tube (the tube is empty between the launch and the end of the reload). No-op for any other weapon node.</summary>
    public static void SetLoaded(Node3D? held, bool loaded)
    {
        if (held.Name == "Held_grenade") { if (held.GetChildCount() > 0 && held.GetChild(0) is Node3D nade && nade.Visible != loaded) nade.Visible = loaded; return; } // the hand is empty between the throw and the next one
        if (held?.GetNodeOrNull<Node3D>(PackRpgNode)?.FindChild(PackRpgRocket, true, false) is Node3D rocket && rocket.Visible != loaded) rocket.Visible = loaded;
    }

    /// <summary>Stand-in RPG for a build without the paid pack: dark tube with a flared rear, laid along -Z.</summary>
    static void BuildTube(Node3D root)
    {
        var dark = new StandardMaterial3D { AlbedoColor = new Color(0.22f, 0.25f, 0.2f), Roughness = 0.8f };
        var tip = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.12f, 0.1f), Roughness = 0.8f };
        root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.05f, Height = 0.8f, Material = dark }, Position = new Vector3(0, 0, -0.25f), RotationDegrees = new Vector3(90, 0, 0) });
        root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.08f, Height = 0.12f, Material = dark }, Position = new Vector3(0, 0, 0.18f), RotationDegrees = new Vector3(90, 0, 0) });
        root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.0f, BottomRadius = 0.045f, Height = 0.16f, Material = tip }, Position = new Vector3(0, 0, -0.73f), RotationDegrees = new Vector3(-90, 0, 0) });
        root.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.05f, 0.14f, 0.06f), Material = dark }, Position = new Vector3(0, -0.12f, -0.1f) });
    }

    const string PackLauncherNode = "PackLauncher";
    /// <summary>The paid pack's launcher is drawn at this share of its authored size (1.44 m long, 0.24 m across: too fat in the first-person view at full size).</summary>
    const float PackLauncherScale = 0.75f;
    /// <summary>Authored distances along the launcher: the pistol grip behind the model's origin, and the mouth in front of it.</summary>
    const float PackLauncherGrip = 0.2f, PackLauncherMouth = 0.627f;
    /// <summary>How far in front of the grip the mouth ends up, metres.</summary>
    const float PackLauncherMuzzle = (PackLauncherGrip + PackLauncherMouth) * PackLauncherScale;

    /// <summary>
    /// The Lock-on launcher of the paid pack (SM_Wep_RocketLauncher_01: tube, two handles and trigger, converted to glb/props/lock_on_launcher_01.glb
    /// by the pack's tools/export_multi_glb.gd; atlas Weapons_01). The pack's props face +Z with the grips under the tube, the hands hold things
    /// facing -Z: turned half round, with the pistol grip at the origin. False when the pack is not there.
    /// </summary>
    static bool AddPackLauncher(Node3D root)
    {
        if (SyntyLib.Instance("props/lock_on_launcher_01", "glb/atlas/military_weapons_01.png") is not { } model) return false;
        model.Name = PackLauncherNode;
        model.Transform = new Transform3D(new Basis(Vector3.Up, Mathf.Pi).Scaled(Vector3.One * PackLauncherScale), new Vector3(0, 0, -PackLauncherGrip * PackLauncherScale));
        root.AddChild(model);
        return true;
    }

    /// <summary>
    /// Stand-in Lock-on launcher (issue #347) for a build without the paid pack, laid along -Z: a long thin tube with a glass seeker in its mouth, a
    /// sight box on top, a grip with the battery ball under the front and a flared rear.
    /// </summary>
    static void BuildLockOnLauncher(Node3D root)
    {
        var olive = new StandardMaterial3D { AlbedoColor = new Color(0.27f, 0.31f, 0.2f), Roughness = 0.75f };
        var dark = new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.13f, 0.12f), Roughness = 0.7f };
        var glass = new StandardMaterial3D { AlbedoColor = new Color(0.25f, 0.4f, 0.45f), Roughness = 0.15f, Metallic = 0.4f };
        var along = new Vector3(90, 0, 0);
        root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.045f, BottomRadius = 0.045f, Height = 1.3f, Material = olive }, Position = new Vector3(0, 0, -0.22f), RotationDegrees = along });
        root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.045f, BottomRadius = 0.07f, Height = 0.1f, Material = dark }, Position = new Vector3(0, 0, 0.48f), RotationDegrees = along });
        root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.052f, BottomRadius = 0.052f, Height = 0.06f, Material = dark }, Position = new Vector3(0, 0, -0.85f), RotationDegrees = along });
        root.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.04f, Height = 0.08f, Material = glass }, Position = new Vector3(0, 0, -0.87f) });
        root.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.05f, 0.07f, 0.16f), Material = dark }, Position = new Vector3(0.03f, 0.075f, -0.25f) });
        root.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.04f, 0.13f, 0.05f), Material = dark }, Position = new Vector3(0, -0.11f, -0.05f) });
        root.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.04f, 0.1f, 0.05f), Material = dark }, Position = new Vector3(0, -0.095f, -0.5f) });
        root.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.045f, Height = 0.09f, Material = olive }, Position = new Vector3(0, -0.07f, -0.68f) });
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
