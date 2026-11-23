using Godot;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Diagnostics;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Server;

/// <summary>
/// A player's input on the server: each tick every joined player (bots included) takes the inputs queued for it. <see cref="ApplyInput"/> decides what an input is for (dead, held, seated, driving, walking),
/// <see cref="RideInput"/> and <see cref="WalkAndFire"/> do it, and a rider in an armed seat fires through <see cref="SeatWeapons"/>.
/// </summary>
public partial class NetServer
{
    void SimulateClient(ServerClient c, float dt)
    {
        // normally one input per tick; if the queue backs up (jitter), catch up with a second one
        var n = c.CatchUp.Steps(c.Inputs.Count); // a second input only while the catch-up credit lasts (#197)
        if (c.Inputs.Count == 0 && c.Car == null && _match.Get(c.Id)?.Life == PlayerLife.Alive) c.StarvedTicks++; // A-08: no input for this tick (the body does not move)
        for (var k = 0; k < n && c.Inputs.Count > 0; k++) ApplyInput(c, c.Inputs.Dequeue(), dt);
        _match.SetPosition(c.Id, new System.Numerics.Vector3(c.Body.GlobalPosition.X, c.Body.GlobalPosition.Y, c.Body.GlobalPosition.Z));
        c.Body.Health = _match.Get(c.Id)?.Health ?? 0;
        var p = c.Body.GlobalPosition - c.Start;
        c.MaxMoved = Mathf.Max(c.MaxMoved, new Vector2(p.X, p.Z).Length());
    }

