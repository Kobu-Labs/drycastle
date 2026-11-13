using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The announced events on the server: the <see cref="IntelBoard"/> every client's intel screen draws, and the announcement
/// that sends players to the base to read it. This is the whole hook a real event uses: <see cref="AnnounceIntel"/> when the
/// event lifecycle announces one, <see cref="WithdrawIntel"/> when it is over. Nothing else of an event is known here. The
/// only thing that calls it today is the admin's test event (<c>/event intel</c>, <see cref="IntelMock"/>).
/// </summary>
public partial class NetServer
{
    /// <summary>The events announced now, on the server's clock (<c>_elapsed</c> seconds).</summary>
    public IntelBoard Intel { get; } = new();

    /// <summary>
    /// Puts an event on the board and announces it: every client gets the board, and the one named in it is announced (a
    /// banner and the village loudspeaker). The same id replaces an earlier event.
    /// </summary>
    public void AnnounceIntel(IntelEvent e)
    {
        Intel.Put(e);
        SendIntel(e.Id);
        GD.Print($"server: intel: announced '{e.Name}' ({e.Id}), begins in {e.StartsAt - _elapsed:F0} s");
    }

    /// <summary>Takes an event off the board (it is over, or it was withdrawn) and tells every client. Nothing is announced.</summary>
    public void WithdrawIntel(string id)
    {
        if (!Intel.Remove(id)) return;
        SendIntel("");
        GD.Print($"server: intel: withdrew {id}");
    }

    void SendIntel(string announcedId) => BroadcastEvent(EventKind.IntelSync, IntelCodec.Encode(Intel.Events, _elapsed, announcedId));

    /// <summary>Join: the board as it is, so the screen is right the moment the joiner reaches it. Nothing is announced to them.</summary>
    void SendIntelOnJoin(Client c)
    {
        if (!Intel.IsEmpty) Send(c, new EventMsg((byte)EventKind.IntelSync, IntelCodec.Encode(Intel.Events, _elapsed)));
    }

    /// <summary>Each tick: an event whose time on the board is up goes, and the clients are told.</summary>
    void IntelTick()
    {
        if (Intel.Prune(_elapsed)) SendIntel("");
    }

    /// <summary>
    /// "/event intel": announces the made-up test event, which begins in <see cref="IntelMock.LeadSeconds"/> seconds and carries
    /// the admin's own position as its place (none when they are dead). "/event intel clear" takes it off again.
    /// </summary>
    void ChatIntel(Client c, bool clear)
    {
        if (clear)
        {
            if (!Intel.Events.Any(e => e.Id == IntelMock.TestId)) { ChatTo(c, ChatLineKind.Info, "There is no test event on the intel screen."); return; }
            WithdrawIntel(IntelMock.TestId);
            ChatToAll(ChatLineKind.System, "", $"{c.Name} took the test event off the intel screen.");
            return;
        }
        System.Numerics.Vector2? where = null;
        if (_match.Get(c.Id) is { Life: PlayerLife.Alive }) { var p = c.Body.GlobalPosition; where = new System.Numerics.Vector2(p.X, p.Z); }
        AnnounceIntel(IntelMock.Test(_elapsed, where));
        ChatToAll(ChatLineKind.System, "", $"{c.Name} announced a test event.");
        ChatTo(c, ChatLineKind.Info, $"Test event on the intel screen at the base: it begins in {IntelMock.LeadSeconds / 60:0.#} minutes" + (where == null ? "." : ", at your position now.") + " /event intel clear takes it off.");
    }
}
