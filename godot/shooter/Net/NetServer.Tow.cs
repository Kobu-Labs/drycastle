using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Towing and the rocket cart on the server (docs/shooter-v2.md "Towing and the rocket cart"; the model is ShooterCore's <see cref="TowLink"/>, <see cref="TowModel"/>, <see cref="TowRules"/>).
/// A trailer is a <see cref="ServerCar"/> with no seats; this file is what the vehicle step does about it: the auto-latch (no key: a tractor that reverses its hitch onto a loose trailer's eye
/// couples), the trailer's own tick (it rolls free, or follows its tractor, whose step moved it), the run-over of its box, the unhitch key, and the explosive load. Server methods for the Scud
/// event are at the end (<see cref="SpawnRocketCart"/>, <see cref="RemoveRocketCarts"/>, <see cref="SetRocketCartLoaded"/>, <see cref="LoadedRocketCartsNear"/>), and the one hook it fills
/// in (<see cref="OnRocketCartDetonated"/>).
/// </summary>
public partial class NetServer
{
    /// <summary>Only a tractor within this of a loose trailer is asked whether it latches it (metres, between origins: the hitch is a few metres behind the tractor's, the eye ahead of the trailer's).</summary>
    const float LatchBroadRange = 14f;

    /// <summary>
    /// The auto-latch (GTA style: it couples by itself): every loose trailer against every tractor of a type that can pull it that has no trailer yet and is not thrown, on land, with a driver or not. The rule
    /// is <see cref="TowRules.CheckLatch"/> (reversing, the hitch onto the eye, lined up); a trailer just released is locked out for a while. A coupled pair is the tractor's from this tick on.
    /// </summary>
    void TrailerLatchTick()
    {
        foreach (var trailer in _cars)
        {
            if (!trailer.IsTrailer || trailer.Destroyed || trailer.Tractor != null || trailer.Thrown != null || trailer.Tow!.State != TrailerState.Loose) continue;
            var at = trailer.GlobalPosition;
            foreach (var tractor in _cars)
            {
                if (tractor.Def.Tow == null || tractor.Destroyed || tractor.Thrown != null || tractor.Towing != null || tractor.Flies || tractor.Afloat) continue;
                if (Dist2D(N(at), N(tractor.GlobalPosition)) > LatchBroadRange) continue;
                if (!trailer.TryLatchTo(tractor)) continue;
                var driver = OccupantAt(tractor, 0);
                tractor.UnhitchKey.Arm(driver?.UseKey.Held ?? false); // a key held as it latched must be let go before it counts
                GD.Print($"server: {tractor.Def.Id} {tractor.Id} hitched {trailer.Def.Id} {trailer.Id}");
                break;
            }
        }
    }

    /// <summary>
    /// One tick of a trailer in the vehicle step. Hitched, it was already stepped by its tractor's step (and its node placed); loose, it rolls on and stops (<see cref="ServerCar.StepFree"/>). Then the
    /// ordinary life of a vehicle: out of the world, its blast when doomed, and the road kill by its box. A trailer the Bigfoot holds is let go of its tractor and carried where it is put.
    /// </summary>
    void StepTrailerCar(ServerCar car, float dt)
    {
        if (car.Thrown != null) { car.Unhitch(); StepThrownVehicle(car, dt); car.SyncTrailerFromNode(); return; }
        car.PrevPos = car.TickEndPos;
        if (car.Tractor == null) car.StepFree(dt);
        else car.Wake(); // pulled: never rests (it is not stepped by its own turn, but it is not asleep either)
        var here = car.GlobalPosition;
        car.TickEndPos = here;
        if (here.Y < KillY) { car.Reset(false); return; }
        if (car.Life.DetonationDue(_elapsed)) { Detonate(car); return; }
        var speed = car.Tow!.Body.Speed;
        if (car.Def.RunOver && RunOver.Contacts(speed)) TrailerRunOver(car, speed);
    }

    /// <summary>
    /// The road kill of a trailer's box (the rule table's ground vehicle row, as <see cref="RunOverCheck"/>): swept from where it stood at the end of the last tick, hurt by its axle speed and its
    /// type's run-over mass, credited to whoever drives the tractor (nobody when it is loose or the tractor has no driver). Approximation: the swing of its tail in a turn is in the sweep's path, not in the speed.
    /// </summary>
    void TrailerRunOver(ServerCar car, float speed)
    {
        var credit = car.Tractor is { } tractor ? OccupantAt(tractor, 0)?.Id ?? (ushort)0 : (ushort)0;
        var dmg = RunOver.Damage(Vehicles!, speed, Vehicles!.RunOverScale(car.Def));
        RunOverTargets(new RunOverSweep(MoverKind.GroundVehicle, N(car.PrevPos), N(car.GlobalPosition), car.State.Yaw, car.HalfWidth, car.HalfLength, N(car.Forward), speed, dmg, credit, car.HitCooldown));
    }

    /// <summary>
    /// The driver's interact key (the input's <paramref name="useDown"/>, for <paramref name="dt"/>) with a trailer on the hook: a hold unhitches it, a tap leaves the vehicle (on release) — so the same key does
    /// both and neither is lost (<see cref="UnhitchKey"/>). True when the driver leaves now. Only the driver's seat unhitches; any other seat leaves on the press as always.
    /// </summary>
    bool TowUseKey(Client c, ServerCar car, bool useDown, bool useEdge, float dt)
    {
        if (c.CarSeat != 0 || car.Towing == null) return useEdge;
        switch (car.UnhitchKey.Tick(useDown, dt))
        {
            case UnhitchAction.Unhitch:
                if (car.Towing.Unhitch()) GD.Print($"server: {c.Name} #{c.Id} unhitched the trailer of {car.Def.Id} {car.Id}");
                return false;
            case UnhitchAction.Exit: return true;
            default: return false;
        }
    }

    // ---- the explosive load ----

    /// <summary>
    /// THE hook: a rocket cart that was carrying its missile is destroyed (bullets, a blast, a crash, a chain reaction; <paramref name="cart"/> is already destroyed, its return timer set and its tractor
    /// let go) at <paramref name="position"/>, credited to <paramref name="attackerId"/> (0: nobody). For now it is an ordinary vehicle explosion; the Scud event replaces this body with the big blast.
    /// An empty cart never comes here: it blows up as a small ordinary vehicle.
    /// </summary>
    void OnRocketCartDetonated(ServerCar cart, NVec3 position, ushort attackerId)
    {
        var weapon = cart.LastWeapon != 0 ? cart.LastWeapon : SpecialWeapons.Vehicle;
        GD.Print($"server: rocket cart {cart.Id} detonated at ({position.X:F1},{position.Y:F1},{position.Z:F1}), credited to #{attackerId}");
        VehicleBlast(cart, new Vector3(position.X, position.Y + 0.8f, position.Z), attackerId, weapon, NVec3.Zero);
    }

    // ---- what the Scud event calls ----

    /// <summary>
    /// Spawns a rocket cart standing at <paramref name="position"/> (its origin on the ground there: the ground under it is found, the given height is only where to look from), facing <paramref name="yaw"/>
    /// (Godot's: 0 faces -Z, positive turns left), loaded unless told otherwise. It is a vehicle like any other (in the list, in the snapshot, hit, run over by) and returns to this place, loaded, when it is destroyed.
    /// </summary>
    public ServerCar SpawnRocketCart(NVec3 position, float yaw, bool loaded = true)
    {
        var kind = Vehicles?.Vehicles.FindIndex(v => v.Trailer is { HasCargo: true }) ?? -1;
        if (kind < 0) throw new InvalidOperationException("vehicles.json has no rocket cart (a trailer with a loaded_hp)");
        var def = Vehicles!.Vehicles[kind];
        var ground = Query.GroundY(MoverKind.GroundVehicle, new Vector3(position.X, position.Y + 2f, position.Z), 3f, 12f) ?? position.Y;
        var car = new ServerCar
        {
            Name = $"{def.Id}{_cars.Count}", Id = (ushort)(VehicleRecord.FirstId + _cars.Count), Kind = kind, Def = def, Colour = VehicleRecord.Pack(kind, 0),
            HalfWidth = def.HalfWidth, HalfLength = def.HalfLength, OriginPos = new Vector3(position.X, ground + 0.02f, position.Z), OriginYaw = yaw,
            FlightWater = Water, FlightBounds = MapFlightBounds,
        };
        World.AddChild(car);
        _cars.Add(car);
        car.SetLoaded(loaded);
        GD.Print($"server: rocket cart {car.Id} spawned at ({position.X:F1},{ground:F1},{position.Z:F1}), {(loaded ? "loaded" : "empty")}");
        return car;
    }

    /// <summary>Removes every rocket cart from the world for good, hitched or not, loaded or not (<see cref="ServerCar.Retire"/>: clients drop them from the next snapshot). Returns how many there were.</summary>
    public int RemoveRocketCarts()
    {
        var n = 0;
        foreach (var car in _cars)
        {
            if (car.Cargo == null || car.Retired) continue;
            car.Retire();
            n++;
        }
        if (n > 0) GD.Print($"server: {n} rocket cart(s) removed");
        return n;
    }

    /// <summary>
    /// Puts the missile on a rocket cart or takes it off (<see cref="ServerCar.SetLoaded"/>): its hit points follow, keeping their share, and every client sees the missile appear or go. False when the cart is
    /// gone (destroyed or removed) or already as asked.
    /// </summary>
    public bool SetRocketCartLoaded(ServerCar cart, bool loaded) => !cart.Destroyed && cart.SetLoaded(loaded);

    /// <summary>The loaded, live rocket carts within <paramref name="radius"/> metres of <paramref name="point"/> (straight-line distance from the cart's origin), nearest first.</summary>
    public List<ServerCar> LoadedRocketCartsNear(NVec3 point, float radius)
    {
        var found = new List<(float D, ServerCar Car)>();
        foreach (var car in _cars)
        {
            if (car.Cargo is not { Loaded: true } || car.Destroyed) continue;
            var d = NVec3.Distance(N(car.GlobalPosition), point);
            if (d <= radius) found.Add((d, car));
        }
        found.Sort((a, b) => a.D.CompareTo(b.D));
        return found.Select(f => f.Car).ToList();
    }
}
