using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// Vehicle blast smoke scripts (ticket #103; headless, flat arena, three cars: car 0 and car 1 side by side, car 2 far away). Alice empties an AK-47 into car 0 from 6.5 m
/// (damage stages 1, 2, 3 must show in the snapshots in order, then the blast event arrives and the car disappears); bob stands 4 m beside it and takes the blast
/// with alice credited, alice takes her own self-reduced share, car 1 takes chain damage and survives, car 2 is untouched. A bazooka rocket then hits car 2 (rocket damage
/// stage). Car 0 comes back after the shortened return time at full health. Last, alice drives car 0 and a debug command destroys it under her: the driver dies in the blast.
/// Both bots print SHOOTER_BLAST_CLIENT_OK.
/// </summary>
public partial class NetClient
{
    IEnumerable<int> BlastBob()
    {
        foreach (var x in WaitFor(() => _vehicleExplosionLog.Count >= 1 && _hitLog.Any(h => h.M.Victim == _myId && h.M.Attacker != _myId), BotSeconds - 2, "the blast and its hit on bob")) yield return x;
        var h = _hitLog.First(h => h.M.Victim == _myId).M;
        Check(h.Damage >= 90, $"bob: the blast hit for {h.Damage} (4 m from the car)");
        Check(h.Kill && Match.KillFeed.Any(k => k.Victim == _myId), "bob: the blast killed him and the kill feed shows it");
        // stay in the game as a target and witness until alice's last scene (the driver's blast)
        foreach (var x in WaitFor(() => _vehicleExplosionLog.Count >= 2, BotSeconds - 2, "the second blast")) yield return x;
        WeaponsResult(true, "");
        yield break;
    }

