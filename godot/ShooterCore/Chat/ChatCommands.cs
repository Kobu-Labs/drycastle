namespace Drycastle.ShooterCore;

public enum ChatArgKind { Int, Choice, Time, Player, IntOrChoice }

/// <summary>One argument of a chat command. <see cref="Min"/> and <see cref="Max"/> bound an <see cref="ChatArgKind.Int"/>.</summary>
public sealed record ChatArg(string Name, ChatArgKind Kind, bool Required = true, int Min = 0, int Max = 0);

/// <summary>A slash command the chat knows. The same table drives the server's parser and the client's autocomplete.</summary>
public sealed record ChatCommand(string Name, string Summary, IReadOnlyList<ChatArg> Args, bool AdminOnly = true)
{
    /// <summary>"/bots [count]": required arguments in angle brackets, optional ones in square brackets.</summary>
    public string Usage => "/" + Name + string.Concat(Args.Select(a => a.Required ? $" <{a.Name}>" : $" [{a.Name}]"));
}

/// <summary>What the chat needs to know about the viewer and the server to complete and validate: admin rights, the vehicle ids (vehicles.json) the server can spawn and the item ids (items.json) /give takes.</summary>
public sealed record ChatContext(bool IsAdmin, IReadOnlyList<string> Vehicles, IReadOnlyList<string>? ItemIds = null)
{
    public IReadOnlyList<string> Items => ItemIds ?? Array.Empty<string>();
}

/// <summary>A command line that parsed: the command and its normalised (lower case) arguments, or the reason it did not.</summary>
public sealed record ChatParse(ChatCommand? Command, IReadOnlyList<string> Args, string? Error)
{
    public bool Ok => Command != null && Error == null;
}

/// <summary>One completion: <see cref="Insert"/> replaces the word being typed, <see cref="Label"/> and <see cref="Detail"/> are what the list shows.</summary>
public sealed record ChatSuggestion(string Insert, string Label, string Detail, bool AppendSpace);

/// <summary>What the chat shows under the input while a command is typed: usage, the active argument's help, the completions, and an error when the text cannot become a valid command.</summary>
public sealed record ChatHint(string Usage, string Summary, string ArgHelp, IReadOnlyList<ChatSuggestion> Suggestions, int ReplaceFrom, string? Error)
{
    public static readonly ChatHint None = new("", "", "", Array.Empty<ChatSuggestion>(), 0, null);
    public bool IsEmpty => Usage == "" && Error == null && Suggestions.Count == 0;
}

/// <summary>The "/time" argument: a time of day, or a switch for the clock's movement.</summary>
public readonly record struct ChatTimeSetting(int? Minute, bool? Frozen);

public static class ChatTime
{
    public static readonly (string Name, int Minute)[] Presets =
    {
        ("dawn", 6 * 60), ("morning", 9 * 60), ("noon", 12 * 60), ("dusk", 18 * 60), ("night", 22 * 60), ("midnight", 0),
    };

    public static string Format(int minute) => $"{minute / 60 % 24:00}:{minute % 60:00}";

    /// <summary>"14:30", "9", a preset ("noon"), "freeze" (stop the clock where it is) or "resume". Null when it is none of them.</summary>
    public static ChatTimeSetting? Parse(string s)
    {
        s = s.Trim().ToLowerInvariant();
        if (s == "freeze") return new ChatTimeSetting(null, true);
        if (s == "resume") return new ChatTimeSetting(null, false);
        foreach (var (name, minute) in Presets) if (name == s) return new ChatTimeSetting(minute, null);
        var parts = s.Split(':');
        if (parts.Length > 2 || !TryInt(parts[0], 2, out var h) || h > 23) return null;
        var m = 0;
        if (parts.Length == 2 && (parts[1].Length != 2 || !TryInt(parts[1], 2, out m) || m > 59)) return null;
        return new ChatTimeSetting(h * 60 + m, null);
    }

    static bool TryInt(string s, int maxDigits, out int v)
    {
        v = 0;
        if (s.Length == 0 || s.Length > maxDigits) return false;
        foreach (var ch in s) if (ch is < '0' or > '9') return false;
        v = int.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }
}

