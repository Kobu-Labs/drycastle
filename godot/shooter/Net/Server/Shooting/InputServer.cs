using Godot;
using Drycastle.Shooter.Net.Server.BusGarage;
using Drycastle.Shooter.Net.Server.Houses;
using Drycastle.Shooter.Net.Server.CarPool;
using Drycastle.Shooter.Net.Server.Gear;
using Drycastle.Shooter.Net.Server.Parish;
using Drycastle.Shooter.Net.Server.Playground;
using Drycastle.Shooter.Net.Server.Seating;
using Drycastle.Shooter.Net.Server.Streaks;
using Drycastle.Shooter.Net.Server.Thieves;
using Drycastle.Shooter.Net.Server.VillageRadio;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Diagnostics;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Server.Shooting;

/// <summary>
/// A player's input on the server: each tick every joined player (bots included) takes the inputs queued for it. <see cref="Apply"/> decides what an input is for (dead, held, seated, driving, walking),
/// <see cref="RideInput"/> and <see cref="WalkAndFire"/> do it, and a rider in an armed seat fires through <see cref="SeatWeapons"/>.
/// </summary>
internal sealed class InputServer
{
    readonly ServerContext _ctx;
    readonly StreakServer _streaks;
    readonly RozhlasServer _rozhlas;
    readonly ThievesServer _thieves;
    readonly ItemsServer _gear;
    readonly Sitters _sitters;
    readonly PounceServer _pounce;
    readonly SandboxServer _sandbox;
    readonly MassServer _mass;
    readonly GarageServer _garage;
    readonly HouseDoorServer _houses;
    readonly CarSeats _seats;
    readonly VehicleGuns _guns;
    readonly ShotServer _shots;
    readonly SmokeStats _smoke;
    readonly Action<ServerClient, bool> _plantationUse;
    readonly Func<ServerClient, bool, bool, float, bool> _scudUseKey;
    readonly float _killY;

    /// <param name="plantationUse">The use key is fed to the plantation's key competition (the high a player is getting).</param>
    /// <param name="scudUseKey">Beside a Scud truck with an empty rack and a loaded cart a press waits to be a tap (it gets in) or a hold (it loads the rocket); true when the key's usual action runs now.</param>
    public InputServer(ServerContext ctx, StreakServer streaks, RozhlasServer rozhlas, ThievesServer thieves, ItemsServer gear, Sitters sitters, PounceServer pounce, SandboxServer sandbox, MassServer mass,
        GarageServer garage, HouseDoorServer houses, CarSeats seats, VehicleGuns guns, ShotServer shots, SmokeStats smoke, Action<ServerClient, bool> plantationUse, Func<ServerClient, bool, bool, float, bool> scudUseKey, float killY)
    {
        _ctx = ctx; _streaks = streaks; _rozhlas = rozhlas; _thieves = thieves; _gear = gear; _sitters = sitters; _pounce = pounce; _sandbox = sandbox; _mass = mass; _garage = garage; _houses = houses; _seats = seats; _guns = guns;
        _shots = shots; _smoke = smoke; _plantationUse = plantationUse; _scudUseKey = scudUseKey; _killY = killY;
    }

    public void Simulate(ServerClient c, float dt)
    {
        // normally one input per tick; if the queue backs up (jitter), catch up with a second one
        var n = c.CatchUp.Steps(c.Inputs.Count); // a second input only while the catch-up credit lasts (#197)
        if (c.Inputs.Count == 0 && c.Car == null && _ctx.Match.Get(c.Id)?.Life == PlayerLife.Alive) c.StarvedTicks++; // A-08: no input for this tick (the body does not move)
        for (var k = 0; k < n && c.Inputs.Count > 0; k++) Apply(c, c.Inputs.Dequeue(), dt);
        _ctx.Match.SetPosition(c.Id, new System.Numerics.Vector3(c.Body.GlobalPosition.X, c.Body.GlobalPosition.Y, c.Body.GlobalPosition.Z));
        c.Body.Health = _ctx.Match.Get(c.Id)?.Health ?? 0;
        var p = c.Body.GlobalPosition - c.Start;
        c.MaxMoved = Mathf.Max(c.MaxMoved, new Vector2(p.X, p.Z).Length());
    }

