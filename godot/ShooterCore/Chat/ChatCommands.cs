namespace Drycastle.ShooterCore.Chat;

/// <summary><see cref="Text"/>: free text; as the last argument it takes the rest of the line, case and spaces kept.</summary>
public enum ChatArgKind { Int, Choice, Time, Player, IntOrChoice, Text }

/// <summary>One argument of a chat command. <see cref="Min"/> and <see cref="Max"/> bound an <see cref="ChatArgKind.Int"/>.</summary>
public sealed record ChatArg(string Name, ChatArgKind Kind, bool Required = true, int Min = 0, int Max = 0);

/// <summary>A slash command the chat knows. The same table drives the server's parser and the client's autocomplete. A <see cref="ClientSide"/> command is run by the client that typed it and never reaches the server.</summary>
public sealed record ChatCommand(string Name, string Summary, IReadOnlyList<ChatArg> Args, bool AdminOnly = true, bool ClientSide = false)
{
    /// <summary>"/bots [count]": required arguments in angle brackets, optional ones in square brackets.</summary>
    public string Usage => "/" + Name + string.Concat(Args.Select(a => a.Required ? $" <{a.Name}>" : $" [{a.Name}]"));
}

/// <summary>What the chat needs to know about the viewer and the server to complete and validate: admin rights, the vehicle ids (vehicles.json) the server can spawn and the item ids (items.json) /give takes.</summary>
public sealed record ChatContext(bool IsAdmin, IReadOnlyList<string> Vehicles, IReadOnlyList<string>? ItemIds = null, IReadOnlyList<string>? CompanionIds = null)
{
    public IReadOnlyList<string> Items => ItemIds ?? Array.Empty<string>();
    /// <summary>The Companion ids (companions.json) /companion takes.</summary>
    public IReadOnlyList<string> Companions => CompanionIds ?? Array.Empty<string>();
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

    /// <summary>Bounds of /explode: the blast radius and how far ahead of the player it goes off, metres.</summary>
    public const int MaxExplodeSize = 60, MaxExplodeDistance = 500;
    /// <summary>Bounds of /explode's wave speed, metres a second.</summary>
    public const int MinExplodeSpeed = 5, MaxExplodeSpeed = 400;

    /// <summary>Most Gunmen one /gunman puts down.</summary>
    public const int MaxTestGunmen = 12;
    /// <summary>Highest tier /drill takes (a tier past trap_house.json drill's lists takes their last entry).</summary>
    const int MaxDrillTier = 9;

    /// <summary>Largest /carve radius, metres (the MOAB's carve is 14).</summary>
    public const int MaxCarveRadius = 20;

    /// <summary>The value of /companion that takes the Companion away.</summary>
    public const string CompanionOff = "off";