/// <summary>The chat's commands, their parsing and their autocomplete. Pure text in, text out: the server runs the parsed command, the client only draws the hint.</summary>
public static class ChatCommands
{
    /// <summary>Most bots a server takes (they need no peer slot, so this is apart from the player limit).</summary>
    public const int MaxBots = 64;

    /// <summary>Most citizens one /citizens asks for (the data's max_count may be lower).</summary>
    public const int MaxCitizens = 200;

    public static readonly IReadOnlyList<ChatCommand> All = new ChatCommand[]
    {
        new("help", "List the commands, or explain one", new[] { new ChatArg("command", ChatArgKind.Choice, false) }, AdminOnly: false),
        new("bots", "Set how many bots play, or show the count. Takes effect at once", new[] { new ChatArg("count", ChatArgKind.Int, false, 0, MaxBots) }),
        new("spawn", "Spawn a vehicle in front of you", new[] { new ChatArg("vehicle", ChatArgKind.Choice) }),
        new("sandbox", "Turn sandbox mode on or off for a player (name or #id), or flip it", new[] { new ChatArg("player", ChatArgKind.Player), new ChatArg("state", ChatArgKind.Choice, false) }),
        new("give", "Give an item to a player (name or #id), or to yourself. It is worn if the slot is empty, else it goes in the bag", new[] { new ChatArg("item", ChatArgKind.Choice), new ChatArg("player", ChatArgKind.Player, false) }),
        new("restart", "Restart the round now, optionally in another mode (ffa or tdm)", new[] { new ChatArg("mode", ChatArgKind.Choice, false) }),
        new("event", "Trigger a village event now", new[] { new ChatArg("event", ChatArgKind.Choice), new ChatArg("action", ChatArgKind.Choice, false) }),
        new("citizens", "Send citizens to the church to sit on its pews (a number), or remove them all (clear)", new[] { new ChatArg("count", ChatArgKind.IntOrChoice, true, 1, MaxCitizens) }),
        new("church", "TEST: open or close the church doors, or show whether they are open", new[] { new ChatArg("action", ChatArgKind.Choice, false) }),
        new("status", "Give a player (name or #id), or yourself, the divine blessing or the devil's curse, or clear their statuses", new[] { new ChatArg("status", ChatArgKind.Choice), new ChatArg("player", ChatArgKind.Player, false) }),
        new("time", "Set the time of day, freeze or resume the clock, or show the time", new[] { new ChatArg("time", ChatArgKind.Time, false) }),
    };

    public static ChatCommand? Find(string name) => All.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The commands this viewer may use: everything for an admin, /help for anyone.</summary>
    public static IEnumerable<ChatCommand> Visible(ChatContext ctx) => All.Where(c => ctx.IsAdmin || !c.AdminOnly);

