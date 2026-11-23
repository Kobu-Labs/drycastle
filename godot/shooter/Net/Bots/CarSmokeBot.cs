using Godot;
using Drycastle.Shooter.Net.Server;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Bots;

/// <summary>
/// Vehicle smoke scripts (car or motorcycle: the server's --car-kind; headless, on the flat arena with three of that type). Alice teleports next to car 0, enters it (E), tries to fire (must do
/// nothing), drives straight ahead with full throttle (must cover more than 20 m) into bob, who stands 40 m ahead and must take the run-over damage of the type's top speed
/// (capped at 100 and a kill for car and motorcycle); she brakes with the handbrake, exits with E (to a free side, on foot again) and the empty car stays where it was left
/// (it never despawns). Bob checks the hit event he received. Both print SHOOTER_CAR_CLIENT_OK.
/// </summary>
public sealed class CarSmokeBot : SmokeBot
{
    readonly bool _alice;
    IEnumerator<int>? _script;
    float _moveY;
    MoveButtons _buttons, _pulse;
    int _kind;

    public CarSmokeBot(IBotHost host, RozhlasTestBot? chat, string role) : base(host, chat) { _alice = role == "a"; }

    public override MoveInput Step()
    {
        try
        {
            _script ??= (_alice ? Alice() : Bob()).GetEnumerator();
            if (!Host.LinkEnded && !_script.MoveNext()) _script = null;
        }
        catch (SmokeAbort e) { Result(false, e.Message); }
        var mi = new MoveInput { MoveY = _moveY, Yaw = 0, Buttons = Ctx.Dead ? MoveButtons.None : _buttons | _pulse, SpeedScale = 1 };
        _pulse = MoveButtons.None;
        return mi;
    }

    public override bool TimedOut()
    {
        Result(false, "timeout");
        return true;
    }

    void Result(bool ok, string why)
    {
        if (Host.LinkEnded) return;
        Host.Leave();
        if (!ok) GD.PrintErr($"client {Ctx.PlayerName}: car smoke failure: {why}");
        GD.Print(ok ? "SHOOTER_CAR_CLIENT_OK" : "SHOOTER_CAR_CLIENT_FAIL");
        Host.Quit(ok);
    }

    IEnumerable<int> Bob()
    {
        foreach (var x in WaitFor(() => Host.Logs.Hits.Any(h => h.M.Victim == Ctx.MyId && h.M.Weapon == SpecialWeapons.Vehicle), Host.Seconds - 2, "run-over hit on bob")) yield return x;
        var h = Host.Logs.Hits.First(h => h.M.Victim == Ctx.MyId && h.M.Weapon == SpecialWeapons.Vehicle).M;
        CheckRunOver(h.Damage, h.Kill, "bob");
        if (h.Kill) Check(Ctx.Match.KillFeed.Any(k => k.Victim == Ctx.MyId && k.Weapon == SpecialWeapons.Vehicle), "bob: the kill feed credits the vehicle");
        Result(true, "");
        yield break;
    }

