using Godot;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Intel;
using Drycastle.ShooterCore.Net;


namespace Drycastle.Shooter.Net.Server.Intel;

/// <summary>
/// The announced events on the server: the <see cref="IntelBoard"/> every client's intel screen draws, and the announcement
/// that sends players to the base to read it. This is the whole hook a real event uses: <see cref="Announce"/> when the
/// event lifecycle announces one, <see cref="Withdraw"/> when it is over. Nothing else of an event is known here. Besides the events,
/// the only thing that calls it is the admin's test event (<c>/event intel</c>, <see cref="IntelMock"/>).
/// </summary>
internal sealed class IntelServer
{
    readonly ServerContext _ctx;
    readonly IntelBoard _board = new();

    public IntelServer(ServerContext ctx) => _ctx = ctx;

    /// <summary>
    /// Puts an event on the board and announces it: every client gets the board, and the one named in it is announced (a
    /// banner and the village loudspeaker). The same id replaces an earlier event.
    /// </summary>
    public void Announce(IntelEvent e)
    {
        _board.Put(e);
        Send(e.Id);
        GD.Print($"server: intel: announced '{e.Name}' ({e.Id}), begins in {e.StartsAt - _ctx.Now:F0} s");
    }

    /// <summary>Takes an event off the board (it is over, or it was withdrawn) and tells every client. Nothing is announced.</summary>
    public void Withdraw(string id)
    {
        if (!_board.Remove(id)) return;
        Send("");
        GD.Print($"server: intel: withdrew {id}");
    }

    void Send(string announcedId) => _ctx.Broadcast(EventKind.IntelSync, IntelCodec.Encode(_board.Events, _ctx.Now, announcedId));

    /// <summary>Join: the board as it is (empty too: a client that was on another server drops what that one showed), so the screen is right the moment the joiner reaches it. Nothing is announced to them.</summary>
    public void SendOnJoin(ServerClient c) => _ctx.SendEvent(c, EventKind.IntelSync, IntelCodec.Encode(_board.Events, _ctx.Now));

    /// <summary>Each tick: an event whose time on the board is up goes, and the clients are told.</summary>
    public void Tick()
    {
        if (_board.Prune(_ctx.Now)) Send("");
    }

    /// <summary>
    /// "/event intel": announces the made-up test event, which begins in <see cref="IntelMock.LeadSeconds"/> seconds and carries
    /// the admin's own position as its place (none when they are dead). "/event intel clear" takes it off again.
    /// </summary>
    public void Chat(ServerClient c, bool clear)
    {
        if (clear)
        {
            if (!_board.Events.Any(e => e.Id == IntelMock.TestId)) { _ctx.ChatTo(c, ChatLineKind.Info, "There is no test event on the intel screen."); return; }
            Withdraw(IntelMock.TestId);
            _ctx.ChatToAll(ChatLineKind.System, "", $"{c.Name} took the test event off the intel screen.");
            return;
        }
        System.Numerics.Vector2? where = null;
        if (_ctx.IsAlive(c.Id)) { var p = c.Body.GlobalPosition; where = new System.Numerics.Vector2(p.X, p.Z); }
        Announce(IntelMock.Test(_ctx.Now, where));
        _ctx.ChatToAll(ChatLineKind.System, "", $"{c.Name} announced a test event.");
        _ctx.ChatTo(c, ChatLineKind.Info, $"Test event on the intel screen at the base: it begins in {IntelMock.LeadSeconds / 60:0.#} minutes" + (where == null ? "." : ", at your position now.") + " /event intel clear takes it off.");
    }
}