    /// <summary>
    /// One input of one player: acked always, then what the player is doing decides what it does. The keys are fed first (also while dead), then in order: nothing while dead, held by the Bigfoot, seated in the church,
    /// pinned by the demon; the killstreak key; a held body (a controlled streak entity, a thrown car); driving or riding; the use key's actions on foot; and at last walking, the weapon and the grenade.
    /// </summary>
    void Apply(ServerClient c, NetInput i, float dt)
    {
        c.Ack = i.Seq; c.HasAck = true;
        // a killstreak's control session (a turret being placed) takes the owner's input first; while it holds it,
        // Fire and Use belong to the session, not to the weapon or the vehicles
        var controlled = _streaks.Rules.RouteInput(c.Id, i);
        if (controlled) i.Buttons &= (ushort)(ushort.MaxValue ^ (ushort)(MoveButtons.Fire | MoveButtons.Use));
        if (c.Car == null && _ctx.Now < c.LaunchGraceUntil && (_ctx.Now < c.LaunchedAt + 0.3 || !c.Body.State.OnFloor)) i.Buttons &= (ushort)(ushort.MaxValue ^ (ushort)MoveButtons.Fire); // thrown by the Bigfoot and in the air: no shooting (#360)
        var useDown = (i.Buttons & (ushort)MoveButtons.Use) != 0;
        var useEdge = c.UseKey.Pressed(useDown);
        _rozhlas.OnInput(c, useDown);
        _thieves.OnUse(c, useDown);
        _plantationUse(c, useDown);
        var lightsEdge = Headlights.Edge((i.Buttons & (ushort)MoveButtons.Headlights) != 0, ref c.LightsHeld);
        _gear.SetHeadLamp(c, HeadLamp.Next(c.HeadLampOn, lightsEdge, c.Car == null, _ctx.Match.Get(c.Id)?.Life == PlayerLife.Alive)); // H on foot: the head lamp (in a vehicle: the car's lights, below)
        var streakDown = (i.Buttons & (ushort)MoveButtons.Killstreak) != 0;
        var streakEdge = c.StreakKey.Pressed(streakDown);
        // the throw key is fed every input; it is the grenade's only on foot and free-handed (a held key, a vehicle, death or a pin put the grenade away)
        var canThrow = c.Car == null && !controlled && _ctx.Match.Get(c.Id)?.Life == PlayerLife.Alive && _ctx.Tick > c.HeldUntilTick && !_sitters.IsSeated(c.Id) && !_pounce.IsPinned(c.Id)
            && !c.Body.State.OnLadder && _ctx.Now >= c.LaunchGraceUntil;
        var throwStep = c.Throw.Tick((i.Buttons & (ushort)MoveButtons.Throw) != 0, (i.Buttons & (ushort)MoveButtons.Aim) != 0, canThrow);
        if (_ctx.Match.Get(c.Id)?.Life != PlayerLife.Alive) return; // dead or waiting: inputs are acked but ignored
        if (c.Car == null && _ctx.Tick <= c.HeldUntilTick) return; // in the Bigfoot's hand: the input is acked, the body is where the Bigfoot puts it (issue #264)
        if (c.Car == null && _sitters.Input(c, i, useEdge)) return; // seated in the church: no walking, no weapon
        if (c.Car == null && _pounce.TryYaw(c.Id, out var pinYaw)) { c.Body.Yaw = pinYaw; c.Body.Pitch = 0; return; } // pinned by the demon: held on the floor, no walking, no weapon, no use
        if (streakEdge && !controlled && !_sandbox.StreakKey(c)) _streaks.Activate(c);
        if (c.Car == null && _streaks.Rules.ControlOf(c.Id) is { FreezesBody: true }) return; // controlling a streak entity that holds the body (the missile, a gunship): the body stands still
        if (c.Car is { Thrown: not null }) return; // the Bigfoot threw it: nobody steers, fires or gets out until it lands (issue #264)
        if (c.Car != null) { RideInput(c, i, dt, controlled, useDown, useEdge, lightsEdge); return; }
        var useAct = _scudUseKey(c, useDown, useEdge, dt); // beside a truck with an empty rack and a loaded cart a press waits to be a tap (it gets in) or a hold (it loads the rocket)
        if (useAct && _thieves.LootInReach(c) == null && (_mass.Sit(c) || _garage.Use(c) || _houses.Use(c) || _seats.TryEnter(c))) return; // the use key on lying Loot sends it back, it does not take a seat
        WalkAndFire(c, i, dt, throwStep);
    }

