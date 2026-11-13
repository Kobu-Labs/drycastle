using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The announced events on the client. The server sends the whole board (at join and at every change); it goes to the intel
/// screen at the base. When the message names an event that is being announced now, the player is told: a toast that says
/// suspicious activity was detected and to report to the base (naming its grid reference when the map has one), the same line in
/// the chat log so it can be read again, and the village loudspeaker (<see cref="Rozhlas"/>), which only those near a pole hear.
/// The announcement says nothing about the event itself: what it is, when and where is for the intel room.
/// </summary>
public partial class NetClient
{
    void OnIntelEvent(EventKind kind, byte[] payload)
    {
        if (kind != EventKind.IntelSync || IntelCodec.Decode(payload, Now) is not { } update) return;
        IntelScreen.Current?.Board.Replace(update.Events);
        if (update.Announced is { } e)
        {
            GD.Print($"client {PlayerName}: announcement, '{e.Name}' begins in {e.StartsAt - Now:F0} s");
            Announce();
        }
    }

    void Announce()
    {
        var at = IntelScreen.Current?.BaseReference;
        var text = at == null ? Strings.T("intel.announce") : Strings.F("intel.announce.at", at);
        UiShell.Instance?.Toast(text, ToastKind.Warn, null, 8.0);
        _hud?.AddChatLine(new ChatLine(ChatLineKind.System, "", text));
        Rozhlas.Current?.Trigger();
    }
}
