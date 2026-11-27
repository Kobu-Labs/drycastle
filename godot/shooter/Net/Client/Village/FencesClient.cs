using Godot;
using Drycastle.Shooter.Fences;
using Drycastle.ShooterCore.Fences;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client.Village;

/// <summary>
/// Fence destruction's events (docs/fence-destruction.md Contract 4, <see cref="FenceCodec"/>) to the client's fences (<see cref="FenceWorldNode"/>): 224 the pieces of one section that
/// broke, 225 the broken pieces of a joiner (or a reset at a new round). The falling pieces (<see cref="FenceDebris"/>) live under <see cref="ClientContext.Owned"/>, so a leave frees them
/// and the next server's first event makes new ones.
/// </summary>
public sealed class FencesClient
{
    readonly ClientContext _ctx;

    public FencesClient(ClientContext ctx) => _ctx = ctx;

    public void OnEvent(EventKind kind, byte[] payload)
    {
        if (kind is not (EventKind.FenceBreak or EventKind.FenceSync) || FenceWorldNode.Current is not { } fences) return;
        Wire(fences);
        switch (kind)
        {
            case EventKind.FenceBreak when FenceCodec.DecodeBreak(payload) is { } brk:
                fences.OnBreak(brk);
                break;
            case EventKind.FenceSync when FenceCodec.DecodeSync(payload) is { } sync:
                fences.OnSync(sync);
                break;
        }
    }

    /// <summary>The debris (top level at the origin under Owned), made again once a leave has freed it.</summary>
    void Wire(FenceWorldNode fences)
    {
        if (fences.Debris is { } d && GodotObject.IsInstanceValid(d) && d.IsInsideTree()) return;
        var debris = FenceDebris.Create(fences.Config, fences.Map);
        _ctx.Owned.AddChild(debris);
        fences.Debris = debris;
    }

    /// <summary>Leaving the server: the fences whole again and the debris gone (the next server sends its own state).</summary>
    public void Teardown()
    {
        if (FenceWorldNode.Current is not { } fences) return;
        fences.ResetAll();
        fences.Debris = null; // freed with Owned
    }
}
