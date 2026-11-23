using Godot;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Weapons;
using Drycastle.ShooterCore.Wildlife;

namespace Drycastle.Shooter.Net.Server;

/// <summary>
/// The admin's commands that are not yet a feature's own (<see cref="ChatServer"/> parses a line, checks who may and calls the handler registered here), and the admin's team and squad changes.
/// Everything an admin changes takes effect on the next tick: <see cref="Bots"/> is read each tick by <see cref="BotsTick"/>, the clock goes to every client at once.
/// </summary>
public partial class NetServer
{
    /// <summary>Player tokens (lower case compare) of the admins, from --admin-token. A guest (no token) is never an admin.</summary>
    public HashSet<string> AdminTokens = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>--admin-all (dev servers only): every human is an admin, so nobody has to look up a token. Release servers never pass it.</summary>
    public bool AdminAll;

    /// <summary>Hands each admin command to the chat; the commands of a feature that has its own class register themselves there instead.</summary>
    void RegisterChatCommands()
    {
        _chat.Register("bots", ChatBots);
        _chat.Register("spawn", (c, args) => ChatSpawn(c, args[0]));
        _chat.Register("unicorn", _wildlife.ChatUnicorn);
        _chat.Register("phoenix", _wildlife.ChatPhoenix);
        _chat.Register("sandbox", ChatSandbox);
        _chat.Register("give", ChatGive);
        _chat.Register("companion", _companions.ChatCommand);
        _chat.Register("time", ChatTimeCommand);
        _chat.Register("explode", ChatExplode);
        _chat.Register("restart", ChatRestart);
        _chat.Register("event", ChatEvent);
        _chat.Register("citizens", _citizens.Chat);
        _chat.Register("church", _church.Chat);
        _chat.Register("rozhlas", _rozhlas.ChatCommand);
        _chat.Register("status", _status.ChatCommand);
        _chat.Register("scud", ChatScud);
        _chat.Register("squad", ChatSquad);
    }

    void ChatTo(ServerClient c, ChatLineKind kind, string text) => _ctx.ChatTo(c, kind, text);

    void ChatToAll(ChatLineKind kind, string sender, string text) => _ctx.ChatToAll(kind, sender, text);

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
        var id = _rocketIds.Next();
        BroadcastEvent(EventKind.Explosion, WeaponCodec.Encode(new ExplosionMsg(id, c.Id, (byte)weapon, N(at), size, speed)));
        ChatTo(c, ChatLineKind.Info, speed > 0 ? $"A {size} m blast {distance} m in front of you, its wave at {speed} m/s." : $"A {size} m blast {distance} m in front of you.");
    }

    /// <summary>"/give item [player]": a new item, with no pickups counted, goes on the player (the admin without a name) if its slot is empty, else into their bag. Both full refuses it. A living player only.</summary>
    void ChatGive(ServerClient c, IReadOnlyList<string> args)
    {
        var index = Items.IndexOf(args[0]);
        if (index < 0) { ChatTo(c, ChatLineKind.Error, $"Unknown item '{args[0]}'."); return; }
        var target = args.Count > 1 ? _ctx.FindHuman(args[1]) : c;
        if (target == null) { ChatTo(c, ChatLineKind.Error, $"No player '{args[1]}'. Use the name without spaces, or #id."); return; }
        if (_match.Get(target.Id)?.Life != PlayerLife.Alive) { ChatTo(c, ChatLineKind.Error, $"{target.Name} is not alive."); return; }
        var def = Items.Items[index];
        var placed = ItemRules.Place(Items, new ItemStack(index), target.Worn, target.Bag, swap: false, out _);
        if (placed == ItemPlace.NoRoom)
        {
            ChatTo(c, ChatLineKind.Error, target == c ? $"The {def.Name} does not fit: your slot and bag are full." : $"The {def.Name} does not fit: {target.Name}'s slot and bag are full.");
            return;
        }
        _gear.SendEquipment(target);
        var worn = placed == ItemPlace.Worn;
        ChatTo(c, ChatLineKind.Info, target == c ? $"You {(worn ? "wear" : "carry")} the {def.Name} now." : $"{target.Name} {(worn ? "wears" : "carries")} the {def.Name} now.");
        if (target != c) ChatTo(target, ChatLineKind.Info, $"{c.Name} gave you the {def.Name}.");
    }

    /// <summary>"/sandbox player [on|off]": a human by name (spaces ignored) or "#id". Without a state it flips.</summary>
    void ChatSandbox(ServerClient c, IReadOnlyList<string> args)
    {
        if (!AllowSandbox) { ChatTo(c, ChatLineKind.Error, "Sandbox mode is not allowed on this server (--allow-sandbox is off)."); return; }
        var who = args[0];
        var target = _ctx.FindHuman(who);
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
        if (!_chat.IsAdmin(c)) { GD.Print($"server: {c.Name} #{c.Id} sent a team change without being an admin"); return; }
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
            case "demon": _demon.Chat(c); break;
            default: _eventHandlers.Start(args[0], c); break; // bigfoot, mass, thieves: the kinds of the handler table
        }
    }

    void ChatTimeCommand(ServerClient c, IReadOnlyList<string> args)
    {
        var now = _tick / (double)Wire.TickRate;
        string Shown() => Drycastle.ShooterCore.Chat.ChatTime.Format((int)(Clock.HourAt(now) * 60));
        if (args.Count == 0)
        {
            ChatTo(c, ChatLineKind.Info, $"It is {Shown()}{(Clock.Frozen ? ", the clock is frozen" : "")}. A day lasts {Clock.DayLengthSeconds / 60:0.#} minutes.");
            return;
        }
        var set = Drycastle.ShooterCore.Chat.ChatTime.Parse(args[0])!.Value;
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
