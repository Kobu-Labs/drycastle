using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// Chat on the client: lines from the server go to the HUD's chat window, the player's lines and commands go to the server,
/// and the server's day clock changes (an admin's /time) reach the sky. The server decides everything (who is an admin, what a
/// command does); the client only draws the hints from the same grammar (<see cref="ChatCommands"/>).
/// </summary>
public partial class NetClient
{
    /// <summary>What the server said at join: admin or not, and the vehicles /spawn takes. Until then, a player with no commands.</summary>
    public ChatContext ChatCtx { get; private set; } = new(false, Array.Empty<string>());

    public bool ChatOpen => _hud?.ChatOpen ?? false;

    /// <summary>Closes the chat box if it is open (Esc). True when it was.</summary>
    public bool CloseChat() => _hud?.CloseChat() ?? false;

    void WireChatHud(Hud hud)
    {
        hud.ChatInfo = () => ChatCtx;
        hud.ChatSubmit = SendChat;
    }

    void OnChatEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.ChatStatus when ChatEvents.DecodeStatus(payload) is { } st:
                ChatCtx = new ChatContext(st.Admin, st.Vehicles);
                break;
            case EventKind.ChatLine when ChatEvents.DecodeLine(payload) is { } line:
                _hud?.AddChatLine(line);
                break;
            case EventKind.RozhlasStart:
                Rozhlas.Current?.Trigger(); // the village loudspeaker starts now
                break;
            case EventKind.ClockSet when ChatEvents.DecodeClock(payload) is { } change:
                DayNightCycle.Current?.SetServerClock(change.Clock, change.Tick); // the sky jumps to the new time at once
                break;
        }
    }

    /// <summary>Sends a chat line, or a command when it starts with "/". The server answers with chat lines.</summary>
    public void SendChat(string text)
    {
        if (ChatEvents.Clean(text).Length > 0) SendSandboxEvent(EventKind.ChatSend, ChatEvents.EncodeSend(text));
    }

    /// <summary>
    /// The chat key (T, rebindable) opens the box, and so does typing "/" on any layout (it opens with the slash in it). Not
    /// while a menu is open or the box already is. True when the key was used.
    /// </summary>
    bool HandleChatInput(InputEvent e)
    {
        if (_hud == null || Bot || e is not InputEventKey { Pressed: true, Echo: false } k) return false;
        if (_hud.ChatOpen || MenuOpen?.Invoke() == true) return false;
        var slash = k.Unicode == '/';
        if (!slash && !e.IsActionPressed("chat")) return false;
        _hud.OpenChat(slash ? "/" : "");
        GetViewport().SetInputAsHandled();
        return true;
    }
}