    /// <summary>
    /// An input of a player who sits in a vehicle: a helicopter or a predicted ground vehicle is stepped here from the driver's inputs, other cars in StepVehicles; no walking or weapon timers.
    /// A gunner's inputs fire the car's gun instead (its aim is the view yaw and pitch).
    /// </summary>
    void RideInput(ServerClient c, NetInput i, float dt, bool controlled, bool useDown, bool useEdge, bool lightsEdge)
    {
        if (c.Car == null) return; // (the caller checked)
        // The pilot of a helicopter steps it here, once per input, as the body on foot is: the pilot's client predicts the same steps (ADR 0006).
        if (c.CarSeat == 0 && c.Car.Flies) c.Car.StepFlight(FlightInput.FromNet(i, handsOff: controlled), dt, _ctx.Tick);
        else if (c.CarSeat == 0 && c.Car.Drives) { c.Car.Wake(); c.Car.StepDrive(DriveInput.FromNet(i, handsOff: controlled), dt); c.Car.DriveTick = _ctx.Tick; } // the same for a driven vehicle (ADR 0010, ADR 0014): a driver's input wakes a parked one
        else if (c.CarSeat == 0) { c.Throttle = Quant.Axis(i.MoveY); c.Steer = Quant.Axis(i.MoveX); c.Handbrake = (i.Buttons & (ushort)MoveButtons.Jump) != 0; }
        else if (c.Car.Gun != null && c.Car.Def.Seats[c.CarSeat].Role == "gunner")
        {
            _guns.Input(c, i);
            c.Body.Yaw = Quant.Yaw(i.Yaw); c.Body.Pitch = Quant.Pitch(i.Pitch); // a gunner's body faces where they aim, for everyone who sees them at the gun
        }
        else if (!controlled && RiderRules.Weapons(c.Car.Def, c.CarSeat, i) is { } seated) SeatWeapons(c, c.Car, i, seated); // an armed seat: the rider's own weapons (ADR 0011)
        if (!RiderPose.FollowsAim(c.Car.Def.Seats[c.CarSeat])) SeatLook(c, c.Car, i); // every other seat: the body is the vehicle's and the head looks where the rider's first-person view does (ADR 0026)
        if (lightsEdge) c.Car.Lights = Headlights.Toggle(c.Car.Lights);
        if (c.Car.Def.Launcher != null ? _scudUseKey(c, useDown, useEdge, dt) : TowServer.UseKey(c, c.Car, useDown, useEdge, dt)) _seats.Exit(c); // a Scud truck: a tap leaves, a hold loads a cart's rocket (a truck does not tow); with a trailer on the hook a tap leaves and a hold unhitches (UnhitchKey); otherwise the press leaves
    }

    /// <summary>An input of a living player on foot: the look, the move step, recoil and breath, the weapon (and the grenade), the fall and the void.</summary>
    void WalkAndFire(ServerClient c, NetInput i, float dt, GrenadeThrowStep throwStep)
    {
        c.Body.Yaw = Quant.Yaw(i.Yaw);
        c.Body.Pitch = Quant.Pitch(i.Pitch);
        var moveT = Prof.Begin(Prof.S.PlayerMove);
        c.Body.Simulate(i.ToMove(c.W.SpeedFor(i.Slot)), dt);
        Prof.End(Prof.S.PlayerMove, moveT);
        c.StepTicks++;
        c.Recoil.Tick(_ctx.Now, (i.Buttons & (ushort)MoveButtons.Fire) != 0, c.W.Current.Recoil);
        // breath hold: sprint is cancelled while aiming, so the sprint bit with aim held is the breath key
        c.Breath.Tick(_ctx.Now, (i.Buttons & (ushort)MoveButtons.Aim) != 0 && c.W.Current.ZoomFov > 0,
            (i.Buttons & (ushort)MoveButtons.Sprint) != 0, c.W.Current, c.Perks.HoldBreathTime);
        var wi = new WeaponInput((i.Buttons & (ushort)MoveButtons.Fire) != 0 && !c.Body.State.OnLadder, (i.Buttons & (ushort)MoveButtons.Reload) != 0, i.Slot, (i.Buttons & (ushort)MoveButtons.Switch) != 0, LockOnServer.HasLock(c));
        if (c.W.Update(_ctx.Now, wi) is { } shot)
        {
            if (_ctx.Match.NotifyFired(c.Id)) _smoke.EarlyProtectionEnds++; // spawn protection ends on a real shot, not on Fire held with an empty magazine
            { var fireT = Prof.Begin(Prof.S.Fire); _shots.Fire(c, i, shot); Prof.End(Prof.S.Fire, fireT); }
        }
        if (throwStep == GrenadeThrowStep.Throw && c.W.TryThrow(_ctx.Now) is { } nade)
        {
            if (_ctx.Match.NotifyFired(c.Id)) _smoke.EarlyProtectionEnds++;
            { var fireT = Prof.Begin(Prof.S.Fire); _shots.Fire(c, i, nade); Prof.End(Prof.S.Fire, fireT); }
        }
        if (c.W.Changed) _ctx.SendAmmo(c);
        c.Slot = c.W.HeldIndex;
        if (c.Body.LastFallDamage > 0 && _ctx.Now >= c.LaunchGraceUntil) _ctx.Match.ApplyDamage(c.Id, c.Id, SpecialWeapons.Fall, c.Body.LastFallDamage); // a launched player lands for the Bigfoot's damage, not the fall's
        if (c.Body.GlobalPosition.Y < _killY) GD.Print($"server: {c.Name} fell out of the world at {c.Body.GlobalPosition} (killY {_killY}, spawn {_ctx.Match.Get(c.Id)?.Position})");
        if (c.Body.GlobalPosition.Y < _killY) _ctx.Match.ApplyDamage(c.Id, c.Id, SpecialWeapons.Void, 9999, ignoreProtection: true);
    }

