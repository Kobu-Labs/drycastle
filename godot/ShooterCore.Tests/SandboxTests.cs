using System.Numerics;
using System.Text;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>Ticket #156: sandbox mode. Hello flag and negotiation, godmode, no score, unlimited ammo, streak grant.</summary>
public class SandboxTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly GameData Shipped = GameData.Load(DataDir);

    // ---- hello flag and negotiation ----

    [Fact]
    public void HelloCarriesTheSandboxFlagAndRoundTrips()
    {
        var bytes = Wire.Encode(new HelloMsg(Wire.ProtocolVersion, "alice", "tok", HelloFlags.Sandbox));
        var back = Assert.IsType<HelloMsg>(Wire.DecodeReliable(bytes));
        Assert.Equal("alice", back.Name);
        Assert.Equal("tok", back.Token);
        Assert.Equal(HelloFlags.Sandbox, back.Flags);
        Assert.Equal(Wire.ProtocolVersion, back.Version);
    }

    [Fact]
    public void HelloWithoutFlagsDecodesAsNoFlags()
    {
        var bytes = Wire.Encode(new HelloMsg(Wire.ProtocolVersion, "bob", "tok"));
        Assert.Equal(0, Assert.IsType<HelloMsg>(Wire.DecodeReliable(bytes)).Flags);
    }

    [Fact]
    public void HelloWithoutTheTrailingFlagByteDecodesAsNoFlags()
    {
        // the layout before v10: type, version, name, token; no flags byte
        var name = Encoding.UTF8.GetBytes("old");
        var token = Encoding.UTF8.GetBytes("t");
        var bytes = new List<byte> { Wire.THello, 9, (byte)name.Length };
        bytes.AddRange(name);
        bytes.Add((byte)token.Length);
        bytes.AddRange(token);
        var h = Assert.IsType<HelloMsg>(Wire.DecodeReliable(bytes.ToArray()));
        Assert.Equal(0, h.Flags);
        Assert.Equal("t", h.Token);
    }

    [Fact]
    public void NoAnswerWhenTheClientDidNotAsk()
    {
        Assert.Null(SandboxRules.Decide(requested: false, serverAllows: true));
        Assert.Null(SandboxRules.Decide(requested: false, serverAllows: false));
    }

    [Fact]
    public void GrantedOnlyWhenTheServerAllowsIt()
    {
        var granted = SandboxRules.Decide(requested: true, serverAllows: true);
        Assert.True(granted!.Value.Granted);
        Assert.Equal(SandboxRules.GrantedReason, granted.Value.Reason);

        var refused = SandboxRules.Decide(requested: true, serverAllows: false);
        Assert.False(refused!.Value.Granted);
        Assert.Equal(SandboxRules.RefusedReason, refused.Value.Reason);
    }

    [Fact]
    public void StatusAndGrantPayloadsRoundTrip()
    {
        var status = SandboxEvents.DecodeStatus(SandboxEvents.EncodeStatus(new SandboxAnswer(false, "no")));
        Assert.Equal(new SandboxAnswer(false, "no"), status);
        Assert.Equal("uav", SandboxEvents.DecodeGrant(SandboxEvents.EncodeGrant("uav")));
    }

    [Fact]
    public void TruncatedSandboxPayloadsAreRejected()
    {
        var status = SandboxEvents.EncodeStatus(new SandboxAnswer(true, "hello"));
        Assert.Null(SandboxEvents.DecodeStatus(status.AsSpan(0, status.Length - 1)));
        Assert.Null(SandboxEvents.DecodeStatus(ReadOnlySpan<byte>.Empty));
        var grant = SandboxEvents.EncodeGrant("attack_helicopter");
        Assert.Null(SandboxEvents.DecodeGrant(grant.AsSpan(0, 3)));
        Assert.Null(SandboxEvents.DecodeGrant(ReadOnlySpan<byte>.Empty));
    }

    // ---- godmode and scoring (MatchSim) ----

    sealed class Rig
    {
        public double T;
        public readonly MatchSim M;
        public Rig(MatchConfig? cfg = null) =>
            M = new MatchSim(cfg ?? new MatchConfig(), () => T, new[] { new Vector3(0, 0, 0), new Vector3(100, 0, 0), new Vector3(200, 0, 0) }, new Random(1));
        public void Advance(double s) { T += s; M.Update(); }
        /// <summary>Past the respawn delay and the spawn protection, so the player is alive and can take damage.</summary>
        public void Settle() { for (var i = 0; i < 8; i++) Advance(1); }
    }

    [Fact]
    public void SandboxPlayerTakesNoDamage()
    {
        var r = new Rig();
        r.M.AddPlayer(1, "sb", sandbox: true);
        r.M.AddPlayer(2, "enemy");
        r.Settle();
        var before = r.M.Get(1)!.Health;
        Assert.Equal(DamageResult.Ignored, r.M.ApplyDamage(1, 2, 0, 500));
        Assert.Equal(DamageResult.Ignored, r.M.ApplyDamage(1, 2, 0, 500, ignoreProtection: true)); // falls and drowning too
        Assert.Equal(DamageResult.Ignored, r.M.ApplyDamage(1, 1, 0, 500)); // self damage too
        Assert.Equal(PlayerLife.Alive, r.M.Get(1)!.Life);
        Assert.Equal(before, r.M.Get(1)!.Health);
    }

    [Fact]
    public void SandboxKillsAreShownButNotScored()
    {
        var r = new Rig(new MatchConfig { ScoreGoal = 2 });
        r.M.AddPlayer(1, "sb", sandbox: true);
        r.M.AddPlayer(2, "enemy");
        r.Settle();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(DamageResult.Killed, r.M.ApplyDamage(2, 1, 0, 500));
            r.Settle();
        }
        Assert.Equal(0, r.M.Get(1)!.Kills);
        Assert.Equal(MatchPhase.Running, r.M.Phase); // the score goal does not end the round
        Assert.Equal(3, r.M.Get(2)!.Deaths);
    }

    [Fact]
    public void NormalKillsStillScoreAndSandboxRanksLast()
    {
        var r = new Rig();
        r.M.AddPlayer(1, "sb", sandbox: true);
        r.M.AddPlayer(2, "a");
        r.M.AddPlayer(3, "b");
        r.Settle();
        Assert.Equal(DamageResult.Killed, r.M.ApplyDamage(3, 2, 0, 500));
        Assert.Equal(1, r.M.Get(2)!.Kills);
        Assert.Equal(new ushort[] { 2, 3, 1 }, r.M.Ranking().Select(p => p.Id).ToArray());
    }

    [Fact]
    public void SandboxPlayersNeverWinARound()
    {
        var r = new Rig(new MatchConfig { ScoreGoal = 1 });
        r.M.AddPlayer(1, "sb", sandbox: true);
        r.M.AddPlayer(2, "a");
        r.Settle();
        r.M.ApplyDamage(2, 1, 0, 500); // a sandbox kill: no round end
        Assert.Equal(MatchPhase.Running, r.M.Phase);
        Assert.DoesNotContain((ushort)1, r.M.Winners);
    }

    // ---- unlimited ammo (PlayerWeapons) ----

    [Fact]
    public void UnlimitedAmmoNeverRunsOutOrReloads()
    {
        var weapons = Shipped.Weapons;
        var ak = weapons.Find("ak47")!;
        var w = new PlayerWeapons(weapons) { Unlimited = true };
        var akSlot = weapons.Weapons.FindIndex(x => x.Id == "ak47") + 1;
        var mag = ak.MagazineSize;
        var fired = 0;
        for (var i = 0; i < ak.MagazineSize * 2 + 5; i++)
        {
            if (w.Update(i * 1.0, new WeaponInput(Fire: true, Reload: false, Slot: akSlot, Next: false)) is not null) fired++;
            w.Update(i * 1.0 + 0.5, new WeaponInput(Fire: false, Reload: true, Slot: akSlot, Next: false));
        }
        Assert.True(fired > ak.MagazineSize, $"fired {fired}");
        Assert.Equal(mag, w.Mag(akSlot));
        Assert.False(w.Reloading);
    }

    [Fact]
    public void WithoutSandboxTheMagazineRunsOut()
    {
        var weapons = Shipped.Weapons;
        var ak = weapons.Find("ak47")!;
        var w = new PlayerWeapons(weapons);
        var akSlot = weapons.Weapons.FindIndex(x => x.Id == "ak47") + 1;
        var fired = 0;
        for (var i = 0; i < ak.MagazineSize; i++)
            if (w.Update(i * 1.0, new WeaponInput(Fire: true, Reload: false, Slot: akSlot, Next: false)) is not null) fired++;
        Assert.Equal(ak.MagazineSize, fired);
        Assert.Equal(0, w.Mag(akSlot));
        // the next pull with an empty magazine starts a reload instead of firing
        Assert.Null(w.Update(ak.MagazineSize * 1.0, new WeaponInput(Fire: true, Reload: false, Slot: akSlot, Next: false)));
        Assert.True(w.Reloading);
    }

    // ---- sandbox killstreaks (KillstreakService) ----

    [Fact]
    public void SandboxGrantsAStreakWithoutKillsAndAgainAndAgain()
    {
        var sent = new List<(ushort Owner, EventKind Kind, byte[] Payload)>();
        var service = new KillstreakService(Shipped.Killstreaks,
            Shipped.Killstreaks.Streaks.Select(s => (IKillstreak)new LoggingKillstreak(s.Id)),
            (o, k, p) => sent.Add((o, k, p)), _ => { });
        service.Arm(7, new[] { "", "", "" }, 0);
        Assert.Equal(0, service.KillCount(7)); // no kills at all

        Assert.True(service.SandboxActivate(7, "uav", 0, Vector3.Zero));
        Assert.True(service.SandboxActivate(7, "uav", 1, Vector3.Zero)); // the running one is replaced, not refused
        Assert.True(service.SandboxActivate(7, "uav", 2, Vector3.Zero));
        Assert.Equal(1, service.ActiveCount(7, "uav"));
        Assert.Equal(3, sent.Count(s => s.Kind == EventKind.StreakUsed));
        Assert.Equal(2, sent.Count(s => s.Kind == EventKind.StreakEnded && s.Payload[^1] == (byte)KillstreakEndReason.Replaced));
    }

    [Fact]
    public void SandboxRefusesAnUnknownStreak()
    {
        var service = new KillstreakService(Shipped.Killstreaks,
            Shipped.Killstreaks.Streaks.Select(s => (IKillstreak)new LoggingKillstreak(s.Id)), (_, _, _) => { }, _ => { });
        service.Arm(7, new[] { "", "", "" }, 0);
        Assert.False(service.SandboxActivate(7, "no_such_streak", 0, Vector3.Zero));
    }
}
