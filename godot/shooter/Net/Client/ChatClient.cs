using Godot;
using Drycastle.Shooter.HeadsUp;
using Drycastle.Shooter.Scenery;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Companions;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// Chat on the client: lines from the server go to the HUD's chat window, the player's lines and commands go to the server,
/// and the server's day clock changes (an admin's /time) reach the sky. The server decides everything (who is an admin, what a
/// command does); the client only draws the hints from the same grammar (<see cref="ChatCommands"/>).
/// </summary>
public sealed class ChatClient
{
    readonly ClientContext _ctx;
    readonly ItemsFile _items;
    readonly CompanionsFile _companions;
    readonly SquadsClient _squads;

    /// <summary>What the server said at join: admin or not, and the vehicles /spawn takes (the items /give takes are items.json's, which the client has itself). Until then, a player with no commands.</summary>
    public ChatContext Context { get; private set; } = new(false, Array.Empty<string>());

    public ChatClient(ClientContext ctx, ItemsFile items, CompanionsFile companions, SquadsClient squads)
    {
        _ctx = ctx;
        _items = items;
        _companions = companions;
        _squads = squads;
    }

    public bool IsOpen => _ctx.Hud?.ChatOpen ?? false;

    /// <summary>Closes the chat box if it is open (Esc). True when it was.</summary>
    public bool Close() => _ctx.Hud?.CloseChat() ?? false;

    public void WireHud(Hud hud)
    {
        hud.ChatInfo = () => Context;
        hud.ChatSubmit = Send;
    }

    public void OnEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.ChatStatus when ChatEvents.DecodeStatus(payload) is { } st:
                Context = new ChatContext(st.Admin, st.Vehicles, _items.Items.Select(i => i.Id).ToList(), _companions.Companions.Select(i => i.Id).ToList());
                break;
            case EventKind.ChatLine when ChatEvents.DecodeLine(payload) is { } line:
                _ctx.Hud?.AddChatLine(line);
                _squads.OnChatLine(line);
                break;
            case EventKind.ClockSet when ChatEvents.DecodeClock(payload) is { } change:
                DayNightCycle.Current?.SetServerClock(change.Clock, change.Tick); // the sky jumps to the new time at once
                break;
        }
    }

    /// <summary>Sends a chat line, or a command when it starts with "/". The server answers with chat lines.</summary>
    public void Send(string text)
    {
        if (ChatEvents.Clean(text).Length > 0) _ctx.Send(EventKind.ChatSend, ChatEvents.EncodeSend(text));
    }

    /// <summary>
    /// The chat key (T, rebindable) opens the box, and so does typing "/" on any layout (it opens with the slash in it). Not
    /// while a menu is open or the box already is. True when the key was used.
    /// </summary>
    public bool HandleInput(InputEvent e)
    {
        if (_ctx.Hud is not { } hud || _ctx.Bot || e is not InputEventKey { Pressed: true, Echo: false } k) return false;
        if (!OverlayArbiter.CanOpenChat(hud.ChatOpen, _ctx.MenuIsOpen)) return false;
        var slash = k.Unicode == '/';
        if (!slash && !e.IsActionPressed("chat")) return false;
        hud.OpenChat(slash ? "/" : "");
        _ctx.Root.GetViewport().SetInputAsHandled();
        return true;
    }
}
