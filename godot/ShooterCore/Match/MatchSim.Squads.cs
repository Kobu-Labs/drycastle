namespace Drycastle.ShooterCore.Match;

/// <summary>Why a squad request did nothing (<see cref="MatchSim.CreateSquad"/>, <see cref="MatchSim.JoinSquad"/>, <see cref="MatchSim.LeaveSquad"/>).</summary>
public enum SquadResult : byte
{
    Ok,
    /// <summary>Squads exist in team deathmatch only.</summary>
    NotTeamMode,
    UnknownPlayer,
    /// <summary>Bots never join squads.</summary>
    Bot,
    /// <summary>The player has no team (unassigned, or a sandbox player).</summary>
    NoTeam,
    UnknownSquad,
    /// <summary>The squad belongs to the other team.</summary>
    WrongTeam,
    Full,
    /// <summary>The team already has a squad of this name.</summary>
    NameTaken,
    AlreadyThere,
    NotInSquad,
}

/// <summary>A squad as the scoreboard lists it: id (never 0) and name. Members are the rows that carry the id.</summary>
public readonly record struct SquadName(byte Id, string Name);

/// <summary>
/// Squads (#379, GLOSSARY "Squad"): an opt-in tag inside a team, kept on <see cref="MatchPlayer.Squad"/>. Up to <see cref="Squads.MaxMembers"/> humans, no leader, one squad per player
/// (joining or creating leaves the old one), a squad with no member is gone. Team deathmatch only; a player sees and joins the own team's squads. Friend logic (<see cref="AreFriends"/>),
/// scoring and colours do not look at squads. A team change, a mode change and leaving the match clear a membership.
/// </summary>
public sealed partial class MatchSim
{
    readonly SortedDictionary<byte, string> _squadNames = new();
    byte _lastSquad;

    /// <summary>Every squad, in id order. Rebuilt for the scoreboard message.</summary>
    public IReadOnlyList<SquadName> SquadList => _squadNames.Select(kv => new SquadName(kv.Key, kv.Value)).ToList();

    /// <summary>The name of a squad, "" when there is none by that id.</summary>
    public string SquadNameOf(byte squad) => _squadNames.GetValueOrDefault(squad, "");

    /// <summary>The members of a squad, in id order.</summary>
    public IReadOnlyList<MatchPlayer> SquadMembers(byte squad) => squad == 0 ? Array.Empty<MatchPlayer>() : _players.Values.Where(p => p.Squad == squad).ToList();

    /// <summary>The squads of a team, in id order (empty outside team deathmatch).</summary>
    public IReadOnlyList<byte> SquadsOf(byte team) =>
        !IsTeamMode || team == Teams.None ? Array.Empty<byte>() : _squadNames.Keys.Where(s => _players.Values.Any(p => p.Squad == s && p.Team == team)).ToList();

    /// <summary>Both in the same squad (never 0).</summary>
    public bool AreSquadMates(ushort a, ushort b) => a != b && _players.TryGetValue(a, out var pa) && _players.TryGetValue(b, out var pb) && pa.Squad != 0 && pa.Squad == pb.Squad;

    SquadResult CanSquad(ushort id, out MatchPlayer p)
    {
        p = null!;
        if (!IsTeamMode) return SquadResult.NotTeamMode;
        if (!_players.TryGetValue(id, out var found)) return SquadResult.UnknownPlayer;
        p = found;
        if (p.Bot) return SquadResult.Bot;
        return p.Team == Teams.None || p.Sandbox ? SquadResult.NoTeam : SquadResult.Ok;
    }

