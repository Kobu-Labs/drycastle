using System.Globalization;
using Godot;
using Suchohrad.ShooterCore;

namespace Suchohrad.Shooter;

/// <summary>
/// Care packages on the client (ticket #124): the crate on its parachute while it falls and on the ground once landed, the
/// open prompt within reach, the hold of the interact key (<see cref="CarePackageRules.OpenHoldSeconds"/>; releasing early
/// cancels it), and the perk the server grants (<see cref="EventKind.PerkGained"/>), which replaces <see cref="Perks"/>
/// for prediction and raises the toast. The server checks the reach and the state again.
/// </summary>
public partial class NetClient
{
    sealed class CrateView
    {
        public Node3D Node = null!;
        public MeshInstance3D Canopy = null!;
    }

    readonly Dictionary<ushort, CrateView> _crates = new();
    /// <summary>The landed crate within reach, from the last render (null when none).</summary>
    ushort? _crateNear;
    /// <summary>Seconds the interact key has been held at the crate; 0 when not holding.</summary>
    double _crateHeld;
    /// <summary>The open request for this hold has been sent; nothing more until the key is released.</summary>
    bool _crateSent;

    /// <summary>Called once from <see cref="_Ready"/>: registers the crate type, follows the crate entities and listens for the perk.</summary>
    void InitCarePackages()
    {
        CarePackageRules.Register(WorldEntityRegistry.Shared);
        WorldEntities.Spawned += OnCrateSpawned;
        WorldEntities.Despawned += OnCrateDespawned;
        EventReceived += OnCarePackageEvent;
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
        World.AddChild(node);
        _crates[e.Id] = new CrateView { Node = node, Canopy = canopy };
    }

    void OnCrateDespawned(ushort id)
    {
        if (!_crates.Remove(id, out var v)) return;
        v.Node.QueueFree();
        if (_crateNear == id) _crateNear = null;
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
        }
        _crateNear = NearestCrateInReach();
    }

    ushort? NearestCrateInReach()
    {
        if (_me == null || Dead || _driving) return null;
        ushort? best = null;
        var bd = CarePackageRules.ReachMetres;
        var at = _me.GlobalPosition;
        foreach (var (id, v) in _crates)
        {
            if (!WorldEntities.Entities.TryGetValue(id, out var e) || e.State.State != CarePackageRules.StateLanded) continue;
            var d = v.Node.GlobalPosition.DistanceTo(at);
            if (d <= bd) { best = id; bd = d; }
        }
        return best;
    }

    /// <summary>The crate prompt in reach: the hold hint, then the countdown while the key is held; null otherwise.</summary>
    string? CratePrompt()
    {
        if (_crateNear == null) return null;
        if (_crateHeld <= 0) return Strings.F("hud.crate.hold", InputBindings.Name("interact"));
        var left = Math.Max(0, CarePackageRules.OpenHoldSeconds - _crateHeld);
        return Strings.F("hud.crate.opening", left.ToString("0.0", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Hold of the interact key at a landed crate. Held for <see cref="CarePackageRules.OpenHoldSeconds"/> sends the open
    /// request once; releasing or leaving reach before that cancels the hold. Runs on the input tick.
    /// </summary>
    void TickCarePackages(double dt)
    {
        var use = Input.MouseMode == Input.MouseModeEnum.Captured && Input.IsActionPressed("interact");
        if (!use)
        {
            _crateHeld = 0;
            _crateSent = false;
            return;
        }
        if (_crateNear is not { } id)
        {
            _crateHeld = 0;
            return;
        }
        if (_crateSent) return;
        _crateHeld += dt;
        if (_crateHeld < CarePackageRules.OpenHoldSeconds) return;
        SendOpenCarePackage(id);
        _crateSent = true;
    }

    void SendOpenCarePackage(ushort crate) =>
        Tx(_peer, Wire.ChanReliable, Wire.Encode(new EventMsg((byte)EventKind.OpenCarePackage, CarePackageCodec.EncodeOpen(crate))), (int)ENetPacketPeer.FlagReliable);

    /// <summary>The perk the server granted: it replaces the perk used for prediction, and the toast names it.</summary>
    void OnCarePackageEvent(EventKind kind, byte[] payload)
    {
        if (kind != EventKind.PerkGained || CarePackageCodec.DecodePerk(payload) is not { } id || PerkData?.Find(id) is not { } perk) return;
        Perks = PerkModifiers.Of(perk);
        UiShell.Instance?.Toast(Strings.F("toast.crate.perk", perk.Name), ToastKind.Info);
    }
}
