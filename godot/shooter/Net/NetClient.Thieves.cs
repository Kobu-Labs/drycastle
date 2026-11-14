using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The Thieves event on the client (docs/events/thieves/): the Gang members, world entities of type <see cref="GangRules.TypeId"/>. Each is a <see cref="GangMemberView"/> (the
/// look number in the state's owner field picks the body, the hp byte says the kind), placed from the interpolated state and shown by its state byte. The server owns
/// the member; the client only draws it.
/// </summary>
public partial class NetClient
{
    /// <summary>thieves.json: set by the host (the looks and clips of the Gang members).</summary>
    public ThievesFile ThievesData = new();

    sealed class GangNode
    {
        public GangMemberView View = null!;
        public Vector3 Last;
        public bool Placed;
        public float Speed;   // metres per second, smoothed: whether it is walking
    }

    readonly Dictionary<ushort, GangNode> _gang = new();

    /// <summary>Called once from <see cref="_Ready"/>: registers the Gang member type and follows its entities.</summary>
    void InitThieves()
    {
        GangRules.Register(WorldEntityRegistry.Shared);
        WorldEntities.Spawned += OnGangSpawned;
        WorldEntities.Despawned += OnGangDespawned;
    }

    void OnGangSpawned(WorldReplica.Entity e)
    {
        if (e.Type != GangRules.TypeId) return;
        var view = GangMemberView.Make(ThievesData.Gang, GangRules.KindOf(e.State.Hp), e.State.Owner);
        view.Name = $"GangMember{e.Id}";
        Owned.AddChild(view);
        _gang[e.Id] = new GangNode { View = view };
    }

    void OnGangDespawned(ushort id)
    {
        if (_gang.Remove(id, out var g)) g.View.QueueFree();
    }

    /// <summary>Places the Gang members (interpolated, a little in the past like every remote body) and shows the state they are in.</summary>
    void RenderGang(double delta)
    {
        if (_gang.Count == 0 || _lastTick == 0) return;
        var rt = RenderTickNow();
        foreach (var (id, g) in _gang)
        {
            if (!WorldEntities.Entities.TryGetValue(id, out var e)) continue;
            if (e.Buf.At(rt) is { } s)
            {
                var p = new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z);
                g.View.GlobalPosition = p;
                g.View.Rotation = new Vector3(0, s.Yaw, 0);
                if (g.Placed && delta > 0) g.Speed = Mathf.Lerp(g.Speed, (p - g.Last).Length() / (float)delta, 0.2f);
                g.Last = p; g.Placed = true;
            }
            g.View.Show(GangRules.StateOf(e.State.State), g.Speed > 0.35f);
        }
    }

    /// <summary>The nearest living Gang member to <paramref name="from"/>, or null: the damage marker of a blow points at it (the hit event names a member by its hit id, which a client does not know).</summary>
    Vector3? NearestGangMember(Vector3 from)
    {
        Vector3? best = null;
        var bestD = float.MaxValue;
        foreach (var (id, g) in _gang)
        {
            if (!WorldEntities.Entities.TryGetValue(id, out var e) || !GangRules.AliveOf(e.State.Hp)) continue;
            var d = g.View.GlobalPosition.DistanceSquaredTo(from);
            if (d < bestD) { bestD = d; best = g.View.GlobalPosition; }
        }
        return best;
    }
}