    /// <summary>
    /// The player starts a squad of their own and is its first member, after leaving the old one. <paramref name="name"/> is trimmed and cut to <see cref="Squads.MaxNameLength"/>;
    /// empty gets the default "&lt;player&gt;'s squad". <see cref="SquadResult.NameTaken"/> when the team has one of that name.
    /// </summary>
    public SquadResult CreateSquad(ushort id, string? name, out byte squad)
    {
        squad = 0;
        if (CanSquad(id, out var p) is var no and not SquadResult.Ok) return no;
        var clean = Squads.CleanName(name) is { Length: > 0 } c ? c : Squads.DefaultName(p.Name);
        if (_players.Values.Any(o => o.Team == p.Team && o.Squad != 0 && _squadNames[o.Squad].Equals(clean, StringComparison.OrdinalIgnoreCase))) return SquadResult.NameTaken;
        var next = NextSquadId();
        if (next == 0) return SquadResult.Full;
        DropSquad(p);
        _squadNames[next] = clean;
        p.Squad = squad = next;
        Emit(new ScoreboardChanged());
        return SquadResult.Ok;
    }

    /// <summary>The player joins a squad of their own team, after leaving the old one. Full, another team's or unknown squads refuse.</summary>
    public SquadResult JoinSquad(ushort id, byte squad)
    {
        if (CanSquad(id, out var p) is var no and not SquadResult.Ok) return no;
        if (!_squadNames.ContainsKey(squad)) return SquadResult.UnknownSquad;
        if (p.Squad == squad) return SquadResult.AlreadyThere;
        var members = SquadMembers(squad);
        if (members.Count > 0 && members[0].Team != p.Team) return SquadResult.WrongTeam;
        if (members.Count >= Squads.MaxMembers) return SquadResult.Full;
        DropSquad(p);
        p.Squad = squad;
        Emit(new ScoreboardChanged());
        return SquadResult.Ok;
    }

    /// <summary>The player leaves their squad; it is gone when they were the last.</summary>
    public SquadResult LeaveSquad(ushort id)
    {
        if (!_players.TryGetValue(id, out var p)) return SquadResult.UnknownPlayer;
        if (p.Squad == 0) return SquadResult.NotInSquad;
        DropSquad(p);
        Emit(new ScoreboardChanged());
        return SquadResult.Ok;
    }

    /// <summary>Takes the player out of their squad (no event), and the squad off the books when nobody is left in it. The callers that change a player's team or remove them use this.</summary>
    void DropSquad(MatchPlayer p)
    {
        var squad = p.Squad;
        if (squad == 0) return;
        p.Squad = 0;
        if (!_players.Values.Any(o => o.Squad == squad)) _squadNames.Remove(squad);
    }

    void ClearSquads()
    {
        foreach (var p in _players.Values) p.Squad = 0;
        _squadNames.Clear();
    }

    /// <summary>The next unused id after the last one handed out (ids are never 0); 0 when all 255 are in use.</summary>
    byte NextSquadId()
    {
        for (var i = 0; i < byte.MaxValue; i++)
        {
            _lastSquad = _lastSquad == byte.MaxValue ? (byte)1 : (byte)(_lastSquad + 1);
            if (!_squadNames.ContainsKey(_lastSquad)) return _lastSquad;
        }
        return 0;
    }
}

/// <summary>Limits and name rules of squads.</summary>
public static class Squads
{
    public const int MaxMembers = 4;
    /// <summary>Most characters of a typed squad name.</summary>
    public const int MaxNameLength = 16;
    /// <summary>The default name is the player's name with "'s squad", cut to this many characters.</summary>
    const int MaxDefaultLength = 32;

    /// <summary>Trimmed, control characters and runs of spaces removed, cut to <see cref="MaxNameLength"/>. Empty for null or blank.</summary>
    public static string CleanName(string? name, int max = MaxNameLength)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var ch in name.Trim())
        {
            if (char.IsControl(ch)) continue;
            if (ch == ' ' && sb.Length > 0 && sb[^1] == ' ') continue;
            sb.Append(ch);
        }
        var s = sb.ToString().Trim();
        if (s.Length > max) s = s[..max].TrimEnd();
        if (s.Length > 0 && char.IsHighSurrogate(s[^1])) s = s[..^1];
        return s;
    }

    public static string DefaultName(string player) => CleanName($"{player}'s squad", MaxDefaultLength);
}