    /// <summary>The values an argument can take: (value, detail) pairs. Empty for a free-form argument.</summary>
    public static IReadOnlyList<(string Value, string Detail)> Choices(ChatCommand cmd, int argIndex, ChatContext ctx)
    {
        if (argIndex < 0 || argIndex >= cmd.Args.Count) return Array.Empty<(string, string)>();
        return cmd.Args[argIndex].Kind switch
        {
            ChatArgKind.Time => ChatTime.Presets.Select(p => (p.Name, ChatTime.Format(p.Minute)))
                .Concat(new[] { ("freeze", "stop the clock"), ("resume", "let it run") }).ToList(),
            ChatArgKind.Choice when cmd.Name == "spawn" => ctx.Vehicles.Select(v => (v, "")).ToList(),
            ChatArgKind.Choice when cmd.Name == "give" => ctx.Items.Select(i => (i, "")).ToList(),
            ChatArgKind.Choice when cmd.Name == "sandbox" => new[] { ("on", "godmode, unlimited ammo, no score"), ("off", "back to normal play") },
            ChatArgKind.Choice when cmd.Name == "status" => new[] { ("bless", "one lethal hit is negated"), ("curse", "your bullets set victims ablaze"), ("clear", "take every status away") },
            ChatArgKind.Choice when cmd.Name == "restart"=> new[] { ("ffa", "free-for-all, everyone alone"), ("tdm", "team deathmatch, two teams") },
            ChatArgKind.Choice when cmd.Name == "event" && argIndex == 0 => new[]
            {
                ("rozhlas", "the village loudspeaker broadcast, for everyone, now"),
                ("intel", "TEST: announce a made-up event and put it on the intel screen at the base"),
                ("mass", "the Holy Mass: the bells ring, the village walks to the church, the pastor leads the Mass"),
                ("demon", "TEST: the pastor turns into the demon now; stand in the church to fight it"),
                ("current", "the running event (with end)"),
            },
            ChatArgKind.Choice when cmd.Name == "event" => new[]
            {
                ("clear", "take the test event off the intel screen again (with intel)"),
                ("end", "conclude the running event as if it was completed (with current)"),
            },
            ChatArgKind.Choice when cmd.Name == "church" => new[] { ("open", "swing the doors open"), ("close", "shut the doors (nobody is crushed: a body in the doorway is moved to the nearer side)") },
            ChatArgKind.IntOrChoice when cmd.Name == "citizens" => new[] { ("clear", "remove every citizen") },
            ChatArgKind.Choice when cmd.Name == "help" => Visible(ctx).Select(c => (c.Name, c.Summary)).ToList(),
            _ => Array.Empty<(string, string)>(),
        };
    }

    static string[] Tokens(string body) => body.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Checks one argument. Returns its normalised text, or null with the error to show.</summary>
    static string? Validate(ChatCommand cmd, int i, string token, ChatContext ctx, out string? error)
    {
        error = null;
        var arg = cmd.Args[i];
        switch (arg.Kind)
        {
            case ChatArgKind.Int:
                if (!int.TryParse(token, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) || n < arg.Min || n > arg.Max)
                {
                    error = $"{arg.Name} must be a whole number from {arg.Min} to {arg.Max}.";
                    return null;
                }
                return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case ChatArgKind.IntOrChoice when token.Length > 0 && token.All(char.IsAsciiDigit):
                goto case ChatArgKind.Int;
            case ChatArgKind.Time:
                if (ChatTime.Parse(token) == null)
                {
                    error = $"'{token}' is not a time. Use HH:MM, a word like noon, freeze or resume.";
                    return null;
                }
                return token.ToLowerInvariant();
            case ChatArgKind.Player:
                return token; // a name or #id: only the server knows who is connected
            default:
                var hit = Choices(cmd, i, ctx).FirstOrDefault(c => c.Value.Equals(token, StringComparison.OrdinalIgnoreCase));
                if (hit.Value == null) { error = $"Unknown {arg.Name} '{token}'."; return null; }
                return hit.Value;
        }
    }

