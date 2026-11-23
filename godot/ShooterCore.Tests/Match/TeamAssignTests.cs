using System.Numerics;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Match;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Match;

/// <summary>The Teams screen's wire and rules (#312).</summary>
public class TeamAssignTests
{
    static MatchSim Match(string mode = "tdm", int players = 4, int bots = 0)
    {
        double t = 0;
        var m = new MatchSim(new MatchConfig { Mode = mode }, () => t, new[] { Vector3.Zero, new Vector3(100, 0, 0) }, new Random(1));
        for (ushort i = 1; i <= players; i++) m.AddPlayer(i, $"p{i}");
        for (ushort i = 1; i <= bots; i++) m.AddPlayer((ushort)(100 + i), $"bot{i}", bot: true);
        return m;
    }

    [Theory]
    [InlineData(TeamAction.Move, 7, Teams.B)]
    [InlineData(TeamAction.Move, 65535, Teams.A)]
    [InlineData(TeamAction.Move, 300, Teams.None)]
    [InlineData(TeamAction.MoveBots, 0, Teams.A)]
    [InlineData(TeamAction.Shuffle, 0, Teams.None)]
    [InlineData(TeamAction.Balance, 0, Teams.None)]
    public void CodecRoundTrips(TeamAction action, int id, byte team)
    {
        var a = new TeamAssign(action, (ushort)id, team);
        Assert.Equal(a, TeamAssignCodec.Decode(TeamAssignCodec.Encode(a)));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 1, 0, 0 })] // short
    [InlineData(new byte[] { 0, 0, 0, 0 })] // no such action
    [InlineData(new byte[] { 5, 0, 0, 0 })]
    [InlineData(new byte[] { 1, 0, 0, 3 })] // no such team
    public void CodecRejectsGarbage(byte[] d) => Assert.Null(TeamAssignCodec.Decode(d));

    [Fact]
    public void ScoreboardRowsCarryTeamSandboxAndBot()
    {
        var rows = new[]
        {
            new ScoreRow(1, "a", 1, 2, 3, Teams.A, Sandbox: true),
            new ScoreRow(2, "b", 0, 0, 5, Teams.B, Bot: true),
            new ScoreRow(3, "c", 0, 0, 5, Teams.None),
            new ScoreRow(4, "d", 0, 0, 5, Teams.B, Sandbox: true, Bot: true),
        };
        var back = MatchCodec.DecodeMatchState(MatchCodec.Encode(new MatchStateMsg(MatchPhase.Running, 9, Array.Empty<ushort>(), rows, MatchMode.TeamDeathmatch, 1, 2)))!;
        Assert.Equal(rows.Select(r => (r.Team, r.Sandbox, r.Bot)), back.Rows.Select(r => (r.Team, r.Sandbox, r.Bot)));
    }

    [Fact]
    public void RulesRefuseOutsideTeamDeathmatch()
    {
        var ffa = Match("ffa", 2);
        Assert.Null(TeamAssignRules.Apply(ffa, new TeamAssign(TeamAction.Move, 1, Teams.A), "admin"));
        Assert.Null(TeamAssignRules.Apply(ffa, new TeamAssign(TeamAction.Shuffle), "admin"));
    }

    [Fact]
    public void MoveAnnouncesAndMoves()
    {
        var m = Match();
        Assert.Equal("Kobu moved p1 to team B.", TeamAssignRules.Apply(m, new TeamAssign(TeamAction.Move, 1, Teams.B), "Kobu"));
        Assert.Equal(Teams.B, m.Get(1)!.Team);
        Assert.Equal("Kobu took p1 out of the round.", TeamAssignRules.Apply(m, new TeamAssign(TeamAction.Move, 1, Teams.None), "Kobu"));
        Assert.Equal(Teams.None, m.Get(1)!.Team);
        Assert.Null(TeamAssignRules.Apply(m, new TeamAssign(TeamAction.Move, 99, Teams.B), "Kobu")); // unknown player
    }

    [Fact]
    public void MoveBotsSendsEveryBotButNoHuman()
    {
        var m = Match(players: 2, bots: 3);
        var humans = m.Players.Where(p => !p.Bot).ToDictionary(p => p.Id, p => p.Team);
        Assert.NotNull(TeamAssignRules.Apply(m, new TeamAssign(TeamAction.MoveBots, 0, Teams.B), "k"));
        Assert.All(m.Players.Where(p => p.Bot), p => Assert.Equal(Teams.B, p.Team));
        Assert.All(m.Players.Where(p => !p.Bot), p => Assert.Equal(humans[p.Id], p.Team));
    }

    [Fact]
    public void MoveBotsWithNoBotsIsHarmless()
    {
        var m = Match();
        Assert.Equal(0, m.MoveBots(Teams.A));
        Assert.Null(m.MoveBots(Teams.None));
    }

    [Fact]
    public void BalanceReportsWhoMoved()
    {
        var m = Match(players: 0, bots: 4);
        m.MoveBots(Teams.A); // all four on A
        var line = TeamAssignRules.Apply(m, new TeamAssign(TeamAction.Balance), "k")!;
        Assert.StartsWith("k balanced the teams:", line);
        Assert.Equal(2, m.Players.Count(p => p.Team == Teams.B));
        Assert.Equal("k balanced the teams: nobody had to move.", TeamAssignRules.Apply(m, new TeamAssign(TeamAction.Balance), "k"));
    }
}
