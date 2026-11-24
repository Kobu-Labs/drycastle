using Godot;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.CarePackages;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client.Gear;

/// <summary>
/// Care packages on the client (ticket #124, reworked in #151): the crate on its parachute while it falls and on the ground
/// once landed, the open prompt within reach, and the hold of the interact key (<see cref="CarePackageRules.OpenHoldSeconds"/>).
/// The hold shows a <see cref="HoldRing"/> that fills over the hold and drains when the key is released. The
/// crate is freed the moment the server despawns it (opened or expired). The reward streak arrives as StreakGranted, which
/// <see cref="KillstreaksClient"/> turns into a toast.
/// </summary>
internal sealed class CarePackagesClient
{
    readonly ClientContext _ctx;
    readonly InteractTargets _targets;
    readonly HoldRing _ring;

    public CarePackagesClient(ClientContext ctx, InteractTargets targets, HoldRing ring)
    {
        _ctx = ctx; _targets = targets; _ring = ring;
        CarePackageRules.Register(WorldEntityRegistry.Shared);
        ctx.WorldEntities.Spawned += OnCrateSpawned;
        ctx.WorldEntities.Despawned += OnCrateDespawned;
    }

    sealed class CrateView
    {
        public Node3D Node = null!;
        public MeshInstance3D Canopy = null!;
        /// <summary>The crate body (hidden while only the smoke canister is out).</summary>
        public MeshInstance3D Box = null!;
        /// <summary>The thrown smoke canister and its smoke: shown while the canister flies and marks the spot.</summary>
        public Node3D Canister = null!;
        public CpuParticles3D Smoke = null!;
        /// <summary>Where the canister lay (seen while it marked), so the smoke keeps rising there while the crate comes down. Null for a crate first seen falling.</summary>
        public Vector3? Landing;
    }

    readonly Dictionary<ushort, CrateView> _crates = new();
    /// <summary>The interact key's hold at the crate: seconds held, and whether the open request for this hold is sent (<see cref="InteractHold"/>).</summary>
    InteractHold _crateHold;
    /// <summary>The crate whose open request is sent and the time until which it is treated as gone (hidden, no prompt, no hold) while the server's despawn is on its way. A refused open brings it back after the wait.</summary>
    (ushort Id, double Until)? _crateOpening;
    const double CrateOpeningWaitSeconds = 1.5;

    static StandardMaterial3D CrateMaterial(Color colour) => new() { AlbedoColor = colour };

