using System.Numerics;
using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

public class MatchTests
{
    [Fact]
    public void TheClientSeesTheSpawnAndDeathThatJustArrivedNotTheLastPlayersOnes()
    {
        // three players spawn in join order, then the first dies and respawns: the newest event is player 1's each time
        var m = new ClientMatchState();
        for (ushort id = 1; id <= 3; id++) m.Apply(EventKind.Spawn, MatchCodec.Encode(new SpawnMsg(id, new Vector3(id, 0, 0), 3)), id);
        Assert.Equal((ushort)3, m.LatestSpawn!.Id);
        m.Apply(EventKind.Death, MatchCodec.Encode(new DeathMsg(3, 2, 3)), 10);
        m.Apply(EventKind.Death, MatchCodec.Encode(new DeathMsg(1, 2, 3)), 11);
        Assert.Equal((ushort)1, m.LatestDeath!.Victim);
        m.Apply(EventKind.Spawn, MatchCodec.Encode(new SpawnMsg(1, new Vector3(50, 0, 0), 3)), 14);
        Assert.Equal((ushort)1, m.LatestSpawn!.Id);
        Assert.Equal(new Vector3(50, 0, 0), m.LatestSpawn.Position);
        m.Apply(EventKind.Death, MatchCodec.Encode(new DeathMsg(1, 3, 3)), 20); // a second death of the same player
        Assert.Equal((ushort)3, m.LatestDeath!.Killer);
    }

    sealed class Rig
    {
        public double T;
        public readonly MatchSim M;
        public readonly List<MatchEvent> Ev = new();
        public Rig(MatchConfig? cfg = null, Vector3[]? spawns = null)
        {
            cfg ??= new MatchConfig();
            spawns ??= new[] { new Vector3(0, 0, 0), new Vector3(100, 0, 0), new Vector3(200, 0, 0) };
            M = new MatchSim(cfg, () => T, spawns, new Random(1));
            M.Event += Ev.Add;
        }
        public void Advance(double s) { T += s; M.Update(); }
        public void Pass() => Advance(3.01); // spawn protection (3 s) is over
        public int Count<TE>() where TE : MatchEvent => Ev.OfType<TE>().Count();
    }

    [Fact]
    public void FirstPlayerStartsRoundAndSpawnsProtected()
    {
        var r = new Rig();
        Assert.Equal(MatchPhase.Waiting, r.M.Phase);
        r.M.AddPlayer(1, "a");
        Assert.Equal(MatchPhase.Running, r.M.Phase);
        Assert.Equal(EntityState.Protected, r.M.StateOf(1));
        r.Advance(2.9);
        Assert.Equal(EntityState.Protected, r.M.StateOf(1));
        r.Advance(0.2);
        Assert.Equal(EntityState.Alive, r.M.StateOf(1));
    }

    [Fact]
    public void KillScoresAndRespawnsAfterDelay()
    {
        var r = new Rig();
        r.M.AddPlayer(1, "a"); r.M.AddPlayer(2, "b"); r.Pass();
        Assert.Equal(DamageResult.Damaged, r.M.ApplyDamage(2, 1, 0, 60));
        Assert.Equal(DamageResult.Killed, r.M.ApplyDamage(2, 1, 0, 60));
        Assert.Equal(1, r.M.Get(1)!.Kills); Assert.Equal(1, r.M.Get(2)!.Deaths);
        Assert.Contains(new PlayerKilled(1, 2, 0), r.Ev);
        Assert.Equal(EntityState.Dead, r.M.StateOf(2));
        Assert.Equal(DamageResult.Ignored, r.M.ApplyDamage(2, 1, 0, 60)); // already dead
        r.Advance(2.9);
        Assert.Equal(EntityState.Dead, r.M.StateOf(2));
        r.Advance(0.2);
        Assert.Equal(EntityState.Protected, r.M.StateOf(2));
        Assert.Equal(100, r.M.Get(2)!.Health);
    }

    [Fact]
    public void SpawnProtectionBlocksDamageUntilFireOrTimeout()
    {
        var r = new Rig();
        r.M.AddPlayer(1, "a"); r.M.AddPlayer(2, "b");
        Assert.Equal(DamageResult.Ignored, r.M.ApplyDamage(2, 1, 0, 500));
        Assert.Equal(0, r.M.Get(1)!.Kills);
        Assert.True(r.M.NotifyFired(2));
        Assert.False(r.M.NotifyFired(2));
        Assert.Equal(EntityState.Alive, r.M.StateOf(2));
        Assert.Equal(DamageResult.Killed, r.M.ApplyDamage(2, 1, 0, 500));
    }

