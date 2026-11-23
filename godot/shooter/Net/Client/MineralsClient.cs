using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Crystals;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Wildlife;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Mining;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// Finding minerals on the client (#401, crystals.md): the server says where the mineral nodes are and whether each can be taken now (<see cref="EventKind.MineralNodes"/>); a node in reach that is active and whose mineral the
/// player has not found shows the prompt, and holding the interact key for <see cref="MineralRules.HoldSeconds"/> (the ring of the other holds) sends <see cref="EventKind.MineralPick"/>.
/// The server answers with the player's finds (<see cref="EventKind.MineralFinds"/>), which feed the log and, for a find that just happened, play the pickup sound (#433). The log's key is <see cref="ScreensClient"/>'s.
/// </summary>
public sealed class MineralsClient
{
    sealed class NodeView
    {
        public MineralNode Node = null!;
        public bool Active;
        public Node3D Visual = null!;
    }

    /// <summary>The pickup sound of a mineral (sounds.json, #433).</summary>
    const string FoundSound = "mineral.found";

    readonly ClientContext _ctx;
    readonly Dictionary<byte, NodeView> _nodes = new();
    /// <summary>The player's finds as the server last said (mineral id to the place among its finders).</summary>
    IReadOnlyDictionary<string, int> _ranks = new Dictionary<string, int>();
    /// <summary>The node in reach that can be taken (from the last render; null when none).</summary>
    byte? _near;
    InteractHold _hold;

    public MineralsClient(ClientContext ctx) { _ctx = ctx; }

    /// <summary>How full the hold ring is for a node (0 when not holding on one).</summary>
    public float HoldFraction => _hold.Held > 0 && _near != null ? (float)Math.Min(1.0, _hold.Held / MineralRules.HoldSeconds) : 0f;

    /// <summary>Called for every event: the nodes and the finds.</summary>
    public void OnEvent(EventKind kind, byte[] payload)
    {
        if (kind == EventKind.MineralNodes && MineralRules.DecodeNodes(payload) is { } nodes) ApplyNodes(nodes);
        else if (kind == EventKind.MineralFinds && MineralRules.DecodeFinds(payload) is { } finds)
        {
            _ranks = finds.Ranks;
            _ctx.Hud?.SetMineralFinds(_ranks);
            if (finds.JustFound is { } found)
            {
                Sfx.Ui(FoundSound);
                if (Minerals.Find(found) is { } m && _ranks.TryGetValue(found, out var rank))
                    UiShell.Instance?.Toast(MineralFormat.FoundLine(Strings.T("toast.mineral.found"), Strings.T("mineral." + m.Id), rank), ToastKind.Info);
            }
        }
    }

    void ApplyNodes(IReadOnlyList<(MineralNode Node, bool Active)> nodes)
    {
        // the set is the whole world's: a node it no longer lists (a dropped mineral somebody took, or the round ended) is gone
        foreach (var id in _nodes.Keys.Where(k => nodes.All(n => n.Node.Id != k)).ToList())
        {
            if (id >= MineralNodeSet.FirstDynamicId && _nodes[id].Visual.IsInsideTree()) PhoenixFx.FirePuff(_ctx.Owned, _nodes[id].Visual.GlobalPosition); // a drop's minute is over: it burns away
            _nodes[id].Visual.QueueFree();
            _nodes.Remove(id);
        }
        foreach (var (node, active) in nodes)
        {
            if (!_nodes.TryGetValue(node.Id, out var v))
            {
                v = new NodeView { Node = node, Visual = MakeVisual(node.MineralId) };
                v.Visual.Name = $"MineralNode{node.Id}";
                v.Visual.Position = new Vector3(node.Position.X, node.Position.Y, node.Position.Z);
                _ctx.Owned.AddChild(v.Visual);
                _nodes[node.Id] = v;
            }
            v.Active = active;
        }
    }

    /// <summary>
    /// The crystal of a fantasy mineral is the crystal lab's <see cref="CrystalFx"/> (the pack's crystal rock, re-dressed; without the private pack it builds its own stand-in rock). A real-life stone has no
    /// effects yet and gets the plain stand-in: a cluster of emissive prisms on a stone.
    /// </summary>
    static Node3D MakeVisual(string mineralId)
    {
        if (CrystalFx.Make(mineralId) is { } fx) return fx; // the crystal lab's model and effects (fantasy minerals); the real-life stones have none yet
        var root = new Node3D();
        root.AddChild(new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.45f, Height = 0.6f },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.3f, 0.32f) },
            Position = new Vector3(0, 0.1f, 0),
        });
        var glow = new Color(0.45f, 0.95f, 0.35f);
        var gem = new StandardMaterial3D { AlbedoColor = glow, EmissionEnabled = true, Emission = glow, EmissionEnergyMultiplier = 0.8f };
        for (var i = 0; i < 5; i++)
        {
            var a = i * Mathf.Tau / 5f;
            var h = 0.5f + 0.12f * (i % 3);
            root.AddChild(new MeshInstance3D
            {
                Mesh = new PrismMesh { Size = new Vector3(0.16f, h, 0.16f) },
                MaterialOverride = gem,
                Position = new Vector3(Mathf.Cos(a) * 0.18f, 0.35f + h / 2f, Mathf.Sin(a) * 0.18f),
                RotationDegrees = new Vector3(Mathf.Sin(a) * 18f, 0, -Mathf.Cos(a) * 18f),
            });
        }
        return root;
    }

    /// <summary>Finds the node in reach that the player can take: active, near (flat) and not found yet.</summary>
    public void Render()
    {
        _near = null;
        if (_nodes.Count == 0 || _ctx.Me == null || _ctx.Dead || _ctx.Driving) return;
        var at = _ctx.Me.GlobalPosition;
        var best = MineralRules.ReachMetres;
        foreach (var (id, v) in _nodes)
        {
            if (!v.Active || _ranks.ContainsKey(v.Node.MineralId)) continue;
            var p = v.Visual.GlobalPosition;
            if (Mathf.Abs(p.Y - at.Y) > 3f) continue;
            var d = new Vector2(p.X - at.X, p.Z - at.Z).Length();
            if (d <= best) { _near = id; best = d; }
        }
    }

    /// <summary>The prompt for the node in reach while the key is not held (the ring carries the hold); null otherwise.</summary>
    public string? Prompt()
    {
        if (_near is not { } id || _hold.Held > 0 || !_nodes.TryGetValue(id, out var v)) return null;
        return Strings.F("hud.mineral.hold", InputBindings.Name("interact"), Minerals.Find(v.Node.MineralId)?.Name ?? v.Node.MineralId);
    }

    /// <summary>Runs on the input tick: the hold of the interact key at the node in reach; completed, it sends the pick once.</summary>
    public void Tick(double dt)
    {
        var use = Input.MouseMode == Input.MouseModeEnum.Captured && Input.IsActionPressed("interact");
        if (_hold.Tick(use, _near != null, dt, MineralRules.HoldSeconds) && _near is { } id)
            _ctx.Send(EventKind.MineralPick, MineralRules.EncodePick(id));
    }
}