    void OnCrateSpawned(WorldReplica.Entity e)
    {
        if (e.Type != CarePackageRules.TypeId) return;
        var node = new Node3D { Name = $"CarePackage{e.Id}" };
        var box = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.9f, 0.6f, 0.6f) },
            MaterialOverride = CrateMaterial(new Color(0.36f, 0.40f, 0.26f)),
            Position = new Vector3(0, 0.3f, 0),
        };
        var canopy = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 2.2f, Height = 2.2f },
            MaterialOverride = CrateMaterial(new Color(0.85f, 0.85f, 0.80f)),
            Scale = new Vector3(1, 0.45f, 1),
            Position = new Vector3(0, 4.5f, 0),
        };
        node.AddChild(box);
        node.AddChild(canopy);
        // the canister the owner threw: a small orange can with a coloured smoke column, so the spot reads from afar
        var canister = new Node3D { Name = "Canister" };
        canister.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.06f, BottomRadius = 0.06f, Height = 0.2f },
            MaterialOverride = CrateMaterial(new Color(0.9f, 0.45f, 0.1f)),
            Position = new Vector3(0, 0.1f, 0),
        });
        var smoke = new CpuParticles3D
        {
            Mesh = new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 8, Rings = 4 },
            Amount = 60, Lifetime = 5.0, LocalCoords = false, Emitting = false,
            Direction = Vector3.Up, Spread = 12f, InitialVelocityMin = 3f, InitialVelocityMax = 5f, Gravity = new Vector3(0, 0.6f, 0),
            ScaleAmountMin = 1.2f, ScaleAmountMax = 2.6f, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.55f, 0.1f, 0.55f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
            Position = new Vector3(0, 0.2f, 0),
        };
        canister.AddChild(smoke);
        node.AddChild(canister);
        _ctx.Owned.AddChild(node);
        _crates[e.Id] = new CrateView { Node = node, Canopy = canopy, Box = box, Canister = canister, Smoke = smoke };
    }

    /// <summary>
    /// The server despawned the crate (opened, expired or round end): the node is freed at once, and the open ring
    /// goes with it when this was the crate in reach.
    /// </summary>
    void OnCrateDespawned(ushort id)
    {
        if (!_crates.Remove(id, out var v)) return;
        v.Node.QueueFree();
        if (_targets.Crate == id)
        {
            _targets.Crate = null;
            _crateHold.Reset();
            _ring.Reset();
        }
    }

    /// <summary>Places the crates (the canopy shows while they fall) and finds the one in reach.</summary>
    public void Render()
    {
        foreach (var (id, v) in _crates)
        {
            if (!_ctx.WorldEntities.Entities.TryGetValue(id, out var e)) continue;
            var p = e.State.Position;
            var flying = e.State.State == CarePackageRules.StateThrown && e.Buf.At(_ctx.RenderTick) != null;
            if (flying) p = e.Buf.At(_ctx.RenderTick)!.Value.Pos; // the canister's arc is smoothed between snapshots
            v.Node.GlobalPosition = new Vector3(p.X, p.Y, p.Z);
            var st = e.State.State;
            var canisterOnly = st == CarePackageRules.StateThrown || st == CarePackageRules.StateMarking;
            v.Canopy.Visible = st == CarePackageRules.StateFalling;
            v.Box.Visible = !canisterOnly;
            v.Canister.Visible = canisterOnly;
            // the smoke starts at the throw and keeps pouring while the crate comes down, until it lands
            if (st == CarePackageRules.StateMarking) v.Landing = new Vector3(p.X, p.Y, p.Z);
            var smoking = canisterOnly || (st == CarePackageRules.StateFalling && v.Landing != null);
            if (v.Smoke.Emitting != smoking) v.Smoke.Emitting = smoking;
            v.Smoke.GlobalPosition = st == CarePackageRules.StateFalling && v.Landing is { } l ? l + new Vector3(0, 0.2f, 0) : v.Node.GlobalPosition + new Vector3(0, 0.2f, 0);
            v.Node.Visible = !IsCrateOpening(id);
        }
        _targets.Crate = NearestInReach();
    }

    bool IsCrateOpening(ushort id) => _crateOpening is { } o && o.Id == id && _ctx.Now < o.Until;

    ushort? NearestInReach()
    {
        if (_ctx.Me == null || _ctx.Dead || _ctx.Driving) return null;
        ushort? best = null;
        var bd = CarePackageRules.ReachMetres;
        var at = _ctx.Me.GlobalPosition;
        foreach (var (id, v) in _crates)
        {
            if (IsCrateOpening(id) || !_ctx.WorldEntities.Entities.TryGetValue(id, out var e) || e.State.State != CarePackageRules.StateLanded) continue;
            var d = v.Node.GlobalPosition.DistanceTo(at);
            if (d <= bd) { best = id; bd = d; }
        }
        return best;
    }

    /// <summary>
    /// The crate prompt in reach while the key is not held. While the key is held the open ring carries the state
    /// ("Opening..."), so the prompt steps aside; null otherwise.
    /// </summary>
    public string? Prompt()
    {
        if (_targets.Crate == null || _crateHold.Held > 0 || _crateHold.Sent) return null;
        return Strings.F("hud.crate.hold", InputBindings.Name("interact"));
    }

    /// <summary>
    /// Hold of the interact key at a landed crate. Held for <see cref="CarePackageRules.OpenHoldSeconds"/> sends the open
    /// request once; releasing or leaving reach before that cancels the hold. Runs on the input tick.
    /// </summary>
    public void Tick(bool use, double dt)
    {
        if (_crateHold.Tick(use, _targets.Crate != null, dt, CarePackageRules.OpenHoldSeconds) && _targets.Crate is { } id)
        {
            SendOpenCarePackage(id);
            _crateOpening = (id, _ctx.Now + CrateOpeningWaitSeconds); // gone for the player now; the despawn confirms
            _targets.Crate = null;
        }
    }

    /// <summary>How far the open hold is (0 to 1): the ring fills with it.</summary>
    public float HoldFraction => _crateHold.Held > 0 && _targets.Crate != null && !_crateHold.Sent ? (float)Math.Min(1.0, _crateHold.Held / CarePackageRules.OpenHoldSeconds) : 0f;

    void SendOpenCarePackage(ushort crate) => _ctx.Send(EventKind.OpenCarePackage, CarePackageCodec.EncodeOpen(crate));
}
