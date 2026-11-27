using Godot;
using Drycastle.Shooter.Flight;
using Drycastle.Shooter.Net.Server.Panes;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Diagnostics;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Waters;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Server.CarPool;

/// <summary>
/// The vehicle step, once a tick after the players took their inputs. In this order: a loose trailer is latched by a tractor that reverses its hitch onto it (before the contacts and the steps: it follows its tractor from
/// this tick on), the vehicles bump into each other (#253), then every vehicle in the order they were made: a destroyed one waits for its return, a trailer is <see cref="TowServer.Step"/>ped, a thrown one flies
/// its arc (the Bigfoot's), the seats of players who have gone are freed and the riders told, the vehicle is stepped (a helicopter and a driven vehicle were already stepped by their pilot's inputs, in <c>InputServer</c>,
/// so only the ones whose inputs stopped coming are here), it crashes or blows up, its riders are put on their seats and a driver runs bodies over. What the vehicles ask of the rest is named in the constructor.
/// </summary>
internal sealed class VehicleLoop
{
    /// <summary>A pilot whose inputs have not arrived for this many ticks is flown by the stabiliser (a hover), so a stalled client does not freeze the helicopter in the air.</summary>
    const uint FlightStarveTicks = 6;

    /// <summary>A driver whose inputs have not arrived for this many ticks is driven hands off (it coasts), so a stalled client does not freeze the vehicle.</summary>
    const uint DriveStarveTicks = 6;

    readonly ServerContext _ctx;
    readonly Fleet _fleet;
    readonly CarSeats _seats;
    readonly CarDamage _damage;
    readonly CarBlast _blast;
    readonly TowServer _tow;
    readonly RunOverServer _runOver;
    readonly GlassServer _glass;
    readonly SmokeStats _smoke;
    readonly VehiclesFile _vehicles;
    readonly Water _water;
    readonly float _killY;
    readonly bool _blastSmoke;
    readonly Action _shoveByVans;
    readonly Action<ServerCar, float> _stepThrown;
    readonly Func<ServerClient, bool> _steersItself;
    readonly Func<ServerCar, float?> _ram;

    /// <summary>Each live vehicle's origin for the broad test of <see cref="ResolveContacts"/> (reused every tick).</summary>
    Vector3[] _contactAt = Array.Empty<Vector3>();

    /// <param name="shoveByVans">The Raid vans push through: a vehicle in a van's box is shoved out (once per tick, before the pairs bump).</param>
    /// <param name="stepThrown">A vehicle thrown by the Bigfoot flies its arc: the server places it, and its riders with it.</param>
    /// <param name="steersItself">The harvester's own driver sends no inputs: the server steers it, one step a tick.</param>
    /// <param name="ram">Every ground vehicle each tick: into a hollow house's wall it hit (HouseDestructionServer.Ram), into the thin fences it drives into (FenceDestructionServer.Drive); the speed it lost to what it went through, else null.</param>
    public VehicleLoop(ServerContext ctx, Fleet fleet, CarSeats seats, CarDamage damage, CarBlast blast, TowServer tow, RunOverServer runOver, GlassServer glass, SmokeStats smoke, VehiclesFile vehicles,
        Water water, float killY, bool blastSmoke, Action shoveByVans, Action<ServerCar, float> stepThrown, Func<ServerClient, bool> steersItself, Func<ServerCar, float?> ram)
    {
        _ram = ram;
        _ctx = ctx; _fleet = fleet; _seats = seats; _damage = damage; _blast = blast; _tow = tow; _runOver = runOver; _glass = glass; _smoke = smoke; _vehicles = vehicles;
        _water = water; _killY = killY; _blastSmoke = blastSmoke; _shoveByVans = shoveByVans; _stepThrown = stepThrown; _steersItself = steersItself;
    }

    public void Step(float dt)
    {
        if (_fleet.Count == 0) return;
        _tow.LatchTick();
        var contactsT = Prof.Begin(Prof.S.VehicleContacts);
        ResolveContacts();
        Prof.End(Prof.S.VehicleContacts, contactsT);
        foreach (var car in _fleet.Cars) StepCar(car, dt);
    }

