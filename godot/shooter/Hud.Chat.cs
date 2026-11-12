using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>The chat window in the HUD (see <see cref="ChatBox"/>): the match owner feeds it lines and takes what the player sends.</summary>
public partial class Hud
{
    ChatBox _chat = null!;

    /// <summary>Admin rights and spawnable vehicles for the command hints (set by the owner; read when the text changes).</summary>
    public Func<ChatContext>? ChatInfo { get; set; }

    /// <summary>The player sent a line (a message, or a command starting with "/").</summary>
    public Action<string>? ChatSubmit { get; set; }

    public bool ChatOpen => _chat?.IsOpen ?? false;

    void BuildChat()
    {
        _chat = new ChatBox(() => ChatInfo?.Invoke() ?? new ChatContext(false, Array.Empty<string>()), t => ChatSubmit?.Invoke(t));
        _root.AddChild(_chat);
    }

    public void OpenChat(string prefill = "") => _chat.Open(prefill);

    /// <summary>Closes the chat box. True when it was open.</summary>
    public bool CloseChat() => _chat.Close();

    public void AddChatLine(ChatLine line) => _chat.AddLine(line);
}
