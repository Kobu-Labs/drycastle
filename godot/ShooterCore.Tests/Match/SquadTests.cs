using System.Numerics;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Match;

/// <summary>Squads (#379): the rules in the match, the wire, the chat command and the scoreboard grouping.</summary>
public class SquadTests
{
    /// <summary>Team deathmatch with players 1..n (names p1..pn) all on team A except those listed for B.</summary>
    static MatchSim Match(int players = 6, string mode = "tdm", params ushort[] teamB)
    {
        double t = 0;
        var m = new MatchSim(new MatchConfig { Mode = mode }, () => t, new[] { Vector3.Zero, new Vector3(100, 0, 0) }, new Random(1));
        for (ushort i = 1; i <= players; i++) m.AddPlayer(i, $"p{i}");
        if (m.IsTeamMode) for (ushort i = 1; i <= players; i++) m.SetTeam(i, teamB.Contains(i) ? Teams.B : Teams.A);
        return m;
    }

    static byte Make(MatchSim m, ushort id, string? name = null)
    {
        Assert.Equal(SquadResult.Ok, m.CreateSquad(id, name, out var s));
        return s;
    }

    [Fact]
    public void CreateTagsTheCreatorAndNamesTheSquad()
    {
        var m = Match();
        var s = Make(m, 1, "  Foxes   den ");
        Assert.NotEqual(0, s);
        Assert.Equal("Foxes den", m.SquadNameOf(s));
        Assert.Equal(s, m.Get(1)!.Squad);
        Assert.Equal(new ushort[] { 1 }, m.SquadMembers(s).Select(p => p.Id).ToArray());
        Assert.Equal(s, m.SquadList.Single().Id);
    }

    [Fact]
    public void CreateWithoutANameGetsTheDefaultAndLongNamesAreCut()
    {
        var m = Match();
        Assert.Equal("p1's squad", m.SquadNameOf(Make(m, 1, "   ")));
        Assert.Equal("0123456789abcdef", m.SquadNameOf(Make(m, 2, "0123456789abcdefGHIJ")));
        Assert.Equal(16, Squads.MaxNameLength);
    }

    [Fact]
    public void NamesAreUniqueWithinATeamButNotAcrossTeams()
    {
        var m = Match(4, "tdm", 3, 4);
        Make(m, 1, "Foxes");
        Assert.Equal(SquadResult.NameTaken, m.CreateSquad(2, "FOXES", out var none));
        Assert.Equal(0, none);
        Assert.Equal(0, m.Get(2)!.Squad);
        Assert.Equal(SquadResult.Ok, m.CreateSquad(3, "Foxes", out _)); // the other team
    }

    [Fact]
    public void JoinFillsUpToFourThenRefuses()
    {
        var m = Match();
        var s = Make(m, 1);
        for (ushort i = 2; i <= 4; i++) Assert.Equal(SquadResult.Ok, m.JoinSquad(i, s));
        Assert.Equal(Squads.MaxMembers, m.SquadMembers(s).Count);
        Assert.Equal(SquadResult.Full, m.JoinSquad(5, s));
        Assert.Equal(0, m.Get(5)!.Squad);
        Assert.Equal(SquadResult.AlreadyThere, m.JoinSquad(4, s));
    }

    [Fact]
    public void AFullSquadTakesAMemberAgainWhenOneLeaves()
    {
        var m = Match();
        var s = Make(m, 1);
        for (ushort i = 2; i <= 4; i++) m.JoinSquad(i, s);
        Assert.Equal(SquadResult.Ok, m.LeaveSquad(4));
        Assert.Equal(SquadResult.Ok, m.JoinSquad(5, s));
    }

    [Fact]
    public void OnlyTheOwnTeamsSquadsCanBeSeenAndJoined()
    {
        var m = Match(4, "tdm", 3, 4);
        var a = Make(m, 1, "Alpha");
        var b = Make(m, 3, "Bravo");
        Assert.Equal(new[] { a }, m.SquadsOf(Teams.A));
        Assert.Equal(new[] { b }, m.SquadsOf(Teams.B));
        Assert.Equal(SquadResult.WrongTeam, m.JoinSquad(2, b));
        Assert.Equal(SquadResult.UnknownSquad, m.JoinSquad(2, 99));
        Assert.Equal(0, m.Get(2)!.Squad);
    }

