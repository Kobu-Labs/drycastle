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

    bool IsAdmin(ServerClient c) => !c.IsBot && (AdminAll || (c.Token.Length > 0 && AdminTokens.Contains(c.Token)));

    ChatContext ChatCtx(ServerClient c) => new(IsAdmin(c), Vehicles?.Vehicles.Select(v => v.Id).ToList() ?? new List<string>(), Items.Items.Select(i => i.Id).ToList(), CompanionData.Companions.Select(i => i.Id).ToList());

    /// <summary>Join: tells the client if it is an admin and which vehicles it may name.</summary>
    void SendChatStatus(ServerClient c)
    {
        var ctx = ChatCtx(c);
        Send(c, new EventMsg((byte)EventKind.ChatStatus, ChatEvents.EncodeStatus(new ChatStatus(ctx.IsAdmin, ctx.Vehicles))));
        if (ctx.IsAdmin) GD.Print($"server: {c.Name} #{c.Id} is an admin");
    }

    void ChatTo(ServerClient c, ChatLineKind kind, string text) => _ctx.ChatTo(c, kind, text);

    void ChatToAll(ChatLineKind kind, string sender, string text) => _ctx.ChatToAll(kind, sender, text);

    void OnChatSend(ServerClient c, byte[] payload)
    {
        if (!c.Joined || ChatEvents.DecodeSend(payload) is not { } text) return;
        if (!c.Chat.TryTake(_elapsed)) { ChatTo(c, ChatLineKind.Error, "You are sending messages too fast."); return; }
        if (text[0] == '/') { RunChatCommand(c, text); return; }
        GD.Print($"server: chat {c.Name}: {text}");
        ChatToAll(ChatLineKind.Player, c.Name, text);
    }

    void RunChatCommand(ServerClient c, string text)
    {
        var parsed = ChatCommands.Parse(text, ChatCtx(c));
        if (!parsed.Ok)
        {
            if (!IsAdmin(c) && parsed.Command is { AdminOnly: true }) GD.Print($"server: {c.Name} #{c.Id} tried {text} without being an admin");
            ChatTo(c, ChatLineKind.Error, parsed.Error ?? "That did not work.");
            return;
        }
        if (parsed.Command!.ClientSide) { ChatTo(c, ChatLineKind.Info, $"/{parsed.Command.Name} runs on your own client."); return; } // a modified client sent it: nothing to do here
        var args = parsed.Args;
        switch (parsed.Command!.Name)
        {
            case "help": ChatHelp(c, args); break;
            case "bots": ChatBots(c, args); break;
            case "spawn": ChatSpawn(c, args[0]); break;
            case "unicorn": ChatUnicorn(c); break;
            case "phoenix": ChatPhoenix(c); break;
            case "sandbox": ChatSandbox(c, args); break;
            case "give": ChatGive(c, args); break;
            case "companion": ChatCompanion(c, args); break;
            case "time": ChatTimeCommand(c, args); break;
            case "explode": ChatExplode(c, args); break;
            case "restart": ChatRestart(c, args); break;
            case "event": ChatEvent(c, args); break;
            case "citizens": ChatCitizens(c, args); break;
            case "church": ChatChurch(c, args); break;
            case "rozhlas": _rozhlas.ChatCommand(c, args); break;
            case "status": ChatStatus(c, args); break;
            case "scud": ChatScud(c, args); break;
            case "squad": ChatSquad(c, args); break;
        }
        GD.Print($"server: {c.Name} #{c.Id} ran {text}");
    }

    void ChatHelp(ServerClient c, IReadOnlyList<string> args)
    {
        var ctx = ChatCtx(c);
        if (args.Count == 1 && ChatCommands.Find(args[0]) is { } one)
        {
            ChatTo(c, ChatLineKind.Info, $"{one.Usage}: {one.Summary}.");
            return;
        }
        ChatTo(c, ChatLineKind.Info, ctx.IsAdmin ? "Commands (Tab completes, Up and Down bring back what you sent):" : "Most commands are for admins. You can use:");
        foreach (var cmd in ChatCommands.Visible(ctx)) ChatTo(c, ChatLineKind.Info, $"{cmd.Usage}: {cmd.Summary}.");
    }

    void ChatBots(ServerClient c, IReadOnlyList<string> args)
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

    void ChatSpawn(ServerClient c, string id)
    {
        var kind = Vehicles?.Vehicles.FindIndex(v => v.Id == id) ?? -1;
        if (kind < 0) { ChatTo(c, ChatLineKind.Error, $"Unknown vehicle '{id}'."); return; }
        if (_match.Get(c.Id) is not { Life: PlayerLife.Alive }) { ChatTo(c, ChatLineKind.Error, "You must be alive to spawn a vehicle."); return; }
        if (TrySpawnVehicle(c, (byte)kind, AdminVehicleCap) is { } why) { ChatTo(c, ChatLineKind.Error, why); return; }
        ChatTo(c, ChatLineKind.Info, $"Spawned a {id} in front of you.");
    }

    /// <summary>"/phoenix" (admins, a test): a Phoenix now, whatever the hour (<see cref="WildlifeKeeper.SpawnPhoenix"/>); the night counter is not touched, so the scheduled one still comes.</summary>
    void ChatPhoenix(ServerClient c)
    {
        if (_wildlife == null || !_wildlife.Active) { ChatTo(c, ChatLineKind.Error, "There is no wildlife in this round."); return; }
        if (_wildlife.SpawnPhoenix(_wildlifeGround ??= new WildlifeGround(this))) ChatTo(c, ChatLineKind.Info, "A Phoenix flies over the map.");
        else ChatTo(c, ChatLineKind.Error, "No Phoenix: wildlife.json has none, or the map is full.");
    }

    /// <summary>"/unicorn" (admins, a test): an extra Unicorn on the ground 50 m in front of the player, past its flee_radius (40 m, animals.json) so it is not startled (see <see cref="WildlifeKeeper.SpawnExtra"/>).</summary>
    void ChatUnicorn(ServerClient c)
    {
        if (_wildlife == null) { ChatTo(c, ChatLineKind.Error, "There is no wildlife on this map."); return; }
        if (_match.Get(c.Id) is not { Life: PlayerLife.Alive }) { ChatTo(c, ChatLineKind.Error, "You must be alive to spawn a Unicorn."); return; }
        var yaw = c.Body.Yaw;
        var at = c.Body.GlobalPosition + new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw)) * 50f;
        if (Query.Ground(MoverKind.Player, at, 60f, 200f) is { } ground) at = ground.Position;
        if (_wildlife.SpawnExtra(Mysteries.Unicorn, N(at), _wildlifeGround ??= new WildlifeGround(this)))
            ChatTo(c, ChatLineKind.Info, "A Unicorn stands in front of you.");
        else ChatTo(c, ChatLineKind.Error, "No room for a Unicorn here (no ground, or the map is full).");
    }

    /// <summary>
    /// "/explode size distance [speed]" (#345, a test of the blast's look): the explosion event of a rocket nobody fired, of that radius, on the first surface under the point
    /// that far ahead of the player (their own height when there is none). Clients show the blast, its sound and its dust; no damage, glass or knockback is worked out.
    /// The speed is the one its Blast wave leaves at (the dust's own without it).
    /// </summary>
    void ChatExplode(ServerClient c, IReadOnlyList<string> args)
    {
        var size = int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
        var distance = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        var speed = args.Count > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 0;
        var yaw = c.Body.Yaw;
        var at = c.Body.GlobalPosition + new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw)) * distance;
        if (Query.Ground(MoverKind.Player, at, 60f, 200f) is { } ground) at = ground.Position;
        var weapon = Math.Max(0, Weapons?.Weapons.FindIndex(w => w.Kind == WeaponKind.Projectile) ?? 0); // the sound of the blast: the rpg's
        var id = _nextRocket;
        _nextRocket = _nextRocket == ushort.MaxValue ? (ushort)0x8000 : (ushort)(_nextRocket + 1);
        BroadcastEvent(EventKind.Explosion, WeaponCodec.Encode(new ExplosionMsg(id, c.Id, (byte)weapon, N(at), size, speed)));
        ChatTo(c, ChatLineKind.Info, speed > 0 ? $"A {size} m blast {distance} m in front of you, its wave at {speed} m/s." : $"A {size} m blast {distance} m in front of you.");
    }

    /// <summary>A human player by name (spaces ignored) or "#id", or null.</summary>
    ServerClient? FindHuman(string who)
    {
        var humans = _byId.Values.Where(o => !o.IsBot && o.Joined).ToList();
        return who.StartsWith('#') && ushort.TryParse(who.AsSpan(1), out var id)
            ? humans.FirstOrDefault(o => o.Id == id)
            : humans.FirstOrDefault(o => o.Name.Replace(" ", "").Equals(who, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"/give item [player]": a new item, with no pickups counted, goes on the player (the admin without a name) if its slot is empty, else into their bag. Both full refuses it. A living player only.</summary>
    void ChatGive(ServerClient c, IReadOnlyList<string> args)
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
    void ChatSandbox(ServerClient c, IReadOnlyList<string> args)
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

    /// <summary>An admin's change from the Teams screen (#312, <see cref="TeamAssign"/>). Anyone else is ignored (and logged). Everyone is told what was done.</summary>
    void OnTeamAssign(ServerClient c, byte[] payload)
    {
        if (TeamAssignCodec.Decode(payload) is not { } a) return;
        if (!IsAdmin(c)) { GD.Print($"server: {c.Name} #{c.Id} sent a team change without being an admin"); return; }
        // a sandbox player dropped on a team: sandbox mode ends first (it has no team), then the move
        if (a is { Action: TeamAction.Move, Team: Teams.A or Teams.B } && _match.IsTeamMode && _byId.TryGetValue(a.Id, out var target) && target.Sandbox) SetSandbox(target, false);
        if (TeamAssignRules.Apply(_match, a, c.Name) is not { } line) return;
        ChatToAll(ChatLineKind.System, "", line);
        GD.Print($"server: {c.Name} #{c.Id} team {a.Action} {a.Id} {a.Team}");
    }

    /// <summary>A player's squad request from the Squads screen (#379, <see cref="SquadAssign"/>). The scoreboard carries the result; a refusal comes back as a chat line.</summary>
    void OnSquadAssign(ServerClient c, byte[] payload)
    {
        if (SquadAssignCodec.Decode(payload) is not { } a) return;
        RunSquad(c, a);
        GD.Print($"server: {c.Name} #{c.Id} squad {a.Action} {a.Squad} '{a.Name}'");
    }

    /// <summary>"/squad create [name] | join name-or-number | leave | list" (#379): the same requests as the Squads screen.</summary>
    void ChatSquad(ServerClient c, IReadOnlyList<string> args)
    {
        var name = args.Count > 1 ? args[1] : "";
        switch (args[0])
        {
            case "list": foreach (var line in SquadRules.List(_match, c.Id)) ChatTo(c, ChatLineKind.Info, line); break;
            case "create": RunSquad(c, new SquadAssign(SquadAction.Create, 0, name)); break;
            case "leave": RunSquad(c, new SquadAssign(SquadAction.Leave)); break;
            case "join":
                if (name == "") { ChatTo(c, ChatLineKind.Error, "Say which squad: /squad join <name or number>. /squad list shows them."); break; }
                if (SquadRules.Find(_match, c.Id, name) is var squad && squad == 0) { ChatTo(c, ChatLineKind.Error, $"Your team has no squad '{name}'. /squad list shows them."); break; }
                RunSquad(c, new SquadAssign(SquadAction.Join, squad));
                break;
        }
    }

    void RunSquad(ServerClient c, SquadAssign a)
    {
        if (!c.Joined || c.IsBot) return;
        var reply = SquadRules.Apply(_match, c.Id, a);
        ChatTo(c, reply.Ok ? ChatLineKind.Info : ChatLineKind.Error, reply.Text);
        foreach (var (id, text) in reply.Tell) if (_byId.TryGetValue(id, out var mate)) ChatTo(mate, ChatLineKind.Info, text);
    }

    /// <summary>"/restart [ffa|tdm]": a new round now, in the current mode or the named one. Everyone is told which.</summary>
    void ChatRestart(ServerClient c, IReadOnlyList<string> args)
    {
        var mode = args.Count > 0 ? (args[0] == "tdm" ? MatchMode.TeamDeathmatch : MatchMode.FreeForAll) : _match.Mode;
        var changed = mode != _match.Mode;
        _match.Restart(mode);
        var name = mode == MatchMode.TeamDeathmatch ? "team deathmatch" : "free-for-all";
        ChatToAll(ChatLineKind.System, "", changed ? $"{c.Name} restarted the round in {name}." : $"{c.Name} restarted the round.");
        GD.Print($"server: round restarted by {c.Name} #{c.Id}, mode {name}");
    }

    /// <summary>
    /// "/event rozhlas": starts a detour broadcast of the village loudspeaker now (<see cref="RozhlasServer.ChatEvent"/>). "/event intel [clear]": the test announcement (<see cref="IntelServer.Chat"/>).
    /// "/event bigfoot", "mass" and "thieves" start that kind through the handler table (<see cref="InitEvents"/>); "/event current end" concludes the running
    /// event as a win (for testing) and "/event current cancel" removes it with no reward, by its kind (<see cref="ChatCurrentEvent"/>). Everyone is told who did.
    /// </summary>
    void ChatEvent(ServerClient c, IReadOnlyList<string> args)
    {
        var action = args.Count > 1 ? args[1] : "";
        var allowed = args[0] switch { "intel" => "clear", "current" => "end or cancel", _ => "" }; // the parser only lets known events and actions through; whether they belong together is checked here
        if (args[0] == "current") { ChatCurrentEvent(c, action); return; }
        if (action != "" && (args[0] != "intel" || action != "clear"))
        {
            ChatTo(c, ChatLineKind.Error, allowed == "" ? $"/event {args[0]} takes no action." : $"/event {args[0]} takes {allowed}.");
            return;
        }
        switch (args[0])
        {
            case "rozhlas":
                _rozhlas.ChatEvent(c);
                break;
            case "intel": _intel.Chat(c, action == "clear"); break;
            case "demon": ChatDemon(c); break;
            default: _eventHandlers.Start(args[0], c); break; // bigfoot, mass, thieves: the kinds of the handler table
        }
    }

    void ChatTimeCommand(ServerClient c, IReadOnlyList<string> args)
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
