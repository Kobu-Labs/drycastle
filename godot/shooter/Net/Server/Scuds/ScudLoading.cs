using Godot;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Thieves;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Scud;
using Drycastle.ShooterCore.Wildlife;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Scuds;

/// <summary>
/// Loading a rocket from a rocket cart onto an empty Scud truck: who may start it (<see cref="Offer"/>), the interact key that is a tap (get in or out as always) or a hold (load, <see cref="UseKey"/>), and what the launcher
/// is told about its cart every tick (<see cref="SenseOf"/>). The launcher's own machine counts the clocks (<see cref="ScudLauncher"/>).
/// </summary>
internal sealed class ScudLoading
{
    readonly ServerContext _ctx;
    readonly ScudFile _scud;
    readonly Fleet _fleet;
    readonly ScudRigs _rigs;
    readonly ThievesServer _thieves;
    readonly Func<ServerCar, bool, bool> _setCartLoaded;

    /// <param name="setCartLoaded">Puts the missile on a rocket cart or takes it off; false when the cart is gone or already as asked (<c>NetServer.SetRocketCartLoaded</c>).</param>
    public ScudLoading(ServerContext ctx, ScudFile scud, Fleet fleet, ScudRigs rigs, ThievesServer thieves, Func<ServerCar, bool, bool> setCartLoaded)
    {
        _ctx = ctx; _scud = scud; _fleet = fleet; _rigs = rigs; _thieves = thieves; _setCartLoaded = setCartLoaded;
    }

    /// <summary>The box of a vehicle on the ground plane, as the reload rules measure it.</summary>
    public static BodyBox BoxOf(ServerCar car) => new(car.GlobalPosition.X, car.GlobalPosition.Z, car.HalfWidth, car.HalfLength, car.State.Yaw);

    /// <summary>Every live rocket cart, as the rules want it.</summary>
    IEnumerable<ScudCartCandidate> CartCandidates() =>
        _fleet.Cars.Where(c => c.Cargo != null && !c.Retired).Select(c => new ScudCartCandidate(c.Id, BoxOf(c), c.Cargo!.Loaded && !c.Life.Doomed, !c.Destroyed));

    /// <summary>What the launcher is told about the cart it is loading from this tick: alive, still loaded, not doomed and in reach (null when it is loading from none).</summary>
    public ScudCartSense? SenseOf(ServerCar truck, ScudRig rig)
    {
        if (rig.Launcher.Cart == 0) return null;
        var cart = _fleet.Find(rig.Launcher.Cart);
        if (cart == null) return new ScudCartSense(false, default);
        var ok = ScudReloadRules.NearestCart(BoxOf(truck), CartCandidates().Where(k => k.Id == cart.Id), (float)_scud.Reload.CartReachM + 1f) != null; // a metre of slack: the cart settling is not leaving
        return new ScudCartSense(ok, new NVec3(cart.GlobalPosition.X, cart.GlobalPosition.Y, cart.GlobalPosition.Z));
    }

    /// <summary>The transfer point: the rocket leaves the cart (it is empty from now on, and weighs and breaks like an empty one).</summary>
    public void TakeRocket(ushort cartId)
    {
        if (_fleet.Find(cartId) is { } cart && _setCartLoaded(cart, false)) GD.Print($"server: rocket cart {cartId} unloaded onto a Scud truck");
    }

    /// <summary>
    /// The truck a player may load from a cart now, and the cart: the truck they ride (any seat), else the nearest launcher truck within <see cref="ScudReloadData.PlayerReachM"/> of the player on foot (not while loot lies in
    /// reach: that key is the loot's); the rack must be empty and a loaded, live cart within <see cref="ScudReloadData.CartReachM"/> of the truck. Null when nothing is on offer.
    /// </summary>
    (ServerCar Truck, ServerCar Cart)? Offer(ServerClient c)
    {
        if (_rigs.Count == 0) return null;
        var p = c.Body.GlobalPosition;
        IEnumerable<ServerCar> trucks;
        if (c.Car != null) trucks = c.Car.Def.Launcher != null ? new[] { c.Car } : Array.Empty<ServerCar>();
        else if (_thieves.LootInReach(c) != null) return null;
        else trucks = _fleet.Cars.Where(t => t.Def.Launcher != null);
        foreach (var truck in trucks)
        {
            if (truck.Destroyed || truck.Retired || !_rigs.TryGet(truck.Id, out var rig)) continue;
            var box = BoxOf(truck);
            if (!ScudReloadRules.MayStart(rig.Launcher.State, c.Car == truck, box, p.X, p.Z, _scud.Reload)) continue;
            if (ScudReloadRules.NearestCart(box, CartCandidates(), (float)_scud.Reload.CartReachM) is { } near && _fleet.Find(near.Id) is { } cart) return (truck, cart);
        }
        return null;
    }

    /// <summary>Whether a loading is on offer to the player (the interact key has a rival: the plantation's hold asks).</summary>
    public bool HasOffer(ServerClient c) => Offer(c) != null;

    /// <summary>
    /// The interact key of a player who may be beside a loading offer (<see cref="Offer"/>): with one standing, a press waits to be a tap (true here: the key does what it always does, get in or out of the
    /// truck) or a hold (the loading starts, false here); with none the key is the plain press. Returns whether the key's usual action runs now.
    /// </summary>
    public bool UseKey(ServerClient c, bool useDown, bool useEdge, float dt)
    {
        var offer = Offer(c);
        if (offer is not { } o) { c.ReloadKey.Disarm(useDown); return useEdge; }
        switch (c.ReloadKey.Tick(useDown, dt, (float)_scud.Reload.HoldSeconds))
        {
            case ScudKeyAction.Hold: Start(c, o.Truck, o.Cart); return false;
            case ScudKeyAction.Tap: return true;
            default: return false;
        }
    }

    void Start(ServerClient c, ServerCar truck, ServerCar cart)
    {
        if (_rigs.Of(truck) is not { } rig || !rig.Launcher.BeginLoad(cart.Id, N(truck.GlobalPosition), N(cart.GlobalPosition))) return;
        rig.Starter = c.Id;
        GD.Print($"server: {c.Name} #{c.Id} began loading Scud truck {truck.Id} from rocket cart {cart.Id}");
        _rigs.NotifyCrew(truck.Id, ScudNoticeCode.LoadStarted);
        if (c.Car != truck) _ctx.SendEvent(c, EventKind.ScudNotice, ScudCodec.Encode(ScudNoticeCode.LoadStarted));
    }

    static NVec3 N(Vector3 v) => new(v.X, v.Y, v.Z);
}