    void StepCar(ServerCar car, float dt)
    {
        car.CrashSoundGap.Tick(dt);
        if (car.Retired) return; // taken out of the world for good (RocketCarts.RemoveAll)
        if (car.Destroyed) { WaitForReturn(car); return; }
        if (car.IsTrailer) { _tow.Step(car, dt); return; } // no seats, no driver: it follows its tractor, or rolls free
        // seats of players who have gone (disconnect, death) are freed; the driver is seat 0 and only the driver steers
        for (var s = 0; s < car.Seat.Count; s++) if (car.Seat.Occupant(s) != 0 && _fleet.OccupantAt(car, s) == null) car.Seat.Clear(s);
        _seats.SendState(car);
        if (car.Thrown != null) { car.Towing?.Unhitch(); _stepThrown(car, dt); return; } // flying on the Bigfoot's arc: no driving, no crash, no run-over (issue #264)
        var driver = _fleet.OccupantAt(car, 0);
        var occupied = car.Seat.Occupied;
        if (occupied) car.Wake(); // somebody aboard: the vehicle is stepped every tick until it is empty and rests again
        car.PrevPos = car.Drives ? car.TickEndPos : car.GlobalPosition; // a driven vehicle has already moved this tick, on its driver's inputs
        var stepT = Prof.Begin(Prof.S.VehicleStep);
        if (car.Flies) StepFlying(car, driver, dt);
        else if (car.Drives) StepDriven(car, driver, dt);
        else if (driver != null) car.Step(car.Def, driver.Throttle, driver.Steer, driver.Handbrake, dt, _water);
        else if (!car.Asleep || occupied) car.StepParked(dt, _water); // a parked car at rest is not stepped until disturbed (ParkedRules, T2-04)
        Prof.End(Prof.S.VehicleStep, stepT);
        var here = car.GlobalPosition; // read once: a native read each time, every car every tick
        car.TickEndPos = here;
        if (here.Y < _killY) { foreach (var o in _fleet.Occupants(car)) _seats.Leave(o, false); car.Reset(false); return; }
        // a hollow house's wall it hit hard enough breaks (any vehicle), a thin fence it drives into breaks (ground vehicles, moving or stopped by it): through, the crash is only what it took
        if ((car.LastImpact > 0 || !car.Flies) && _ram(car) is { } lost) car.LastImpact = lost;
        // a wall takes speed from a driven car; a helicopter is hurt by what it hits whoever flies it, or nobody
        if (driver != null || car.NpcInput != null || car.Flies) _damage.Crash(car, car.LastImpact, 0);
        if (car.Life.DetonationDue(_ctx.Now)) { _blast.Detonate(car); return; }
        PutRidersOnSeats(car);
        if (driver != null && car.Def.RunOver && !car.Afloat && (!car.Flies || car.Flight.Agl <= car.Def.Flight!.RunOverAgl)) _runOver.Vehicle(car, driver); // road kill: on land only; a helicopter only skimming the ground
    }

    /// <summary>A new round: everyone is out of their seat for good (<see cref="CarSeats.Detach"/>: a body left flagged cannot go prone), every vehicle is back where it started and its glass whole.</summary>
    public void RoundReset()
    {
        foreach (var cl in _ctx.Connections) _seats.Detach(cl);
        foreach (var car in _fleet.Cars) { if (car.Retired) continue; car.Seat.Clear(); car.Reset(); _glass.RestoreCar(car); }
        _glass.RestoreForRound();
    }

    /// <summary>A destroyed vehicle waits for its return time, then comes back repaired where it started.</summary>
    void WaitForReturn(ServerCar car)
    {
        if (!car.Life.ReturnDue(_ctx.Now)) return;
        car.Reset();
        _glass.RestoreCar(car);
        _smoke.DestroyedReturns++;
        if (_blastSmoke && _smoke.DestroyedReturns >= 2 && _smoke.FinishAt == 0) _smoke.FinishAt = _ctx.Now + 1.5;
        GD.Print($"server: {car.Def.Id} {car.Id} returned after its destruction");
    }

    /// <summary>The riders are put on their seats (no list per car per tick); a passive rider's figure keeps facing along the hull as it turns (a gunner's follows the aim).</summary>
    void PutRidersOnSeats(ServerCar car)
    {
        for (var s = 0; s < car.Seat.Count; s++)
        {
            if (_fleet.OccupantAt(car, s) is not { } o) continue;
            var at = CarPose.SeatPos(car, o.CarSeat);
            o.Body.Teleport(at);
            _ctx.Match.SetPosition(o.Id, at.ToNumerics());
            if (!RiderPose.FollowsAim(car.Def.Seats[o.CarSeat])) CarSeats.ApplyHeadLook(o, car);
        }
    }