    [Fact]
    public void OneSquadPerPlayerAndAnEmptySquadDisappears()
    {
        var m = Match();
        var a = Make(m, 1, "Alpha");
        var b = Make(m, 2, "Bravo");
        Assert.Equal(SquadResult.Ok, m.JoinSquad(1, b)); // leaves Alpha, which had nobody else
        Assert.Equal(b, m.Get(1)!.Squad);
        Assert.Equal("", m.SquadNameOf(a));
        Assert.Equal(new[] { b }, m.SquadList.Select(q => q.Id).ToArray());
        var c = Make(m, 1, "Charlie"); // creating leaves Bravo, which keeps player 2
        Assert.Equal(c, m.Get(1)!.Squad);
        Assert.Equal(new[] { 2 }, m.SquadMembers(b).Select(p => (int)p.Id).ToArray());
    }

    [Fact]
    public void LeaveTakesThePlayerOutAndDisbandsTheLastOne()
    {
        var m = Match();
        var s = Make(m, 1);
        Assert.Equal(SquadResult.NotInSquad, m.LeaveSquad(2));
        Assert.Equal(SquadResult.Ok, m.LeaveSquad(1));
        Assert.Equal(0, m.Get(1)!.Squad);
        Assert.Empty(m.SquadList);
        Assert.Equal("", m.SquadNameOf(s));
    }

    [Fact]
    public void ATeamChangeLeavesTheSquad()
    {
        var m = Match();
        var s = Make(m, 1);
        m.JoinSquad(2, s);
        m.SetTeam(2, Teams.B);
        Assert.Equal(0, m.Get(2)!.Squad);
        Assert.Equal(new ushort[] { 1 }, m.SquadMembers(s).Select(p => p.Id).ToArray());
        m.SetTeam(1, Teams.None); // taken out of the round: the squad, now empty, is gone
        Assert.Empty(m.SquadList);
        Assert.False(m.AreSquadMates(1, 2));
    }

    [Fact]
    public void BalanceAndShuffleDropTheMovedPlayersSquads()
    {
        var m = Match(6); // everyone on A
        var s = Make(m, 1);
        foreach (ushort i in new ushort[] { 2, 3, 4 }) m.JoinSquad(i, s);
        m.ShuffleTeams();
        var rest = m.Players.Where(p => p.Squad == s).ToList();
        Assert.True(rest.Count < 4); // the shuffle dealt some of the four to the other team: they left the squad
        Assert.True(rest.Select(p => p.Team).Distinct().Count() <= 1, "what is left of a squad is one team");
    }

    [Fact]
    public void AModeChangeClearsEverySquad()
    {
        var m = Match();
        var s = Make(m, 1);
        m.JoinSquad(2, s);
        m.Restart(MatchMode.FreeForAll);
        Assert.Empty(m.SquadList);
        Assert.All(m.Players, p => Assert.Equal(0, p.Squad));
        m.Restart(MatchMode.TeamDeathmatch);
        Assert.Empty(m.SquadList);
    }

    [Fact]
    public void ARestartInTheSameModeKeepsThem()
    {
        var m = Match();
        var s = Make(m, 1);
        m.Restart();
        Assert.Equal(s, m.Get(1)!.Squad);
    }

    [Fact]
    public void LeavingTheMatchLeavesTheSquad()
    {
        var m = Match();
        var s = Make(m, 1);
        m.JoinSquad(2, s);
        m.RemovePlayer(1);
        Assert.Equal(new ushort[] { 2 }, m.SquadMembers(s).Select(p => p.Id).ToArray());
        m.RemovePlayer(2);
        Assert.Empty(m.SquadList);
    }

    [Fact]
    public void BotsNeverJoin()
    {
        var m = Match(2);
        m.AddPlayer(100, "bot", bot: true);
        var s = Make(m, 1);
        Assert.Equal(SquadResult.Bot, m.JoinSquad(100, s));
        Assert.Equal(SquadResult.Bot, m.CreateSquad(100, "Bots", out _));
        Assert.Equal(0, m.Get(100)!.Squad);
        Assert.Single(m.SquadList);
    }

