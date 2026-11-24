using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Server.Sessions;

/// <summary>Reads a packet of inputs: the snapshot and world acks it carries, and the inputs the player has not sent before, queued for the ticks (at most 8).</summary>
internal sealed class InputIntake
{
    readonly ServerContext _ctx;
    readonly SnapshotHistory _sentStates;
    readonly WorldHistory _sentWorld;
    readonly List<NetInput> _scratch = new();

    public InputIntake(ServerContext ctx, SnapshotHistory sentStates, WorldHistory sentWorld)
    {
        _ctx = ctx; _sentStates = sentStates; _sentWorld = sentWorld;
    }

    public void OnPacket(ServerClient c, byte[] d)
    {
        _scratch.Clear();
        if (!Wire.TryDecodeInputs(d, _scratch, out var snapTick, out var worldTick)) return;
        var tick = _ctx.Tick;
        if (snapTick != Wire.NoTick && snapTick <= tick && _sentStates.Has(snapTick) && (c.SnapAck == Wire.NoTick || snapTick > c.SnapAck)) c.SnapAck = snapTick; // an older input's ack arriving late changes nothing
        if (worldTick != Wire.NoTick && worldTick <= tick && _sentWorld.Has(worldTick) && (c.WAck == Wire.NoTick || worldTick > c.WAck)) c.WAck = worldTick; // the world channel's ack, the same rule
        foreach (var i in _scratch)
        {
            // redundant copies of already-seen inputs are skipped
            if (c.HasSeq && !NetInput.Newer(i.Seq, c.LastSeq)) continue;
            c.LastSeq = i.Seq; c.HasSeq = true;
            c.Inputs.Enqueue(i);
        }
        while (c.Inputs.Count > 8) c.Inputs.Dequeue(); // a stalled sim never builds unbounded lag
    }
}
