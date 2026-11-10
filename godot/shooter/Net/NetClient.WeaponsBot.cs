using Godot;
using Suchohrad.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Suchohrad.Shooter;

/// <summary>
/// Weapons smoke scripts (headless). Bot "a" walks through the scenario on the server's flat test arena (teleported with the
/// debug command) and asserts the results from the events and snapshots it receives; bot "b" stands still, is the target
/// and checks the hit events it receives. Both print SHOOTER_WEAPONS_CLIENT_OK.
/// </summary>
public partial class NetClient
{
    sealed class WbAbort : Exception { public WbAbort(string m) : base(m) { } }

    IEnumerator<int>? _wbScript;
    MoveButtons _wbPulse;
    int _wbSlot = 1;
    float _wbYaw, _wbPitch;
    ushort _wbBob;

    MoveInput WeaponsBotStep()
    {
        try
        {
            _wbScript ??= (BazookaBot != "" ? (WeaponsBot == "a" ? BazAlice() : BazBob()) : WeaponsBot == "a" ? AliceScript() : BobScript()).GetEnumerator();
            if (!_done && !_wbScript.MoveNext()) _wbScript = null;
        }
        catch (WbAbort e) { WeaponsFail(e.Message); }
        if (WeaponsBot == "a") { _wantSlot = _wbSlot; _me!.Pitch = _wbPitch; }
        var mi = new MoveInput { Yaw = WeaponsBot == "a" ? _wbYaw : 0, Buttons = Dead ? MoveButtons.None : _wbPulse, SpeedScale = 1 };
        _wbPulse = MoveButtons.None;
        return mi;
    }

    void WeaponsFail(string why) => WeaponsResult(false, why);

    void WeaponsResult(bool ok, string why)
    {
        if (_done) return;
        _done = true;
        if (!ok) GD.PrintErr($"client {PlayerName}: weapons smoke failure: {why}");
        GD.Print(ok ? $"SHOOTER_{(BazookaBot != "" ? "BAZOOKA" : "WEAPONS")}_CLIENT_OK" : $"SHOOTER_{(BazookaBot != "" ? "BAZOOKA" : "WEAPONS")}_CLIENT_FAIL");
        GetTree().Quit(ok ? 0 : 2);
    }

    void Check(bool ok, string what)
    {
        if (!ok) throw new WbAbort($"check failed: {what}");
        GD.Print($"client {PlayerName}: ok - {what}");
    }

    IEnumerable<int> Wait(double seconds)
    {
        var end = Now + seconds;
        while (Now < end) yield return 0;
    }

    IEnumerable<int> WaitFor(Func<bool> cond, double timeout, string what)
    {
        var end = Now + timeout;
        while (!cond())
        {
            if (Now > end) throw new WbAbort($"timeout waiting for {what}");
            yield return 0;
        }
    }

    // ---- bob: the target ----

    IEnumerable<int> BobScript()
    {
        foreach (var x in WaitFor(() => _hitLog.Count(h => h.M.Victim == _myId) >= 5, BotSeconds - 2, "5 hits on bob")) yield return x;
        var hits = _hitLog.Where(h => h.M.Victim == _myId).Select(h => h.M).ToList();
        Check(hits.Select(h => (int)h.Damage).SequenceEqual(new[] { 30, 60, 100, 50, 50 }), $"bob received hit events 30, 60, 100, 50, 50 (got {string.Join(",", hits.Select(h => h.Damage))})");
        Check(hits[1].Headshot && !hits[0].Headshot, "bob: only the second hit is a headshot");
        Check(hits[2].Backstab && hits[2].Kill && hits[4].Kill && !hits[3].Kill, "bob: backstab kill, then two knife hits ending in a kill");
        Check(_shotLog.Count >= 7, $"bob saw {_shotLog.Count} shot events (fired by alice)");
        WeaponsResult(true, "");
        yield break;
    }

    // ---- alice: the shooter ----

