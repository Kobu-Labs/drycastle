using Godot;
using Drycastle.Shooter.Net.Server.ChatRoom;
using Drycastle.Shooter.Net.Server.Playground;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Match;

namespace Drycastle.Shooter.Net.Server.Squads;

/// <summary>
/// Teams and squads on the server: the admin's change from the Teams screen (#312, <see cref="TeamAssign"/>), a player's squad request from the Squads screen (#379, <see cref="SquadAssign"/>)
/// and the chat command that makes the same requests. The rules are <see cref="TeamAssignRules"/> and <see cref="SquadRules"/>; the scoreboard carries the result, a refusal comes back as a chat line.
/// </summary>
internal sealed class TeamServer
{
    readonly ServerContext _ctx;
    readonly ChatServer _chat;
    readonly SandboxServer _sandbox;

    public TeamServer(ServerContext ctx, ChatServer chat, SandboxServer sandbox) { _ctx = ctx; _chat = chat; _sandbox = sandbox; }

    /// <summary>An admin's change from the Teams screen. Anyone else is ignored (and logged). Everyone is told what was done.</summary>
    public void OnTeamAssign(ServerClient c, byte[] payload)
    {
        if (TeamAssignCodec.Decode(payload) is not { } a) return;
        if (!_chat.IsAdmin(c)) { GD.Print($"server: {c.Name} #{c.Id} sent a team change without being an admin"); return; }
        // a sandbox player dropped on a team: sandbox mode ends first (it has no team), then the move
        if (a is { Action: TeamAction.Move, Team: Teams.A or Teams.B } && _ctx.Match.IsTeamMode && _ctx.Players.TryGetValue(a.Id, out var target) && target.Sandbox) _sandbox.SetSandbox(target, false);
        if (TeamAssignRules.Apply(_ctx.Match, a, c.Name) is not { } line) return;
        _ctx.ChatTo(c, ChatLineKind.Info, line);
        GD.Print($"server: {c.Name} #{c.Id} team {a.Action} {a.Id} {a.Team}");
    }

    /// <summary>A player's squad request from the Squads screen.</summary>
    public void OnSquadAssign(ServerClient c, byte[] payload)
    {
        if (SquadAssignCodec.Decode(payload) is not { } a) return;
        Run(c, a);
        GD.Print($"server: {c.Name} #{c.Id} squad {a.Action} {a.Squad} '{a.Name}'");
    }

    /// <summary>"/squad create [name] | join name-or-number | leave | list" (#379): the same requests as the Squads screen.</summary>
    public void ChatSquad(ServerClient c, IReadOnlyList<string> args)
    {
        var name = args.Count > 1 ? args[1] : "";
        switch (args[0])
        {
            case "list": foreach (var line in SquadRules.List(_ctx.Match, c.Id)) _ctx.ChatTo(c, ChatLineKind.Info, line); break;
            case "create": Run(c, new SquadAssign(SquadAction.Create, 0, name)); break;
            case "leave": Run(c, new SquadAssign(SquadAction.Leave)); break;
            case "join":
                if (name == "") { _ctx.ChatTo(c, ChatLineKind.Error, "Say which squad: /squad join <name or number>. /squad list shows them."); break; }
                if (SquadRules.Find(_ctx.Match, c.Id, name) is var squad && squad == 0) { _ctx.ChatTo(c, ChatLineKind.Error, $"Your team has no squad '{name}'. /squad list shows them."); break; }
                Run(c, new SquadAssign(SquadAction.Join, squad));
                break;
        }
    }

    void Run(ServerClient c, SquadAssign a)
    {
        if (!c.Joined || c.IsBot) return;
        var reply = SquadRules.Apply(_ctx.Match, c.Id, a);
        _ctx.ChatTo(c, reply.Ok ? ChatLineKind.Info : ChatLineKind.Error, reply.Text);
        foreach (var (id, text) in reply.Tell) if (_ctx.Players.TryGetValue(id, out var mate)) _ctx.ChatTo(mate, ChatLineKind.Info, text);
    }
}
