using Godot;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Server.Playground;

/// <summary>The client's debug messages (dev servers and the smokes only, <c>--debug-cmds</c>): teleport a player, damage a vehicle, kill a player.</summary>
internal sealed class DebugServer
{
    readonly ServerContext _ctx;
    readonly Fleet _fleet;
    readonly CarDamage _carDamage;
    readonly SmokeStats _smoke;

    public DebugServer(ServerContext ctx, Fleet fleet, CarDamage carDamage, SmokeStats smoke)
    {
        _ctx = ctx; _fleet = fleet; _carDamage = carDamage; _smoke = smoke;
    }

    public void OnMessage(ServerClient c, byte[] payload)
    {
        if (WeaponCodec.DecodeDebugTeleport(payload) is { } tp)
        {
            if (!_ctx.Players.TryGetValue(tp.Target, out var t)) return;
            var p = new Vector3(tp.Pos.X, tp.Pos.Y + 0.1f, tp.Pos.Z);
            t.Body.Teleport(p);
            t.Inputs.Clear();
            _ctx.Match.SetPosition(t.Id, tp.Pos);
            // the owning client resynchronises on its own spawn event (no protection)
            _ctx.Broadcast(EventKind.Spawn, MatchCodec.Encode(new SpawnMsg(t.Id, tp.Pos, 0)));
            return;
        }
        if (WeaponCodec.DecodeDebugVehicleDamage(payload) is { } vd)
        {
            var car = _fleet.Find(vd.Vehicle);
            if (car != null) _carDamage.Damage(car, vd.Amount, c.Id, SpecialWeapons.Debug, 0);
            return;
        }
        if (MatchCodec.DecodeDebugKill(payload) is not { } target) return;
        var r = _ctx.Match.ApplyDamage(target, c.Id, SpecialWeapons.Debug, 9999);
        if (r == DamageResult.Ignored && _ctx.Match.IsProtected(target)) _smoke.IgnoredHits++;
        GD.Print($"server: debug kill {c.Name} -> #{target}: {r}");
    }
}
