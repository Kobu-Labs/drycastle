using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// Bazooka smoke scripts (headless, on the weapons smoke's flat arena). Alice fires: a rocket into the wall with bob 3 m
/// behind it (splash 73 through the wall, stopped by the wall, flight time), the reload (3.5 s), a rocket at her own feet
/// (self damage 50, knockback), a second reload and a direct hit on bob (100, kill, ammo refill). Bob checks the hits he received.
/// </summary>
public partial class NetClient
{
    IEnumerable<int> BazBob()
    {
        foreach (var x in WaitFor(() => _hitLog.Count(h => h.M.Victim == _myId) >= 2, BotSeconds - 2, "2 hits on bob")) yield return x;
        var hits = _hitLog.Where(h => h.M.Victim == _myId).Select(h => h.M).ToList();
        Check(hits[0].Damage is >= 72 and <= 74 && !hits[0].Kill && hits[0].Weapon == 2, $"bob: first hit is splash through the wall, 73 damage (got {hits[0].Damage})");
        Check(hits[1].Damage == 100 && hits[1].Kill, "bob: second hit is the direct hit, 100 damage and a kill");
        Check(_explosionLog.Count >= 3 && _launchLog.Count >= 3, $"bob saw {_launchLog.Count} launch and {_explosionLog.Count} explosion events");
        WeaponsResult(true, "");
        yield break;
    }