    public static readonly IReadOnlyList<ChatCommand> All = new ChatCommand[]
    {
        new("help", "List the commands, or explain one", new[] { new ChatArg("command", ChatArgKind.Choice, false) }, AdminOnly: false),
        new("fps", "Show or hide the FPS counter in the top right (remembered in settings)", Array.Empty<ChatArg>(), AdminOnly: false, ClientSide: true),
        new("bots", "Set how many bots play, or show the count. Takes effect at once", new[] { new ChatArg("count", ChatArgKind.Int, false, 0, MaxBots) }),
        new("spawn", "Spawn a vehicle in front of you", new[] { new ChatArg("vehicle", ChatArgKind.Choice) }),
        new("unicorn", "TEST: an extra wild Unicorn on the ground in front of you (a Hunt when killed; it is not respawned)", Array.Empty<ChatArg>()),
        new("phoenix", "TEST: a Phoenix in the sky now (any hit makes it fall, a second one while it lies burns it; it is not the night schedule's)", Array.Empty<ChatArg>()),
        new("sandbox", "Turn sandbox mode on or off for a player (name or #id), or flip it", new[] { new ChatArg("player", ChatArgKind.Player), new ChatArg("state", ChatArgKind.Choice, false) }),
        new("give", "Give an item to a player (name or #id), or to yourself. It is worn if the slot is empty, else it goes in the bag", new[] { new ChatArg("item", ChatArgKind.Choice), new ChatArg("player", ChatArgKind.Player, false) }),
        new("companion", "TEST: give a player (name or #id), or yourself, a Companion for this session, or take it away (off). It is not stored on the profile", new[] { new ChatArg("companion", ChatArgKind.Choice), new ChatArg("player", ChatArgKind.Player, false) }),
        new("restart", "Restart the round now, optionally in another mode (ffa or tdm)", new[] { new ChatArg("mode", ChatArgKind.Choice, false) }),
        new("event", "Trigger a village event now", new[] { new ChatArg("event", ChatArgKind.Choice), new ChatArg("action", ChatArgKind.Choice, false) }),
        new("citizens", "Send citizens to the church to sit on its pews (a number), or remove them all (clear)", new[] { new ChatArg("count", ChatArgKind.IntOrChoice, true, 1, MaxCitizens) }),
        new("church", "TEST: open or close the church doors, or show whether they are open", new[] { new ChatArg("action", ChatArgKind.Choice, false) }),
        new("rozhlas", "TEST: kill the rozhlas announcer now (hijack the mic for 3 minutes), take or release the set, or show the loudspeaker's phase", new[] { new ChatArg("action", ChatArgKind.Choice, false) }),
        new("status", "Give a player (name or #id), or yourself, the divine blessing, the devil's curse or a high, or clear their statuses", new[] { new ChatArg("status", ChatArgKind.Choice), new ChatArg("player", ChatArgKind.Player, false) }),
        new("explode", "TEST: a blast of that radius (metres) on the ground that many metres in front of you. The look, the sound and the dust only: it hurts nobody. The speed is how fast its blast wave leaves, metres a second (a blast of 9 m and larger has one)",
            new[] { new ChatArg("size", ChatArgKind.Int, true, 1, MaxExplodeSize), new ChatArg("distance", ChatArgKind.Int, true, 0, MaxExplodeDistance), new ChatArg("speed", ChatArgKind.Int, false, MinExplodeSpeed, MaxExplodeSpeed) }),
        new("scud", "TEST: the Scud truck. spawn: a truck stands in front of you; reload: the truck you ride or the nearest within 40 m is loaded at once; blast: a Scud blast 200 m ahead of you (kills, destroys, shatters glass); state: its launcher's state",
            new[] { new ChatArg("action", ChatArgKind.Choice) }),
        new("gunman", "TEST: Gunmen of the Rival crew (3 by default) 12 m in front of you, hostile to you only; posted: they hold their spots, turn and shoot, never walk; clear: remove them",
            new[] { new ChatArg("count", ChatArgKind.IntOrChoice, false, 1, MaxTestGunmen), new ChatArg("mode", ChatArgKind.Choice, false) }),
        new("squad", "Squads (team deathmatch): create one with a name, join one by name or number, leave yours, or list your team's", new[] { new ChatArg("action", ChatArgKind.Choice), new ChatArg("name", ChatArgKind.Text, false) }, AdminOnly: false),
        new("night", "TEST (this client only): tune the night lighting, all in percent: moon light (default 8), ambient floor (4), night exposure (90). No arguments shows the values, reset restores them. Use /time midnight to see it",
            new[] { new ChatArg("moon", ChatArgKind.IntOrChoice, false, 0, 100), new ChatArg("ambient", ChatArgKind.Int, false, 0, 100), new ChatArg("exposure", ChatArgKind.Int, false, 10, 200) }, ClientSide: true),
        new("time", "Set the time of day, freeze or resume the clock, or show the time", new[] { new ChatArg("time", ChatArgKind.Time, false) }),
        new("carve", "TEST: carve a sphere of that radius (metres) out of the hollow houses where your crosshair meets the world. No damage, no glass, no blast", new[] { new ChatArg("radius", ChatArgKind.Int, true, 1, MaxCarveRadius) }),
        new("houses", "TEST: make every hollow house whole again, for everyone", new[] { new ChatArg("action", ChatArgKind.Choice) }),
        new("cartel", "TEST: the Cartel, for yourself: join or quit anywhere, add Respect (respect <n>), start a Job of a kind at any tier anywhere (job <kind> [tier], e.g. job test 2), or show your standing (status). On your running Job: end it (end win|fail), set its timer (time <seconds>), send an Assault on a heist (assault), send a Rival chase car after its van (chase), or go to its marker (goto)",
            new[] { new ChatArg("action", ChatArgKind.Choice), new ChatArg("value", ChatArgKind.Text, false) }),
        new("traphouse", "TEST: dress a hollow house as a Trap house now, for everyone: the nearest to you (no argument, or nearest) or one by building id (w...); off strips every one", new[] { new ChatArg("house", ChatArgKind.Text, false) }),
        new("drill", "TEST: arm the safe of the dressed Trap house nearest you for yourself at a tier (1 by default): hold E at it to set the Drill, again to fix a jam; open, it gives cash Duffels. off disarms every one",
            new[] { new ChatArg("tier", ChatArgKind.IntOrChoice, false, 1, MaxDrillTier) }),
        new("duffel", "TEST: a Duffel of that kind (weed, cash or product) in your hands, you its party of one: press the throw key to throw it", new[] { new ChatArg("kind", ChatArgKind.Choice) }),
        new("fences", "TEST: make every broken fence whole again, for everyone", new[] { new ChatArg("action", ChatArgKind.Choice) }),
        new("weather", "Show the weather, or force a thunderstorm (Thundernite can be taken in one) or clear skies, for everyone", new[] { new ChatArg("weather", ChatArgKind.Choice, false) }),
        new("rubble", "What the pieces of a fallen house part do for the rest of the round: fade (sink away) or stay (piles until the round ends). No argument shows it", new[] { new ChatArg("mode", ChatArgKind.Choice, false) }),
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
            ChatArgKind.Choice when cmd.Name == "companion" => ctx.Companions.Select(i => (i, "")).Append((CompanionOff, "no Companion")).ToList(),
            ChatArgKind.Choice when cmd.Name == "sandbox" => new[] { ("on", "godmode, unlimited ammo, no score"), ("off", "back to normal play") },
            ChatArgKind.Choice when cmd.Name == "status" => new[] { ("bless", "one lethal hit is negated"), ("curse", "your bullets set victims ablaze"), ("high", "high from the plantation, as after a hold in it"), ("clear", "take every status away") },
            ChatArgKind.Choice when cmd.Name == "squad" => new[] { ("create", "start a squad; the name is optional (up to 16 characters)"), ("join", "join your team's squad by name or number from list (up to 4 players)"), ("leave", "leave your squad"), ("list", "your team's squads and who is in them") },
            ChatArgKind.Choice when cmd.Name == "restart"=> new[] { ("ffa", "free-for-all, everyone alone"), ("tdm", "team deathmatch, two teams") },
            ChatArgKind.Choice when cmd.Name == "event" && argIndex == 0 => new[]
            {
                ("bigfoot", "start the Bigfoot event: it spawns, hunts the players and drops its head when it falls"),
                ("rozhlas", "a detour broadcast of the village loudspeaker, for everyone, now (as a scheduled one: the mic is live)"),
                ("intel", "TEST: announce a made-up event and put it on the intel screen at the base"),
                ("mass", "the Holy Mass: the bells ring, the village walks to the church, the pastor leads the Mass"),
                ("thieves", "the Thieves event: Raids of thieves drive into the village in vans to steal Loot (cannot be lost)"),
                ("harvester", "a harvester drives onto a crop field near the players and cuts it; kill the driver to take it, keep clear of the reel (ignores the 12 minute cooldown, one at a time)"),
                ("scud", "the Scud event: a Scud truck hides somewhere in a search area on the intel board, rocket carts wait at the bunkers; the first player to board it wins (nobody in 20 minutes: it is removed)"),
                ("demon", "TEST: the pastor turns into the demon now; stand in the church to fight it"),
                ("current", "the running event (with end)"),
            },
            ChatArgKind.Choice when cmd.Name == "event" => new[]
            {
                ("clear", "take the test event off the intel screen again (with intel)"),
                ("end", "conclude the running event as if it was completed (with current)"),
                ("cancel", "remove the running event with no reward (with current; the Holy Mass cannot be cancelled)"),
            },
            ChatArgKind.Choice when cmd.Name == "scud" => new[] { ("spawn", "a Scud truck stands in front of you"), ("reload", "the truck you ride (or the nearest within 40 m) is loaded at once, when its rack is down"), ("blast", "TEST: a Scud blast 200 m ahead of you, credited to you"), ("state", "say the launcher's state of the truck") },
            ChatArgKind.Choice when cmd.Name == "cartel" => new[] { ("join", "join the Cartel (no Contact needed)"), ("quit", "quit the Cartel: rank and Respect are kept"), ("respect", "add that much Respect (a rank-up is announced)"), ("job", "start a Job: the kind, then the tier (1 by default; any tier)"), ("status", "your membership, Respect, rank and Job"), ("end", "end your running Job now: win (it pays) or fail"), ("time", $"set your running Job's timer: seconds left, 1 to {Cartel.CartelAdmin.MaxTimerSeconds}"), ("assault", "send an Assault on your running heist now (max_assaults_alive still holds)"), ("chase", "send a Rival chase car after your running heist's van now (max_alive and the spot rules still hold)"), ("goto", "go to your Job's marker, on the ground under it") },
            ChatArgKind.Choice when cmd.Name == "duffel" => new[] { ("weed", "light: a long throw, a little slower"), ("cash", "medium"), ("product", "heavy: a short throw, much slower") },
            ChatArgKind.Choice when cmd.Name == "houses" => new[] { ("restore", "every hollow house whole, as at the round's start") },
            ChatArgKind.Text when cmd.Name == "traphouse" => new[] { ("nearest", "the hollow house nearest to you"), ("off", "strip every Trap house") },
            ChatArgKind.Choice when cmd.Name == "fences" => new[] { ("restore", "every fence whole, as at the round's start") },
            ChatArgKind.Choice when cmd.Name == "weather" => new[] { ("storm", "a thunderstorm now: it darkens the sky, lightning strikes, and it lasts a normal storm's time"), ("clear", "clear skies now, the next storm after its normal wait") },
            ChatArgKind.Choice when cmd.Name == "rubble" => new[] { ("fade", "the pieces sink away after a while"), ("stay", "the pieces stay as piles for the round") },
            ChatArgKind.Choice when cmd.Name == "church" => new[] { ("open", "swing the doors open"), ("close", "shut the doors (nobody is crushed: a body in the doorway is moved to the nearer side)") },
            ChatArgKind.Choice when cmd.Name == "rozhlas" => new[] { ("hijack", "the announcement cuts and the mic stays live for 3 minutes (only while a detour broadcast is on air)"), ("take", "TEST: hold the set yourself (stands in for holding E at the dictation table; only while hijacked)"), ("release", "TEST: let the set holder go"), ("goto", "TEST: stand at the dictation table (where E takes the set during a hijack)"), ("feedtest", "TEST: push a few seconds of test voice and a shot through the live feed (ffmpeg voice chain, 2 s delay) to every megaphone"), ("combattest", "TEST: push a shot, a blast and a death sound through the live feed, wherever you stand") },
            ChatArgKind.IntOrChoice when cmd.Name == "night" => new[] { ("reset", "back to the shipped night") },
            ChatArgKind.IntOrChoice when cmd.Name == "drill" => new[] { ("off", "disarm every safe") },
            ChatArgKind.IntOrChoice when cmd.Name == "citizens" => new[] { ("clear", "remove every citizen") },
            ChatArgKind.IntOrChoice or ChatArgKind.Choice when cmd.Name == "gunman" => new[] { ("posted", "they hold their spots facing you (trap house defenders)"), ("clear", "remove the test Gunmen") },
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
            case ChatArgKind.Text:
                return token;
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
        if (cmd.Args.Count > 0 && cmd.Args[^1].Kind == ChatArgKind.Text && args.Length > cmd.Args.Count) // a text argument takes the rest of the line
        {
            args = args[..(cmd.Args.Count - 1)].Append(string.Join(' ', args[(cmd.Args.Count - 1)..])).ToArray();
        }
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
        if (argIndex >= cmd.Args.Count && cmd.Args.Count > 0 && cmd.Args[^1].Kind == ChatArgKind.Text) argIndex = cmd.Args.Count - 1; // the text runs on
        if (argIndex >= cmd.Args.Count)
            return new ChatHint(cmd.Usage, cmd.Summary, "", Array.Empty<ChatSuggestion>(), from, "No more arguments.");

        var arg = cmd.Args[argIndex];
        var more = argIndex + 1 < cmd.Args.Count;
        var suggestions = Ranked(Choices(cmd, argIndex, ctx), c => c.Value, current)
            .Select(c => new ChatSuggestion(c.Value, c.Value, c.Detail, more)).ToList();
        var help = arg.Kind switch
        {
            ChatArgKind.Int => $"{arg.Name}: a whole number from {arg.Min} to {arg.Max}",
            ChatArgKind.IntOrChoice => $"{arg.Name}: a whole number from {arg.Min} to {arg.Max}, or {(cmd.Name == "night" ? "reset" : "clear")}",
            ChatArgKind.Time => $"{arg.Name}: HH:MM, a word, freeze or resume",
            ChatArgKind.Player => $"{arg.Name}: a player's name (no spaces) or #id from the scoreboard",
            ChatArgKind.Text => $"{arg.Name}: free text",
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
