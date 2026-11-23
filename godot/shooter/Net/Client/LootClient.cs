using Godot;
using Drycastle.Shooter.HeadsUp;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Thieves;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Thieves;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The Loot of the Thieves event on the client (GLOSSARY "Loot"; docs/events/thieves/04): Loot that lies on the ground (world entity <see cref="LootRules.TypeId"/>, drawn with the kind's prop,
/// <see cref="LootProp.Lying"/>), the use prompt and the hold ring that sends it back. The hold is the care package's (the ring is the same <see cref="RadialProgress"/>, the prompt the same line under the crosshair): while held within
/// reach the ring fills over <see cref="LootConfig.ReturnSeconds"/> and the server, which counts the same hold from the inputs, sends the Loot back. Moving off, releasing the key or taking a hit starts it over.
/// </summary>
public sealed class LootClient
{
    sealed record Lying(Node3D Node, LootKind Kind);

    readonly ClientContext _ctx;
    readonly ThievesFile _data;
    readonly Dictionary<ushort, Lying> _lying = new();
    /// <summary>The lying Loot within reach from the last render (null when none), the hold of the use key on it, and the health seen last frame (a drop is a hit taken).</summary>
    ushort? _near;
    LootReturnHold _hold;
    float _health;

    public LootClient(ClientContext ctx, ThievesFile data)
    {
        _ctx = ctx;
        _data = data;
        LootRules.Register(WorldEntityRegistry.Shared);
        LootProp.Config = data.Gang.View.Carry; // the pack's tire and the like (ticket 08)
        ctx.WorldEntities.Spawned += OnSpawned;
        ctx.WorldEntities.Despawned += OnDespawned;
    }

    /// <summary>How full the hold ring is for the Loot (0 when not holding on it).</summary>
    public float HoldFraction => _hold.Held > 0 && _near != null ? (float)Math.Min(1.0, _hold.Held / _data.Loot.ReturnSeconds) : 0f;

    void OnSpawned(WorldReplica.Entity e)
    {
        if (e.Type != LootRules.TypeId || LootRules.KindOf(e.State.State) is not { } kind) return;
        var node = new Node3D { Name = $"Loot{e.Id}" };
        node.AddChild(LootProp.Lying(kind));
        _ctx.Owned.AddChild(node);
        node.GlobalPosition = new Vector3(e.State.Position.X, e.State.Position.Y, e.State.Position.Z);
        node.Rotation = new Vector3(0, e.State.YawRad, 0);
        _lying[e.Id] = new Lying(node, kind);
    }

    /// <summary>Taken up, sent back or reset: the node goes, and so does the ring when this was the Loot in reach.</summary>
    void OnDespawned(ushort id)
    {
        if (!_lying.Remove(id, out var l)) return;
        l.Node.QueueFree();
        if (_near == id)
        {
            _near = null;
            _hold.Reset();
        }
    }

    /// <summary>Finds the lying Loot in reach of this player (on foot, alive).</summary>
    public void Render()
    {
        _near = null;
        if (_lying.Count == 0 || _ctx.Me == null || _ctx.Dead || _ctx.Driving) return;
        var bd = _data.Loot.ReachM;
        var at = _ctx.Me.GlobalPosition;
        foreach (var (id, l) in _lying)
        {
            var p = l.Node.GlobalPosition;
            if (Mathf.Abs(p.Y - at.Y) > 2.2f) continue;
            var d = new Vector2(p.X - at.X, p.Z - at.Z).Length();
            if (d <= bd) { _near = id; bd = d; }
        }
    }

    /// <summary>The prompt for the lying Loot in reach while the key is not held (the ring carries the hold); null otherwise.</summary>
    public string? Prompt()
    {
        if (_near is not { } id || _hold.Held > 0 || !_lying.TryGetValue(id, out var l)) return null;
        return Strings.F("hud.loot.hold", InputBindings.Name("interact"), Strings.T(LootProp.NameKey(l.Kind)));
    }

    /// <summary>Runs on the input tick: follows the hold of the use key on the Loot in reach (the server counts the same hold; nothing is sent).</summary>
    public void TickReturn(double dt)
    {
        var use = Input.MouseMode == Input.MouseModeEnum.Captured && Input.IsActionPressed("interact");
        var health = _ctx.Me?.Health ?? 100f;
        if (health < _health) _hold.Interrupt();
        _health = health;
        _hold.Tick(use, _near, dt, _data.Loot.ReturnSeconds);
    }
}