    /// <summary>
    /// One input of one player: acked always, then what the player is doing decides what it does. The keys are fed first (also while dead), then in order: nothing while dead, held by the Bigfoot, seated in the church,
    /// pinned by the demon; the killstreak key; a held body (a controlled streak entity, a thrown car); driving or riding; the use key's actions on foot; and at last walking, the weapon and the grenade.
    /// </summary>
    void ApplyInput(ServerClient c, NetInput i, float dt)
    {
        c.Ack = i.Seq; c.HasAck = true;
        // a killstreak's control session (a turret being placed) takes the owner's input first; while it holds it,
        // Fire and Use belong to the session, not to the weapon or the vehicles
        var controlled = _streaks.RouteInput(c.Id, i);
        if (controlled) i.Buttons &= (ushort)(ushort.MaxValue ^ (ushort)(MoveButtons.Fire | MoveButtons.Use));
        if (c.Car == null && _elapsed < c.LaunchGraceUntil && (_elapsed < c.LaunchedAt + 0.3 || !c.Body.State.OnFloor)) i.Buttons &= (ushort)(ushort.MaxValue ^ (ushort)MoveButtons.Fire); // thrown by the Bigfoot and in the air: no shooting (#360)
        var useDown = (i.Buttons & (ushort)MoveButtons.Use) != 0;
        var useEdge = c.UseKey.Pressed(useDown);
        _rozhlas.OnInput(c, useDown);
        LootUse(c, useDown);
        PlantationUse(c, useDown);
        var lightsEdge = Headlights.Edge((i.Buttons & (ushort)MoveButtons.Headlights) != 0, ref c.LightsHeld);
        _gear.SetHeadLamp(c, HeadLamp.Next(c.HeadLampOn, lightsEdge, c.Car == null, _match.Get(c.Id)?.Life == PlayerLife.Alive)); // H on foot: the head lamp (in a vehicle: the car's lights, below)
        var streakDown = (i.Buttons & (ushort)MoveButtons.Killstreak) != 0;
        var streakEdge = c.StreakKey.Pressed(streakDown);
        // the throw key is fed every input; it is the grenade's only on foot and free-handed (a held key, a vehicle, death or a pin put the grenade away)
        var canThrow = c.Car == null && !controlled && _match.Get(c.Id)?.Life == PlayerLife.Alive && _tick > c.HeldUntilTick && !_sitters.IsSeated(c.Id) && !_pins.ContainsKey(c.Id)
            && !c.Body.State.OnLadder && _elapsed >= c.LaunchGraceUntil;
        var throwStep = c.Throw.Tick((i.Buttons & (ushort)MoveButtons.Throw) != 0, (i.Buttons & (ushort)MoveButtons.Aim) != 0, canThrow);
        if (_match.Get(c.Id)?.Life != PlayerLife.Alive) return; // dead or waiting: inputs are acked but ignored
        if (c.Car == null && _tick <= c.HeldUntilTick) return; // in the Bigfoot's hand: the input is acked, the body is where the Bigfoot puts it (issue #264)
        if (c.Car == null && _sitters.Input(c, i, useEdge)) return; // seated in the church: no walking, no weapon
        if (c.Car == null && _pins.TryGetValue(c.Id, out var pinYaw)) { c.Body.Yaw = pinYaw; c.Body.Pitch = 0; return; } // pinned by the demon: held on the floor, no walking, no weapon, no use
        if (streakEdge && !controlled) StreakActivate(c);
        if (c.Car == null && _streaks.ControlOf(c.Id) is { FreezesBody: true }) return; // controlling a streak entity that holds the body (the missile, a gunship): the body stands still
        if (c.Car is { Thrown: not null }) return; // the Bigfoot threw it: nobody steers, fires or gets out until it lands (issue #264)
        if (c.Car != null) { RideInput(c, i, dt, controlled, useDown, useEdge, lightsEdge); return; }
        var useAct = ScudUseKey(c, useDown, useEdge, dt); // beside a truck with an empty rack and a loaded cart a press waits to be a tap (it gets in) or a hold (it loads the rocket)
        if (useAct && LootInReach(c) == null && (MassSit(c) || _garage.Use(c) || TryEnterCar(c))) return; // the use key on lying Loot sends it back, it does not take a seat
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
        if (c.CarSeat == 0 && c.Car.Flies) c.Car.StepFlight(FlightInput.FromNet(i, handsOff: controlled), dt, _tick);
        else if (c.CarSeat == 0 && c.Car.Drives) { c.Car.Wake(); c.Car.StepDrive(DriveInput.FromNet(i, handsOff: controlled), dt); c.Car.DriveTick = _tick; } // the same for a driven vehicle (ADR 0010, ADR 0014): a driver's input wakes a parked one
        else if (c.CarSeat == 0) { c.Throttle = Quant.Axis(i.MoveY); c.Steer = Quant.Axis(i.MoveX); c.Handbrake = (i.Buttons & (ushort)MoveButtons.Jump) != 0; }
        else if (c.Car.Gun != null && c.Car.Def.Seats[c.CarSeat].Role == "gunner")
        {
            GunnerInput(c, i);
            c.Body.Yaw = Quant.Yaw(i.Yaw); c.Body.Pitch = Quant.Pitch(i.Pitch); // a gunner's body faces where they aim, for everyone who sees them at the gun
        }
        else if (!controlled && RiderRules.Weapons(c.Car.Def, c.CarSeat, i) is { } seated) SeatWeapons(c, c.Car, i, seated); // an armed seat: the rider's own weapons (ADR 0011)
        if (!RiderPose.FollowsAim(c.Car.Def.Seats[c.CarSeat])) SeatLook(c, c.Car, i); // every other seat: the body is the vehicle's and the head looks where the rider's first-person view does (ADR 0026)
        if (lightsEdge) c.Car.Lights = Headlights.Toggle(c.Car.Lights);
        if (c.Car.Def.Launcher != null ? ScudUseKey(c, useDown, useEdge, dt) : TowUseKey(c, c.Car, useDown, useEdge, dt)) ExitCar(c); // a Scud truck: a tap leaves, a hold loads a cart's rocket (a truck does not tow); with a trailer on the hook a tap leaves and a hold unhitches (UnhitchKey); otherwise the press leaves
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
        c.Recoil.Tick(_elapsed, (i.Buttons & (ushort)MoveButtons.Fire) != 0, c.W.Current.Recoil);
        // breath hold: sprint is cancelled while aiming, so the sprint bit with aim held is the breath key
        c.Breath.Tick(_elapsed, (i.Buttons & (ushort)MoveButtons.Aim) != 0 && c.W.Current.ZoomFov > 0,
            (i.Buttons & (ushort)MoveButtons.Sprint) != 0, c.W.Current, c.Perks.HoldBreathTime);
        var wi = new WeaponInput((i.Buttons & (ushort)MoveButtons.Fire) != 0 && !c.Body.State.OnLadder, (i.Buttons & (ushort)MoveButtons.Reload) != 0, i.Slot, (i.Buttons & (ushort)MoveButtons.Switch) != 0, HasLock(c));
        if (c.W.Update(_elapsed, wi) is { } shot)
        {
            if (_match.NotifyFired(c.Id)) _smoke.EarlyProtectionEnds++; // spawn protection ends on a real shot, not on Fire held with an empty magazine
            { var fireT = Prof.Begin(Prof.S.Fire); FireWeapon(c, i, shot); Prof.End(Prof.S.Fire, fireT); }
        }
        if (throwStep == GrenadeThrowStep.Throw && c.W.TryThrow(_elapsed) is { } nade)
        {
            if (_match.NotifyFired(c.Id)) _smoke.EarlyProtectionEnds++;
            { var fireT = Prof.Begin(Prof.S.Fire); FireWeapon(c, i, nade); Prof.End(Prof.S.Fire, fireT); }
        }
        if (c.W.Changed) SendAmmo(c);
        c.Slot = (byte)(c.W.Slot - 1);
        if (c.Body.LastFallDamage > 0 && _elapsed >= c.LaunchGraceUntil) _match.ApplyDamage(c.Id, c.Id, SpecialWeapons.Fall, c.Body.LastFallDamage); // a launched player lands for the Bigfoot's damage, not the fall's
        if (c.Body.GlobalPosition.Y < KillY) GD.Print($"server: {c.Name} fell out of the world at {c.Body.GlobalPosition} (killY {KillY}, spawn {_match.Get(c.Id)?.Position})");
        if (c.Body.GlobalPosition.Y < KillY) _match.ApplyDamage(c.Id, c.Id, SpecialWeapons.Void, 9999, ignoreProtection: true);
    }

