using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Scud;

namespace Drycastle.Shooter.Net.Server.Scuds;

/// <summary>One truck's launcher and the operator's camera as last sent to the crew.</summary>
internal sealed class ScudRig
{
    public required ScudLauncher Launcher;
    public ScudStateMsg? Sent;
    public float ViewX, ViewZ, ViewZoom = 1f;
    /// <summary>The player who held the key to start the loading from a cart (told how it went even when on foot).</summary>
    public ushort Starter;
    public bool ViewDirty;
    public double ViewSentAt;
}

/// <summary>The launcher of every Scud truck (a vehicle whose type has a vehicles.json <c>launcher</c> block), made the first time a truck is looked at, and the notices its crew gets when a proposal or a load is dropped.</summary>
internal sealed class ScudRigs
{
    readonly ServerContext _ctx;
    readonly ScudFile _scud;
    readonly Fleet _fleet;
    readonly Dictionary<ushort, ScudRig> _rigs = new();

    public ScudRigs(ServerContext ctx, ScudFile scud, Fleet fleet) { _ctx = ctx; _scud = scud; _fleet = fleet; }

    public int Count => _rigs.Count;

    public IEnumerable<KeyValuePair<ushort, ScudRig>> All => _rigs;

    public bool TryGet(ushort carId, out ScudRig rig) => _rigs.TryGetValue(carId, out rig!);

    /// <summary>The launcher of a truck, made on first use; null for a vehicle that is not a launcher.</summary>
    public ScudRig? Of(ServerCar car)
    {
        if (car.Def.Launcher == null) return null;
        if (_rigs.TryGetValue(car.Id, out var rig)) return rig;
        rig = new ScudRig { Launcher = new ScudLauncher(_scud.Launcher, _scud.Reload) };
        _rigs[car.Id] = rig;
        var id = car.Id;
        rig.Launcher.ProposalDropped += why => NotifyCrew(id, ScudCodec.NoticeOf(why));
        rig.Launcher.LoadDropped += why => { NotifyCrew(id, ScudCodec.NoticeOf(why)); NotifyStarter(rig, ScudCodec.NoticeOf(why)); };
        return rig;
    }

    /// <summary>The truck is gone (destroyed): its launcher with it. True when it had one.</summary>
    public bool Remove(ushort carId) => _rigs.Remove(carId);

    public void Clear() => _rigs.Clear();

    /// <summary>A notice to everyone in the truck's seats.</summary>
    public void NotifyCrew(ushort carId, ScudNoticeCode code)
    {
        if (_fleet.Find(carId) is not { } car) return;
        var msg = new EventMsg((byte)EventKind.ScudNotice, ScudCodec.Encode(code));
        foreach (var o in _fleet.Occupants(car)) _ctx.Send(o, msg);
    }

    /// <summary>A notice to the player who started the loading, when they are on foot (those aboard got it with the crew).</summary>
    public void NotifyStarter(ScudRig rig, ScudNoticeCode code)
    {
        if (rig.Starter != 0 && _ctx.Players.TryGetValue(rig.Starter, out var c) && c.Car == null) _ctx.SendEvent(c, EventKind.ScudNotice, ScudCodec.Encode(code));
        rig.Starter = 0;
    }
}
