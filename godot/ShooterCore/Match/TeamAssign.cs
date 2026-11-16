namespace Drycastle.ShooterCore;

/// <summary>What an admin can ask of the teams from the Teams screen (#312).</summary>
public enum TeamAction : byte
{
    /// <summary>One player (by id) to a team, or (team none) out of the round.</summary>
    Move = 1,
    /// <summary>Every bot to a team.</summary>
    MoveBots = 2,
    /// <summary>Everyone on a team dealt at random into two even teams.</summary>
    Shuffle = 3,
    /// <summary>The round-start balance now.</summary>
    Balance = 4,
}

/// <param name="Id">The player for <see cref="TeamAction.Move"/>, else 0.</param>
/// <param name="Team">The target team for <see cref="TeamAction.Move"/> (<see cref="Teams.None"/>: unassigned) and <see cref="TeamAction.MoveBots"/>, else <see cref="Teams.None"/>.</param>
public readonly record struct TeamAssign(TeamAction Action, ushort Id = 0, byte Team = Teams.None);

/// <summary>TeamAssign (client to server, admin only): u8 action, u16 id, u8 team.</summary>
public static class TeamAssignCodec
{
    public static byte[] Encode(TeamAssign m) => new[] { (byte)m.Action, (byte)(m.Id & 0xFF), (byte)(m.Id >> 8), m.Team };

    public static TeamAssign? Decode(ReadOnlySpan<byte> d)
    {
        if (d.Length < 4 || d[0] is < (byte)TeamAction.Move or > (byte)TeamAction.Balance || d[3] > Teams.B) return null;
        return new TeamAssign((TeamAction)d[0], (ushort)(d[1] | d[2] << 8), d[3]);
    }
}

/// <summary>Runs an admin's <see cref="TeamAssign"/> on the match and words what happened for the chat, so the server only checks who sent it and passes the line on.</summary>
public static class TeamAssignRules
{
    /// <returns>The announcement for everyone, or null when nothing was done (unknown player, not team deathmatch, a sandbox player, a bad team).</returns>
    public static string? Apply(MatchSim match, TeamAssign a, string admin)
    {
        if (!match.IsTeamMode) return null;
        var name = match.Get(a.Id)?.Name ?? "";
        switch (a.Action)
        {
            case TeamAction.Move:
                if (!match.SetTeam(a.Id, a.Team)) return null;
                return a.Team == Teams.None ? $"{admin} took {name} out of the round." : $"{admin} moved {name} to team {Teams.Tag(a.Team)}.";
            case TeamAction.MoveBots:
                return match.MoveBots(a.Team) != null ? $"{admin} sent the bots to team {Teams.Tag(a.Team)}." : null;
            case TeamAction.Shuffle:
                return match.ShuffleTeams() ? $"{admin} shuffled the teams." : null;
            case TeamAction.Balance:
                if (match.BalanceTeams() is not { } moved) return null;
                return moved.Count == 0 ? $"{admin} balanced the teams: nobody had to move." : $"{admin} balanced the teams: {string.Join(", ", moved.Select(p => p.Name))} moved.";
            default:
                return null;
        }
    }
}
