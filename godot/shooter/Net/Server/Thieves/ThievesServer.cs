using Godot;
using Drycastle.Shooter.Net.Bots;
using Drycastle.Shooter.Net.Server.Intel;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Server.Thieves;

/// <summary>
/// The Thieves event on the server (GLOSSARY "Thieves event", "Raid"; the design is in docs/events/README.md, the tickets in docs/events/thieves/). An admin starts it
/// with <c>/event thieves</c>; <see cref="ThievesEvent"/> keeps its phase and its wave of vans and this class puts it in the world: it starts (the book, a chat line, the intel entry and
/// the Event announcement), launches the convoy's vans as they fall due (<see cref="RaidServer"/>) and is won when every van of the wave is gone. <c>/event current end</c>
/// wins it at once, <c>/event current cancel</c> removes it with no reward (both remove every van and crew), a new round clears it. It cannot be lost, and it runs to its end with no one
/// near. The parts are <see cref="GangServer"/> (the members), <see cref="RaidServer"/> (the vans), <see cref="LootServer"/> (what they steal) and <see cref="ThievesPayout"/> (the reward).
/// </summary>
internal sealed class ThievesServer
{
    readonly ServerContext _ctx;
    readonly ThievesFile _data;
    readonly EventBook _events;
    readonly IntelServer _intel;
    readonly Random _rng;
    readonly Func<BotNav?> _nav;
    readonly GangServer _gangs;
    readonly RaidServer _raids;
    readonly LootServer _loot;
    readonly ThievesPayout _payout;
    readonly ThievesEvent _event;

    /// <summary>The parts are made in this order by the caller (the Gang, the Raids, the Loot, the reward: each registers its world entity type and the reward wraps the hit routing).</summary>
    public ThievesServer(ServerContext ctx, ThievesFile data, EventBook events, IntelServer intel, Random rng, Func<BotNav?> nav, GangServer gangs, RaidServer raids, LootServer loot, ThievesPayout payout, ThievesEvent phase)
    {
        _ctx = ctx; _data = data; _events = events; _intel = intel; _rng = rng; _nav = nav; _gangs = gangs; _raids = raids; _loot = loot; _payout = payout; _event = phase;
    }

    /// <summary>"/event thieves": the run begins, the intel board and the village are told.</summary>
    public void Start(ServerClient admin)
    {
        if (_events.WhyNotStart(EventBook.Thieves) is { } busy) { _ctx.ChatTo(admin, ChatLineKind.Error, busy); return; }
        if (_ctx.Match.Phase != MatchPhase.Running) { _ctx.ChatTo(admin, ChatLineKind.Error, "No round is running."); return; }
        if (_nav() == null) { _ctx.ChatTo(admin, ChatLineKind.Error, "The Thieves need the navigation mesh, which is not built (no map, or it failed: see the server log)."); return; }
        if (_raids.NoRoads) { _ctx.ChatTo(admin, ChatLineKind.Error, "The map has no roads (roads.json) for the Raid vans."); return; }
        ClearCrews(); // what a run before left (wrecks, crews that fled) and the stops it used
        _events.Start(EventBook.Thieves, _ctx.Now);
        _raids.Admin = admin.Id;
        _event.Begin(_data.Raids, _rng); // the first van of the convoy is due on the next tick
        _intel.Announce(ThievesEvent.Intel(_ctx.Now));
        _ctx.ChatTo(admin, ChatLineKind.Info, $"{admin.Name} started the Thieves event. Thieves are coming to the village.");
        GD.Print($"server: Thieves event started by {admin.Name} #{admin.Id}");
    }

    /// <summary>Once a tick (after the Mass): the routes the Gang members wait for, the Raids (their crews, their vans' contact with the world), the voice lines and the wave: the next van
    /// when it is due, and the win once every van is gone. What the Thieves steal runs in their members (<see cref="RaidWork"/>).</summary>
    public void Tick(float dt)
    {
        _gangs.RouteTick();
        _raids.Tick(dt);
        _loot.ScanVehicles(dt);
        _raids.Contacts();
        _gangs.AudioTick(dt);
        if (!_event.Running) return;
        switch (_event.Tick(dt, _raids.Active))
        {
            case ThievesStep.Launch: _raids.Launch(_loot.Places); break;
            case ThievesStep.Won: Finish(true, null); break;
        }
    }

