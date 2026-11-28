using System.Numerics;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Match;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Match;

/// <summary>Open world: free-for-all with no round end, squads open to everyone, of any size, whose members cannot hurt each other.</summary>
public class OpenWorldTests
{
    sealed class Rig
    {
        public double T;
        public readonly MatchSim M;
        public Rig(int players = 6, Action<MatchConfig>? tweak = null)
        {
            var cfg = new MatchConfig { Mode = "ow", ScoreGoal = 2, TimeLimit = 60 };
            tweak?.Invoke(cfg);
            M = new MatchSim(cfg, () => T, new[] { Vector3.Zero, new Vector3(100, 0, 0), new Vector3(200, 0, 0) }, new Random(1));
            for (ushort i = 1; i <= players; i++) M.AddPlayer(i, $"p{i}");
        }
        public void Pass(double s = 3.01) { T += s; M.Update(); }
    }

    [Fact]
    public void ParsesTheMode()
    {
        Assert.Equal(MatchMode.OpenWorld, new MatchConfig { Mode = "ow" }.ParsedMode());
        Assert.Equal(MatchMode.OpenWorld, new MatchConfig { Mode = "OpenWorld" }.ParsedMode());
    }

    [Fact]
    public void NoTimeLimitAndNoScoreGoal()
    {
        var r = new Rig(2);
        Assert.Equal(MatchPhase.Running, r.M.Phase);
        r.Pass();
        for (var i = 0; i < 5; i++) { r.M.ApplyDamage(2, 1, 0, 500); r.Pass(); r.Pass(); } // respawn, then spawn protection
        r.Pass(10_000);
        Assert.Equal((MatchPhase.Running, 5, 0f), (r.M.Phase, r.M.Get(1)!.Kills, r.M.TimeRemaining));
    }

    [Fact]
    public void NobodyHasATeamAndSquadsHaveNoSizeLimit()
    {
        var r = new Rig(8);
        Assert.All(r.M.Players, p => Assert.Equal(Teams.None, p.Team));
        Assert.Equal(SquadResult.Ok, r.M.CreateSquad(1, "Foxes", out var s));
        for (ushort i = 2; i <= 8; i++) Assert.Equal(SquadResult.Ok, r.M.JoinSquad(i, s));
        Assert.Equal(8, r.M.SquadMembers(s).Count);
        Assert.Equal(new[] { s }, r.M.SquadsOf(Teams.None));
    }

    [Fact]
    public void SquadMatesCannotHurtEachOtherOthersCan()
    {
        var r = new Rig(3);
        r.Pass();
        r.M.CreateSquad(1, null, out var s);
        r.M.JoinSquad(2, s);
        Assert.True(r.M.AreFriends(1, 2));
        Assert.False(r.M.AreFriends(1, 3));
        Assert.Equal(DamageResult.Ignored, r.M.ApplyDamage(2, 1, 0, 50));
        Assert.Equal(DamageResult.Damaged, r.M.ApplyDamage(3, 1, 0, 50));
        Assert.True(r.M.VehicleHitBlocked(3, new ushort[] { 0, 0 }) == false && r.M.VehicleHitBlocked(1, new ushort[] { 2 }));
    }

    [Fact]
    public void TheFriendlyFireSwitchDoesNotApply()
    {
        var r = new Rig(2, c => c.FriendlyFire = true);
        r.Pass();
        r.M.CreateSquad(1, null, out var s);
        r.M.JoinSquad(2, s);
        Assert.Equal(DamageResult.Ignored, r.M.ApplyDamage(2, 1, 0, 50));
        Assert.True(r.M.VehicleHitBlocked(1, new ushort[] { 2 }));
    }

    [Fact]
    public void SandboxPlayersAndBotsStayOutOfSquads()
    {
        var r = new Rig(1);
        r.M.AddPlayer(2, "sb", sandbox: true);
        r.M.AddPlayer(3, "bot", bot: true);
        Assert.Equal(SquadResult.Sandbox, r.M.CreateSquad(2, null, out _));
        Assert.Equal(SquadResult.Bot, r.M.CreateSquad(3, null, out _));
    }

    [Fact]
    public void TheModeTravelsOnTheWire()
    {
        var bytes = MatchCodec.Encode(new MatchStateMsg(MatchPhase.Running, 0, Array.Empty<ushort>(),
            new[] { new ScoreRow(1, "a", 0, 0, 0, Squad: 4), new ScoreRow(2, "b", 0, 0, 0, Squad: 4), new ScoreRow(3, "c", 0, 0, 0) }, MatchMode.OpenWorld, 0, 0, new[] { new SquadName(4, "Foxes", 1) }));
        var c = new ClientMatchState();
        c.Apply(Drycastle.ShooterCore.Net.EventKind.MatchState, bytes, 0);
        Assert.Equal(MatchMode.OpenWorld, c.Mode);
        Assert.True(c.AreFriends(1, 2) && c.AreSquadMates(1, 2));
        Assert.False(c.AreFriends(1, 3));
    }
}
