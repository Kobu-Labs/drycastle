using Godot;
using Drycastle.Shooter.Net.Server.Combat;
using Drycastle.Shooter.Net.Server.Intel;
using Drycastle.Shooter.Net.Server.Parish;
using Drycastle.Shooter.Net.Server.VillageRadio;
using Drycastle.Shooter.Vehicles;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Scenery;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Server.ChatRoom;

/// <summary>
/// The admin's commands that no feature owns (<see cref="ChatServer"/> parses a line, checks who may and calls the handler registered for it): the clock, a test blast, the round restart and
/// the <c>/event</c> command, which starts or ends a kind of event through the event table. Everything an admin changes takes effect on the next tick; the clock goes to every client at once.
/// </summary>
internal sealed class AdminCommands
{
    readonly ServerContext _ctx;
    readonly Func<DayClock> _clock;
    readonly Action<DayClock> _setClock;
    readonly Func<WorldQuery> _query;
    readonly WeaponsFile _weapons;
    readonly RocketIds _rocketIds;
    readonly RozhlasServer _rozhlas;
    readonly IntelServer _intel;
    readonly DemonServer _demon;
    readonly Action<string, ServerClient> _startEvent;
    readonly Action<ServerClient, string> _endCurrentEvent;

    /// <param name="startEvent">Starts the event kind of the table (bigfoot, mass, thieves, ...) for the admin.</param>
    /// <param name="endCurrentEvent">"/event current end" or "cancel": concludes or removes the running event by its kind.</param>
    public AdminCommands(ServerContext ctx, Func<DayClock> clock, Action<DayClock> setClock, Func<WorldQuery> query, WeaponsFile weapons, RocketIds rocketIds,
        RozhlasServer rozhlas, IntelServer intel, DemonServer demon, Action<string, ServerClient> startEvent, Action<ServerClient, string> endCurrentEvent)
    {
        _ctx = ctx; _clock = clock; _setClock = setClock; _query = query; _weapons = weapons; _rocketIds = rocketIds;
        _rozhlas = rozhlas; _intel = intel; _demon = demon; _startEvent = startEvent; _endCurrentEvent = endCurrentEvent;
    }

    /// <summary>
    /// "/explode size distance [speed]" (#345, a test of the blast's look): the explosion event of a rocket nobody fired, of that radius, on the first surface under the point
    /// that far ahead of the player (their own height when there is none). Clients show the blast, its sound and its dust; no damage, glass or knockback is worked out.
    /// The speed is the one its Blast wave leaves at (the dust's own without it).
    /// </summary>
    public void Explode(ServerClient c, IReadOnlyList<string> args)
    {
        var size = int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
        var distance = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        var speed = args.Count > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 0;
        var yaw = c.Body.Yaw;
        var at = c.Body.GlobalPosition + new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw)) * distance;
        if (_query().Ground(MoverKind.Player, at, 60f, 200f) is { } ground) at = ground.Position;
        var weapon = Math.Max(0, _weapons?.Weapons.FindIndex(w => w.Kind == WeaponKind.Projectile) ?? 0); // the sound of the blast: the rpg's
        var id = _rocketIds.Next();
        _ctx.Broadcast(EventKind.Explosion, WeaponCodec.Encode(new ExplosionMsg(id, c.Id, (byte)weapon, at.ToNumerics(), size, speed)));
        _ctx.ChatTo(c, ChatLineKind.Info, speed > 0 ? $"A {size} m blast {distance} m in front of you, its wave at {speed} m/s." : $"A {size} m blast {distance} m in front of you.");
    }

    /// <summary>"/restart [ffa|tdm]": a new round now, in the current mode or the named one. Everyone is told which.</summary>
    public void Restart(ServerClient c, IReadOnlyList<string> args)
    {
        var match = _ctx.Match;
        var mode = args.Count > 0 ? (args[0] == "tdm" ? MatchMode.TeamDeathmatch : MatchMode.FreeForAll) : match.Mode;
        var changed = mode != match.Mode;
        match.Restart(mode);
        var name = mode == MatchMode.TeamDeathmatch ? "team deathmatch" : "free-for-all";
        _ctx.ChatTo(c, ChatLineKind.Info, changed ? $"{c.Name} restarted the round in {name}." : $"{c.Name} restarted the round.");
        GD.Print($"server: round restarted by {c.Name} #{c.Id}, mode {name}");
    }

    /// <summary>
    /// "/event rozhlas": starts a detour broadcast of the village loudspeaker now (<see cref="RozhlasServer.ChatEvent"/>). "/event intel [clear]": the test announcement (<see cref="IntelServer.Chat"/>).
    /// "/event bigfoot", "mass" and "thieves" start that kind through the event table; "/event current end" concludes the running
    /// event as a win (for testing) and "/event current cancel" removes it with no reward, by its kind. Everyone is told who did.
    /// </summary>
    public void Event(ServerClient c, IReadOnlyList<string> args)
    {
        var action = args.Count > 1 ? args[1] : "";
        var allowed = args[0] switch { "intel" => "clear", "current" => "end or cancel", _ => "" }; // the parser only lets known events and actions through; whether they belong together is checked here
        if (args[0] == "current") { _endCurrentEvent(c, action); return; }
        if (action != "" && (args[0] != "intel" || action != "clear"))
        {
            _ctx.ChatTo(c, ChatLineKind.Error, allowed == "" ? $"/event {args[0]} takes no action." : $"/event {args[0]} takes {allowed}.");
            return;
        }
        switch (args[0])
        {
            case "rozhlas": _rozhlas.ChatEvent(c); break;
            case "intel": _intel.Chat(c, action == "clear"); break;
            case "demon": _demon.Chat(c); break;
            default: _startEvent(args[0], c); break; // bigfoot, mass, thieves: the kinds of the handler table
        }
    }

    /// <summary>"/time": tells the time; with an argument sets the hour, or freezes or releases the clock. The new clock goes to every client.</summary>
    public void Time(ServerClient c, IReadOnlyList<string> args)
    {
        var clock = _clock();
        var now = _ctx.Tick / (double)Wire.TickRate;
        string Shown() => ChatTime.Format((int)(_clock().HourAt(now) * 60));
        if (args.Count == 0)
        {
            _ctx.ChatTo(c, ChatLineKind.Info, $"It is {Shown()}{(clock.Frozen ? ", the clock is frozen" : "")}. A day lasts {clock.DayLengthSeconds / 60:0.#} minutes.");
            return;
        }
        var set = ChatTime.Parse(args[0])!.Value;
        string said;
        if (set.Minute is { } m)
        {
            _setClock(clock.WithHour(now, m / 60.0));
            said = $"set the time to {Shown()}";
        }
        else
        {
            _setClock(clock.WithFrozen(now, set.Frozen == true));
            said = set.Frozen == true ? $"froze the time at {Shown()}" : "set the time running again";
        }
        _ctx.Broadcast(EventKind.ClockSet, ChatEvents.EncodeClock(new ClockChange(_clock(), _ctx.Tick)));
        _ctx.ChatTo(c, ChatLineKind.Info, $"{c.Name} {said}.");
    }
}