    /// <summary>"/event current end" while the Thieves event runs: a win, for testing.</summary>
    public void End(ServerClient c)
    {
        if (!_event.Running) { _ctx.ChatTo(c, ChatLineKind.Info, "No event is running."); return; }
        Finish(true, c);
    }

    /// <summary>"/event current cancel" while the Thieves event runs: over, no reward.</summary>
    public void Cancel(ServerClient c)
    {
        if (!_event.Running) { _ctx.ChatTo(c, ChatLineKind.Info, "No event is running."); return; }
        Finish(false, c);
    }

    /// <summary>
    /// The run is over: off the intel board, the phase is Over and the book records the outcome. <paramref name="by"/> is the admin who ended it, null when it ended by
    /// itself. When it ended by itself every van is gone already (the crews that fled run on to the edge); an admin's end or cancel removes what is left.
    /// </summary>
    void Finish(bool won, ServerClient? by)
    {
        _event.Finish();
        if (by != null || !won) ClearCrews(); // an admin's end or cancel removes every van and crew; a win by itself leaves what is still fleeing to run off
        if (!won) _loot.Reset(); // a cancelled event never happened: everything taken goes back (a win keeps what was stolen, and lying Loot stays returnable)
        _intel.Withdraw(ThievesEvent.IntelId);
        _events.End(EventBook.Thieves, won ? EventPhase.Won : EventPhase.Despawned, _ctx.Now);
        if (by != null) _ctx.ChatTo(by, ChatLineKind.Info, won ? "You ended the Thieves event as a win." : "You cancelled the Thieves event.");
        _payout.Settle(won);
        GD.Print($"server: Thieves event {(won ? "won" : "cancelled")}{(by == null ? "" : $" by {by}")}");
    }

    /// <summary>Join: what a joiner needs to see of a running Thieves event. The vans and crews are world entities, which a joiner is sent with the rest of the world (the intel board is sent by the intel server); the dishes taken are not.</summary>
    public void SendOnJoin(ServerClient c) => _loot.SendOnJoin(c);

    /// <summary>A new round clears the event (the world's entities went with it): a running run is despawned without a reward, the phase is Idle. What it stole is put back.</summary>
    public void RoundReset()
    {
        var wasRunning = _event.Running;
        _event.Reset();
        ClearCrews();
        _loot.Reset();
        _payout.Clear();
        if (!wasRunning) return;
        _intel.Withdraw(ThievesEvent.IntelId);
        _events.End(EventBook.Thieves, EventPhase.Despawned, _ctx.Now);
    }

    /// <summary>Every Raid goes, vans and members, the dead too.</summary>
    void ClearCrews() { _raids.Clear(); _gangs.Roster.Clear(); }

    // ---- what the rest of the server asks ----

    public void Prepare() => _gangs.Prepare();

    public bool DeliverRoute(long ticket, IReadOnlyList<NVec3> route) => _gangs.DeliverRoute(ticket, route);

    /// <summary>The Gang members and the vans as hit targets; with a view tick: where the shooter saw them.</summary>
    public void AddTargets(List<HitTarget> into, uint? view = null)
    {
        _gangs.AddTargets(into, view);
        _raids.AddTargets(into, view);
    }

    /// <summary>Gang members and vans run and drive: each keeps a ring of poses for the lag compensation.</summary>
    public void RecordPoses(uint tick)
    {
        _gangs.RecordPoses(tick);
        _raids.RecordPoses(tick);
    }

    public void PlaceVanSolids() => _raids.PlaceSolids();

    public void ShoveCarsByVans() => _raids.ShoveCars();

    public void RoadkillCrew(ushort hitId) => _raids.RoadkillCrew(hitId);

    public ushort? LootInReach(ServerClient c) => _loot.InReach(c);

    /// <summary>Every input of a player: the use key's hold on lying Loot, which sends it back (the player is credited while the event runs).</summary>
    public void OnUse(ServerClient c, bool useDown)
    {
        if (!_loot.Use(c, useDown)) return;
        if (_event.Running) _payout.NoteLootReturned(c.Id);
        GD.Print($"server: {c.Name} #{c.Id} sent Loot back");
    }
}