    /// <summary>
    /// One input of a rider in an armed seat (ADR 0011): what the weapon half of an input does on foot. The view is the aim, held inside the seat's
    /// arc (which turns and tilts with the vehicle), and it is where everyone sees the rider point; recoil and breath run as on foot; the weapon
    /// fires, reloads and switches by its own rules, and a shot is resolved from the seat (<see cref="ShotServer.Fire"/>).
    /// </summary>
    void SeatWeapons(ServerClient c, ServerCar car, NetInput i, WeaponInput input)
    {
        var (yaw, pitch, roll) = Fleet.AttitudeOf(car);
        var aim = SeatAimRules.Aim(car.Def.Seats[c.CarSeat].Aim, yaw, pitch, roll, Quant.Yaw(i.Yaw), Quant.Pitch(i.Pitch));
        c.Body.Yaw = aim.ViewYaw; c.Body.Pitch = aim.ViewPitch;
        c.Recoil.Tick(_ctx.Now, input.Fire, c.W.Current.Recoil);
        c.Breath.Tick(_ctx.Now, (i.Buttons & (ushort)MoveButtons.Aim) != 0 && c.W.Current.ZoomFov > 0,
            (i.Buttons & (ushort)MoveButtons.Sprint) != 0, c.W.Current, c.Perks.HoldBreathTime);
        if (c.W.Update(_ctx.Now, input with { Locked = LockOnServer.HasLock(c) }) is { } shot)
        {
            if (_ctx.Match.NotifyFired(c.Id)) _smoke.EarlyProtectionEnds++;
            { var fireT = Prof.Begin(Prof.S.Fire); _shots.Fire(c, i, shot, car); Prof.End(Prof.S.Fire, fireT); }
        }
        if (c.W.Changed) _ctx.SendAmmo(c);
        c.Slot = c.W.HeldIndex;
    }

    /// <summary>
    /// The look of a rider on a passive seat (ADR 0026): with the head-look bit set (their first-person view) the input's view is the head's, held to the neck's pitch
    /// and, for a helicopter's pilot, to the pitch alone (their yaw is the heading they steer to); with it clear the head faces ahead. The body keeps the vehicle's yaw
    /// either way and stands upright on the seat, as the hull tilts.
    /// </summary>
    void SeatLook(ServerClient c, ServerCar car, NetInput i)
    {
        var (off, pitch) = RiderPose.Passive(c.CarSeat == 0 && car.Flies, (i.Buttons & (ushort)MoveButtons.HeadLook) != 0, Fleet.AttitudeOf(car).Yaw, Quant.Yaw(i.Yaw), Quant.Pitch(i.Pitch));
        c.HeadOff = off; c.HeadPitch = pitch;
        CarSeats.ApplyHeadLook(c, car);
    }
}