    [Fact]
    public void SuicideScoresZeroButCountsDeath()
    {
        var r = new Rig();
        r.M.AddPlayer(1, "a"); r.Pass();
        Assert.Equal(DamageResult.Killed, r.M.ApplyDamage(1, 1, SpecialWeapons.Fall, 500));
        Assert.Equal(0, r.M.Get(1)!.Kills); Assert.Equal(1, r.M.Get(1)!.Deaths);
        Assert.Contains(new PlayerKilled(1, 1, SpecialWeapons.Fall), r.Ev);
    }

    [Fact]
    public void VoidKillIgnoresProtection()
    {
        var r = new Rig();
        r.M.AddPlayer(1, "a");
        Assert.Equal(DamageResult.Ignored, r.M.ApplyDamage(1, 1, SpecialWeapons.Void, 999));
        Assert.Equal(DamageResult.Killed, r.M.ApplyDamage(1, 1, SpecialWeapons.Void, 999, ignoreProtection: true));
    }

    [Fact]
    public void KillGoalEndsRoundThenRestartsWithScoresReset()
    {
        var r = new Rig(new MatchConfig { ScoreGoal = 2 });
        r.M.AddPlayer(1, "a"); r.M.AddPlayer(2, "b"); r.Pass();
        r.M.ApplyDamage(2, 1, 0, 500);
        Assert.Equal(MatchPhase.Running, r.M.Phase);
        r.Advance(3.1);
        r.M.NotifyFired(2); r.Pass();
        r.M.ApplyDamage(2, 1, 0, 500);
        Assert.Equal(MatchPhase.Ending, r.M.Phase);
        Assert.Equal(new ushort[] { 1 }, r.M.Winners);
        Assert.Equal(DamageResult.Ignored, r.M.ApplyDamage(1, 2, 0, 500)); // frozen
        r.Advance(9.9);
        Assert.Equal(MatchPhase.Ending, r.M.Phase);
        r.Advance(0.2);
        Assert.Equal(MatchPhase.Running, r.M.Phase);
        Assert.All(r.M.Players, p => { Assert.Equal(0, p.Kills); Assert.Equal(0, p.Deaths); Assert.Equal(PlayerLife.Alive, p.Life); });
        Assert.Equal(EntityState.Protected, r.M.StateOf(1));
    }

    [Fact]
    public void TimeLimitWinnerHasMostKillsThenFewestDeaths()
    {
        var r = new Rig();
        r.M.AddPlayer(1, "a"); r.M.AddPlayer(2, "b"); r.M.AddPlayer(3, "c"); r.Pass();
        r.M.ApplyDamage(2, 1, 0, 500); // a 1/0, b 0/1
        r.M.ApplyDamage(1, 3, 0, 500); // c 1/0, a 1/1
        r.Advance(599);
        Assert.Equal(MatchPhase.Ending, r.M.Phase);
        Assert.Equal(new ushort[] { 3 }, r.M.Winners);
    }

    [Fact]
    public void TieOnKillsAndDeathsSharesTheWin()
    {
        var r = new Rig();
        r.M.AddPlayer(1, "a"); r.M.AddPlayer(2, "b"); r.Pass();
        r.M.ApplyDamage(2, 1, 0, 500); r.M.ApplyDamage(1, 2, 0, 500);
        r.Advance(600);
        Assert.Equal(MatchPhase.Ending, r.M.Phase);
        Assert.Equal(new ushort[] { 1, 2 }, r.M.Winners);
    }

    [Fact]
    public void NobodyWinsATimeLimitRoundWithoutAKill()
    {
        var r = new Rig();
        r.M.AddPlayer(1, "a"); r.M.AddPlayer(2, "b"); r.Pass();
        r.Advance(600);
        Assert.Equal(MatchPhase.Ending, r.M.Phase);
        Assert.Empty(r.M.Winners);
    }

    [Fact]
    public void JoinInProgressSpawnsWithZeroScoreAndLeaveRemoves()
    {
        var r = new Rig();
        r.M.AddPlayer(1, "a"); r.Pass();
        r.M.AddPlayer(2, "b"); // killer later
        Assert.Equal(PlayerLife.Alive, r.M.Get(2)!.Life);
        Assert.Equal(0, r.M.Get(2)!.Kills);
        Assert.Equal(EntityState.Protected, r.M.StateOf(2));
        r.M.RemovePlayer(1);
        Assert.Null(r.M.Get(1));
        Assert.Equal(MatchPhase.Running, r.M.Phase);
        r.M.RemovePlayer(2);
        Assert.Equal(MatchPhase.Waiting, r.M.Phase);
    }

