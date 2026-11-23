using Godot;
using Drycastle.Shooter.Intel;
using Drycastle.Shooter.Loudspeaker;
using Drycastle.Shooter.Maps;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Intel;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// The announced events on the client. The server sends the whole board (at join and at every change); it goes to the intel
/// screen at the base. When the message names an event that is being announced now, the player is told: a toast that says
/// suspicious activity was detected and to report to the base (naming its grid reference when the map has one), the same line in
/// the chat log so it can be read again, and the village loudspeaker (<see cref="Rozhlas"/>), which only those near a pole hear.
/// The announcement says nothing about the event itself: what it is, when and where is for the intel room.
/// </summary>
public partial class NetClient
{
    /// <summary>The announced events as the server last sent them (also where there is no intel screen): the Scud event's search area is drawn on the tactical map from them, and its truck is kept off the maps' vehicle dots.</summary>
    IReadOnlyList<IntelEvent> _intelEvents = Array.Empty<IntelEvent>();

    /// <summary>
    /// The Scud event is on: its intel entry (a search area) is on the board. An unfound truck must not show as a vehicle dot on the minimap or the tactical map, or the circle would be pointless; the entry is
    /// withdrawn the moment somebody boards it. A modified client can still read its position from the vehicle records, which every client gets (docs/events/scud.md).
    /// </summary>
    bool ScudTruckHidden(VehicleDef def) => def.Launcher != null && _intelEvents.Any(e => e.Kind == "scud" && e.IsArea);

    void OnIntelEvent(EventKind kind, byte[] payload)
    {
        if (kind != EventKind.IntelSync || IntelCodec.Decode(payload, Now) is not { } update) return;
        IntelScreen.Current?.Board.Replace(update.Events);
        _intelEvents = update.Events;
        _hud?.SearchAreas.Set(update.Events.Where(e => e.IsArea).Select(e => new MapSearchAreas.Area(Strings.T("intel.map.search"), e.Where!.Value, e.Radius!.Value)));
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
        Rozhlas.Current?.PlayAnnouncement(); // the megaphones: client-side, does not touch the server's rozhlas phase
    }
}
