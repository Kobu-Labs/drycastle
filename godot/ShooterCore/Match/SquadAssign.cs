using System.Text;

namespace Drycastle.ShooterCore.Match;

/// <summary>What a player can ask of their squad from the Squads screen or /squad (#379).</summary>
public enum SquadAction : byte
{
    /// <summary>A new squad with a name (empty: the default), the player its first member.</summary>
    Create = 1,
    /// <summary>Into the squad by id.</summary>
    Join = 2,
    Leave = 3,
}

/// <param name="Squad">The squad for <see cref="SquadAction.Join"/>, else 0.</param>
/// <param name="Name">The name for <see cref="SquadAction.Create"/>, else empty. The server cleans it (<see cref="Squads.CleanName"/>).</param>
public readonly record struct SquadAssign(SquadAction Action, byte Squad = 0, string Name = "");

/// <summary>SquadAssign (client to server): u8 action, u8 squad, u8 nameLen, name (utf8). The result travels in the scoreboard (squad byte per row, squad names).</summary>
public static class SquadAssignCodec
{
    public static byte[] Encode(SquadAssign m)
    {
        var name = Encoding.UTF8.GetBytes(Squads.CleanName(m.Name));
        var b = new byte[3 + name.Length];
        b[0] = (byte)m.Action; b[1] = m.Squad; b[2] = (byte)name.Length;
        name.CopyTo(b, 3);
        return b;
    }

    public static SquadAssign? Decode(ReadOnlySpan<byte> d)
    {
        if (d.Length < 3 || d[0] is < (byte)SquadAction.Create or > (byte)SquadAction.Leave || d.Length < 3 + d[2]) return null;
        return new SquadAssign((SquadAction)d[0], d[1], Encoding.UTF8.GetString(d.Slice(3, d[2])));
    }
}

/// <summary>What a squad request answered: the line for the player, and the squad mates who are told who came or went.</summary>
/// <param name="Tell">Who gets which line (the squad that was joined, the one that was left).</param>
public sealed record SquadReply(bool Ok, string Text, IReadOnlyList<(ushort Id, string Text)> Tell)
{
    public static SquadReply Fail(string text) => new(false, text, Array.Empty<(ushort, string)>());
}

/// <summary>Runs a player's squad request on the match and words the answers, so the server only passes them on (/squad and the Squads screen share it).</summary>
public static class SquadRules
{
    public static SquadReply Apply(MatchSim match, ushort id, SquadAssign a)
    {
        var me = match.Get(id);
        if (me == null) return SquadReply.Fail("You are not in the match.");
        var old = me.Squad;
        var name = me.Name;
        SquadResult r;
        byte now = 0;
        switch (a.Action)
        {
            case SquadAction.Create: r = match.CreateSquad(id, a.Name, out now); break;
            case SquadAction.Join: r = match.JoinSquad(id, a.Squad); now = a.Squad; break;
            case SquadAction.Leave: r = match.LeaveSquad(id); break;
            default: return SquadReply.Fail("Unknown squad request.");
        }
        if (r != SquadResult.Ok) return SquadReply.Fail(Why(r));
        var tell = new List<(ushort, string)>();
        if (old != 0 && old != now) tell.AddRange(match.SquadMembers(old).Select(p => (p.Id, $"{name} left the squad."))); // what is left of the old squad
        if (a.Action == SquadAction.Join) tell.AddRange(match.SquadMembers(now).Where(p => p.Id != id).Select(p => (p.Id, $"{name} joined the squad.")));
        var text = a.Action switch
        {
            SquadAction.Create => $"You started the squad {match.SquadNameOf(now)}.",
            SquadAction.Join => $"You joined the squad {match.SquadNameOf(now)}.",
            _ => "You left your squad.",
        };
        return new SquadReply(true, text, tell);
    }

    static string Why(SquadResult r) => r switch
    {
        SquadResult.NotTeamMode => "Squads are for team deathmatch.",
        SquadResult.Bot => "Bots cannot join squads.",
        SquadResult.NoTeam => "Join a team first: a squad is part of a team.",
        SquadResult.UnknownSquad => "That squad is gone.",
        SquadResult.WrongTeam => "That squad is on the other team.",
        SquadResult.Full => $"That squad is full ({Squads.MaxMembers} players).",
        SquadResult.NameTaken => "Your team already has a squad with that name.",
        SquadResult.AlreadyThere => "You are already in that squad.",
        SquadResult.NotInSquad => "You are not in a squad.",
        _ => "That did not work.",
    };

    /// <summary>The squad of the player's team named <paramref name="text"/> (case ignored), or the one at that 1-based place in <see cref="List"/>. 0 when there is none.</summary>
    public static byte Find(MatchSim match, ushort id, string text)
    {
        if (match.Get(id) is not { } me) return 0;
        var squads = match.SquadsOf(me.Team);
        foreach (var s in squads) if (match.SquadNameOf(s).Equals(text.Trim(), StringComparison.OrdinalIgnoreCase)) return s;
        return int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= squads.Count ? squads[n - 1] : (byte)0;
    }

    /// <summary>The own team's squads, one line each: place, name, count and members. A line saying so when there are none.</summary>
    public static IReadOnlyList<string> List(MatchSim match, ushort id)
    {
        if (!match.IsTeamMode) return new[] { "Squads are for team deathmatch." };
        if (match.Get(id) is not { } me || me.Team == Teams.None) return new[] { "Join a team first: a squad is part of a team." };
        var squads = match.SquadsOf(me.Team);
        if (squads.Count == 0) return new[] { "Your team has no squads. /squad create [name] starts one." };
        return squads.Select((s, i) => $"{i + 1}. {match.SquadNameOf(s)} ({match.SquadMembers(s).Count}/{Squads.MaxMembers}): {string.Join(", ", match.SquadMembers(s).Select(p => p.Name))}{(s == me.Squad ? " (yours)" : "")}").ToList();
    }
}
