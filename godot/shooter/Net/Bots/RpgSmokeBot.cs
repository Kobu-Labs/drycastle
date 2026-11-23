using Godot;
using Drycastle.Shooter.Net.Server;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Bots;

/// <summary>
/// RPG smoke scripts (headless, on the weapons smoke's flat arena). Alice fires: a rocket into the wall with bob 6 m
/// behind it (splash through the wall, stopped by the wall, flight time), the reload, a rocket at her own feet
/// (self damage, knockback), a second reload and a direct hit on bob (kill, ammo refill). Bob checks the hits he received.
/// Every expected number is read from weapons.json (the rpg is weapon 2, hotbar slot 3 in the arena).
/// </summary>
public sealed class RpgSmokeBot : ArenaBot
{
    public RpgSmokeBot(IBotHost host, RozhlasTestBot? chat, string role) : base(host, chat, role) { }

    protected override string Tag => "RPG";

    WeaponDef SmokeRpg => Ctx.Weapons!.Weapons[2];
    /// <summary>Splash 6 m from the blast (bob behind the wall: far enough to live through it), and the self damage of a rocket at the own feet.</summary>
    int RpgSplash6 => (int)MathF.Round(ProjectileMath.SplashDamage(SmokeRpg, 6f, false));
    int RpgSelf => (int)MathF.Round(ProjectileMath.SplashDamage(SmokeRpg, 0.5f, true));

    protected override IEnumerable<int> Bob()
    {
        foreach (var x in WaitFor(() => Host.Logs.Hits.Count(h => h.M.Victim == Ctx.MyId) >= 2, Host.Seconds - 2, "2 hits on bob")) yield return x;
        var hits = Host.Logs.Hits.Where(h => h.M.Victim == Ctx.MyId).Select(h => h.M).ToList();
        Check(Math.Abs(hits[0].Damage - RpgSplash6) <= 2 && !hits[0].Kill && hits[0].Weapon == 2, $"bob: first hit is splash through the wall, {RpgSplash6} damage (got {hits[0].Damage})");
        Check(hits[1].Damage > 0 && hits[1].Kill, "bob: second hit is the direct hit, a kill");
        Check(Host.Logs.Explosions.Count >= 3 && Host.Logs.Launches.Count >= 3, $"bob saw {Host.Logs.Launches.Count} launch and {Host.Logs.Explosions.Count} explosion events");
        Result(true, "");
        yield break;
    }

