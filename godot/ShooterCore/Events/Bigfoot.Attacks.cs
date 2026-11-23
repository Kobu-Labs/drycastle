using System.Numerics;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Weapons;
using static Drycastle.ShooterCore.Movement.Planar;

namespace Drycastle.ShooterCore.Events;

public sealed partial class Bigfoot
{
    // ---- attacks ----

    Vector3 Chest => Position + new Vector3(0, _d.HeightM * 0.7f, 0);
    static Vector3 ChestOf(Vector3 feet) => feet + new Vector3(0, MoveConsts.CapsuleHeight * 0.6f, 0);
    Vector3 Forward => new(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw));
    Vector3 Hand => Position + Forward * _d.Grab.HandForwardM + new Vector3(0, _d.Grab.HandHeightM, 0);

    void ReleaseTrunk(IReadOnlyList<BigfootPlayer> players)
    {
        Rest();
        _trunkCd = _d.Trunk.CooldownSeconds * CooldownMul * (0.8f + 0.4f * (float)_rng.NextDouble());
        BigfootPlayer? t = null;
        foreach (var p in players) if (p.Id == _target) t = p;
        if (t is not { } tp) return;
        var from = Position + Forward * 1.2f + new Vector3(0, _d.HeightM * 0.85f, 0);
        // the flight time is the distance over the log's speed; the aim is where the target will be when it arrives, plus the lead
        var seconds = Math.Clamp(Flat(tp.Feet, from) / MathF.Max(_d.Trunk.SpeedMps, 1f), _d.Trunk.MinFlightSeconds, _d.Trunk.LifeSeconds - 0.2f);
        var aim = ChestOf(tp.Feet + _targetVelocity * (seconds + _d.Trunk.AimLeadSeconds));
        seconds = Math.Clamp(Flat(aim, from) / MathF.Max(_d.Trunk.SpeedMps, 1f), _d.Trunk.MinFlightSeconds, _d.Trunk.LifeSeconds - 0.2f);
        _w.SpawnTrunk(from, Ballistics.LaunchVelocity(from, aim, seconds));
        _w.Fx(BigfootFxKind.TrunkThrow, from);
    }

    void Stomp()
    {
        _act.Enter(BigfootAct.Stomp, BigfootRules.StompSeconds);
        _stompCd = _d.Stomp.CooldownSeconds * CooldownMul;
        _waves.Add(new Wave { Origin = Position, Radius = 0 });
        _w.Fx(BigfootFxKind.Shockwave, Position);
    }

    void TickWaves(IReadOnlyList<BigfootPlayer> players, float dt)
    {
        for (var i = _waves.Count - 1; i >= 0; i--)
        {
            var wave = _waves[i];
            var before = wave.Radius;
            wave.Radius += _d.Stomp.WaveSpeedMps * dt;
            foreach (var p in players)
            {
                if (p.InVehicle || wave.Hit.Contains(p.Id)) continue;
                var d = Flat(p.Feet, wave.Origin);
                if (d < before - _d.Stomp.WaveBandM || d > wave.Radius) continue; // the ring this tick covers [before - band, radius]
                if (!p.OnFloor) continue;                                             // a jump avoids it
                if (_w.GroundY(p.Feet.X, p.Feet.Z, p.Feet.Y) is { } g && p.Feet.Y - g > _d.Stomp.WaveHeightM) continue;
                wave.Hit.Add(p.Id);
                var away = new Vector3(p.Feet.X - wave.Origin.X, 0, p.Feet.Z - wave.Origin.Z);
                away = away.LengthSquared() > 1e-6f ? Vector3.Normalize(away) : Forward;
                _w.Knock(p.Id, away * _d.Stomp.KnockbackMps + new Vector3(0, _d.Stomp.KnockbackLiftMps, 0));
                _w.Hurt(p.Id, _d.RollDamage(_rng), BigfootHurt.Shockwave);
            }
            if (wave.Radius >= _d.Stomp.WaveMaxRadiusM) _waves.RemoveAt(i);
        }
    }

    void ThrowTheVehicle()
    {
        _act.Enter(BigfootAct.Launch, BigfootRules.LaunchSwingSeconds); // the swing of the throw
        _vehicleCd = _d.VehicleThrow.CooldownSeconds * CooldownMul;
        if (_vehicleToThrow == 0) return;
        var c = new BigfootVehicle(_vehicleToThrow, _vehicleFrom); // it is overhead now, lifted from here
        _vehicleToThrow = 0;
        // a random heading and distance; the first one with ground under it is where it lands, else the last one with ground, else it goes straight up and down
        Vector3? landing = null;
        for (var i = 0; i < _d.VehicleThrow.PlacementTries; i++)
        {
            var angle = (float)_rng.NextDouble() * MathF.Tau;
            var dist = _d.VehicleThrow.MinDistanceM + (float)_rng.NextDouble() * (_d.VehicleThrow.MaxDistanceM - _d.VehicleThrow.MinDistanceM);
            var x = c.Feet.X + MathF.Cos(angle) * dist; var z = c.Feet.Z + MathF.Sin(angle) * dist;
            if (_w.GroundY(x, z, c.Feet.Y) is not { } y) continue;
            landing = new Vector3(x, y, z);
            break;
        }
        landing ??= c.Feet;
        _w.ThrowVehicle(c.Id, landing.Value, _d.VehicleThrow.FlightSeconds, _d.VehicleThrow.SpinRadPerS);
        _w.Fx(BigfootFxKind.VehicleThrow, c.Feet, c.Id, (byte)Math.Clamp((int)MathF.Round(_d.VehicleThrow.FlightSeconds * 10), 1, 255));
    }

    void TakeHold(IReadOnlyList<BigfootPlayer> players)
    {
        _grabCd = _d.Grab.CooldownSeconds * CooldownMul;
        BigfootPlayer? p = null;
        foreach (var q in players) if (q.Id == _target && !q.InVehicle && Flat(q.Feet, Position) <= _d.Grab.RangeM + 1.5f) p = q;
        if (p is not { } held) { Rest(); return; } // the player got away (or boarded a vehicle) during the reach; a hunt goes on
        EndHunt();
        _held = held.Id; _act.Enter(BigfootAct.Hold, _d.Grab.HoldSeconds);
        _w.Fx(BigfootFxKind.Grab, Position, held.Id);
        _w.Hold(held.Id, Hand);
    }

    void TickHold(IReadOnlyList<BigfootPlayer> players, float dt)
    {
        var still = false;
        foreach (var p in players) if (p.Id == _held) still = true;
        if (!still) { ReleaseHeld(); Rest(); return; } // the held player died or left
        if (!_act.Tick(dt)) { _w.Hold(_held, Hand); return; } // the hold time is the Hold act's countdown
        LaunchHeld(players);
    }

    void LaunchHeld(IReadOnlyList<BigfootPlayer> players)
    {
        var who = _held;
        var from = Hand;
        // another living player on foot if there is one (the nearest to the held player), else a random direction
        BigfootPlayer? other = null; var bd = float.MaxValue;
        foreach (var p in players)
        {
            if (p.Id == who || p.InVehicle) continue;
            var d = Flat(p.Feet, from);
            if (d < bd) { bd = d; other = p; }
        }
        // at another player when one is far enough; a nearer one only gives the direction, and the throw goes over their head
        Vector3 to;
        if (other is { } o && Flat(o.Feet, from) >= _d.Grab.MinLaunchDistanceM) to = o.Feet + new Vector3(0, 0.9f, 0);
        else
        {
            var a = (float)_rng.NextDouble() * MathF.Tau;
            var dir = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
            var far = _d.Grab.RandomLaunchSpeedMps * _d.Grab.LaunchFlightSeconds;
            if (other is { } near) { dir = near.Feet - from; dir.Y = 0; dir = dir.LengthSquared() > 1e-4f ? Vector3.Normalize(dir) : new Vector3(1, 0, 0); far = _d.Grab.MinLaunchDistanceM; }
            to = new Vector3(from.X, Position.Y, from.Z) + dir * far;
        }
        var v = Ballistics.LaunchVelocity(from, to, _d.Grab.LaunchFlightSeconds);
        _held = 0;
        _flyers.Add(new Flyer { Player = who, Left = _d.Grab.LaunchGraceSeconds });
        _w.Launch(who, v, _d.Grab.LaunchGraceSeconds);
        _w.Fx(BigfootFxKind.Launch, from, who);
        _act.Enter(BigfootAct.Launch, BigfootRules.LaunchSwingSeconds);
    }

    /// <summary>
    /// A vehicle it lifted for a throw that will not happen (wipe, death, removal) is put down at once; without this it hung overhead until the lift's own
    /// time-out. A throw that happened already cleared <c>_vehicleToThrow</c>.
    /// </summary>
    void DropLiftedVehicle()
    {
        if (_vehicleToThrow == 0) return;
        var car = _vehicleToThrow;
        _vehicleToThrow = 0;
        _w.DropVehicle(car);
    }

    void ReleaseHeld()
    {
        if (_held == 0) return;
        var who = _held;
        _held = 0;
        _w.Launch(who, Vector3.Zero, 0);
    }

    /// <summary>The launched players in the air: what they hit on the way is hurt (once each), and the landing hurts them (2 to 5, in place of the fall).</summary>
    void TickFlyers(IReadOnlyList<BigfootPlayer> players, float dt)
    {
        for (var i = _flyers.Count - 1; i >= 0; i--)
        {
            var f = _flyers[i];
            f.Age += dt; f.Left -= dt;
            BigfootPlayer? me = null;
            foreach (var p in players) if (p.Id == f.Player) me = p;
            if (me is not { } m || f.Left <= 0) { _flyers.RemoveAt(i); continue; } // died, left, or the grace is over
            foreach (var p in players)
            {
                if (p.Id == f.Player || p.InVehicle || f.Hit.Contains(p.Id)) continue;
                if (Vector3.Distance(p.Feet + new Vector3(0, 0.9f, 0), m.Feet + new Vector3(0, 0.9f, 0)) > _d.Grab.LaunchHitRadiusM + PlayerHitBox.WideRadius) continue;
                f.Hit.Add(p.Id);
                _w.Hurt(p.Id, _d.RollDamage(_rng), BigfootHurt.LaunchHit);
            }
            if (f.Age > 0.3f && m.OnFloor)
            {
                _w.Hurt(f.Player, _d.RollDamage(_rng), BigfootHurt.Landing);
                _flyers.RemoveAt(i);
            }
        }
    }
}