    /// <summary>Parses a full command line (starting with "/"). Authority is checked here too, so the server needs only this.</summary>
    public static ChatParse Parse(string input, ChatContext ctx)
    {
        var t = Tokens(input.TrimStart().TrimStart('/'));
        if (t.Length == 0) return new ChatParse(null, Array.Empty<string>(), "Type a command name after the slash. /help lists them.");
        var cmd = Find(t[0]);
        if (cmd == null) return new ChatParse(null, Array.Empty<string>(), $"Unknown command /{t[0].ToLowerInvariant()}. /help lists the commands.");
        if (cmd.AdminOnly && !ctx.IsAdmin) return new ChatParse(cmd, Array.Empty<string>(), $"Only admins can use /{cmd.Name}.");
        var args = t.Skip(1).ToArray();
        if (args.Length > cmd.Args.Count) return new ChatParse(cmd, Array.Empty<string>(), $"Too many arguments. Usage: {cmd.Usage}");
        var normal = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var v = Validate(cmd, i, args[i], ctx, out var err);
            if (v == null) return new ChatParse(cmd, Array.Empty<string>(), err);
            normal.Add(v);
        }
        for (var i = args.Length; i < cmd.Args.Count; i++)
            if (cmd.Args[i].Required) return new ChatParse(cmd, Array.Empty<string>(), $"Missing <{cmd.Args[i].Name}>. Usage: {cmd.Usage}");
        return new ChatParse(cmd, normal, null);
    }

    static IEnumerable<T> Ranked<T>(IEnumerable<T> items, Func<T, string> name, string prefix)
    {
        var all = items.ToList();
        return all.Where(x => name(x).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Concat(all.Where(x => !name(x).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name(x).Contains(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The hint for the text typed so far. Text that is not a command (no leading slash) has none.</summary>
    public static ChatHint Hint(string input, ChatContext ctx)
    {
        if (!input.StartsWith('/')) return ChatHint.None;
        var body = input[1..];
        var trailing = body.EndsWith(' ');
        var t = Tokens(body);

        if (t.Length == 0 || (t.Length == 1 && !trailing))
        {
            var prefix = t.Length == 0 ? "" : t[0];
            var hits = Ranked(Visible(ctx), c => c.Name, prefix).ToList();
            var sug = hits.Select(c => new ChatSuggestion(c.Name, c.Usage, c.Summary, c.Args.Count > 0)).ToList();
            string? error = null;
            if (hits.Count == 0) error = ctx.IsAdmin ? $"Unknown command /{prefix}. /help lists them." : "Commands are for admins. /help lists what you can use.";
            return new ChatHint(hits.Count == 1 ? hits[0].Usage : "", hits.Count == 1 ? hits[0].Summary : "", "", sug, 1, error);
        }

        var cmd = Find(t[0]);
        if (cmd == null) return new ChatHint("", "", "", Array.Empty<ChatSuggestion>(), input.Length, $"Unknown command /{t[0].ToLowerInvariant()}. /help lists them.");
        if (cmd.AdminOnly && !ctx.IsAdmin) return new ChatHint(cmd.Usage, cmd.Summary, "", Array.Empty<ChatSuggestion>(), input.Length, $"Only admins can use /{cmd.Name}.");

        var argIndex = trailing ? t.Length - 1 : t.Length - 2;
        var current = trailing ? "" : t[^1];
        var from = input.Length - current.Length;
        if (argIndex >= cmd.Args.Count)
            return new ChatHint(cmd.Usage, cmd.Summary, "", Array.Empty<ChatSuggestion>(), from, "No more arguments.");

        var arg = cmd.Args[argIndex];
        var more = argIndex + 1 < cmd.Args.Count;
        var suggestions = Ranked(Choices(cmd, argIndex, ctx), c => c.Value, current)
            .Select(c => new ChatSuggestion(c.Value, c.Value, c.Detail, more)).ToList();
        var help = arg.Kind switch
        {
            ChatArgKind.Int => $"{arg.Name}: a whole number from {arg.Min} to {arg.Max}",
            ChatArgKind.IntOrChoice => $"{arg.Name}: a whole number from {arg.Min} to {arg.Max}, or clear",
            ChatArgKind.Time => $"{arg.Name}: HH:MM, a word, freeze or resume",
            ChatArgKind.Player => $"{arg.Name}: a player's name (no spaces) or #id from the scoreboard",
            _ => $"{arg.Name}",
        } + (arg.Required ? "" : " (optional)");

        string? err = null;
        if (current != "" && suggestions.Count == 0 || (arg.Kind == ChatArgKind.Int && current != "") || (arg.Kind == ChatArgKind.IntOrChoice && current.Length > 0 && current.All(char.IsAsciiDigit)))
            if (Validate(cmd, argIndex, current, ctx, out var e) == null) err = e;
        return new ChatHint(cmd.Usage, cmd.Summary, help, suggestions, from, err);
    }

    /// <summary>The input after taking <paramref name="s"/>: the typed word is replaced, and a space follows when more arguments are expected.</summary>
    public static string Complete(string input, ChatHint hint, ChatSuggestion s) =>
        input[..Math.Min(hint.ReplaceFrom, input.Length)] + s.Insert + (s.AppendSpace ? " " : "");
}