    [Fact]
    public void FreeForAllAndTeamlessPlayersCannotHaveSquads()
    {
        var ffa = Match(2, "ffa");
        Assert.Equal(SquadResult.NotTeamMode, ffa.CreateSquad(1, null, out _));
        var m = Match(2);
        m.SetTeam(2, Teams.None);
        Assert.Equal(SquadResult.NoTeam, m.CreateSquad(2, null, out _));
        Assert.Equal(SquadResult.UnknownPlayer, m.CreateSquad(77, null, out _));
        m.AddPlayer(3, "sb", sandbox: true);
        Assert.Equal(SquadResult.NoTeam, m.CreateSquad(3, null, out _));
    }

    [Fact]
    public void SquadsDoNotChangeFriendsOrTeams()
    {
        var m = Match(4, "tdm", 3, 4);
        var s = Make(m, 1);
        m.JoinSquad(2, s);
        Assert.True(m.AreSquadMates(1, 2));
        Assert.False(m.AreSquadMates(1, 1));
        Assert.True(m.AreFriends(1, 2));
        Assert.Equal(Teams.A, m.Get(2)!.Team);
        Assert.False(m.AreSquadMates(3, 4));
    }

    [Fact]
    public void AnyChangeAnnouncesAScoreboard()
    {
        var m = Match();
        var n = 0;
        m.Event += e => { if (e is ScoreboardChanged) n++; };
        var s = Make(m, 1);
        m.JoinSquad(2, s);
        m.LeaveSquad(2);
        Assert.Equal(3, n);
        m.JoinSquad(2, 99); // a refusal says nothing
        Assert.Equal(3, n);
    }

    [Fact]
    public void ManyCreatesNeverReuseALiveId()
    {
        var m = Match(2);
        var live = Make(m, 1, "Keep");
        for (var i = 0; i < 600; i++)
        {
            Assert.Equal(SquadResult.Ok, m.CreateSquad(2, $"s{i}", out var s));
            Assert.NotEqual(live, s);
            Assert.NotEqual(0, s);
        }
        Assert.Equal("Keep", m.SquadNameOf(live));
    }

    // ---- wire ----

    [Fact]
    public void ScoreboardCarriesSquadsAndNames()
    {
        var rows = new[]
        {
            new ScoreRow(1, "a", 1, 2, 3, Teams.A, Squad: 7),
            new ScoreRow(2, "b", 0, 0, 5, Teams.B, Bot: true),
            new ScoreRow(3, "c", 0, 0, 5, Teams.A, Squad: 7),
            new ScoreRow(4, "d", 0, 0, 5, Teams.A, Squad: 9),
        };
        var squads = new[] { new SquadName(7, "Foxes"), new SquadName(9, "Ünïcode") };
        var back = MatchCodec.DecodeMatchState(MatchCodec.Encode(new MatchStateMsg(MatchPhase.Running, 9, Array.Empty<ushort>(), rows, MatchMode.TeamDeathmatch, 1, 2, squads)))!;
        Assert.Equal(rows, back.Rows);
        Assert.Equal(squads, back.Squads);
    }

    [Fact]
    public void ASquadlessScoreboardHasAnEmptyList()
    {
        var back = MatchCodec.DecodeMatchState(MatchCodec.Encode(new MatchStateMsg(MatchPhase.Running, 9, Array.Empty<ushort>(), new[] { new ScoreRow(1, "a", 0, 0, 0, Teams.A) }, MatchMode.TeamDeathmatch)))!;
        Assert.Empty(back.Squads!);
        Assert.Equal(0, back.Rows[0].Squad);
    }