    [Fact]
    public void JoinDuringEndScreenWaitsForRestart()
    {
        var r = new Rig(new MatchConfig { TimeLimit = 5 });
        r.M.AddPlayer(1, "a"); r.Advance(5);
        Assert.Equal(MatchPhase.Ending, r.M.Phase);
        r.M.AddPlayer(2, "b");
        Assert.Equal(PlayerLife.Unspawned, r.M.Get(2)!.Life);
        r.Advance(10);
        Assert.Equal(PlayerLife.Alive, r.M.Get(2)!.Life);
    }

    [Fact]
    public void SpawnAvoidsLivingPlayersWithinMinDistance()
    {
        var spawns = new[] { new Vector3(0, 0, 0), new Vector3(30, 0, 0), new Vector3(100, 0, 0) };
        var rng = new Random(5);
        for (var i = 0; i < 20; i++)
            Assert.Equal(new Vector3(100, 0, 0), SpawnChooser.Choose(spawns, new[] { new Vector3(0, 0, 0) }, 40, rng));
    }

    [Fact]
    public void SpawnFallsBackToFarthestCandidate()
    {
        var spawns = new[] { new Vector3(0, 0, 0), new Vector3(30, 0, 0), new Vector3(10, 0, 0) };
        var living = new[] { new Vector3(0, 0, 0) };
        Assert.Equal(new Vector3(30, 0, 0), SpawnChooser.Choose(spawns, living, 40, new Random(1)));
        Assert.Equal(Vector3.Zero, SpawnChooser.Choose(Array.Empty<Vector3>(), living, 40, new Random(1)));
    }

    [Fact]
    public void CodecsRoundTrip()
    {
        var m = new MatchStateMsg(MatchPhase.Ending, 7.5f, new ushort[] { 2 }, new[] { new ScoreRow(2, "Anička", 25, 3, 40), new ScoreRow(5, "b", 0, 9, 0) });
        var d = MatchCodec.DecodeMatchState(MatchCodec.Encode(m))!;
        Assert.Equal(m.Phase, d.Phase); Assert.Equal(7.5f, d.TimeRemaining);
        Assert.Equal(m.Winners, d.Winners); Assert.Equal(m.Rows, d.Rows);
        Assert.Equal(new KillMsg(1, 2, 3), MatchCodec.DecodeKill(MatchCodec.Encode(new KillMsg(1, 2, 3))));
        Assert.Equal(new SpawnMsg(4, new Vector3(1, 2, 3), 3), MatchCodec.DecodeSpawn(MatchCodec.Encode(new SpawnMsg(4, new Vector3(1, 2, 3), 3))));
        Assert.Equal(new DeathMsg(4, 5, 3), MatchCodec.DecodeDeath(MatchCodec.Encode(new DeathMsg(4, 5, 3))));
        Assert.Equal((ushort)9, MatchCodec.DecodeDebugKill(MatchCodec.EncodeDebugKill(9)));
        Assert.Null(MatchCodec.DecodeMatchState(new byte[] { 1, 0 }));
    }

    [Fact]
    public void ClientStoreTracksPhaseFeedAndRestarts()
    {
        var c = new ClientMatchState();
        byte[] St(MatchPhase p, int k) => MatchCodec.Encode(new MatchStateMsg(p, 10, p == MatchPhase.Ending ? new ushort[] { 1 } : Array.Empty<ushort>(), new[] { new ScoreRow(1, "a", k, 0, 12) }));
        c.Apply(EventKind.MatchState, St(MatchPhase.Running, 0), 0);
        c.Apply(EventKind.Kill, MatchCodec.Encode(new KillMsg(1, 2, 0)), 1);
        Assert.Single(c.KillFeed);
        c.Apply(EventKind.MatchState, St(MatchPhase.Ending, 25), 2);
        Assert.Equal(1, c.RoundsEnded);
        Assert.Contains("winner(s): a", c.ScoreboardText(2));
        c.Apply(EventKind.MatchState, St(MatchPhase.Running, 0), 12);
        Assert.Equal(1, c.Restarts); Assert.Empty(c.KillFeed);
        Assert.Equal(7f, c.TimeRemaining(15), 3);
    }
}
