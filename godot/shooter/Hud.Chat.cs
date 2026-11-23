using Godot;
using Drycastle.ShooterCore.Chat;

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
        _chat = new ChatBox(() => ChatInfo?.Invoke() ?? new ChatContext(false, Array.Empty<string>()), RunChat);
        _root.AddChild(_chat);
    }

    /// <summary>What the player sent: a client-side command (/fps) runs here and never leaves the client; anything else goes to the owner to send.</summary>
    void RunChat(string text)
    {
        if (text.StartsWith('/') && ChatCommands.Parse(text, ChatInfo?.Invoke() ?? new ChatContext(false, Array.Empty<string>())) is { Ok: true, Command: { ClientSide: true } cmd } parsed)
        {
            if (cmd.Name == "fps") ToggleFps();
            else if (cmd.Name == "night") RunNight(parsed.Args);
            return;
        }
        ChatSubmit?.Invoke(text);
    }

    /// <summary>Opens the chat box. The Hunter's Log closes first: it sits on the top layer, so it would cover the box and take its Q / E keys.</summary>
    public void OpenChat(string prefill = "")
    {
        CloseHuntersLog();
        CloseMineralLog();
        _chat.Open(prefill);
    }

    /// <summary>Closes the chat box. True when it was open.</summary>
    public bool CloseChat() => _chat.Close();

    public void AddChatLine(ChatLine line) => _chat.AddLine(line);

    /// <summary>TEST /night: tune this client's night lighting in percent; no arguments shows it, reset restores it.</summary>
    void RunNight(IReadOnlyList<string> args)
    {
        if (DayNightCycle.Current is not { } d) return;
        if (args.Count > 0 && args[0] == "reset") d.ResetNightTuning();
        else if (args.Count > 0)
            d.TuneNight(int.Parse(args[0]) / 100f, args.Count > 1 ? int.Parse(args[1]) / 100f : null, args.Count > 2 ? int.Parse(args[2]) / 100f : null);
        AddChatLine(new ChatLine(ChatLineKind.Info, "", $"night: moon {d.MoonPeak * 100:0}, ambient {d.MinAmbient * 100:0}, exposure {d.NightExposure * 100:0} (percent)"));
    }
}
