using Godot;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client;

/// <summary>The enemy markers' points of the gunship and missile overlays (ticket #157): the chest of every live enemy player and target dummy.</summary>
internal sealed class EnemyMarkers
{
    readonly ClientContext _ctx;
    /// <summary>Reused each frame, no allocation.</summary>
    readonly List<Vector3> _points = new();

    public EnemyMarkers(ClientContext ctx) { _ctx = ctx; }

    /// <summary>
    /// The points from the remote replicas (players and target dummies share them): every one but the owner, their friends and the dead (a dummy's Defeat pose, a player's body).
    /// Positions are the replicas' feet plus the chest height. The list is reused: read it before the next call.
    /// </summary>
    public List<Vector3> Points()
    {
        _points.Clear();
        foreach (var (id, r) in _ctx.Remotes)
        {
            if (id == _ctx.MyId || _ctx.Match.AreFriends(_ctx.MyId, id) || r.State == EntityState.Dead || !GodotObject.IsInstanceValid(r.Node)) continue;
            _points.Add(r.Node.GlobalPosition + Vector3.Up * 1.0f);
        }
        return _points;
    }
}
