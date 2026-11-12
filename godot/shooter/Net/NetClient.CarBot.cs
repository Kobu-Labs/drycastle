using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Vehicle smoke scripts (car, motorcycle or bicycle: the server's --car-kind; headless, on the flat arena with three of that type). Alice teleports next to car 0, enters it (E), tries to fire (must do
/// nothing), drives straight ahead with full throttle (must cover more than 20 m) into bob, who stands 40 m ahead and must take the run-over damage of the type's top speed
/// (capped at 100 and a kill for car and motorcycle, 30 for the slow bicycle); she brakes with the handbrake, exits with E (to a free side, on foot again) and the empty car returns to its origin
/// after the shortened abandon timer. Bob checks the hit event he received. Both print SHOOTER_CAR_CLIENT_OK.
/// </summary>
public partial class NetClient
{
    IEnumerator<int>? _cbScript;
    float _cbMoveY;
    MoveButtons _cbButtons, _cbPulse;

    MoveInput CarBotStep()
    {
        try
        {
            _cbScript ??= (CarBot == "a" ? CarAlice() : CarBob()).GetEnumerator();
            if (!_done && !_cbScript.MoveNext()) _cbScript = null;
        }
        catch (WbAbort e) { CarResult(false, e.Message); }
        var mi = new MoveInput { MoveY = _cbMoveY, Yaw = 0, Buttons = Dead ? MoveButtons.None : _cbButtons | _cbPulse, SpeedScale = 1 };
        _cbPulse = MoveButtons.None;
        return mi;
    }

    void CarResult(bool ok, string why)
    {
        if (_done) return;
        _done = true;
        if (!ok) GD.PrintErr($"client {PlayerName}: car smoke failure: {why}");
        GD.Print(ok ? "SHOOTER_CAR_CLIENT_OK" : "SHOOTER_CAR_CLIENT_FAIL");
        GetTree().Quit(ok ? 0 : 2);
    }

    IEnumerable<int> CarBob()
    {
        foreach (var x in WaitFor(() => _hitLog.Any(h => h.M.Victim == _myId && h.M.Weapon == SpecialWeapons.Vehicle), BotSeconds - 2, "run-over hit on bob")) yield return x;
        var h = _hitLog.First(h => h.M.Victim == _myId && h.M.Weapon == SpecialWeapons.Vehicle).M;
        CheckRunOver(h.Damage, h.Kill, "bob");
        if (h.Kill) Check(Match.KillFeed.Any(k => k.Victim == _myId && k.Weapon == SpecialWeapons.Vehicle), "bob: the kill feed credits the vehicle");
        CarResult(true, "");
        yield break;
    }

    IEnumerable<int> CarAlice()
    {
        var c = new NVec3(NetServer.ArenaX, 0, NetServer.ArenaZ);
        var carId = VehicleRecord.FirstId;
        foreach (var x in WaitFor(() => Match.Rows.Any(r => r.Name == "bob") && _cars.Count >= 3, 15, "bob to join and the cars to appear")) yield return x;
        Check(_cars.Count == 3 && _cars.Values.Select(v => v.Node).Distinct().Count() == 3, "three vehicle entities in the snapshots");
        _kind = _cars[carId].Kind;
        var kindDef = Vehicles!.Vehicles[_kind];
        GD.Print($"client {PlayerName}: smoke vehicle type {kindDef.Id} (kind {_kind})");
        Check(_cars.Values.All(v => v.Kind == _kind), "all three entities carry the same type in their record");
        _wbBob = Match.Rows.First(r => r.Name == "bob").Id;
        foreach (var x in WaitBobAlive()) yield return x;
        var car0 = _cars[carId].Pos;
        Check(_cars[carId].Driver == 0, "car 0 starts empty");

        // stand next to the car, bob 40 m ahead in its path (car 0 faces -Z)
        var alicePos = car0 + new NVec3(2.2f, 0, 0); var bobPos = car0 + new NVec3(0, 0, -40);
        Teleport(_myId, alicePos); Teleport(_wbBob, bobPos);
        foreach (var x in WaitFor(() => _gotSelf && NearXZ(_me!.State.Position, alicePos) && NearXZ(BobPos, bobPos) && _me.State.OnFloor && BobPos.Y < 0.05f, 4, "teleport and landing")) yield return x;
        foreach (var x in Wait(0.4)) yield return x;

        // enter (E)
        _cbPulse = MoveButtons.Use;
        foreach (var x in WaitFor(() => _driving && _cars[carId].Driver == _myId, 2, "entering the car")) yield return x;
        Check(true, "E next to the car: now the driver (snapshot driver id = alice, own state InVehicle)");

        // no shooting while driving
        var shots = _shotLog.Count;
        for (var k = 0; k < 6; k++) { _cbPulse = MoveButtons.Fire; foreach (var x in Wait(0.1)) yield return x; }
        Check(_shotLog.Count == shots, "firing while driving does nothing");

        // full throttle: more than 20 m, reaches run-over speed, bob dies on the way
        var start = _cars[carId].Pos;
        _cbMoveY = 1;
        var maxD = 0f;
        foreach (var x in WaitFor(() => { maxD = MathF.Max(maxD, XZ(_cars[carId].Pos - start)); return maxD > 20f && _hitLog.Any(h => h.M.Victim == _wbBob && h.M.Weapon == SpecialWeapons.Vehicle); }, 8, "driving more than 20 m and running over bob")) yield return x;
        var hit = _hitLog.Last(h => h.M.Victim == _wbBob).M;
        Check(hit.Attacker == _myId, "run-over: attacker alice");
        CheckRunOver(hit.Damage, hit.Kill, "alice");
        Check(_cars[carId].Kmh > 8 * 3.6f, $"HUD speed above the run-over threshold ({_cars[carId].Kmh:F0} km/h)");
        Check(_myState == EntityState.InVehicle && Math.Abs(_me!.Health - 100) < 0.5f, "the driver took no damage");

        // handbrake to a stop
        _cbMoveY = 0; _cbButtons = MoveButtons.Jump;
        var last = _cars[carId].Pos;
        foreach (var x in WaitFor(() => { var s = XZ(_cars[carId].Pos - last); last = _cars[carId].Pos; return s < 0.02f && _cars[carId].Kmh < 1f; }, 5, "stopping with the handbrake")) yield return x;
        _cbButtons = MoveButtons.None;
        var away = XZ(_cars[carId].Pos - car0);
        Check(away > 20f, $"the car ended {away:F0} m from its origin");

        // exit (E): on foot next to the car, free side
        _cbPulse = MoveButtons.Use;
        foreach (var x in WaitFor(() => !_driving && _cars[carId].Driver == 0, 2, "leaving the car")) yield return x;
        foreach (var x in Wait(0.5)) yield return x;
        var gap = XZ(_me!.State.Position - _cars[carId].Pos);
        Check(gap is > 1.0f and < 4.5f && _me.State.OnFloor, $"exit puts alice on the ground beside the car ({gap:F1} m from its centre)");

        // abandoned: back at the origin after the (shortened) timer
        var t0 = Now;
        foreach (var x in WaitFor(() => XZ(_cars[carId].Pos - car0) < 0.6f, 9, "the abandoned car to return to its origin")) yield return x;
        Check(Now - t0 > 1.0, $"the abandoned car returned to its origin after {Now - t0:F1} s");
        foreach (var x in Wait(0.5)) yield return x;
        CarResult(true, "");
    }

    int _kind;

    /// <summary>The run-over damage must match the rule at the type's top speed (the run-up is long enough to reach it): within 15 percent below, never above, and a kill exactly when it reaches the cap.</summary>
    void CheckRunOver(float damage, bool kill, string who)
    {
        _kind = _cars.Values.First().Kind;
        var f = Vehicles!;
        var exp = RunOver.Damage(f, f.Vehicles[_kind].TopSpeed);
        Check(damage <= exp + 0.5f && damage >= exp * 0.85f, $"{who}: run-over damage {damage:F0} matches the rule at top speed ({exp:F0})");
        Check(kill == (exp >= f.RunOverMaxDamage), $"{who}: kill flag {kill} as expected for {f.Vehicles[_kind].Id}");
    }

    static float XZ(NVec3 v) => MathF.Sqrt(v.X * v.X + v.Z * v.Z);
}
