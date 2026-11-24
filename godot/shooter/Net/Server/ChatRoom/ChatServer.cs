using Godot;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Server.ChatRoom;

/// <summary>
/// The chat and the admin's commands. A line from a player goes to everyone; a line starting with "/" is a command, which only
/// an admin may use (the player token of the admin is their identity, ADR 0003). The grammar is <see cref="ChatCommands"/>, shared with the client's autocomplete;
/// this class parses and checks who may, and hands what parsed to the handler a feature registered under the command's name with <see cref="Register"/>.
/// </summary>
internal sealed class ChatServer
{
    readonly ServerContext _ctx;
    readonly Func<bool, ChatContext> _grammarFor;
    readonly ISet<string> _adminTokens;
    readonly bool _adminAll;
    readonly Dictionary<string, Action<ServerClient, IReadOnlyList<string>>> _handlers = new();

    /// <param name="grammarFor">The command grammar as one player sees it: what they may name (vehicles, items, companions) and whether they are an admin.</param>
    /// <param name="adminTokens">Player tokens (lower case compare) of the admins, from --admin-token. A guest (no token) is never an admin.</param>
    /// <param name="adminAll">--admin-all (dev servers only): every human is an admin. Release servers never pass it.</param>
    public ChatServer(ServerContext ctx, Func<bool, ChatContext> grammarFor, ISet<string> adminTokens, bool adminAll)
    {
        _ctx = ctx; _grammarFor = grammarFor; _adminTokens = adminTokens; _adminAll = adminAll;
        Register("help", Help);
    }

    /// <summary>The handler of a command of <see cref="ChatCommands"/>, run with the parsed arguments after the checks; a command nobody registered is only logged.</summary>
    public void Register(string command, Action<ServerClient, IReadOnlyList<string>> handler) => _handlers.Add(command, handler);

    public bool IsAdmin(ServerClient c) => !c.IsBot && (_adminAll || (c.Token.Length > 0 && _adminTokens.Contains(c.Token)));

    ChatContext GrammarOf(ServerClient c) => _grammarFor(IsAdmin(c));

    /// <summary>Join: tells the client if it is an admin and which vehicles it may name.</summary>
    public void SendStatus(ServerClient c)
    {
        var grammar = GrammarOf(c);
        _ctx.SendEvent(c, EventKind.ChatStatus, ChatEvents.EncodeStatus(new ChatStatus(grammar.IsAdmin, grammar.Vehicles)));
        if (grammar.IsAdmin) GD.Print($"server: {c.Name} #{c.Id} is an admin");
    }

    /// <summary>A line a player typed: a chat line to everyone (rate limited), or a command.</summary>
    public void OnSend(ServerClient c, byte[] payload)
    {
        if (!c.Joined || ChatEvents.DecodeSend(payload) is not { } text) return;
        if (!c.Chat.TryTake(_ctx.Now)) { _ctx.ChatTo(c, ChatLineKind.Error, "You are sending messages too fast."); return; }
        if (text[0] == '/') { Run(c, text); return; }
        GD.Print($"server: chat {c.Name}: {text}");
        _ctx.ChatToAll(ChatLineKind.Player, c.Name, text);
    }

    void Run(ServerClient c, string text)
    {
        var parsed = ChatCommands.Parse(text, GrammarOf(c));
        if (!parsed.Ok)
        {
            if (!IsAdmin(c) && parsed.Command is { AdminOnly: true }) GD.Print($"server: {c.Name} #{c.Id} tried {text} without being an admin");
            _ctx.ChatTo(c, ChatLineKind.Error, parsed.Error ?? "That did not work.");
            return;
        }
        if (parsed.Command!.ClientSide) { _ctx.ChatTo(c, ChatLineKind.Info, $"/{parsed.Command.Name} runs on your own client."); return; } // a modified client sent it: nothing to do here
        if (_handlers.TryGetValue(parsed.Command.Name, out var run)) run(c, parsed.Args);
        GD.Print($"server: {c.Name} #{c.Id} ran {text}");
    }

    void Help(ServerClient c, IReadOnlyList<string> args)
    {
        var grammar = GrammarOf(c);
        if (args.Count == 1 && ChatCommands.Find(args[0]) is { } one)
        {
            _ctx.ChatTo(c, ChatLineKind.Info, $"{one.Usage}: {one.Summary}.");
            return;
        }
        _ctx.ChatTo(c, ChatLineKind.Info, grammar.IsAdmin ? "Commands (Tab completes, Up and Down bring back what you sent):" : "Most commands are for admins. You can use:");
        foreach (var cmd in ChatCommands.Visible(grammar)) _ctx.ChatTo(c, ChatLineKind.Info, $"{cmd.Usage}: {cmd.Summary}.");
    }
}