    IEnumerable<int> Alice()
    {
        var c = new NVec3(NetServer.ArenaX, 0, NetServer.ArenaZ);
        var carId = VehicleRecord.FirstId;
        foreach (var x in WaitFor(() => Ctx.Match.Rows.Any(r => r.Name == "bob") && Host.Cars.Count() >= 3, 15, "bob to join and the cars to appear")) yield return x;
        Check(Host.Cars.Count() == 3 && Host.Cars.Select(v => v.Node).Distinct().Count() == 3, "three vehicle entities in the snapshots");
        _kind = Car(carId).Kind;
        var kindDef = Host.Vehicles!.Vehicles[_kind];
        GD.Print($"client {Ctx.PlayerName}: smoke vehicle type {kindDef.Id} (kind {_kind})");
        Check(Host.Cars.All(v => v.Kind == _kind), "all three entities carry the same type in their record");
        BobId = Ctx.Match.Rows.First(r => r.Name == "bob").Id;
        foreach (var x in WaitBobAlive()) yield return x;
        var car0 = Car(carId).Pos;
        Check(Car(carId).Driver == 0, "car 0 starts empty");

        // stand next to the car, bob 40 m ahead in its path (car 0 faces -Z)
        var alicePos = car0 + new NVec3(2.2f, 0, 0); var bobPos = car0 + new NVec3(0, 0, -40);
        Teleport(Ctx.MyId, alicePos); Teleport(BobId, bobPos);
        foreach (var x in WaitFor(() => Host.OwnSpawned && NearXZ(Ctx.Me!.State.Position, alicePos) && NearXZ(BobPos, bobPos) && Ctx.Me!.State.OnFloor && BobPos.Y < 0.05f, 4, "teleport and landing")) yield return x;
        foreach (var x in Wait(0.4)) yield return x;

        // enter (E)
        _pulse = MoveButtons.Use;
        foreach (var x in WaitFor(() => Host.Driving && Car(carId).Driver == Ctx.MyId, 2, "entering the car")) yield return x;
        Check(true, "E next to the car: now the driver (snapshot driver id = alice, own state InVehicle)");

        // no shooting while driving
        var shots = Host.Logs.Shots.Count;
        for (var k = 0; k < 6; k++) { _pulse = MoveButtons.Fire; foreach (var x in Wait(0.1)) yield return x; }
        Check(Host.Logs.Shots.Count == shots, "firing while driving does nothing");

        // full throttle: more than 20 m, reaches run-over speed, bob dies on the way
        var start = Car(carId).Pos;
        _moveY = 1;
        var maxD = 0f;
        foreach (var x in WaitFor(() => { maxD = MathF.Max(maxD, XZ(Car(carId).Pos - start)); return maxD > 20f && Host.Logs.Hits.Any(h => h.M.Victim == BobId && h.M.Weapon == SpecialWeapons.Vehicle); }, 8, "driving more than 20 m and running over bob")) yield return x;
        var hit = Host.Logs.Hits.Last(h => h.M.Victim == BobId).M;
        Check(hit.Attacker == Ctx.MyId, "run-over: attacker alice");
        CheckRunOver(hit.Damage, hit.Kill, "alice");
        Check(Car(carId).Kmh > 8 * 3.6f, $"HUD speed above the run-over threshold ({Car(carId).Kmh:F0} km/h)");
        Check(Host.MyState == EntityState.InVehicle && Math.Abs(Ctx.Me!.Health - 100) < 0.5f, "the driver took no damage");

        // handbrake to a stop
        _moveY = 0; _buttons = MoveButtons.Jump;
        var last = Car(carId).Pos;
        foreach (var x in WaitFor(() => { var s = XZ(Car(carId).Pos - last); last = Car(carId).Pos; return s < 0.02f && Car(carId).Kmh < 1f; }, 5, "stopping with the handbrake")) yield return x;
        _buttons = MoveButtons.None;
        var away = XZ(Car(carId).Pos - car0);
        Check(away > 20f, $"the car ended {away:F0} m from its origin");

        // exit (E): on foot next to the car, free side
        _pulse = MoveButtons.Use;
        foreach (var x in WaitFor(() => !Host.Driving && Car(carId).Driver == 0, 2, "leaving the car")) yield return x;
        foreach (var x in Wait(0.5)) yield return x;
        var gap = XZ(Ctx.Me!.State.Position - Car(carId).Pos);
        Check(gap is > 1.0f and < 4.5f && Ctx.Me!.State.OnFloor, $"exit puts alice on the ground beside the car ({gap:F1} m from its centre)");

        // abandoned: it stays where it was left (#294: no despawn)
        var left = Car(carId).Pos;
        foreach (var x in Wait(4)) yield return x;
        Check(XZ(Car(carId).Pos - car0) > 20f && XZ(Car(carId).Pos - left) < 0.5f, "the abandoned car stays where it was left");
        Result(true, "");
    }


    /// <summary>The run-over damage must match the rule at the type's top speed (the run-up is long enough to reach it): within 15 percent below, never above, and a kill exactly when it reaches the cap.</summary>
    void CheckRunOver(float damage, bool kill, string who)
    {
        _kind = Host.Cars.First().Kind;
        var f = Host.Vehicles!;
        var exp = RunOver.Damage(f, f.Vehicles[_kind].TopSpeed);
        Check(damage <= exp + 0.5f && damage >= exp * 0.85f, $"{who}: run-over damage {damage:F0} matches the rule at top speed ({exp:F0})");
        Check(kill == (exp >= f.RunOverMaxDamage), $"{who}: kill flag {kill} as expected for {f.Vehicles[_kind].Id}");
    }
}