    /// <summary>
    /// Vehicles bump into each other (#253). Every pair of live vehicles that overlap (hit box, and a helicopter's rotor disc) is moved apart and
    /// exchanges its closing speed (<see cref="VehicleContact"/>); each loses speed by what the bump took from it, and a loss above the crash
    /// minimum hurts it by the crash rules, credited to the other vehicle's driver (nobody when that one is empty). Riders take nothing from the
    /// bump itself, only from the blast of a vehicle it destroys. The server alone resolves it: the pilot's client predicts against the world, not
    /// against other vehicles (the client sees them a snapshot late), and the reconciliation carries the bump to it.
    /// </summary>
    void ResolveContacts()
    {
        // each origin is read once here (a native call per read, and every pair asks): only a bump moves a vehicle inside this pass, so only those are read again
        if (_contactAt.Length < _fleet.Count) _contactAt = new Vector3[_fleet.Count];
        for (var i = 0; i < _fleet.Count; i++) _contactAt[i] = _fleet.Cars[i].GlobalPosition;
        _shoveByVans();
        const float range2 = Fleet.ContactBroadRange * Fleet.ContactBroadRange;
        for (var i = 0; i < _fleet.Count; i++)
        {
            var a = _fleet.Cars[i];
            if (a.Destroyed || a.Thrown != null) continue; // a thrown vehicle bumps nothing, and is bumped by nothing
            for (var j = i + 1; j < _fleet.Count; j++)
            {
                var b = _fleet.Cars[j];
                if (b.Destroyed || b.Thrown != null) continue;
                if (a.Towing == b || b.Towing == a) continue; // a tractor and its own trailer overlap at a deep articulation by design: they are one body
                if (_contactAt[i].DistanceSquaredTo(_contactAt[j]) > range2) continue;
                var shapeA = a.ContactShape(); var shapeB = b.ContactShape();
                if (VehicleContact.Test(shapeA, shapeB, a.Flies || b.Flies) is not { } hit) continue;
                var bump = VehicleContact.Bump(hit, a.BumpBody(), b.BumpBody(), _vehicles.BumpRestitution);
                a.ApplyBump(bump.MoveA, bump.DeltaVA);
                b.ApplyBump(bump.MoveB, bump.DeltaVB);
                _contactAt[i] = a.GlobalPosition; _contactAt[j] = b.GlobalPosition;
                _damage.Crash(a, bump.SpeedLossA, _fleet.OccupantAt(b, 0)?.Id ?? 0);
                _damage.Crash(b, bump.SpeedLossB, _fleet.OccupantAt(a, 0)?.Id ?? 0);
            }
        }
    }

    /// <summary>
    /// The tick of a flying vehicle (the utility helicopter, ADR 0006). With a pilot, the flight was already stepped in <c>InputServer</c>, once
    /// for each of the pilot's inputs (that is what lets the pilot's client predict it), so there is nothing to step here unless the inputs have
    /// stopped arriving. Without one the stabiliser flies: a hover while riders are aboard, then down (<see cref="FlightModel"/>). The hardest impact
    /// since the last tick goes to the crash damage rules, and skids in water cost hit points every second.
    /// </summary>
    void StepFlying(ServerCar car, ServerClient? pilot, float dt)
    {
        if (pilot == null)
        {
            // a helicopter resting on the ground is not stepped until disturbed (FlightRest); the flight tick still follows the server tick, as a step would leave it
            if (car.Asleep) car.FlightTick = _ctx.Tick;
            else car.StepFlightUnpiloted(dt, _ctx.Tick);
        }
        else if (_ctx.Tick - car.FlightTick > FlightStarveTicks) car.StepFlight(FlightInput.Idle, dt, _ctx.Tick);
        car.LastImpact = car.FlightImpact;
        car.LastImpactVelocity = car.FlightImpactVelocity;
        car.FlightImpact = 0;
        if (car.Flight.InWater) _damage.Damage(car, car.Def.Flight!.WaterDamagePerSecond * dt, 0, SpecialWeapons.Vehicle, 0);
    }

    /// <summary>
    /// The tick of a driven vehicle (the amphibious transport, ADR 0010; the car, ADR 0014). With a driver, it was already stepped in <c>InputServer</c>, once
    /// for each of the driver's inputs (that is what lets the driver's client predict it), so there is nothing to step here unless the inputs have
    /// stopped arriving. With an NPC at the wheel (<see cref="ServerCar.NpcInput"/>) it is stepped by the NPC's inputs every tick. With nobody at the wheel it is stepped hands off every tick: it coasts to a stop, on its wheels or on the water. The hardest
    /// impact since the last tick goes to the crash damage rules.
    /// </summary>
    void StepDriven(ServerCar car, ServerClient? driver, float dt)
    {
        if (driver != null && _steersItself(driver))
        {
            car.Wake();
            car.StepDrive(new DriveInput { Throttle = driver.Throttle, Steer = driver.Steer, Handbrake = driver.Handbrake }, dt);
        }
        else if (driver == null && car.NpcInput is { } npc)
        {
            car.Wake();
            car.StepDrive(npc, dt); // an NPC at the wheel (a Rival chase car): its inputs are the server's, one step a tick
        }
        else if (driver == null || _ctx.Tick - car.DriveTick > DriveStarveTicks)
        {
            if (!car.Asleep) car.StepDriveUnpiloted(dt); // a parked one at rest is not stepped until disturbed (DriveRest)
        }
        car.LastImpact = car.DriveImpact;
        car.LastImpactVelocity = car.DriveImpactVelocity;
        car.DriveImpact = 0;
    }
}
