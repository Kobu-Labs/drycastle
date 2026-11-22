using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Care packages on the client (ticket #124, reworked in #151): the crate on its parachute while it falls and on the ground
/// once landed, the open prompt within reach, and the hold of the interact key (<see cref="CarePackageRules.OpenHoldSeconds"/>).
/// The hold shows a <see cref="RadialProgress"/> ring that fills over the hold and drains when the key is released. The
/// crate is freed the moment the server despawns it (opened or expired). The reward streak arrives as StreakGranted, which
/// <see cref="NetClient.Killstreaks"/> turns into a toast.
/// </summary>
public partial class NetClient
{
    /// <summary>Seconds for the open ring to drain back to empty after the key is released (instant with reduce motion).</summary>
    const float RingDrainSeconds = 0.25f;

    sealed class CrateView
    {
        public Node3D Node = null!;
        public MeshInstance3D Canopy = null!;
    }

    readonly Dictionary<ushort, CrateView> _crates = new();
    /// <summary>The landed crate within reach, from the last render (null when none).</summary>
    ushort? _crateNear;
    /// <summary>The interact key's hold at the crate: seconds held, and whether the open request for this hold is sent (<see cref="InteractHold"/>).</summary>
    InteractHold _crateHold;
    /// <summary>The ring's shown fill (0 to 1): follows the hold up and drains down after a release.</summary>
    float _ringShown;
    /// <summary>The crate whose open request is sent and the time until which it is treated as gone (hidden, no prompt, no hold) while the server's despawn is on its way. A refused open brings it back after the wait.</summary>
    (ushort Id, double Until)? _crateOpening;
    const double CrateOpeningWaitSeconds = 1.5;

    /// <summary>Called once from <see cref="_Ready"/>: registers the crate type and follows the crate entities.</summary>
    void InitCarePackages()
    {
        CarePackageRules.Register(WorldEntityRegistry.Shared);
        WorldEntities.Spawned += OnCrateSpawned;
        WorldEntities.Despawned += OnCrateDespawned;
    }

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
        Owned.AddChild(node);
        _crates[e.Id] = new CrateView { Node = node, Canopy = canopy };
    }

    /// <summary>
    /// The server despawned the crate (opened, expired or round end): the node is freed at once, and the open ring
    /// goes with it when this was the crate in reach.
    /// </summary>
    void OnCrateDespawned(ushort id)
    {
        if (!_crates.Remove(id, out var v)) return;
        v.Node.QueueFree();
        if (_crateNear == id)
        {
            _crateNear = null;
            _crateHold.Reset();
            _ringShown = 0;
            _hud?.SetRadial(null);
        }
    }

    /// <summary>Places the crates (the canopy shows while they fall) and finds the one in reach.</summary>
    void RenderCarePackages()
    {
        foreach (var (id, v) in _crates)
        {
            if (!WorldEntities.Entities.TryGetValue(id, out var e)) continue;
            var p = e.State.Position;
            v.Node.GlobalPosition = new Vector3(p.X, p.Y, p.Z);
            v.Canopy.Visible = e.State.State == CarePackageRules.StateFalling;
            v.Node.Visible = !IsCrateOpening(id);
        }
        _crateNear = NearestCrateInReach();
    }

    bool IsCrateOpening(ushort id) => _crateOpening is { } o && o.Id == id && Time.GetTicksMsec() / 1000.0 < o.Until;

    ushort? NearestCrateInReach()
    {
        if (_me == null || Dead || _driving) return null;
        ushort? best = null;
        var bd = CarePackageRules.ReachMetres;
        var at = _me.GlobalPosition;
        foreach (var (id, v) in _crates)
        {
            if (IsCrateOpening(id) || !WorldEntities.Entities.TryGetValue(id, out var e) || e.State.State != CarePackageRules.StateLanded) continue;
            var d = v.Node.GlobalPosition.DistanceTo(at);
            if (d <= bd) { best = id; bd = d; }
        }
        return best;
    }

    /// <summary>
    /// The crate prompt in reach while the key is not held. While the key is held the open ring carries the state
    /// ("Opening..."), so the prompt steps aside; null otherwise.
    /// </summary>
    string? CratePrompt()
    {
        if (_crateNear == null || _crateHold.Held > 0 || _crateHold.Sent) return null;
        return Strings.F("hud.crate.hold", InputBindings.Name("interact"));
    }

    /// <summary>
    /// Hold of the interact key at a landed crate. Held for <see cref="CarePackageRules.OpenHoldSeconds"/> sends the open
    /// request once; releasing or leaving reach before that cancels the hold. Runs on the input tick.
    /// </summary>
    void TickCarePackages(double dt)
    {
        var use = Input.MouseMode == Input.MouseModeEnum.Captured && Input.IsActionPressed("interact");
        if (_crateHold.Tick(use, _crateNear != null, dt, CarePackageRules.OpenHoldSeconds) && _crateNear is { } id)
        {
            SendOpenCarePackage(id);
            _crateOpening = (id, Time.GetTicksMsec() / 1000.0 + CrateOpeningWaitSeconds); // gone for the player now; the despawn confirms
            _crateNear = null;
        }
        TickRozhlasSetHold(use, dt); // the other hold of the same key (NetClient.Rozhlas.cs); the ring shows whichever is on
        ShowHoldRing(dt);
    }

    /// <summary>The ring fills with the hold and drains after a release; with reduce motion the drain is instant.</summary>
    void ShowHoldRing(double dt)
    {
        var target = _crateHold.Held > 0 && _crateNear != null && !_crateHold.Sent ? (float)Math.Min(1.0, _crateHold.Held / CarePackageRules.OpenHoldSeconds) : 0f;
        target = Math.Max(target, Math.Max(RozhlasSetHoldFraction, Math.Max(LootHoldFraction, MineralHoldFraction)));
        if (target >= _ringShown || GameSettings.Current.ReduceMotion) _ringShown = target;
        else _ringShown = Mathf.MoveToward(_ringShown, target, (float)dt / RingDrainSeconds);
        _hud?.SetRadial(_ringShown > 0 ? _ringShown : null);
    }

    void SendOpenCarePackage(ushort crate) =>
        Tx(_peer, Wire.ChanReliable, Wire.Encode(new EventMsg((byte)EventKind.OpenCarePackage, CarePackageCodec.EncodeOpen(crate))), (int)ENetPacketPeer.FlagReliable);
}
