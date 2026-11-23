using System.Numerics;
using Drycastle.ShooterCore.Bots;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Movement;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Bots;

/// <summary>Who a bot (or a turret, or the helicopter's helper) may shoot at: no teammates in team deathmatch, everyone else in free-for-all.</summary>
public class BotEnemiesTests
{
    static MatchSim Match(string mode, params ushort[] ids)
    {
        var m = new MatchSim(new MatchConfig { Mode = mode }, () => 0, new[] { Vector3.Zero }, new Random(1));
        foreach (var id in ids) m.AddPlayer(id, $"p{id}");
        return m;
    }

    static BotContact At(ushort id, float z) => new(id, new Vector3(0, 0, z), Stance.Stand, false);

    static BotEnemies Built(MatchSim m, params BotContact[] all)
    {
        var e = new BotEnemies();
        e.Rebuild(m, all);
        return e;
    }

    [Fact]
    public void InTeamDeathmatchATeammateIsNoEnemyAndTheOtherTeamIs()
    {
        var m = Match("tdm", 1, 2, 3); // 1 and 3 team A, 2 team B
        var e = Built(m, At(1, 0), At(2, -5), At(3, -10));
        Assert.Equal(new ushort[] { 2 }, e.For(m, 1).Select(c => c.Id));
        Assert.Equal(new ushort[] { 1, 3 }, e.For(m, 2).Select(c => c.Id));
        Assert.False(BotEnemies.IsEnemy(m, 1, 3));
        Assert.True(BotEnemies.IsEnemy(m, 1, 2));
        Assert.False(BotEnemies.IsEnemy(m, 1, 1));
    }

    [Fact]
    public void InFreeForAllEveryoneElseIsAnEnemy()
    {
        var m = Match("ffa", 1, 2, 3);
        var e = Built(m, At(1, 0), At(2, -5), At(3, -10));
        Assert.Equal(new ushort[] { 2, 3 }, e.For(m, 1).Select(c => c.Id).Where(id => id != 1));
        Assert.True(BotEnemies.IsEnemy(m, 1, 3));
        Assert.False(BotEnemies.IsEnemy(m, 1, 1));
    }

    [Fact]
    public void TheGoalIsTheNearestEnemyNotTheNearestTeammate()
    {
        var m = Match("tdm", 1, 2, 3); // 1 and 3 team A
        var e = Built(m, At(1, 0), At(3, -2), At(2, -30));
        Assert.Equal(new Vector3(0, 0, -30), e.Goal(m, 1));
        var ffa = Match("ffa", 1, 2, 3);
        Assert.Equal(new Vector3(0, 0, -2), Built(ffa, At(1, 0), At(3, -2), At(2, -30)).Goal(ffa, 1));
    }

    [Fact]
    public void ABotWithOnlyTeammatesLeftHasNoGoalAndFiresAtNobody()
    {
        var m = Match("tdm", 1, 2, 3, 4); // 1 and 3 team A, 2 and 4 team B
        var e = Built(m, At(1, 0), At(3, -10)); // the other team is dead
        Assert.Null(e.Goal(m, 1));
        Assert.Empty(e.For(m, 1));
    }

    [Fact]
    public void ABrainFedFromItNeverTargetsATeammate()
    {
        var m = Match("tdm", 1, 2, 3);
        var e = Built(m, At(1, 0), At(3, -10)); // a teammate dead ahead, no enemy
        var b = new BotBrain(BotTuning.Default, 7);
        b.Reset(0, 0);
        var w = new FedWorld(m, e);
        var fired = false;
        for (var t = 0.0; t < 3; t += 1f / 30f)
            fired |= b.Tick(t, 1f / 30f, new BotSelf(1, Vector3.Zero, Stance.Stand, 30, 30, 90, false, true), w).Buttons.HasFlag(MoveButtons.Fire);
        Assert.False(fired);
        Assert.Null(b.Target);
    }

    sealed class FedWorld : IBotWorld
    {
        readonly MatchSim _m; readonly BotEnemies _e;
        public FedWorld(MatchSim m, BotEnemies e) { _m = m; _e = e; }
        public IReadOnlyList<BotContact> Enemies(ushort self) => _e.For(_m, self);
        public bool Clear(Vector3 from, Vector3 to) => true;
        public Vector3? Goal(ushort self) => _e.Goal(_m, self);
    }
}
