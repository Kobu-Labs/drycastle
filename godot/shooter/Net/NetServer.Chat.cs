using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The chat and the admin's commands. A line from a player goes to everyone; a line starting with "/" is a command, which only
/// an admin may use (<see cref="AdminTokens"/>, from --admin-token: the player token of the admin is their identity, ADR 0003).
/// The grammar is <see cref="ChatCommands"/>, shared with the client's autocomplete; this file runs what parsed. Everything an
/// admin changes takes effect on the next tick: <see cref="Bots"/> is read each tick by <see cref="BotsTick"/>, the clock goes
/// to every client at once.
/// </summary>
public partial class NetServer
{
    /// <summary>Player tokens (lower case compare) of the admins, from --admin-token. A guest (no token) is never an admin.</summary>
    public HashSet<string> AdminTokens = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>--admin-all (dev servers only): every human is an admin, so nobody has to look up a token. Release servers never pass it.</summary>
    public bool AdminAll;

    bool IsAdmin(Client c) => !c.IsBot && (AdminAll || (c.Token.Length > 0 && AdminTokens.Contains(c.Token)));

    ChatContext ChatCtx(Client c) => new(IsAdmin(c), Vehicles?.Vehicles.Select(v => v.Id).ToList() ?? new List<string>(), Items.Items.Select(i => i.Id).ToList());

    /// <summary>Join: tells the client if it is an admin and which vehicles it may name.</summary>
    void SendChatStatus(Client c)
    {
        var ctx = ChatCtx(c);
        Send(c, new EventMsg((byte)EventKind.ChatStatus, ChatEvents.EncodeStatus(new ChatStatus(ctx.IsAdmin, ctx.Vehicles))));
        if (ctx.IsAdmin) GD.Print($"server: {c.Name} #{c.Id} is an admin");
    }

    void ChatTo(Client c, ChatLineKind kind, string text) =>
        Send(c, new EventMsg((byte)EventKind.ChatLine, ChatEvents.EncodeLine(new ChatLine(kind, "", text))));

    void ChatToAll(ChatLineKind kind, string sender, string text) =>
        BroadcastEvent(EventKind.ChatLine, ChatEvents.EncodeLine(new ChatLine(kind, sender, text)));

    void OnChatSend(Client c, byte[] payload)
    {
        if (!c.Joined || ChatEvents.DecodeSend(payload) is not { } text) return;
        if (!c.Chat.TryTake(_elapsed)) { ChatTo(c, ChatLineKind.Error, "You are sending messages too fast."); return; }
        if (text[0] == '/') { RunChatCommand(c, text); return; }
        GD.Print($"server: chat {c.Name}: {text}");
        ChatToAll(ChatLineKind.Player, c.Name, text);
    }

    void RunChatCommand(Client c, string text)
    {
        var parsed = ChatCommands.Parse(text, ChatCtx(c));
        if (!parsed.Ok)
        {
            if (!IsAdmin(c) && parsed.Command is { AdminOnly: true }) GD.Print($"server: {c.Name} #{c.Id} tried {text} without being an admin");
            ChatTo(c, ChatLineKind.Error, parsed.Error ?? "That did not work.");
            return;
        }
        var args = parsed.Args;
        switch (parsed.Command!.Name)
        {
            case "help": ChatHelp(c, args); break;
            case "bots": ChatBots(c, args); break;
            case "spawn": ChatSpawn(c, args[0]); break;
            case "sandbox": ChatSandbox(c, args); break;
            case "give": ChatGive(c, args); break;
            case "time": ChatTimeCommand(c, args); break;
            case "restart": ChatRestart(c, args); break;
            case "event": ChatEvent(c, args); break;
            case "citizens": ChatCitizens(c, args); break;
        }
        GD.Print($"server: {c.Name} #{c.Id} ran {text}");
    }

    void ChatHelp(Client c, IReadOnlyList<string> args)
    {
        var ctx = ChatCtx(c);
        if (args.Count == 1 && ChatCommands.Find(args[0]) is { } one)
        {
            ChatTo(c, ChatLineKind.Info, $"{one.Usage}: {one.Summary}.");
            return;
        }
        ChatTo(c, ChatLineKind.Info, ctx.IsAdmin ? "Commands (Tab completes, Up and Down bring back what you sent):" : "Commands are for admins. You can use:");
        foreach (var cmd in ChatCommands.Visible(ctx)) ChatTo(c, ChatLineKind.Info, $"{cmd.Usage}: {cmd.Summary}.");
    }

