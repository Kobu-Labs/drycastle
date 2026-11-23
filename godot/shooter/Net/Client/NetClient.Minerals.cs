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
/// The Mineral Log on the client (N, action "mineral_log"; crystals.md): the key and the pause menu open the screen, Esc goes through <see cref="CloseMineralLog"/>, and
/// while it is open the shell leaves the mouse free (<see cref="MineralLogOpen"/>) and the legs keep working.
/// Finding (#401): the server says where the mineral nodes are and whether each can be taken now (<see cref="EventKind.MineralNodes"/>); a node in reach that is active and whose mineral the
/// player has not found shows the prompt, and holding the interact key for <see cref="MineralRules.HoldSeconds"/> (the ring of the other holds) sends <see cref="EventKind.MineralPick"/>.
/// The server answers with the player's finds (<see cref="EventKind.MineralFinds"/>), which feed the log and, for a find that just happened, play the pickup sound (#433).
/// </summary>
public partial class NetClient
{
    sealed class MineralNodeView
    {
        public MineralNode Node = null!;
        public bool Active;
        public Node3D Visual = null!;
    }

    readonly Dictionary<byte, MineralNodeView> _mineralNodes = new();
    /// <summary>The player's finds as the server last said (mineral id to the place among its finders).</summary>
    IReadOnlyDictionary<string, int> _mineralRanks = new Dictionary<string, int>();
    /// <summary>The node in reach that can be taken (from the last render; null when none).</summary>
    byte? _mineralNear;
    InteractHold _mineralHold;

    /// <summary>The pickup sound of a mineral (sounds.json, #433).</summary>
    const string MineralFoundSound = "mineral.found";

    /// <summary>Called for every event: the nodes and the finds.</summary>
    void OnMineralEvent(EventKind kind, byte[] payload)
    {
        if (kind == EventKind.MineralNodes && MineralRules.DecodeNodes(payload) is { } nodes) ApplyMineralNodes(nodes);
        else if (kind == EventKind.MineralFinds && MineralRules.DecodeFinds(payload) is { } finds)
        {
            _mineralRanks = finds.Ranks;
            _hud?.SetMineralFinds(_mineralRanks);
            if (finds.JustFound is { } found)
            {
                Sfx.Ui(MineralFoundSound);
                if (Minerals.Find(found) is { } m && _mineralRanks.TryGetValue(found, out var rank))
                    UiShell.Instance?.Toast(MineralFormat.FoundLine(Strings.T("toast.mineral.found"), Strings.T("mineral." + m.Id), rank), ToastKind.Info);
            }
        }
    }

    void ApplyMineralNodes(IReadOnlyList<(MineralNode Node, bool Active)> nodes)
    {
        // the set is the whole world's: a node it no longer lists (a dropped mineral somebody took, or the round ended) is gone
        foreach (var id in _mineralNodes.Keys.Where(k => nodes.All(n => n.Node.Id != k)).ToList())
        {
            if (id >= MineralNodeSet.FirstDynamicId && _mineralNodes[id].Visual.IsInsideTree()) PhoenixFx.FirePuff(Owned, _mineralNodes[id].Visual.GlobalPosition); // a drop's minute is over: it burns away
            _mineralNodes[id].Visual.QueueFree();
            _mineralNodes.Remove(id);
        }
        foreach (var (node, active) in nodes)
        {
            if (!_mineralNodes.TryGetValue(node.Id, out var v))
            {
                v = new MineralNodeView { Node = node, Visual = MakeMineralVisual(node.MineralId) };
                v.Visual.Name = $"MineralNode{node.Id}";
                v.Visual.Position = new Vector3(node.Position.X, node.Position.Y, node.Position.Z);
                Owned.AddChild(v.Visual);
                _mineralNodes[node.Id] = v;
            }
            v.Active = active;
        }
    }

    /// <summary>
    /// The crystal of a fantasy mineral is the crystal lab's <see cref="CrystalFx"/> (the pack's crystal rock, re-dressed; without the private pack it builds its own stand-in rock). A real-life stone has no
    /// effects yet and gets the plain stand-in: a cluster of emissive prisms on a stone.
    /// </summary>
    static Node3D MakeMineralVisual(string mineralId)
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
    void RenderMinerals()
    {
        _mineralNear = null;
        if (_mineralNodes.Count == 0 || _me == null || Dead || _driving) return;
        var at = _me.GlobalPosition;
        var best = MineralRules.ReachMetres;
        foreach (var (id, v) in _mineralNodes)
        {
            if (!v.Active || _mineralRanks.ContainsKey(v.Node.MineralId)) continue;
            var p = v.Visual.GlobalPosition;
            if (Mathf.Abs(p.Y - at.Y) > 3f) continue;
            var d = new Vector2(p.X - at.X, p.Z - at.Z).Length();
            if (d <= best) { _mineralNear = id; best = d; }
        }
    }

    /// <summary>The prompt for the node in reach while the key is not held (the ring carries the hold); null otherwise.</summary>
    string? MineralPrompt()
    {
        if (_mineralNear is not { } id || _mineralHold.Held > 0 || !_mineralNodes.TryGetValue(id, out var v)) return null;
        return Strings.F("hud.mineral.hold", InputBindings.Name("interact"), Minerals.Find(v.Node.MineralId)?.Name ?? v.Node.MineralId);
    }

    /// <summary>Runs on the input tick: the hold of the interact key at the node in reach; completed, it sends the pick once.</summary>
    void TickMinerals(double dt)
    {
        var use = Input.MouseMode == Input.MouseModeEnum.Captured && Input.IsActionPressed("interact");
        if (_mineralHold.Tick(use, _mineralNear != null, dt, MineralRules.HoldSeconds) && _mineralNear is { } id)
            Tx(_peer, Wire.ChanReliable, Wire.Encode(new EventMsg((byte)EventKind.MineralPick, MineralRules.EncodePick(id))), (int)ENetPacketPeer.FlagReliable);
    }

    /// <summary>How full the hold ring is for a node (0 when not holding on one).</summary>
    float MineralHoldFraction => _mineralHold.Held > 0 && _mineralNear != null ? (float)Math.Min(1.0, _mineralHold.Held / MineralRules.HoldSeconds) : 0f;

    /// <summary>The Mineral Log is up: the mouse is released for it.</summary>
    public bool MineralLogOpen => _hud?.MineralLogOpen ?? false;

    /// <summary>Esc: closes it. True when it was open (the shell then does not open Pause for this Esc).</summary>
    public bool CloseMineralLog() => _hud?.BackMineralLog() ?? false;

    /// <summary>Opens the log (the pause menu's entry).</summary>
    public void OpenMineralLog() => _hud?.OpenMineralLog();

    /// <summary>The key toggles the screen, but not while a menu, the chat box or the corpse chooser is up (<see cref="OverlayArbiter"/>). True when the key was used.</summary>
    bool HandleMineralLogInput(InputEvent e)
    {
        if (_hud == null || Bot || e.IsEcho() || !e.IsActionPressed("mineral_log")) return false;
        if (!_hud.ToggleMineralLog(MenuOpen?.Invoke() == true, ChooserOpen)) return false;
        GetViewport().SetInputAsHandled();
        return true;
    }
}