    IEnumerable<int> BazAlice()
    {
        var c = new NVec3(NetServer.ArenaX, 0, NetServer.ArenaZ);
        foreach (var x in WaitFor(() => Match.Rows.Any(r => r.Name == "bob"), 15, "bob to join")) yield return x;
        _wbBob = Match.Rows.First(r => r.Name == "bob").Id;
        foreach (var x in WaitBobAlive()) yield return x;
        foreach (var x in Wait(0.5)) yield return x;

        // --- 1. rocket into the wall (face at z = c.Z - 2.75, x 13..27); bob 3 m behind it: splash ignores walls
        var a1 = c + new NVec3(20, 0, 10); var b1 = c + new NVec3(20, 0, -6.05f);
        _wbSlot = 3;
        foreach (var x in Place(a1, b1, 0.9f)) yield return x;
        _wbYaw = 0; _wbPitch = 0;
        foreach (var x in WaitFor(() => Ammo is { Slot: 3 }, 2, "ammo state for the bazooka")) yield return x;
        foreach (var x in Wait(0.8)) yield return x; // switch time 0.7 s
        Check(Ammo!.Slots[2] == (1, 4), $"bazooka starts with 1/4 (got {Ammo.Slots[2]})");
        _wbPulse = MoveButtons.Fire;
        foreach (var x in WaitFor(() => _explosionLog.Count >= 1, 3, "first explosion")) yield return x;
        var l = _launchLog[^1]; var e = _explosionLog[^1];
        Check(l.M.Owner == _myId && MathF.Abs(l.M.Velocity.Length() - 40f) < 0.1f, $"launch event: owner alice, speed 40 m/s (got {l.M.Velocity.Length():F1})");
        Check(MathF.Abs(e.M.Position.Z - (c.Z - 2.75f)) < 0.25f && MathF.Abs(e.M.Position.X - (c.X + 20)) < 0.3f, $"the wall stopped the rocket: explosion at z {e.M.Position.Z - c.Z:F2} (face at -2.75)");
        Check(e.T - l.T is > 0.2 and < 0.55, $"flight time {e.T - l.T:F2} s for 12.75 m at 40 m/s");
        Check(_rockets.Count == 0, "the rocket entity is gone after the explosion");
        foreach (var x in Wait(0.4)) yield return x;
        var hit = _hitLog.LastOrDefault().M;
        Check(hit != null && hit.Victim == _wbBob && hit.Damage is >= 72 and <= 74 && !hit.Kill, $"splash at 3 m from the blast: ~73 damage through the wall (got {hit?.Damage})");
        Check(BobHealth is >= 26 and <= 28, $"bob health 27 in the snapshot (got {BobHealth})");
        Check(Ammo!.Slots[2] == (0, 4), $"mag 0/4 after the shot (got {Ammo.Slots[2]})");

        // --- 2. reload: firing with an empty mag starts it, 3.5 s, firing during it does nothing
        foreach (var x in Wait(1.0)) yield return x; // past the 1.0 s fire interval
        var tr = Now; var launches = _launchLog.Count;
        _wbPulse = MoveButtons.Fire;
        foreach (var x in WaitFor(() => Ammo is { ReloadRemaining: > 0 }, 1, "reload to start on a pull with an empty mag")) yield return x;
        foreach (var x in Wait(1.0)) yield return x;
        _wbPulse = MoveButtons.Fire;
        foreach (var x in Wait(0.3)) yield return x;
        Check(_launchLog.Count == launches, "no rocket during the reload");
        foreach (var x in WaitFor(() => Ammo is { Slot: 3 } a && a.Slots[2].Mag == 1, 5, "reload to finish")) yield return x;
        var rt = Now - tr;
        Check(rt is >= 3.35 and <= 3.9, $"bazooka reload took {rt:F2} s (3.5 expected)");
        Check(Ammo!.Slots[2] == (1, 3), $"reload moved one rocket: 1/3 (got {Ammo.Slots[2]})");

        // --- 3. rocket at her own feet: self damage 50 and knockback
        foreach (var x in Place(c, c + new NVec3(-60, 0, -60), 0.9f)) yield return x;
        _wbPitch = -MathF.PI / 2;
        foreach (var x in Wait(0.4)) yield return x;
        var healthBefore = _me!.Health; var explosions = _explosionLog.Count;
        var maxY = 0f; var until = Now + 0.8;
        _wbPulse = MoveButtons.Fire;
        while (Now < until) { maxY = MathF.Max(maxY, _me.State.Position.Y); yield return 0; }
        Check(_explosionLog.Count == explosions + 1, "explosion event for the floor shot");
        hit = _hitLog[^1].M;
        Check(hit.Victim == _myId && hit.Attacker == _myId && hit.Damage == 50, $"self damage: 50 (got {hit.Damage})");
        Check(MathF.Round(_me.Health) == healthBefore - 50, $"own health {healthBefore} -> 50 (got {_me.Health})");
        Check(maxY > 0.25f, $"knockback lifted alice {maxY:F2} m");
        Check(Ammo!.Slots[2] == (0, 3), $"mag 0/3 (got {Ammo.Slots[2]})");

        // --- 4. second reload, then the direct hit on bob (open ground, 12 m)
        foreach (var x in Wait(1.0)) yield return x;
        _wbPulse = MoveButtons.Fire;
        foreach (var x in WaitFor(() => Ammo is { Slot: 3 } a && a.Slots[2].Mag == 1, 5, "second reload")) yield return x;
        foreach (var x in Place(c, c + new NVec3(0, 0, -12), 0.9f)) yield return x;
        foreach (var x in Wait(0.3)) yield return x;
        var hitsBefore = _hitLog.Count;
        _wbPulse = MoveButtons.Fire;
        foreach (var x in WaitFor(() => _hitLog.Count > hitsBefore, 3, "direct hit event")) yield return x;
        hit = _hitLog[^1].M;
        Check(hit.Victim == _wbBob && hit.Damage == 100 && hit.Kill && hit.Attacker == _myId, "direct hit: 100 damage and a kill");
        var e3 = _explosionLog[^1].M;
        Check(MathF.Abs(e3.Position.Z - (c.Z - 11.7f)) < 0.6f, $"explosion at bob's body (z {e3.Position.Z - c.Z:F2})");
        foreach (var x in WaitFor(() => Ammo is { Slot: 3 } a && a.Slots[2] == (0, 4), 2, "reserve refill after the kill")) yield return x;
        Check(true, "the kill refilled the bazooka reserve to 4");
        foreach (var x in Wait(0.5)) yield return x;
        WeaponsResult(true, "");
    }
}