    void ChatBots(Client c, IReadOnlyList<string> args)
    {
        var live = _byId.Values.Count(o => o.IsBot);
        if (args.Count == 0)
        {
            ChatTo(c, ChatLineKind.Info, $"Bots: {Bots} wanted, {live} in the match (at most {MaxBots}).");
            return;
        }
        var n = int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
        if (n == Bots) { ChatTo(c, ChatLineKind.Info, $"Bots are already {n}."); return; }
        Bots = n; // BotsTick reads this every tick: bots join and leave from the next one
        if (n > 0 && _nav == null && !_navTried && !ArenaMode && MapDir != "")
        {
            ChatTo(c, ChatLineKind.Info, "Building the bots' navigation for the first time. The server pauses for a few seconds.");
            _host.Flush(); // the notice goes out before the pause
            BuildBotNav();
            ChatTo(c, ChatLineKind.Info, _nav != null ? $"Bot navigation ready in {_nav.BuildSeconds:F1} s." : "Bot navigation failed: the bots will stand where they spawn. See the server log.");
        }
        ChatToAll(ChatLineKind.System, "", $"{c.Name} set the bots to {n}.");
        GD.Print($"server: bots set to {n} by {c.Name} #{c.Id}");
    }

    void ChatSpawn(Client c, string id)
    {
        var kind = Vehicles?.Vehicles.FindIndex(v => v.Id == id) ?? -1;
        if (kind < 0) { ChatTo(c, ChatLineKind.Error, $"Unknown vehicle '{id}'."); return; }
        if (_match.Get(c.Id) is not { Life: PlayerLife.Alive }) { ChatTo(c, ChatLineKind.Error, "You must be alive to spawn a vehicle."); return; }
        if (TrySpawnVehicle(c, (byte)kind, AdminVehicleCap) is { } why) { ChatTo(c, ChatLineKind.Error, why); return; }
        ChatTo(c, ChatLineKind.Info, $"Spawned a {id} in front of you.");
    }

