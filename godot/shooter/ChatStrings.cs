namespace Drycastle.Shooter;

/// <summary>English strings of the chat window. Registered with <see cref="Strings.Register"/>; the command help itself (usage, summaries, errors) comes from ShooterCore in English.</summary>
public static class ChatStrings
{
    public static readonly (string Key, string Text)[] Table =
    {
        ("bind.chat", "Chat (type / for commands)"),
        ("chat.placeholder", "Message, or / for commands"),
        ("chat.keys", "Enter send   Tab complete   Up/Down history   Esc close"),
    };
}