    protected override IEnumerable<int> Alice()
    {
        var c = new NVec3(NetServer.ArenaX, 0, NetServer.ArenaZ);
        foreach (var x in WaitFor(() => Ctx.Match.Rows.Any(r => r.Name == "bob"), 15, "bob to join")) yield return x;
        BobId = Ctx.Match.Rows.First(r => r.Name == "bob").Id;
        foreach (var x in WaitBobAlive()) yield return x;
        foreach (var x in Wait(0.5)) yield return x;

        // --- 1. rocket into the wall (face at z = c.Z - 2.75, x 13..27); bob 6 m behind it: splash ignores walls
        var a1 = c + new NVec3(20, 0, 10); var b1 = c + new NVec3(20, 0, -9.05f);
        _slot = 3;
        foreach (var x in Place(a1, b1, 0.9f)) yield return x;
        _yaw = 0; _pitch = 0;
        foreach (var x in WaitFor(() => Host.Ammo is { Slot: 3 }, 2, "ammo state for the rpg")) yield return x;
        foreach (var x in Wait(0.8)) yield return x; // switch time 0.7 s
        var rpg = SmokeRpg; var full = rpg.ReserveAmmo;
        Check(Host.Ammo!.Slots[2] == (1, full), $"rpg starts with 1/{full} (got {Host.Ammo.Slots[2]})");
        _pulse = MoveButtons.Fire;
        foreach (var x in WaitFor(() => Host.Logs.Explosions.Count >= 1, 3, "first explosion")) yield return x;
        var l = Host.Logs.Launches[^1]; var e = Host.Logs.Explosions[^1];
        Check(l.M.Owner == Ctx.MyId && MathF.Abs(l.M.Velocity.Length() - rpg.ProjectileSpeed) < 0.1f, $"launch event: owner alice, speed {rpg.ProjectileSpeed:F0} m/s (got {l.M.Velocity.Length():F1})");
        Check(MathF.Abs(e.M.Position.Z - (c.Z - 2.75f)) < 0.25f && MathF.Abs(e.M.Position.X - (c.X + 20)) < 0.3f, $"the wall stopped the rocket: explosion at z {e.M.Position.Z - c.Z:F2} (face at -2.75)");
        var fly = 12.75 / rpg.ProjectileSpeed;
        Check(e.T - l.T > fly * 0.6 && e.T - l.T < fly + 0.25, $"flight time {e.T - l.T:F2} s for 12.75 m at {rpg.ProjectileSpeed:F0} m/s");
        Check(Host.RocketCount == 0, "the rocket entity is gone after the explosion");
        foreach (var x in Wait(0.4)) yield return x;
        var hit = Host.Logs.Hits.LastOrDefault().M;
        Check(hit != null && hit.Victim == BobId && Math.Abs(hit.Damage - RpgSplash6) <= 2 && !hit.Kill, $"splash at 6 m from the blast: ~{RpgSplash6} damage through the wall (got {hit?.Damage})");
        Check(Math.Abs(BobHealth - (100 - RpgSplash6)) <= 2, $"bob health {100 - RpgSplash6} in the snapshot (got {BobHealth})");
        Check(Host.Ammo!.Slots[2] == (0, full), $"mag 0/{full} after the shot (got {Host.Ammo.Slots[2]})");

        // --- 2. reload: firing with an empty mag starts it, it takes reload_time, firing during it does nothing
        foreach (var x in Wait(1.0)) yield return x; // past the 1.0 s fire interval
        var tr = Ctx.Now; var launches = Host.Logs.Launches.Count;
        _pulse = MoveButtons.Fire;
        foreach (var x in WaitFor(() => Host.Ammo is { ReloadRemaining: > 0 }, 1, "reload to start on a pull with an empty mag")) yield return x;
        foreach (var x in Wait(1.0)) yield return x;
        _pulse = MoveButtons.Fire;
        foreach (var x in Wait(0.3)) yield return x;
        Check(Host.Logs.Launches.Count == launches, "no rocket during the reload");
        foreach (var x in WaitFor(() => Host.Ammo is { Slot: 3 } a && a.Slots[2].Mag == 1, rpg.ReloadTime + 1.5, "reload to finish")) yield return x;
        var rt = Ctx.Now - tr;
        Check(rt >= rpg.ReloadTime - 0.15 && rt <= rpg.ReloadTime + 0.4, $"rpg reload took {rt:F2} s ({rpg.ReloadTime:F1} expected)");
        Check(Host.Ammo!.Slots[2] == (1, full - 1), $"reload moved one rocket: 1/{full - 1} (got {Host.Ammo.Slots[2]})");

        // --- 3. rocket at her own feet: self damage and knockback
        foreach (var x in Place(c, c + new NVec3(-60, 0, -60), 0.9f)) yield return x;
        _pitch = -MathF.PI / 2;
        foreach (var x in Wait(0.4)) yield return x;
        var healthBefore = Ctx.Me!.Health; var explosions = Host.Logs.Explosions.Count;
        var maxY = 0f; var until = Ctx.Now + 0.8;
        _pulse = MoveButtons.Fire;
        while (Ctx.Now < until) { maxY = MathF.Max(maxY, Ctx.Me!.State.Position.Y); yield return 0; }
        Check(Host.Logs.Explosions.Count == explosions + 1, "explosion event for the floor shot");
        hit = Host.Logs.Hits[^1].M;
        Check(hit.Victim == Ctx.MyId && hit.Attacker == Ctx.MyId && hit.Damage == RpgSelf, $"self damage: {RpgSelf} (got {hit.Damage})");
        Check(MathF.Round(Ctx.Me!.Health) == healthBefore - RpgSelf, $"own health {healthBefore} -> {healthBefore - RpgSelf} (got {Ctx.Me!.Health})");
        Check(maxY > 0.25f, $"knockback lifted alice {maxY:F2} m");
        Check(Host.Ammo!.Slots[2] == (0, full - 1), $"mag 0/{full - 1} (got {Host.Ammo.Slots[2]})");

        // --- 4. second reload, then the direct hit on bob (open ground, 12 m)
        foreach (var x in Wait(1.0)) yield return x;
        _pulse = MoveButtons.Fire;
        foreach (var x in WaitFor(() => Host.Ammo is { Slot: 3 } a && a.Slots[2].Mag == 1, rpg.ReloadTime + 1.5, "second reload")) yield return x;
        foreach (var x in Place(c, c + new NVec3(0, 0, -12), 0.9f)) yield return x;
        foreach (var x in Wait(0.3)) yield return x;
        var hitsBefore = Host.Logs.Hits.Count;
        _pulse = MoveButtons.Fire;
        foreach (var x in WaitFor(() => Host.Logs.Hits.Count > hitsBefore, 3, "direct hit event")) yield return x;
        hit = Host.Logs.Hits[^1].M;
        Check(hit.Victim == BobId && hit.Damage > 0 && hit.Kill && hit.Attacker == Ctx.MyId, "direct hit: a kill");
        var e3 = Host.Logs.Explosions[^1].M;
        Check(MathF.Abs(e3.Position.Z - (c.Z - 11.7f)) < 0.6f, $"explosion at bob's body (z {e3.Position.Z - c.Z:F2})");
        foreach (var x in WaitFor(() => Host.Ammo is { Slot: 3 } a && a.Slots[2] == (0, full), 2, "reserve refill after the kill")) yield return x;
        Check(true, $"the kill refilled the rpg reserve to {full}");
        foreach (var x in Wait(0.5)) yield return x;
        Result(true, "");
    }
}