    [Fact]
    public void OlderScoreboardsStillDecode()
    {
        var rows = new[] { new ScoreRow(1, "a", 1, 2, 3, Teams.A, Squad: 7), new ScoreRow(2, "b", 0, 0, 5, Teams.B) };
        var full = MatchCodec.Encode(new MatchStateMsg(MatchPhase.Running, 9, Array.Empty<ushort>(), rows, MatchMode.TeamDeathmatch, 1, 2, new[] { new SquadName(7, "Foxes") }));
        var noLeaders = MatchCodec.DecodeMatchState(full[..^2])!; // what v72 sent: ends after the squad names
        Assert.Equal(new[] { new SquadName(7, "Foxes") }, noLeaders.Squads);
        var noSquads = full[..(full.Length - (rows.Length + 1 + 2 + "Foxes".Length + 2))]; // what v70 sent: ends after the team tail
        var old = MatchCodec.DecodeMatchState(noSquads)!;
        Assert.Equal(new[] { Teams.A, Teams.B }, old.Rows.Select(r => r.Team).ToArray());
        Assert.All(old.Rows, r => Assert.Equal(0, r.Squad));
        Assert.Null(old.Squads);
        var noTeams = MatchCodec.DecodeMatchState(noSquads[..^(rows.Length + 5)])!; // and before teams
        Assert.Equal(MatchMode.FreeForAll, noTeams.Mode);
    }

    [Fact]
    public void ATruncatedSquadTailIsRejected() =>
        Assert.Null(MatchCodec.DecodeMatchState(MatchCodec.Encode(new MatchStateMsg(MatchPhase.Running, 9, Array.Empty<ushort>(), new[] { new ScoreRow(1, "a", 0, 0, 0, Teams.A, Squad: 1) }, MatchMode.TeamDeathmatch, 0, 0, new[] { new SquadName(1, "Foxes") }))[..^3]));