    IEnumerable<int> BlastAlice()
    {
        var c = new NVec3(NetServer.ArenaX, 0, NetServer.ArenaZ);
        var car0 = VehicleRecord.FirstId; var car1 = (ushort)(car0 + 1); var car2 = (ushort)(car0 + 2);
        foreach (var x in WaitFor(() => Match.Rows.Any(r => r.Name == "bob") && _cars.Count >= 3, 15, "bob to join and the cars to appear")) yield return x;
        _wbBob = Match.Rows.First(r => r.Name == "bob").Id;
        Check(_cars.Values.All(v => v.Stage == 0 && !v.Destroyed), "all three cars start undamaged");
        foreach (var x in WaitBobAlive()) yield return x;

        // --- 1. AK-47 into car 0: stages 1, 2, 3 in order, then the blast
        var alicePos = c + new NVec3(0, 0, -23.5f); var bobPos = c + new NVec3(-4, 0, -30);
        foreach (var x in Place(alicePos, bobPos, 0.9f)) yield return x;
        _wbYaw = 0; _wbPitch = AimPitch(alicePos, _cars[car0].Pos, 0.75f);
        _wbSlot = 2;
        foreach (var x in WaitFor(() => Ammo is { Slot: 2 }, 2, "ammo state for the AK-47")) yield return x;
        foreach (var x in Wait(0.7)) yield return x;
        var seen = new List<int> { 0 };
        var until = Now + 8;
        while (!_cars[car0].Destroyed && !_cars[car0].Exploded && Now < until)
        {
            _wbPulse = MoveButtons.Fire;
            var st = _cars[car0].Stage;
            if (st != seen[^1]) seen.Add(st);
            yield return 0;
        }
        Check(_cars[car0].Destroyed || _cars[car0].Exploded, "car 0 was destroyed by the rifle fire");
        Check(seen.SequenceEqual(new[] { 0, 1, 2, 3 }), $"damage stages arrived in order 0,1,2,3 (saw {string.Join(",", seen)})");
        foreach (var x in WaitFor(() => _vehicleExplosionLog.Count >= 1, 2, "the blast event")) yield return x;
        var blast = _vehicleExplosionLog[^1].M;
        Check(blast.Id == car0 && blast.Attacker == _myId, $"blast of car 0, credited to alice (attacker #{blast.Attacker})");
        Check(MathF.Abs(blast.Position.X - c.X) < 0.5f && MathF.Abs(blast.Position.Z - (c.Z - 30)) < 0.5f && blast.Radius > 5, $"blast at the car, radius {blast.Radius}");
        foreach (var x in Wait(0.6)) yield return x;
        Check(!_cars[car0].Node.Visible, "the destroyed car is not drawn");
        Check(_cars[car1].Stage >= 1 && !_cars[car1].Destroyed, $"car 1 beside it took chain damage and survived (stage {_cars[car1].Stage})");
        Check(_cars[car2].Stage == 0, "car 2 far away is untouched");
        var bobHit = _hitLog.Where(h => h.M.Victim == _wbBob && h.M.Attacker == _myId).Select(h => h.M).LastOrDefault();
        Check(bobHit != null && bobHit.Damage >= 90, $"bob took the blast, credited to alice (damage {bobHit?.Damage})");
        var selfHit = _hitLog.Where(h => h.M.Victim == _myId && h.M.Attacker == _myId).Select(h => h.M).LastOrDefault();
        Check(selfHit != null && selfHit.Damage is >= 20 and <= 45, $"alice took her self-reduced share of the blast ({selfHit?.Damage})");

        // --- 2. a rocket into car 2: rocket damage stage, no explosion of the car
        foreach (var x in WaitBobAlive()) yield return x;
        var a2 = c + new NVec3(0, 0, -58);
        foreach (var x in Place(a2, c + new NVec3(-60, 0, -20), 0.9f)) yield return x;
        _wbYaw = 0; _wbPitch = AimPitch(a2, _cars[car2].Pos, 0.75f);
        _wbSlot = 3;
        foreach (var x in WaitFor(() => Ammo is { Slot: 3 }, 2, "ammo state for the bazooka")) yield return x;
        foreach (var x in Wait(0.9)) yield return x;
        var explosions = _explosionLog.Count;
        _wbPulse = MoveButtons.Fire;
        foreach (var x in WaitFor(() => _explosionLog.Count > explosions, 3, "the rocket to explode")) yield return x;
        foreach (var x in Wait(0.5)) yield return x;
        Check(_cars[car2].Stage == 1 && !_cars[car2].Destroyed, $"a direct rocket hit took car 2 to stage 1 (stage {_cars[car2].Stage})");

        // --- 3. car 0 returns undamaged after the (shortened) return time
        foreach (var x in WaitFor(() => !_cars[car0].Destroyed, 8, "car 0 to return")) yield return x;
        foreach (var x in Wait(0.3)) yield return x;
        Check(_cars[car0].Stage == 0 && _cars[car0].Node.Visible && XZ(_cars[car0].Pos - (c + new NVec3(0, 0, -30))) < 0.6f, "car 0 is back at its origin, undamaged and drawn again");

        // --- 4. the driver dies with the car
        foreach (var x in WaitBobAlive()) yield return x;
        var a3 = c + new NVec3(2.2f, 0, -30);
        Teleport(_myId, a3);
        foreach (var x in WaitFor(() => _gotSelf && !Dead && NearXZ(_me!.State.Position, a3) && _me.State.OnFloor, 4, "teleport next to car 0")) yield return x;
        foreach (var x in Wait(0.4)) yield return x;
        _wbPulse = MoveButtons.Use;
        foreach (var x in WaitFor(() => _driving && _cars[car0].Driver == _myId, 2, "entering car 0")) yield return x;
        var blasts = _vehicleExplosionLog.Count;
        Tx(_peer!, Wire.ChanReliable, Wire.Encode(new EventMsg((byte)EventKind.Debug, WeaponCodec.EncodeDebugVehicleDamage(car0, 9999))), (int)ENetPacketPeer.FlagReliable);
        _conn.Flush();
        foreach (var x in WaitFor(() => _vehicleExplosionLog.Count > blasts && Dead, 3, "the car to blow up and kill its driver")) yield return x;
        Check(Match.KillFeed.Any(k => k.Victim == _myId), "the driver's death is in the kill feed");
        foreach (var x in WaitFor(() => _cars[car0].Destroyed, 2, "car 0 to be marked destroyed")) yield return x;
        foreach (var x in WaitFor(() => !_cars[car0].Destroyed, 8, "car 0 to return again")) yield return x;
        WeaponsResult(true, "");
    }
}
