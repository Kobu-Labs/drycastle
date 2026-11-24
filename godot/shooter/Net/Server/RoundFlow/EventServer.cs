using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Events;

namespace Drycastle.Shooter.Net.Server.RoundFlow;

/// <summary>
/// The Events (GLOSSARY "Event") a server can run, as one table of handlers per kind. <c>/event &lt;kind&gt;</c> starts a kind through
/// it and <c>/event current end|cancel</c> picks the handler by the kind of the running event (<see cref="EventBook.Current"/>), so the chat commands know no kind.
/// A new kind is: a constant and <see cref="EventBook.Startable"/> (ShooterCore), a choice in <c>ChatCommands</c>, and one <see cref="Register"/> line in <c>NetServer.BuildFeatures</c>
/// with its start, end (as a win) and cancel (no reward; none when the kind can only end). Each handler tells the chat what it did and books its own end in
/// the <see cref="EventBook"/>. The Demon is no kind of its own: it is part of the Holy Mass's run (<c>Parish/DemonServer.cs</c>).
/// </summary>
internal sealed class EventServer
{
    readonly ServerContext _ctx;
    readonly EventBook _book;
    readonly EventHandlers<ServerClient> _handlers = new();

    public EventServer(ServerContext ctx, EventBook book) { _ctx = ctx; _book = book; }

    /// <summary>One kind of event: its name in the chat, how an admin starts it, ends it (a win) and cancels it (none: it can only end).</summary>
    public void Register(string kind, string name, Action<ServerClient> start, Action<ServerClient> end, Action<ServerClient>? cancel = null) =>
        _handlers.Register(kind, name, start, end, cancel);

    /// <summary>"/event &lt;kind&gt;": starts the kind (false when the table has none by that name).</summary>
    public bool Start(string kind, ServerClient admin) => _handlers.Start(kind, admin);

    /// <summary>"/event current end" (a win, for testing) or "/event current cancel" (gone, no reward): the handler of the running kind.</summary>
    public void Current(ServerClient c, string action)
    {
        if (action is not ("end" or "cancel")) { _ctx.ChatTo(c, ChatLineKind.Error, "Usage: /event current end (conclude it as a win) or /event current cancel (remove it, no reward)."); return; }
        var name = "";
        var handled = action == "end" ? _handlers.End(_book, c) : _handlers.Cancel(_book, c, out name);
        switch (handled)
        {
            case EventHandled.NoneRunning: _ctx.ChatTo(c, ChatLineKind.Info, "No event is running."); break;
            case EventHandled.CannotCancel: _ctx.ChatTo(c, ChatLineKind.Error, $"The {name} takes /event current end."); break;
        }
    }
}
