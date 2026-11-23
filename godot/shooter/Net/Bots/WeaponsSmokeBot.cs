using Godot;
using Drycastle.Shooter.Net.Server;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Drycastle.ShooterCore.Net;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Bots;

/// <summary>
/// Weapons smoke scripts (headless). Bot "a" walks through the scenario on the server's flat test arena (teleported with the
/// debug command) and asserts the results from the events and snapshots it receives; bot "b" stands still, is the target
/// and checks the hit events it receives. Both print SHOOTER_WEAPONS_CLIENT_OK.
/// </summary>
public sealed class WeaponsSmokeBot : ArenaBot
{
    public WeaponsSmokeBot(IBotHost host, RozhlasTestBot? chat, string role) : base(host, chat, role) { }

    protected override string Tag => "WEAPONS";

    // ---- bob: the target ----

    protected override IEnumerable<int> Bob()
    {
        foreach (var x in WaitFor(() => Host.Logs.Hits.Count(h => h.M.Victim == Ctx.MyId) >= 5, Host.Seconds - 2, "5 hits on bob")) yield return x;
        var hits = Host.Logs.Hits.Where(h => h.M.Victim == Ctx.MyId).Select(h => h.M).ToList();
        Check(hits.Select(h => (int)h.Damage).SequenceEqual(new[] { 30, 60, 100, 50, 50 }), $"bob received hit events 30, 60, 100, 50, 50 (got {string.Join(",", hits.Select(h => h.Damage))})");
        Check(hits[1].Headshot && !hits[0].Headshot, "bob: only the second hit is a headshot");
        Check(hits[2].Backstab && hits[2].Kill && hits[4].Kill && !hits[3].Kill, "bob: backstab kill, then two knife hits ending in a kill");
        Check(Host.Logs.Shots.Count >= 7, $"bob saw {Host.Logs.Shots.Count} shot events (fired by alice)");
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

        // --- AK-47 on bob, 6 m, no wall
        foreach (var x in Place(c, c + new NVec3(0, 0, -6), 0.9f)) yield return x;
        _slot = 2;
        var t0 = Ctx.Now;
        foreach (var x in WaitFor(() => Host.Ammo is { Slot: 2 }, 2, "ammo state for slot 2")) yield return x;
        Check(Host.Ammo!.Slots[1] == (30, 90) && Host.Ammo.Slots[0] == (15, 60), "AK slot starts with 30/90, pistol still 15/60");
        foreach (var x in Wait(0.15)) yield return x;
        var shots = Host.Logs.Shots.Count;
        _pulse = MoveButtons.Fire; // inside the 0.5 s switch time: nothing happens
        foreach (var x in Wait(0.4)) yield return x;
        Check(Host.Logs.Shots.Count == shots, "firing inside the AK switch time (0.5 s) does nothing");
        foreach (var x in WaitFor(() => Ctx.Now - t0 > 0.7, 2, "switch time")) yield return x;
        var hitsBefore = Host.Logs.Hits.Count;
        _pulse = MoveButtons.Fire;
        foreach (var x in WaitFor(() => Host.Logs.Hits.Count > hitsBefore, 2, "hit event (body shot)")) yield return x;
        var h = Host.Logs.Hits[^1].M;
        Check(h.Attacker == Ctx.MyId && h.Victim == BobId && h.Damage == 30 && !h.Headshot && !h.Kill, "AK body shot: 30 damage, no headshot");
        foreach (var x in Wait(0.3)) yield return x;
        Check(BobHealth == 70, $"bob health 70 in the snapshot (got {BobHealth})");
        Check(Host.Ammo!.Slots[1].Mag == 29, "AK magazine 29 after one shot");

        _pitch = AimPitch(c, c + new NVec3(0, 0, -6), 1.6f);
        foreach (var x in Wait(0.2)) yield return x;
        hitsBefore = Host.Logs.Hits.Count;
        _pulse = MoveButtons.Fire;
        foreach (var x in WaitFor(() => Host.Logs.Hits.Count > hitsBefore, 2, "hit event (headshot)")) yield return x;
        h = Host.Logs.Hits[^1].M;
        Check(h.Damage == 60 && h.Headshot && !h.Kill, "AK headshot: 60 damage (x2)");
        foreach (var x in Wait(0.3)) yield return x;
        Check(BobHealth == 10, $"bob health 10 in the snapshot (got {BobHealth})");

        // --- wall: bob behind the wall (wall plane z = arena z - 3), the shot must stop at it
        var a2 = c + new NVec3(20, 0, 0); var b2 = c + new NVec3(20, 0, -6);
        foreach (var x in Place(a2, b2, 0.9f)) yield return x;
        hitsBefore = Host.Logs.Hits.Count; shots = Host.Logs.Shots.Count;
        _pulse = MoveButtons.Fire;
        foreach (var x in WaitFor(() => Host.Logs.Shots.Count > shots, 2, "shot event behind the wall test")) yield return x;
        var sh = Host.Logs.Shots[^1].M;
        Check(MathF.Abs(sh.End.Z - (c.Z - 2.75f)) < 0.3f && sh.Shooter == Ctx.MyId, $"tracer ends at the wall (z {sh.End.Z - c.Z:F2}, wall face at -2.75)");
        foreach (var x in Wait(0.6)) yield return x;
        Check(Host.Logs.Hits.Count == hitsBefore && BobHealth == 10, "the wall blocked the shot: no hit event, bob still at 10 health");

        // --- reload: AK mag 27 after three shots, reload takes 2.5 s, firing during it does nothing
        Check(Host.Ammo!.Slots[1] == (27, 90), $"AK 27/90 after three shots (got {Host.Ammo.Slots[1]})");
        var tr = Ctx.Now;
        _pulse = MoveButtons.Reload;
        foreach (var x in Wait(1.0)) yield return x;
        shots = Host.Logs.Shots.Count;
        _pulse = MoveButtons.Fire;
        foreach (var x in Wait(0.3)) yield return x;
        Check(Host.Logs.Shots.Count == shots, "firing during a reload does nothing");
        foreach (var x in WaitFor(() => Host.Ammo is { Slot: 2 } a && a.Slots[1].Mag == 30, 4, "reload to finish")) yield return x;
        var rt = Ctx.Now - tr;
        Check(rt >= 2.4 && rt <= 2.9, $"AK reload took {rt:F2}s (2.5 expected)");
        Check(Host.Ammo!.Slots[1] == (30, 87), $"reload moved 3 rounds: 30/87 (got {Host.Ammo.Slots[1]})");

        // --- knife
        _slot = 4;
        foreach (var x in Place(c, c + new NVec3(0, 0, -3.0f), 0.9f)) yield return x;
        foreach (var x in WaitFor(() => Host.Ammo is { Slot: 4 }, 2, "ammo state for the knife")) yield return x;
        foreach (var x in Wait(0.4)) yield return x;
        hitsBefore = Host.Logs.Hits.Count; shots = Host.Logs.Shots.Count;
        _pulse = MoveButtons.Fire;
        foreach (var x in WaitFor(() => Host.Logs.Shots.Count > shots, 2, "knife swing event")) yield return x;
        foreach (var x in Wait(0.4)) yield return x;
        Check(Host.Logs.Hits.Count == hitsBefore, "knife swing at 3 m (reach 1.8 m) hits nothing");
        foreach (var x in Place(c, c + new NVec3(0, 0, -1.2f), 0.9f)) yield return x; // alice is behind bob (bob faces -Z)
        foreach (var x in Wait(0.6)) yield return x;
        hitsBefore = Host.Logs.Hits.Count;
        _pulse = MoveButtons.Fire;
        foreach (var x in WaitFor(() => Host.Logs.Hits.Count > hitsBefore, 2, "backstab hit event")) yield return x;
        h = Host.Logs.Hits[^1].M;
        Check(h.Damage == 100 && h.Backstab && h.Kill && h.Attacker == Ctx.MyId, "knife backstab from behind: 100 damage and a kill");
        foreach (var x in WaitFor(() => Host.Ammo is { Slot: 4 } a && a.Slots[1].Reserve == 90, 2, "reserve refill after the kill")) yield return x;
        Check(Host.Ammo!.Slots[0].Reserve == 60 && Host.Ammo.Slots[1].Reserve == 90 && Host.Ammo.Slots[2].Reserve == 4, "the kill refilled every reserve (AK 87 -> 90)");

        // --- second life: frontal knife, two hits kill
        foreach (var x in WaitFor(() => Ctx.Match.Phase == MatchPhase.Running && BobState != EntityState.Dead, 8, "bob to respawn")) yield return x;
        foreach (var x in WaitBobAlive()) yield return x;
        var a3 = c + new NVec3(0, 0, -1.2f);
        foreach (var x in Place(a3, c, 0.9f)) yield return x; // alice stands in front of bob
        hitsBefore = Host.Logs.Hits.Count;
        _pulse = MoveButtons.Fire;
        foreach (var x in WaitFor(() => Host.Logs.Hits.Count > hitsBefore, 2, "frontal knife hit")) yield return x;
        h = Host.Logs.Hits[^1].M;
        Check(h.Damage == 50 && !h.Backstab && !h.Kill, "knife from the front: 50 damage");
        foreach (var x in Wait(0.6)) yield return x;
        hitsBefore = Host.Logs.Hits.Count;
        _pulse = MoveButtons.Fire;
        foreach (var x in WaitFor(() => Host.Logs.Hits.Count > hitsBefore, 2, "second knife hit")) yield return x;
        h = Host.Logs.Hits[^1].M;
        Check(h.Damage == 50 && h.Kill, "second knife hit kills (two hits)");
        foreach (var x in Wait(0.5)) yield return x;
        Result(true, "");
    }
}