    /// <summary>
    /// One input of a rider in an armed seat (ADR 0011): what the weapon half of an input does on foot. The view is the aim, held inside the seat's
    /// arc (which turns and tilts with the vehicle), and it is where everyone sees the rider point; recoil and breath run as on foot; the weapon
    /// fires, reloads and switches by its own rules, and a shot is resolved from the seat (<see cref="FireWeapon"/>).
    /// </summary>
    void SeatWeapons(ServerClient c, ServerCar car, NetInput i, WeaponInput input)
    {
        var (yaw, pitch, roll) = AttitudeOf(car);
        var aim = SeatAimRules.Aim(car.Def.Seats[c.CarSeat].Aim, yaw, pitch, roll, Quant.Yaw(i.Yaw), Quant.Pitch(i.Pitch));
        c.Body.Yaw = aim.ViewYaw; c.Body.Pitch = aim.ViewPitch;
        c.Recoil.Tick(_elapsed, input.Fire, c.W.Current.Recoil);
        c.Breath.Tick(_elapsed, (i.Buttons & (ushort)MoveButtons.Aim) != 0 && c.W.Current.ZoomFov > 0,
            (i.Buttons & (ushort)MoveButtons.Sprint) != 0, c.W.Current, c.Perks.HoldBreathTime);
        if (c.W.Update(_elapsed, input with { Locked = HasLock(c) }) is { } shot)
        {
            if (_match.NotifyFired(c.Id)) _smoke.EarlyProtectionEnds++;
            { var fireT = Prof.Begin(Prof.S.Fire); FireWeapon(c, i, shot, car); Prof.End(Prof.S.Fire, fireT); }
        }
        if (c.W.Changed) SendAmmo(c);
        c.Slot = (byte)(c.W.Slot - 1);
    }

    /// <summary>
    /// The look of a rider on a passive seat (ADR 0026): with the head-look bit set (their first-person view) the input's view is the head's, held to the neck's pitch
    /// and, for a helicopter's pilot, to the pitch alone (their yaw is the heading they steer to); with it clear the head faces ahead. The body keeps the vehicle's yaw
    /// either way and stands upright on the seat, as the hull tilts.
    /// </summary>
    void SeatLook(ServerClient c, ServerCar car, NetInput i)
    {
        var (off, pitch) = RiderPose.Passive(c.CarSeat == 0 && car.Flies, (i.Buttons & (ushort)MoveButtons.HeadLook) != 0, AttitudeOf(car).Yaw, Quant.Yaw(i.Yaw), Quant.Pitch(i.Pitch));
        c.HeadOff = off; c.HeadPitch = pitch;
        ApplyHeadLook(c, car);
    }

    /// <summary>A passive rider's body yaw and pitch for the snapshot: the hull's yaw now, turned by their look (<see cref="ServerClient.HeadOff"/>), at their look's pitch.</summary>
    void ApplyHeadLook(ServerClient c, ServerCar car)
    {
        c.Body.Yaw = AttitudeOf(car).Yaw + c.HeadOff;
        c.Body.Pitch = c.HeadPitch;
    }

    void SendAmmo(ServerClient c)
    {
        c.W.Changed = false;
        Send(c, new EventMsg((byte)EventKind.Ammo, WeaponCodec.Encode(WeaponCodec.BuildAmmo(c.W, _elapsed))));
    }

    /// <summary>The clients simulated in this tick (reused every tick).</summary>
    readonly List<ServerClient> _simClients = new();
}
