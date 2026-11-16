namespace Drycastle.ShooterCore;

/// <summary>What an admin can ask of the teams from the Teams screen (#312).</summary>
public enum TeamAction : byte
{
    /// <summary>One player (by id) to a team, pinned there.</summary>
    Move = 1,
    /// <summary>One player (by id) is free for the balance again.</summary>
    Unpin = 2,
    /// <summary>Every bot to a team, pinned there.</summary>
    MoveBots = 3,
    /// <summary>Every player changes side, the earned score goes along.</summary>
    Swap = 4,
    /// <summary>Everyone dealt at random into two even teams, pins dropped.</summary>
    Shuffle = 5,
    /// <summary>The round-start balance now.</summary>
    Balance = 6,
    /// <summary>Every pin dropped.</summary>
    UnpinAll = 7,
}

/// <param name="Id">The player for <see cref="TeamAction.Move"/> and <see cref="TeamAction.Unpin"/>, else 0.</param>
/// <param name="Team">The target team for <see cref="TeamAction.Move"/> and <see cref="TeamAction.MoveBots"/>, else <see cref="Teams.None"/>.</param>
public readonly record struct TeamAssign(TeamAction Action, ushort Id = 0, byte Team = Teams.None);

/// <summary>TeamAssign (client to server, admin only): u8 action, u16 id, u8 team.</summary>
public static class TeamAssignCodec
{
    public static byte[] Encode(TeamAssign m) => new[] { (byte)m.Action, (byte)(m.Id & 0xFF), (byte)(m.Id >> 8), m.Team };

    public static TeamAssign? Decode(ReadOnlySpan<byte> d)
    {
        if (d.Length < 4 || d[0] is < (byte)TeamAction.Move or > (byte)TeamAction.UnpinAll || d[3] > Teams.B) return null;
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
                return match.SetTeam(a.Id, a.Team) ? $"{admin} moved {name} to team {Teams.Tag(a.Team)}." : null;
            case TeamAction.Unpin:
                return match.Unpin(a.Id) ? $"{admin} let the balance move {name} again." : null;
            case TeamAction.MoveBots:
                return match.MoveBots(a.Team) != null ? $"{admin} sent the bots to team {Teams.Tag(a.Team)}." : null;
            case TeamAction.Swap:
                return match.SwapTeams() ? $"{admin} swapped the teams." : null;
            case TeamAction.Shuffle:
                return match.ShuffleTeams() ? $"{admin} shuffled the teams." : null;
            case TeamAction.Balance:
                if (match.BalanceTeams() is not { } moved) return null;
                return moved.Count == 0 ? $"{admin} balanced the teams: nobody had to move." : $"{admin} balanced the teams: {string.Join(", ", moved.Select(p => p.Name))} moved.";
            case TeamAction.UnpinAll:
                return match.UnpinAll() ? $"{admin} let the balance move everyone again." : null;
            default:
                return null;
        }
    }
}