    /// <summary>A human player by name (spaces ignored) or "#id", or null.</summary>
    Client? FindHuman(string who)
    {
        var humans = _byId.Values.Where(o => !o.IsBot && o.Joined).ToList();
        return who.StartsWith('#') && ushort.TryParse(who.AsSpan(1), out var id)
            ? humans.FirstOrDefault(o => o.Id == id)
            : humans.FirstOrDefault(o => o.Name.Replace(" ", "").Equals(who, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"/give item [player]": a new item, with no pickups counted, goes on the player (the admin without a name) if its slot is empty, else into their bag. Both full refuses it. A living player only.</summary>
    void ChatGive(Client c, IReadOnlyList<string> args)
    {
        var index = Items.IndexOf(args[0]);
        if (index < 0) { ChatTo(c, ChatLineKind.Error, $"Unknown item '{args[0]}'."); return; }
        var target = args.Count > 1 ? FindHuman(args[1]) : c;
        if (target == null) { ChatTo(c, ChatLineKind.Error, $"No player '{args[1]}'. Use the name without spaces, or #id."); return; }
        if (_match.Get(target.Id)?.Life != PlayerLife.Alive) { ChatTo(c, ChatLineKind.Error, $"{target.Name} is not alive."); return; }
        var def = Items.Items[index];
        var placed = ItemRules.Place(Items, new ItemStack(index), target.Worn, target.Bag, swap: false, out _);
        if (placed == ItemPlace.NoRoom)
        {
            ChatTo(c, ChatLineKind.Error, target == c ? $"The {def.Name} does not fit: your slot and bag are full." : $"The {def.Name} does not fit: {target.Name}'s slot and bag are full.");
            return;
        }
        SendEquipment(target);
        var worn = placed == ItemPlace.Worn;
        ChatTo(c, ChatLineKind.Info, target == c ? $"You {(worn ? "wear" : "carry")} the {def.Name} now." : $"{target.Name} {(worn ? "wears" : "carries")} the {def.Name} now.");
        if (target != c) ChatTo(target, ChatLineKind.Info, $"{c.Name} gave you the {def.Name}.");
    }

    /// <summary>"/sandbox player [on|off]": a human by name (spaces ignored) or "#id". Without a state it flips.</summary>
    void ChatSandbox(Client c, IReadOnlyList<string> args)
    {
        if (!AllowSandbox) { ChatTo(c, ChatLineKind.Error, "Sandbox mode is not allowed on this server (--allow-sandbox is off)."); return; }
        var who = args[0];
        var target = FindHuman(who);
        if (target == null) { ChatTo(c, ChatLineKind.Error, $"No player '{who}'. Use the name without spaces, or #id."); return; }
        var on = args.Count > 1 ? args[1] == "on" : !target.Sandbox;
        if (target.Sandbox == on) { ChatTo(c, ChatLineKind.Info, $"{target.Name} is already {(on ? "in" : "out of")} sandbox mode."); return; }
        SetSandbox(target, on);
        ChatToAll(ChatLineKind.System, "", $"{c.Name} turned sandbox mode {(on ? "on" : "off")} for {target.Name}.");
    }

    /// <summary>"/restart [ffa|tdm]": a new round now, in the current mode or the named one. Everyone is told which.</summary>
    void ChatRestart(Client c, IReadOnlyList<string> args)
    {
        var mode = args.Count > 0 ? (args[0] == "tdm" ? MatchMode.TeamDeathmatch : MatchMode.FreeForAll) : _match.Mode;
        var changed = mode != _match.Mode;
        _match.Restart(mode);
        var name = mode == MatchMode.TeamDeathmatch ? "team deathmatch" : "free-for-all";
        ChatToAll(ChatLineKind.System, "", changed ? $"{c.Name} restarted the round in {name}." : $"{c.Name} restarted the round.");
        GD.Print($"server: round restarted by {c.Name} #{c.Id}, mode {name}");
    }

    /// <summary>"/event rozhlas": starts the village loudspeaker broadcast on every client now. "/event intel [clear]": the test announcement (<see cref="ChatIntel"/>). Everyone is told who did.</summary>
    void ChatEvent(Client c, IReadOnlyList<string> args)
    {
        var clear = args.Count > 1 && args[1] == "clear";
        if (args.Count > 1 && args[0] != "intel") { ChatTo(c, ChatLineKind.Error, $"/event {args[0]} takes no action. Only /event intel takes clear."); return; }
        switch (args[0]) // the parser only lets known events through
        {
            case "rozhlas":
                BroadcastEvent(EventKind.RozhlasStart, Array.Empty<byte>());
                ChatToAll(ChatLineKind.System, "", $"{c.Name} started the rozhlas.");
                GD.Print($"server: rozhlas started by {c.Name} #{c.Id}");
                break;
            case "intel": ChatIntel(c, clear); break;
        }
    }

    void ChatTimeCommand(Client c, IReadOnlyList<string> args)
    {
        var now = _tick / (double)Wire.TickRate;
        string Shown() => Drycastle.ShooterCore.ChatTime.Format((int)(Clock.HourAt(now) * 60));
        if (args.Count == 0)
        {
            ChatTo(c, ChatLineKind.Info, $"It is {Shown()}{(Clock.Frozen ? ", the clock is frozen" : "")}. A day lasts {Clock.DayLengthSeconds / 60:0.#} minutes.");
            return;
        }
        var set = Drycastle.ShooterCore.ChatTime.Parse(args[0])!.Value;
        string said;
        if (set.Minute is { } m)
        {
            Clock = Clock.WithHour(now, m / 60.0);
            said = $"set the time to {Shown()}";
        }
        else
        {
            Clock = Clock.WithFrozen(now, set.Frozen == true);
            said = set.Frozen == true ? $"froze the time at {Shown()}" : "set the time running again";
        }
        BroadcastEvent(EventKind.ClockSet, ChatEvents.EncodeClock(new ClockChange(Clock, _tick)));
        ChatToAll(ChatLineKind.System, "", $"{c.Name} {said}.");
    }
}
