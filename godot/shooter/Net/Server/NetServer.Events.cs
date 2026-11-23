using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Events;

namespace Drycastle.Shooter.Net.Server;

/// <summary>
/// The Events (GLOSSARY "Event") a server can run, as one table of handlers per kind: <see cref="_eventHandlers"/>. <c>/event &lt;kind&gt;</c> starts a kind through
/// it and <c>/event current end|cancel</c> picks the handler by the kind of the running event (<see cref="EventBook.Current"/>), so the chat commands know no kind.
/// A new kind is: a constant and <see cref="EventBook.Startable"/> (ShooterCore), a choice in <c>ChatCommands</c>, and one <c>Register</c> line in <see cref="InitEvents"/>
/// with its start, end (as a win) and cancel (no reward; none when the kind can only end). Each handler tells the chat what it did and books its own end in
/// <see cref="_events"/>. The Demon is no kind of its own: it is part of the Holy Mass's run (<c>Parish/DemonServer.cs</c>).
/// </summary>
public partial class NetServer
{
    readonly EventHandlers<ServerClient> _eventHandlers = new();

    /// <summary>Fills the handler table. Called once from <c>_Ready</c>, after every kind's own Init.</summary>
    void InitEvents()
    {
        // the Holy Mass can only end (its handler answers a cancel with a refusal)
        _eventHandlers.Register(EventBook.Bigfoot, "Bigfoot event", StartBigfoot, EndBigfoot, CancelBigfoot);
        _eventHandlers.Register(EventBook.Mass, "Holy Mass", _mass.Chat, _mass.End);
        _eventHandlers.Register(EventBook.Harvester, "harvester event", _harvester.Start, _harvester.End, _harvester.Cancel);
        _eventHandlers.Register(EventBook.Thieves, "Thieves event", _thieves.Start, _thieves.End, _thieves.Cancel);
        _eventHandlers.Register(EventBook.Scud, "Scud event", _scudEvent.Start, _scudEvent.End, _scudEvent.Cancel);
    }

    /// <summary>"/event current end" (a win, for testing) or "/event current cancel" (gone, no reward): the handler of the running kind.</summary>
    void ChatCurrentEvent(ServerClient c, string action)
    {
        if (action is not ("end" or "cancel")) { ChatTo(c, ChatLineKind.Error, "Usage: /event current end (conclude it as a win) or /event current cancel (remove it, no reward)."); return; }
        var name = "";
        var handled = action == "end" ? _eventHandlers.End(_events, c) : _eventHandlers.Cancel(_events, c, out name);
        switch (handled)
        {
            case EventHandled.NoneRunning: ChatTo(c, ChatLineKind.Info, "No event is running."); break;
            case EventHandled.CannotCancel: ChatTo(c, ChatLineKind.Error, $"The {name} takes /event current end."); break;
        }
    }
}
