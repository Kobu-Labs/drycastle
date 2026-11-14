using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The Loot of the Thieves event on the client (GLOSSARY "Loot"; docs/events/thieves/04): Loot that lies on the ground (world entity <see cref="LootRules.TypeId"/>, drawn with the kind's prop,
/// <see cref="LootProp.Lying"/>), the use prompt and the hold ring that sends it back, and the wheels the Thieves took off cars (the vehicle record's wheel bits hide the wheel nodes of the sedan model; a model
/// without wheel nodes shows nothing hidden). The hold is the care package's (the ring is the same <see cref="RadialProgress"/>, the prompt the same line under the crosshair): while held within
/// reach the ring fills over <see cref="LootConfig.ReturnSeconds"/> and the server, which counts the same hold from the inputs, sends the Loot back. Moving off, releasing the key or taking a hit starts it over.
/// </summary>
public partial class NetClient
{
    sealed class LyingNode
    {
        public Node3D Node = null!;
        public LootKind Kind;
    }

    readonly Dictionary<ushort, LyingNode> _lying = new();
    /// <summary>The lying Loot within reach from the last render (null when none), the hold of the use key on it, and the health seen last frame (a drop is a hit taken).</summary>
    ushort? _lootNear;
    LootReturnHold _lootHold;
    float _lootHealth;

    void InitLootView()
    {
        LootRules.Register(WorldEntityRegistry.Shared);
        WorldEntities.Spawned += OnLootSpawned;
        WorldEntities.Despawned += OnLootDespawned;
    }

    void OnLootSpawned(WorldReplica.Entity e)
    {
        if (e.Type != LootRules.TypeId || LootRules.KindOf(e.State.State) is not { } kind) return;
        var node = new Node3D { Name = $"Loot{e.Id}" };
        node.AddChild(LootProp.Lying(kind));
        Owned.AddChild(node);
        node.GlobalPosition = new Vector3(e.State.Position.X, e.State.Position.Y, e.State.Position.Z);
        node.Rotation = new Vector3(0, e.State.YawRad, 0);
        _lying[e.Id] = new LyingNode { Node = node, Kind = kind };
    }

    /// <summary>Taken up, sent back or reset: the node goes, and so does the ring when this was the Loot in reach.</summary>
    void OnLootDespawned(ushort id)
    {
        if (!_lying.Remove(id, out var l)) return;
        l.Node.QueueFree();
        if (_lootNear == id)
        {
            _lootNear = null;
            _lootHold.Reset();
        }
    }

    /// <summary>Finds the lying Loot in reach of this player (on foot, alive).</summary>
    void RenderLoot()
    {
        _lootNear = null;
        if (_lying.Count == 0 || _me == null || Dead || _driving) return;
        var bd = ThievesData.Loot.ReachM;
        var at = _me.GlobalPosition;
        foreach (var (id, l) in _lying)
        {
            var p = l.Node.GlobalPosition;
            if (Mathf.Abs(p.Y - at.Y) > 2.2f) continue;
            var d = new Vector2(p.X - at.X, p.Z - at.Z).Length();
            if (d <= bd) { _lootNear = id; bd = d; }
        }
    }

    /// <summary>The prompt for the lying Loot in reach while the key is not held (the ring carries the hold); null otherwise.</summary>
    string? LootPrompt()
    {
        if (_lootNear is not { } id || _lootHold.Held > 0 || !_lying.TryGetValue(id, out var l)) return null;
        return Strings.F("hud.loot.hold", InputBindings.Name("interact"), Strings.T(LootProp.NameKey(l.Kind)));
    }

    /// <summary>Runs on the input tick: follows the hold of the use key on the Loot in reach (the server counts the same hold; nothing is sent).</summary>
    void TickLootReturn(double dt)
    {
        var use = Input.MouseMode == Input.MouseModeEnum.Captured && Input.IsActionPressed("interact");
        var health = _me?.Health ?? 100f;
        if (health < _lootHealth) _lootHold.Interrupt();
        _lootHealth = health;
        _lootHold.Tick(use, _lootNear, dt, ThievesData.Loot.ReturnSeconds);
    }

    /// <summary>How full the hold ring is for the Loot (0 when not holding on it).</summary>
    float LootHoldFraction => _lootHold.Held > 0 && _lootNear != null ? (float)Math.Min(1.0, _lootHold.Held / ThievesData.Loot.ReturnSeconds) : 0f;

    // ---- the wheels the Thieves took ----

    static readonly string[] WheelNodeNames = { "wheel-front-left", "wheel-front-right", "wheel-back-left", "wheel-back-right" };

    /// <summary>The wheel nodes of a vehicle model by wheel index (front left, front right, back left, back right; null where the model has none).</summary>
    static Node3D?[] FindWheels(Node3D car)
    {
        var wheels = new Node3D?[WheelNodeNames.Length];
        for (var i = 0; i < wheels.Length; i++) wheels[i] = car.FindChild(WheelNodeNames[i], true, false) as Node3D;
        return wheels;
    }

    /// <summary>Hides the wheels the record says are taken and shows the ones that are back (a change of the mask only).</summary>
    static void ApplyWheels(CarView v, byte mask)
    {
        if (v.Wheels == null || v.WheelsOff == mask) return;
        v.WheelsOff = mask;
        for (var i = 0; i < v.Wheels.Length; i++)
            if (v.Wheels[i] is { } w && GodotObject.IsInstanceValid(w)) w.Visible = (mask & (1 << i)) == 0;
    }
}
