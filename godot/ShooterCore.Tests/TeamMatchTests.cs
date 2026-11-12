using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

public class TeamMatchTests
{
    sealed class Rig
    {
        public double T;
        public readonly MatchSim M;
        public Rig(Action<MatchConfig>? tweak = null, Vector3[]? spawns = null)
        {
            var cfg = new MatchConfig { Mode = "tdm", TeamScoreGoal = 3 };
            tweak?.Invoke(cfg);
            spawns ??= new[] { new Vector3(0, 0, 0), new Vector3(100, 0, 0), new Vector3(200, 0, 0) };
            M = new MatchSim(cfg, () => T, spawns, new Random(1));
        }
        public void Pass() { T += 3.01; M.Update(); }
        public void Kill(ushort by, ushort victim) { M.ApplyDamage(victim, by, 0, 200); T += 3.01; M.Update(); }
    }

    [Fact]
    public void ModeDefaultsToFreeForAllAndRejectsUnknown()
    {
        Assert.Equal(MatchMode.FreeForAll, new MatchConfig().ParsedMode());
        Assert.Equal(MatchMode.TeamDeathmatch, new MatchConfig { Mode = "TDM" }.ParsedMode());
        Assert.Throws<DataFileException>(() => new MatchConfig { Mode = "ctf" }.ParsedMode());
        var json = DataFile.Parse<MatchConfig>("{\"version\":1,\"mode\":\"tdm\",\"friendly_fire\":true,\"team_score_goal\":9}", "match.json");
        Assert.Equal((MatchMode.TeamDeathmatch, true, 9), (json.ParsedMode(), json.FriendlyFire, json.TeamScoreGoal));
    }

    [Fact]
    public void FreeForAllHasNoTeams()
    {
        var m = new MatchSim(new MatchConfig(), () => 0, new[] { Vector3.Zero }, new Random(1));
        m.AddPlayer(1, "a"); m.AddPlayer(2, "b");
        Assert.All(m.Players, p => Assert.Equal(Teams.None, p.Team));
        Assert.False(m.AreFriends(1, 2));
    }

    [Fact]
    public void JoinersAreBalancedAcrossTeams()
    {
        var r = new Rig();
        for (ushort i = 1; i <= 5; i++) r.M.AddPlayer(i, $"p{i}");
        Assert.Equal(3, r.M.Players.Count(p => p.Team == Teams.A));
        Assert.Equal(2, r.M.Players.Count(p => p.Team == Teams.B));
        r.M.RemovePlayer(1); r.M.RemovePlayer(3); // both from team A
        r.M.AddPlayer(6, "p6");
        Assert.Equal(Teams.A, r.M.Get(6)!.Team); // A is now the smaller one
    }

    [Fact]
    public void SandboxPlayersHaveNoTeamAndDoNotCountForBalance()
    {
        var r = new Rig();
        r.M.AddPlayer(1, "sb", sandbox: true); r.M.AddPlayer(2, "a"); r.M.AddPlayer(3, "b");
        Assert.Equal(Teams.None, r.M.Get(1)!.Team);
        Assert.NotEqual(r.M.Get(2)!.Team, r.M.Get(3)!.Team);
    }

    [Fact]
    public void RoundStartEvensOutLopsidedTeams()
    {
        var r = new Rig(c => c.EndScreenTime = 1);
        for (ushort i = 1; i <= 4; i++) r.M.AddPlayer(i, $"p{i}"); // A: 1,3  B: 2,4
        r.M.RemovePlayer(2); r.M.RemovePlayer(4);                  // B is empty
        r.Pass();
        r.T += 601; r.M.Update();                                  // time is up
        Assert.Equal(MatchPhase.Ending, r.M.Phase);
        r.T += 1.01; r.M.Update();                                 // next round: 2 vs 0 becomes 1 vs 1
        Assert.Equal(1, r.M.Players.Count(p => p.Team == Teams.A));
        Assert.Equal(1, r.M.Players.Count(p => p.Team == Teams.B));
    }

    [Fact]
    public void FriendlyFireIsOffByDefaultButSelfDamageStays()
    {
        var r = new Rig();
        r.M.AddPlayer(1, "a"); r.M.AddPlayer(2, "b"); r.M.AddPlayer(3, "c"); // 1 and 3 are team A
        r.Pass();
        Assert.True(r.M.AreFriends(1, 3));
        Assert.Equal(DamageResult.Ignored, r.M.ApplyDamage(3, 1, 0, 200));
        Assert.Equal(PlayerLife.Alive, r.M.Get(3)!.Life);
        Assert.Equal(DamageResult.Killed, r.M.ApplyDamage(2, 1, 0, 200));                  // enemy
        Assert.Equal(DamageResult.Killed, r.M.ApplyDamage(1, 1, SpecialWeapons.Fall, 200)); // suicide
    }

