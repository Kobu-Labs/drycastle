using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Net;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Harvest;

/// <summary>The harvester's one cosmetic event (<see cref="EventKind.HarvesterFx"/>): a bale lit or burnt out, a body drawn in or swallowed, a bale thrown out.</summary>
internal static class HarvestFx
{
    public static void Send(ServerContext ctx, HarvesterCodec.FxKind kind, NVec3 at, ushort id, ushort victim = 0) =>
        ctx.Broadcast(EventKind.HarvesterFx, HarvesterCodec.EncodeFx(new HarvesterCodec.FxMsg(kind, at, id, victim)));
}