    [Theory]
    [InlineData(SquadAction.Create, 0, "Foxes")]
    [InlineData(SquadAction.Create, 0, "")]
    [InlineData(SquadAction.Join, 200, "")]
    [InlineData(SquadAction.Leave, 0, "")]
    public void RequestCodecRoundTrips(SquadAction action, int squad, string name)
    {
        var a = new SquadAssign(action, (byte)squad, name);
        Assert.Equal(a, SquadAssignCodec.Decode(SquadAssignCodec.Encode(a)));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 1, 0 })]
    [InlineData(new byte[] { 0, 0, 0 })] // no such action
    [InlineData(new byte[] { 4, 0, 0 })]
    [InlineData(new byte[] { 1, 0, 5, 65 })] // name shorter than it says
    public void RequestCodecRejectsGarbage(byte[] d) => Assert.Null(SquadAssignCodec.Decode(d));

    [Fact]
    public void ClientStateKnowsSquadsAndMates()
    {
        var c = new ClientMatchState();
        var rows = new[] { new ScoreRow(1, "a", 0, 0, 0, Teams.A, Squad: 7), new ScoreRow(2, "b", 0, 0, 0, Teams.A, Squad: 7), new ScoreRow(3, "c", 0, 0, 0, Teams.A), new ScoreRow(4, "d", 0, 0, 0, Teams.B, Squad: 8) };
        c.Apply(EventKind.MatchState, MatchCodec.Encode(new MatchStateMsg(MatchPhase.Running, 9, Array.Empty<ushort>(), rows, MatchMode.TeamDeathmatch, 0, 0, new[] { new SquadName(7, "Foxes"), new SquadName(8, "Owls") })), 0);
        Assert.True(c.AreSquadMates(1, 2));
        Assert.False(c.AreSquadMates(1, 3)); // a teammate, not a squad mate
        Assert.True(c.AreFriends(1, 3));
        Assert.False(c.AreSquadMates(1, 4));
        Assert.False(c.AreSquadMates(1, 1));
        Assert.Equal("Foxes", c.SquadNameOf(c.SquadOf(2)));
        Assert.Equal("", c.SquadNameOf(c.SquadOf(3)));
    }

    // ---- requests and answers ----

    [Fact]
    public void RulesAnswerAndTellTheMates()
    {
        var m = Match();
        var made = SquadRules.Apply(m, 1, new SquadAssign(SquadAction.Create, 0, "Foxes"));
        Assert.True(made.Ok);
        Assert.Contains("Foxes", made.Text);
        var s = m.Get(1)!.Squad;
        var joined = SquadRules.Apply(m, 2, new SquadAssign(SquadAction.Join, s));
        Assert.True(joined.Ok);
        Assert.Equal(new[] { ((ushort)1, "p2 joined the squad.") }, joined.Tell.ToArray());
        var other = SquadRules.Apply(m, 3, new SquadAssign(SquadAction.Create, 0, "Owls")).Ok;
        Assert.True(other);
        var moved = SquadRules.Apply(m, 2, new SquadAssign(SquadAction.Join, m.Get(3)!.Squad)); // leaves Foxes for Owls: both sides are told
        Assert.Equal(new[] { ((ushort)1, "p2 left the squad."), ((ushort)3, "p2 joined the squad.") }, moved.Tell.ToArray());
        var left = SquadRules.Apply(m, 1, new SquadAssign(SquadAction.Leave));
        Assert.True(left.Ok);
        Assert.Empty(left.Tell); // nobody was left in Foxes
    }

    [Fact]
    public void RulesWordTheRefusals()
    {
        var m = Match(5);
        m.AddPlayer(100, "bot", bot: true);
        var s = Make(m, 1);
        for (ushort i = 2; i <= 4; i++) m.JoinSquad(i, s);
        Assert.Contains("full", SquadRules.Apply(m, 5, new SquadAssign(SquadAction.Join, s)).Text);
        Assert.Contains("Bots", SquadRules.Apply(m, 100, new SquadAssign(SquadAction.Create)).Text);
        Assert.Contains("not in a squad", SquadRules.Apply(m, 5, new SquadAssign(SquadAction.Leave)).Text);
        Assert.False(SquadRules.Apply(m, 77, new SquadAssign(SquadAction.Leave)).Ok);
        Assert.Contains("team deathmatch", SquadRules.Apply(Match(2, "ffa"), 1, new SquadAssign(SquadAction.Create)).Text);
    }

    [Fact]
    public void FindTakesANameOrAPlaceInTheList()
    {
        var m = Match(4, "tdm", 4);
        var a = Make(m, 1, "Alpha");
        var b = Make(m, 2, "Bravo");
        Make(m, 4, "Delta"); // the other team's
        Assert.Equal(a, SquadRules.Find(m, 3, "alpha"));
        Assert.Equal(b, SquadRules.Find(m, 3, "2"));
        Assert.Equal(a, SquadRules.Find(m, 3, "1"));
        Assert.Equal(0, SquadRules.Find(m, 3, "3"));
        Assert.Equal(0, SquadRules.Find(m, 3, "Delta"));
        Assert.Equal(0, SquadRules.Find(m, 3, "0"));
    }

    [Fact]
    public void ListShowsTheOwnTeamsSquads()
    {
        var m = Match(3);
        Assert.Contains("no squads", SquadRules.List(m, 1).Single());
        var s = Make(m, 1, "Alpha");
        m.JoinSquad(2, s);
        Assert.Equal(new[] { "1. Alpha (2/4): p1 (leader), p2 (yours)" }, SquadRules.List(m, 1).ToArray());
        Assert.Equal(new[] { "1. Alpha (2/4): p1 (leader), p2" }, SquadRules.List(m, 3).ToArray());
    }

    [Fact]
    public void ListNamesTheLeaderFirst()
    {
        var m = Match(3);
        var s = Make(m, 2, "Alpha");
        m.JoinSquad(1, s); // a lower id joined later: the creator still leads and comes first
        Assert.Equal(new[] { "1. Alpha (2/4): p2 (leader), p1 (yours)" }, SquadRules.List(m, 1).ToArray());
    }

    // ---- leader (#494) ----

    static List<SquadLeaderChanged> Leaders(MatchSim m)
    {
        var seen = new List<SquadLeaderChanged>();
        m.Event += e => { if (e is SquadLeaderChanged lc) seen.Add(lc); };
        return seen;
    }

    [Fact]
    public void TheCreatorLeads()
    {
        var m = Match();
        var seen = Leaders(m);
        var s = Make(m, 3);
        m.JoinSquad(1, s);
        m.JoinSquad(2, s);
        Assert.Equal(3, m.SquadLeader(s));
        Assert.True(m.IsSquadLeader(3));
        Assert.False(m.IsSquadLeader(1));
        Assert.False(m.IsSquadLeader(5)); // in no squad: not a leader
        Assert.Equal(0, m.SquadLeader(0));
        Assert.Equal(0, m.SquadLeader(99));
        Assert.Equal(new ushort[] { 3, 1, 2 }, m.SquadMembers(s).Select(p => p.Id).ToArray()); // by seniority
        Assert.Equal(new SquadName(s, "p3's squad", 3), m.SquadList.Single());
        Assert.Equal(new[] { new SquadLeaderChanged(s, 0, 3) }, seen); // joins do not move it
    }

    [Fact]
    public void WhenTheLeaderLeavesTheLongestStandingMemberLeads()
    {
        var m = Match();
        var s = Make(m, 1);
        foreach (ushort i in new ushort[] { 4, 2, 3 }) m.JoinSquad(i, s);
        var seen = Leaders(m);
        m.LeaveSquad(3); // not the leader: nothing moves
        Assert.Empty(seen);
        m.LeaveSquad(1);
        Assert.Equal(4, m.SquadLeader(s)); // joined before 2
        m.JoinSquad(1, s); // back, but now the newest
        Assert.Equal(4, m.SquadLeader(s));
        m.LeaveSquad(4);
        Assert.Equal(2, m.SquadLeader(s));
        Assert.Equal(new[] { new SquadLeaderChanged(s, 1, 4), new SquadLeaderChanged(s, 4, 2) }, seen);
    }

    [Fact]
    public void EveryWayOutHandsTheLeadOn()
    {
        var m = Match(7);
        var s = Make(m, 1);
        foreach (ushort i in new ushort[] { 2, 3, 4 }) m.JoinSquad(i, s);
        var seen = Leaders(m);
        m.SetTeam(1, Teams.B); // a team change
        Assert.Equal(2, m.SquadLeader(s));
        m.RemovePlayer(2); // leaving the match
        Assert.Equal(3, m.SquadLeader(s));
        Make(m, 3, "Other"); // creating another squad
        Assert.Equal(4, m.SquadLeader(s));
        m.SetSandbox(4, true); // sandbox: no team
        Assert.Empty(m.SquadMembers(s));
        Assert.Equal(0, m.SquadLeader(s));
        Assert.Equal(new[] { new SquadLeaderChanged(s, 1, 2), new SquadLeaderChanged(s, 2, 3), new SquadLeaderChanged(s, 3, 4), new SquadLeaderChanged(s, 4, 0) },
            seen.Where(e => e.Squad == s).ToArray());
    }

    [Fact]
    public void AModeChangeEndsEveryLead()
    {
        var m = Match();
        var a = Make(m, 1, "Alpha");
        var b = Make(m, 2, "Bravo");
        m.JoinSquad(3, a);
        var seen = Leaders(m);
        m.Restart(MatchMode.FreeForAll);
        Assert.False(m.IsSquadLeader(1));
        Assert.Equal(new[] { new SquadLeaderChanged(a, 1, 0), new SquadLeaderChanged(b, 2, 0) }, seen);
    }

    [Fact]
    public void AHandoverIsToldToTheSquad()
    {
        var m = Match();
        var s = Make(m, 1);
        m.JoinSquad(2, s);
        m.JoinSquad(3, s);
        Assert.Empty(SquadRules.LeaderTold(m, new SquadLeaderChanged(s, 0, 1))); // creation
        m.LeaveSquad(1);
        Assert.Equal(new[] { ((ushort)2, "You lead the squad now."), ((ushort)3, "p2 leads the squad now.") }, SquadRules.LeaderTold(m, new SquadLeaderChanged(s, 1, 2)).ToArray());
        Assert.Empty(SquadRules.LeaderTold(m, new SquadLeaderChanged(s, 2, 0))); // gone
    }

    [Fact]
    public void TheLeaderTravelsToTheClient()
    {
        var rows = new[] { new ScoreRow(1, "a", 0, 0, 0, Teams.A, Squad: 7), new ScoreRow(2, "b", 0, 0, 0, Teams.A, Squad: 7), new ScoreRow(3, "c", 0, 0, 0, Teams.A) };
        var squads = new[] { new SquadName(7, "Foxes", 2) };
        var bytes = MatchCodec.Encode(new MatchStateMsg(MatchPhase.Running, 9, Array.Empty<ushort>(), rows, MatchMode.TeamDeathmatch, 0, 0, squads));
        Assert.Equal(squads, MatchCodec.DecodeMatchState(bytes)!.Squads);
        Assert.Null(MatchCodec.DecodeMatchState(bytes[..^1])); // half a leader
        var c = new ClientMatchState();
        c.Apply(EventKind.MatchState, bytes, 0);
        Assert.Equal(2, c.LeaderOf(7));
        Assert.True(c.IsSquadLeader(2));
        Assert.False(c.IsSquadLeader(1));
        Assert.False(c.IsSquadLeader(3));
        Assert.Equal(0, c.LeaderOf(8));
    }

    // ---- /squad ----

    static readonly ChatContext Player = new(false, Array.Empty<string>());

    [Fact]
    public void SquadCommandIsForEveryoneAndKeepsTheNameWhole()
    {
        var p = ChatCommands.Parse("/squad create Red  Fox Den", Player);
        Assert.True(p.Ok);
        Assert.Equal(new[] { "create", "Red Fox Den" }, p.Args.ToArray());
        Assert.Equal(new[] { "list" }, ChatCommands.Parse("/SQUAD LIST", Player).Args.ToArray());
        Assert.Equal(new[] { "join", "2" }, ChatCommands.Parse("/squad join 2", Player).Args.ToArray());
        Assert.Contains(ChatCommands.Visible(Player), c => c.Name == "squad");
        Assert.False(ChatCommands.Parse("/squad", Player).Ok);
        Assert.False(ChatCommands.Parse("/squad dance", Player).Ok);
        Assert.False(ChatCommands.Parse("/bots 3", Player).Ok); // the others stay admin only
    }

    [Fact]
    public void SquadCommandCompletesItsActions()
    {
        var h = ChatCommands.Hint("/squad ", Player);
        Assert.Equal(new[] { "create", "join", "leave", "list" }, h.Suggestions.Select(s => s.Insert).ToArray());
        Assert.Null(ChatCommands.Hint("/squad create My squad na", Player).Error);
    }

    // ---- scoreboard ----

    [Fact]
    public void ScoreboardGroupsATeamBySquad()
    {
        var rows = new[]
        {
            new ScoreRow(1, "a", 9, 0, 0, Teams.A),
            new ScoreRow(2, "b", 8, 0, 0, Teams.A, Squad: 5),
            new ScoreRow(3, "c", 7, 0, 0, Teams.A, Squad: 6),
            new ScoreRow(4, "d", 6, 0, 0, Teams.A, Squad: 5),
            new ScoreRow(5, "e", 5, 0, 0, Teams.A),
            new ScoreRow(6, "f", 4, 0, 0, Teams.B, Squad: 5),
        };
        var split = HudLogic.SplitTeams(rows, Teams.A);
        Assert.Equal(new ushort[] { 2, 4, 3, 1, 5 }, split.Left.Select(r => r.Id).ToArray()); // squad 5 (best member 8), squad 6, then the unsquadded in score order
        Assert.Equal(new ushort[] { 6 }, split.Right.Select(r => r.Id).ToArray());
        var plain = rows.Where(r => r.Squad == 0).ToList();
        Assert.Same(plain, HudLogic.GroupBySquad(plain));
    }

    // ---- colour ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SquadBlueIsBlueAndApartFromTheGreenTeammate(bool colourBlind)
    {
        static double Lum(StatePalette.Rgb c)
        {
            static double Lin(double u) => u <= 0.04045 ? u / 12.92 : Math.Pow((u + 0.055) / 1.055, 2.4);
            return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        }
        static double Contrast(StatePalette.Rgb a, StatePalette.Rgb b) { var (x, y) = (Lum(a), Lum(b)); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }
        var squad = TeamPalette.Squad(colourBlind);
        var mate = StatePalette.Good(colourBlind);
        Assert.True(squad.B > squad.R + 0.3f && squad.B > squad.G + 0.2f, "squad is blue-dominant");
        Assert.True(Contrast(squad, mate) >= 1.3, "also apart in lightness, for monochrome vision");
        Assert.True(Contrast(squad, new StatePalette.Rgb(0.07f, 0.08f, 0.11f)) >= 3, "reads on the HUD panel");
    }
}