    [Fact]
    public void FriendlyFireOnKillsButScoresNothing()
    {
        var r = new Rig(c => c.FriendlyFire = true);
        r.M.AddPlayer(1, "a"); r.M.AddPlayer(2, "b"); r.M.AddPlayer(3, "c");
        r.Pass();
        Assert.Equal(DamageResult.Killed, r.M.ApplyDamage(3, 1, 0, 200));
        Assert.Equal(0, r.M.Get(1)!.Kills);
        Assert.Equal(1, r.M.Get(3)!.Deaths);
        Assert.Equal(0, r.M.TeamScore(Teams.A));
    }

    [Fact]
    public void TeamScoreSumsKillsAndReachingTheGoalEndsTheRound()
    {
        var r = new Rig();
        for (ushort i = 1; i <= 4; i++) r.M.AddPlayer(i, $"p{i}"); // A: 1,3  B: 2,4
        r.Pass();
        r.Kill(1, 2); r.Kill(3, 4);
        Assert.Equal(2, r.M.TeamScore(Teams.A));
        Assert.Equal(MatchPhase.Running, r.M.Phase);
        r.Kill(3, 2);
        Assert.Equal(MatchPhase.Ending, r.M.Phase);
        Assert.Equal(Teams.A, r.M.WinningTeam);
        Assert.Equal(new ushort[] { 1, 3 }, r.M.Winners.OrderBy(i => i).ToArray());
    }

    [Fact]
    public void TimeLimitPicksTheHigherTeamOrNoneOnATie()
    {
        var r = new Rig(c => { c.TimeLimit = 60; c.TeamScoreGoal = 99; });
        r.M.AddPlayer(1, "a"); r.M.AddPlayer(2, "b");
        r.Pass();
        r.T += 61; r.M.Update();
        Assert.Equal(MatchPhase.Ending, r.M.Phase);
        Assert.Empty(r.M.Winners); // 0:0
        Assert.Equal(Teams.None, r.M.WinningTeam);

        var q = new Rig(c => { c.TimeLimit = 600; c.TeamScoreGoal = 99; });
        q.M.AddPlayer(1, "a"); q.M.AddPlayer(2, "b"); q.Pass();
        q.Kill(2, 1);
        q.T += 601; q.M.Update();
        Assert.Equal(Teams.B, q.M.WinningTeam);
        Assert.Equal(new ushort[] { 2 }, q.M.Winners.ToArray());
    }

    [Fact]
    public void RankingGroupsTheLeadingTeamFirst()
    {
        var r = new Rig(c => c.TeamScoreGoal = 99);
        for (ushort i = 1; i <= 4; i++) r.M.AddPlayer(i, $"p{i}"); // A: 1,3  B: 2,4
        r.Pass();
        r.Kill(2, 1); r.Kill(4, 3); r.Kill(4, 1);
        var order = r.M.Ranking().Select(p => p.Id).ToArray();
        Assert.Equal(new ushort[] { 4, 2, 3, 1 }, order); // team B (3 kills) first, best killer first; then A, fewer deaths first
    }

    [Fact]
    public void SpawnsAvoidEnemiesButMayShareWithTeammates()
    {
        var r = new Rig(c => c.MinSpawnDistance = 40, new[] { new Vector3(0, 0, 0), new Vector3(500, 0, 0) });
        r.M.AddPlayer(1, "a"); r.M.AddPlayer(2, "b"); r.Pass();
        r.M.SetPosition(1, new Vector3(0, 0, 0)); r.M.SetPosition(2, new Vector3(500, 0, 0)); // 1 is A, 2 is B
        r.M.AddPlayer(3, "c"); // team A: the only spawn that is not next to an enemy is the one its teammate stands on
        Assert.Equal(Teams.A, r.M.Get(3)!.Team);
        Assert.Equal(new Vector3(0, 0, 0), r.M.Get(3)!.Position);
    }

    [Fact]
    public void CodecCarriesTeamsAndTeamScores()
    {
        var m = new MatchStateMsg(MatchPhase.Running, 12.5f, new ushort[] { 1 },
            new[] { new ScoreRow(1, "a", 3, 1, 20, Teams.A), new ScoreRow(2, "b", 1, 3, 30, Teams.B) }, MatchMode.TeamDeathmatch, 3, 1);
        var d = MatchCodec.DecodeMatchState(MatchCodec.Encode(m))!;
        Assert.Equal(MatchMode.TeamDeathmatch, d.Mode);
        Assert.Equal((3, 1), (d.ScoreA, d.ScoreB));
        Assert.Equal(new[] { Teams.A, Teams.B }, d.Rows.Select(x => x.Team).ToArray());
        Assert.Equal("b", d.Rows[1].Name);
    }

