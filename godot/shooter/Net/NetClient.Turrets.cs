using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// Sentry turrets on the client: the primitive-built turret (a base, a head and a barrel, no imported model), its damage
/// smoke (<see cref="VehicleFx.Damage"/>, the vehicles' own), the placing ghost and its prompt. The ghost is drawn only for
/// its owner, green when the spot is valid and red when not; the prompt says the same in words, so colour is never the only cue.
/// The server decides validity and placement; the client only shows the state it sends.
/// </summary>
public partial class NetClient
{
    sealed class TurretView
    {
        public Node3D Node = null!;
        public MeshInstance3D Barrel = null!;
        public MeshInstance3D[] Parts = null!;
        public VehicleFx.Damage Smoke = null!;
        public ushort Owner;
        public byte LastState = 255;
    }

    static readonly Vector3 TurretSmokeAt = new(0, 1.05f, 0);
    static readonly float[] TurretDamageStages = { 0.66f, 0.33f, 0f };

    readonly Dictionary<ushort, TurretView> _turrets = new();
    /// <summary>The owner's placing ghost is up and its spot is valid; null when there is no ghost of ours.</summary>
    bool? _ghostValid;

    /// <summary>Called once from <see cref="_Ready"/>: registers the turret type and follows its entities.</summary>
    void InitTurrets()
    {
        TurretRules.Register(WorldEntityRegistry.Shared);
        WorldEntities.Spawned += OnTurretSpawned;
        WorldEntities.Despawned += OnTurretDespawned;
    }

    void OnTurretSpawned(WorldReplica.Entity e)
    {
        if (e.Type != TurretRules.TypeId) return;
        var node = new Node3D { Name = $"Turret{e.Id}" };
        var parts = new[]
        {
            Part(node, new BoxMesh { Size = new Vector3(0.7f, 0.25f, 0.7f) }, new Vector3(0, 0.125f, 0)),                 // base
            Part(node, new BoxMesh { Size = new Vector3(0.45f, 0.3f, 0.45f) }, new Vector3(0, 0.55f, 0)),                 // head
        };
        var barrel = Part(node, new BoxMesh { Size = new Vector3(0.12f, 0.12f, 0.7f) }, new Vector3(0, 0.6f, -0.5f));  // barrel
        var smoke = VehicleFx.Damage.MakeAt(node, TurretSmokeAt);
        Owned.AddChild(node);
        _turrets[e.Id] = new TurretView { Node = node, Barrel = barrel, Parts = new[] { parts[0], parts[1], barrel }, Smoke = smoke, Owner = e.State.Owner };
    }

    void OnTurretDespawned(ushort id)
    {
        if (!_turrets.Remove(id, out var v)) return;
        v.Smoke.Stop();
        v.Node.QueueFree();
    }

    static MeshInstance3D Part(Node3D parent, Mesh mesh, Vector3 at)
    {
        var m = new MeshInstance3D { Mesh = mesh, Position = at, MaterialOverride = TurretMaterials.Solid };
        parent.AddChild(m);
        return m;
    }

    /// <summary>Places the turrets, shows the ghost to its owner only, and follows the damage stage.</summary>
    void RenderTurrets()
    {
        _ghostValid = null;
        foreach (var (id, v) in _turrets)
        {
            if (!WorldEntities.Entities.TryGetValue(id, out var e)) continue;
            var s = e.State;
            var p = s.Position;
            v.Node.GlobalPosition = new Vector3(p.X, p.Y, p.Z);
            v.Node.Rotation = new Vector3(0, s.YawRad, 0);
            var ghost = TurretRules.IsGhost(s.State);
            v.Node.Visible = !ghost || v.Owner == _myId;
            if (ghost)
            {
                if (v.Owner == _myId) _ghostValid = TurretRules.IsGhostValid(s.State);
                if (s.State != v.LastState) SetGhostLook(v, TurretRules.IsGhostValid(s.State));
            }
            else
            {
                if (v.LastState != s.State) SetArmedLook(v, TurretRules.IsFlashing(s.State));
                v.Smoke.Near(true);
                v.Smoke.Set(VehicleDamage.Stage(s.Hp, 255f, TurretDamageStages));
            }
            v.LastState = s.State;
        }
    }

    static void SetGhostLook(TurretView v, bool valid)
    {
        var mat = valid ? TurretMaterials.GhostValid : TurretMaterials.GhostInvalid;
        foreach (var p in v.Parts) p.MaterialOverride = mat;
        v.Smoke.Stop();
    }

    static void SetArmedLook(TurretView v, bool flashing)
    {
        foreach (var p in v.Parts) p.MaterialOverride = TurretMaterials.Solid;
        v.Barrel.MaterialOverride = flashing ? TurretMaterials.Muzzle : TurretMaterials.Solid;
    }

    /// <summary>The placement prompt while our ghost is up (null otherwise): the validity in words, the keys to confirm or cancel.</summary>
    string? TurretPrompt()
    {
        if (_ghostValid is not { } valid || Dead || _driving) return null;
        var confirm = InputBindings.Name("interact");
        var cancel = InputBindings.Name("killstreak");
        return valid ? Strings.F("hud.turret.valid", confirm, cancel) : Strings.F("hud.turret.invalid", confirm, cancel);
    }
}

/// <summary>The turret's materials, built once: solid steel, the two ghost tints (ShooterTheme's colour-blind-aware pair) and the muzzle glow.</summary>
static class TurretMaterials
{
    public static readonly StandardMaterial3D Solid = new() { AlbedoColor = new Color(0.30f, 0.32f, 0.30f), Roughness = 0.7f, Metallic = 0.3f };
    public static readonly StandardMaterial3D Muzzle = new() { AlbedoColor = new Color(0.30f, 0.32f, 0.30f), EmissionEnabled = true, Emission = new Color(1f, 0.75f, 0.3f), EmissionEnergyMultiplier = 3f };
    public static readonly StandardMaterial3D GhostValid = Ghost(ShooterTheme.Good);
    public static readonly StandardMaterial3D GhostInvalid = Ghost(ShooterTheme.Danger);

    static StandardMaterial3D Ghost(Color tint)
    {
        var c = tint; c.A = 0.45f;
        return new StandardMaterial3D { AlbedoColor = c, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
    }
}