    static float AimYaw(NVec3 from, NVec3 to) => MathF.Atan2(-(to.X - from.X), -(to.Z - from.Z));
    static float AimPitch(NVec3 from, NVec3 to, float height)
    {
        var h = MathF.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Z - from.Z) * (to.Z - from.Z));
        return MathF.Atan2(to.Y + height - (from.Y + MoveConsts.EyeHeight), h);
    }

    void Teleport(ushort id, NVec3 pos)
    {
        _peer!.Send(Wire.ChanReliable, Wire.Encode(new EventMsg((byte)EventKind.Debug, WeaponCodec.EncodeDebugTeleport(id, pos))), (int)ENetPacketPeer.FlagReliable);
        _conn.Flush();
    }

    NVec3 BobPos => _dec.States.TryGetValue(_wbBob, out var r) ? r.Position : default;
    EntityState BobState => _dec.States.TryGetValue(_wbBob, out var r) ? r.State : EntityState.Dead;
    int BobHealth => _dec.States.TryGetValue(_wbBob, out var r) ? r.Health : -1;

    /// <summary>Put alice and bob at the given arena positions, wait until both stand there, and face bob (aiming at <paramref name="aimHeight"/>).</summary>
    IEnumerable<int> Place(NVec3 alice, NVec3 bob, float aimHeight)
    {
        Teleport(_myId, alice); Teleport(_wbBob, bob);
        foreach (var x in WaitFor(() => _gotSelf && !Dead && NearXZ(_me!.State.Position, alice) && NearXZ(BobPos, bob) && _me.State.OnFloor && BobPos.Y < 0.05f, 4, "teleport and landing")) yield return x;
        foreach (var x in Wait(0.3)) yield return x;
        _wbYaw = AimYaw(alice, bob);
        _wbPitch = AimPitch(alice, bob, aimHeight);
    }

    static bool NearXZ(NVec3 a, NVec3 b) => MathF.Abs(a.X - b.X) < 0.3f && MathF.Abs(a.Z - b.Z) < 0.3f;

    IEnumerable<int> WaitBobAlive()
    {
        foreach (var x in WaitFor(() => Match.Phase == MatchPhase.Running && BobState == EntityState.Alive && _myState == EntityState.Alive, 8, "bob alive and unprotected")) yield return x;
    }

    IEnumerable<int> AliceScript()
    {
        var c = new NVec3(NetServer.ArenaX, 0, NetServer.ArenaZ);
        foreach (var x in WaitFor(() => Match.Rows.Any(r => r.Name == "bob"), 15, "bob to join")) yield return x;
        _wbBob = Match.Rows.First(r => r.Name == "bob").Id;
        foreach (var x in WaitBobAlive()) yield return x;
        foreach (var x in Wait(0.5)) yield return x;

        // --- AK-47 on bob, 6 m, no wall
        foreach (var x in Place(c, c + new NVec3(0, 0, -6), 0.9f)) yield return x;
        _wbSlot = 2;
        var t0 = Now;
        foreach (var x in WaitFor(() => Ammo is { Slot: 2 }, 2, "ammo state for slot 2")) yield return x;
        Check(Ammo!.Slots[1] == (30, 90) && Ammo.Slots[0] == (15, 60), "AK slot starts with 30/90, pistol still 15/60");
        foreach (var x in Wait(0.15)) yield return x;
        var shots = _shotLog.Count;
        _wbPulse = MoveButtons.Fire; // inside the 0.5 s switch time: nothing happens
        foreach (var x in Wait(0.4)) yield return x;
        Check(_shotLog.Count == shots, "firing inside the AK switch time (0.5 s) does nothing");
        foreach (var x in WaitFor(() => Now - t0 > 0.7, 2, "switch time")) yield return x;
        var hitsBefore = _hitLog.Count;
        _wbPulse = MoveButtons.Fire;
        foreach (var x in WaitFor(() => _hitLog.Count > hitsBefore, 2, "hit event (body shot)")) yield return x;
        var h = _hitLog[^1].M;
        Check(h.Attacker == _myId && h.Victim == _wbBob && h.Damage == 30 && !h.Headshot && !h.Kill, "AK body shot: 30 damage, no headshot");
        foreach (var x in Wait(0.3)) yield return x;
        Check(BobHealth == 70, $"bob health 70 in the snapshot (got {BobHealth})");
        Check(Ammo!.Slots[1].Mag == 29, "AK magazine 29 after one shot");

        _wbPitch = AimPitch(c, c + new NVec3(0, 0, -6), 1.6f);
        foreach (var x in Wait(0.2)) yield return x;
        hitsBefore = _hitLog.Count;
        _wbPulse = MoveButtons.Fire;
        foreach (var x in WaitFor(() => _hitLog.Count > hitsBefore, 2, "hit event (headshot)")) yield return x;
        h = _hitLog[^1].M;
        Check(h.Damage == 60 && h.Headshot && !h.Kill, "AK headshot: 60 damage (x2)");
        foreach (var x in Wait(0.3)) yield return x;
        Check(BobHealth == 10, $"bob health 10 in the snapshot (got {BobHealth})");

        // --- wall: bob behind the wall (wall plane z = arena z - 3), the shot must stop at it
        var a2 = c + new NVec3(20, 0, 0); var b2 = c + new NVec3(20, 0, -6);
        foreach (var x in Place(a2, b2, 0.9f)) yield return x;
        hitsBefore = _hitLog.Count; shots = _shotLog.Count;
        _wbPulse = MoveButtons.Fire;
        foreach (var x in WaitFor(() => _shotLog.Count > shots, 2, "shot event behind the wall test")) yield return x;
        var sh = _shotLog[^1].M;
        Check(MathF.Abs(sh.End.Z - (c.Z - 2.75f)) < 0.3f && sh.Shooter == _myId, $"tracer ends at the wall (z {sh.End.Z - c.Z:F2}, wall face at -2.75)");
        foreach (var x in Wait(0.6)) yield return x;
        Check(_hitLog.Count == hitsBefore && BobHealth == 10, "the wall blocked the shot: no hit event, bob still at 10 health");

        // --- reload: AK mag 27 after three shots, reload takes 2.5 s, firing during it does nothing
        Check(Ammo!.Slots[1] == (27, 90), $"AK 27/90 after three shots (got {Ammo.Slots[1]})");
        var tr = Now;
        _wbPulse = MoveButtons.Reload;
        foreach (var x in Wait(1.0)) yield return x;
        shots = _shotLog.Count;
        _wbPulse = MoveButtons.Fire;
        foreach (var x in Wait(0.3)) yield return x;
        Check(_shotLog.Count == shots, "firing during a reload does nothing");
        foreach (var x in WaitFor(() => Ammo is { Slot: 2 } a && a.Slots[1].Mag == 30, 4, "reload to finish")) yield return x;
        var rt = Now - tr;
        Check(rt >= 2.4 && rt <= 2.9, $"AK reload took {rt:F2}s (2.5 expected)");
        Check(Ammo!.Slots[1] == (30, 87), $"reload moved 3 rounds: 30/87 (got {Ammo.Slots[1]})");

        // --- knife
        _wbSlot = 4;
        foreach (var x in Place(c, c + new NVec3(0, 0, -3.0f), 0.9f)) yield return x;
        foreach (var x in WaitFor(() => Ammo is { Slot: 4 }, 2, "ammo state for the knife")) yield return x;
        foreach (var x in Wait(0.4)) yield return x;
        hitsBefore = _hitLog.Count; shots = _shotLog.Count;
        _wbPulse = MoveButtons.Fire;
        foreach (var x in WaitFor(() => _shotLog.Count > shots, 2, "knife swing event")) yield return x;
        foreach (var x in Wait(0.4)) yield return x;
        Check(_hitLog.Count == hitsBefore, "knife swing at 3 m (reach 1.8 m) hits nothing");
        foreach (var x in Place(c, c + new NVec3(0, 0, -1.2f), 0.9f)) yield return x; // alice is behind bob (bob faces -Z)
        foreach (var x in Wait(0.6)) yield return x;
        hitsBefore = _hitLog.Count;
        _wbPulse = MoveButtons.Fire;
        foreach (var x in WaitFor(() => _hitLog.Count > hitsBefore, 2, "backstab hit event")) yield return x;
        h = _hitLog[^1].M;
        Check(h.Damage == 100 && h.Backstab && h.Kill && h.Attacker == _myId, "knife backstab from behind: 100 damage and a kill");
        foreach (var x in WaitFor(() => Ammo is { Slot: 4 } a && a.Slots[1].Reserve == 90, 2, "reserve refill after the kill")) yield return x;
        Check(Ammo!.Slots[0].Reserve == 60 && Ammo.Slots[1].Reserve == 90 && Ammo.Slots[2].Reserve == 4, "the kill refilled every reserve (AK 87 -> 90)");

        // --- second life: frontal knife, two hits kill
        foreach (var x in WaitFor(() => Match.Phase == MatchPhase.Running && BobState != EntityState.Dead, 8, "bob to respawn")) yield return x;
        foreach (var x in WaitBobAlive()) yield return x;
        var a3 = c + new NVec3(0, 0, -1.2f);
        foreach (var x in Place(a3, c, 0.9f)) yield return x; // alice stands in front of bob
        hitsBefore = _hitLog.Count;
        _wbPulse = MoveButtons.Fire;
        foreach (var x in WaitFor(() => _hitLog.Count > hitsBefore, 2, "frontal knife hit")) yield return x;
        h = _hitLog[^1].M;
        Check(h.Damage == 50 && !h.Backstab && !h.Kill, "knife from the front: 50 damage");
        foreach (var x in Wait(0.6)) yield return x;
        hitsBefore = _hitLog.Count;
        _wbPulse = MoveButtons.Fire;
        foreach (var x in WaitFor(() => _hitLog.Count > hitsBefore, 2, "second knife hit")) yield return x;
        h = _hitLog[^1].M;
        Check(h.Damage == 50 && h.Kill, "second knife hit kills (two hits)");
        foreach (var x in Wait(0.5)) yield return x;
        WeaponsResult(true, "");
    }
}