    [Fact]
    public void OlderMatchStateWithoutTeamTailStillDecodes()
    {
        var full = MatchCodec.Encode(new MatchStateMsg(MatchPhase.Running, 1f, Array.Empty<ushort>(), new[] { new ScoreRow(1, "a", 2, 0, 5, Teams.A) }, MatchMode.TeamDeathmatch, 2, 0));
        var old = full[..^(1 + 2 + 2 + 1)]; // drop mode, both scores and the one team byte
        var d = MatchCodec.DecodeMatchState(old)!;
        Assert.Equal(MatchMode.FreeForAll, d.Mode);
        Assert.Equal(Teams.None, d.Rows[0].Team);
        Assert.Equal(2, d.Rows[0].Kills);
    }

    [Fact]
    public void ClientStateKnowsFriends()
    {
        var c = new ClientMatchState();
        var msg = new MatchStateMsg(MatchPhase.Running, 5f, Array.Empty<ushort>(),
            new[] { new ScoreRow(1, "a", 0, 0, 0, Teams.A), new ScoreRow(2, "b", 0, 0, 0, Teams.A), new ScoreRow(3, "c", 0, 0, 0, Teams.B) }, MatchMode.TeamDeathmatch, 0, 0);
        c.Apply(EventKind.MatchState, MatchCodec.Encode(msg), 0);
        Assert.True(c.AreFriends(1, 2));
        Assert.False(c.AreFriends(1, 3));
        Assert.False(c.AreFriends(1, 1));
        Assert.False(c.AreFriends(1, 99));
    }

    [Fact]
    public void UavDoesNotShowTeammates()
    {
        var alive = new[]
        {
            new UavContact(1, 0, 0, 1, Teams.A), new UavContact(2, 10, 0, 1, Teams.A),
            new UavContact(3, 20, 0, 1, Teams.B), new UavContact(4, 30, 0, 0, Teams.B), // 4 is hidden by Ghost
        };
        Assert.Equal(new[] { new Vector2(20, 0) }, UavRules.Visible(alive, 1).ToArray());
        // free-for-all contacts carry no team: everyone else shows
        Assert.Equal(2, UavRules.Visible(new[] { new UavContact(1, 0, 0, 1), new UavContact(2, 1, 0, 1), new UavContact(3, 2, 0, 1) }, 1).Count);
    }
}

public class TeamPaletteTests
{
    static double Lum(StatePalette.Rgb c)
    {
        static double Lin(double u) => u <= 0.04045 ? u / 12.92 : Math.Pow((u + 0.055) / 1.055, 2.4);
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }
    static double Contrast(StatePalette.Rgb a, StatePalette.Rgb b) { var (x, y) = (Lum(a), Lum(b)); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }

    // ShooterTheme.Surface, the HUD panel colour (the lightest thing the text sits on is darker than the world behind it)
    static readonly StatePalette.Rgb Panel = new(0.07f, 0.08f, 0.11f);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FriendAndEnemyReadOnThePanel(bool colourBlind)
    {
        Assert.True(Contrast(TeamPalette.Friend(colourBlind), Panel) >= 4.5);
        Assert.True(Contrast(TeamPalette.Enemy(colourBlind), Panel) >= 4.5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FriendAndEnemyDifferInHueAndLightness(bool colourBlind)
    {
        var f = TeamPalette.Friend(colourBlind); var e = TeamPalette.Enemy(colourBlind);
        Assert.True(f.B > f.R + 0.3f, "friend is blue-dominant");
        Assert.True(e.R > e.B + 0.3f, "enemy is orange-dominant");
        Assert.True(Contrast(f, e) >= 1.2, "also apart in lightness, for monochrome vision");
    }

    [Fact]
    public void HudLogicRelationsAndSort()
    {
        Assert.Equal(HudLogic.Relation.Friend, HudLogic.RelationOf(Teams.A, Teams.A));
        Assert.Equal(HudLogic.Relation.Enemy, HudLogic.RelationOf(Teams.A, Teams.B));
        Assert.Equal(HudLogic.Relation.Neutral, HudLogic.RelationOf(Teams.None, Teams.B));
        Assert.Equal((5, 2, Teams.B), HudLogic.TeamScores(Teams.B, 2, 5));
        var rows = new[] { new ScoreRow(1, "a", 1, 0, 0, Teams.A), new ScoreRow(2, "b", 4, 0, 0, Teams.B), new ScoreRow(3, "c", 0, 0, 0, Teams.A) };
        Assert.Equal(new ushort[] { 2, 1, 3 }, HudLogic.SortTeams(rows, 1, 4).Select(r => r.Id).ToArray());
    }
}
