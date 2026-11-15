using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The Thieves event on the server (GLOSSARY "Thieves event", "Raid"; the design is in docs/events/README.md, the tickets in docs/events/thieves/). An admin starts it
/// with <c>/event thieves</c>; <see cref="ThievesEvent"/> keeps its phase and its wave of vans and this file puts it in the world: it starts (the book, a chat line, the intel entry and
/// the Event announcement), launches the convoy's vans as they fall due (<see cref="LaunchRaid"/>, <c>NetServer.Thieves.Raid.cs</c>) and is won when every van of the wave is gone. <c>/event current end</c>
/// wins it at once, <c>/event current cancel</c> removes it with no reward (both remove every van and crew), a new round clears it. It cannot be lost, and it runs to its end with no one
/// near. The Loot plugs in at the marked places (<see cref="ThievesTick"/>, <see cref="ThievesFinish"/>, <see cref="SendThievesOnJoin"/>, <see cref="ThievesRoundReset"/>).
/// </summary>
public partial class NetServer
{
    /// <summary>thieves.json: every number of the event (set by the host).</summary>
    public ThievesFile ThievesData = new();

    ThievesEvent? _thieves;

    /// <summary>The Thieves event's phase; null until <c>_Ready</c>.</summary>
    public ThievesEvent? Thieves => _thieves;

    /// <summary>Called once from <c>_Ready</c>. The Gang members' and the Raid vans' world entity types register here.</summary>
    void InitThieves()
    {
        _thieves = new ThievesEvent();
        InitGangs();
        InitRaids();
        InitLoot();
        InitThievesReward();
    }

    /// <summary>"/event thieves": the run begins, the intel board and the village are told.</summary>
    void StartThieves(Client admin)
    {
        if (_events.WhyNotStart(EventBook.Thieves) is { } busy) { ChatTo(admin, ChatLineKind.Error, busy); return; }
        if (_match.Phase != MatchPhase.Running) { ChatTo(admin, ChatLineKind.Error, "No round is running."); return; }
        if (_nav == null) { ChatTo(admin, ChatLineKind.Error, "The Thieves need the navigation mesh, which is not built (no map, or it failed: see the server log)."); return; }
        if (RaidRoads.IsEmpty) { ChatTo(admin, ChatLineKind.Error, "The map has no roads (roads.json) for the Raid vans."); return; }
        ClearGangs(); // what a run before left (wrecks, crews that fled) and the stops it used
        _events.Start(EventBook.Thieves, _elapsed);
        _thievesAdmin = admin.Id;
        _thieves!.Begin(ThievesData.Raids, _rng); // the first van of the convoy is due on the next tick
        AnnounceIntel(ThievesEvent.Intel(_elapsed));
        ChatToAll(ChatLineKind.System, "", $"{admin.Name} started the Thieves event. Thieves are coming to the village.");
        GD.Print($"server: Thieves event started by {admin.Name} #{admin.Id}");
    }

    /// <summary>
    /// Once a tick (after <see cref="MassTick"/>): the routes the Gang members wait for, the Raids (their crews, their vans' contact with the world) and the wave: the next van
    /// when it is due, and the win once every van is gone. What the Thieves steal runs in their members (<see cref="RaidWork"/>).
    /// </summary>
    void ThievesTick(float dt)
    {
        GangsTick();
        RaidsTick(dt);
        GangAudioTick(dt); // the voice lines (ticket 09)
        if (_thieves is not { Running: true }) return;
        switch (_thieves.Tick(dt, _raids?.Active ?? 0))
        {
            case ThievesStep.Launch: LaunchRaid(); break;
            case ThievesStep.Won: ThievesFinish(true, null); break;
        }
    }

    /// <summary>"/event current end" while the Thieves event runs: a win, for testing.</summary>
    void EndThieves(Client c)
    {
        if (_thieves is not { Running: true }) { ChatTo(c, ChatLineKind.Info, "No event is running."); return; }
        ThievesFinish(true, c.Name);
    }

    /// <summary>"/event current cancel" while the Thieves event runs: over, no reward.</summary>
    void CancelThieves(Client c)
    {
        if (_thieves is not { Running: true }) { ChatTo(c, ChatLineKind.Info, "No event is running."); return; }
        ThievesFinish(false, c.Name);
    }

    /// <summary>
    /// The run is over: off the intel board, the phase is Over and the book records the outcome. <paramref name="by"/> is the admin who ended it, null when it ended by
    /// itself. When it ended by itself every van is gone already (the crews that fled run on to the edge); an admin's end or cancel removes what is left. HOOK for the reward: the win's reward (ticket 10) is given here.
    /// </summary>
    void ThievesFinish(bool won, string? by)
    {
        _thieves!.Finish();
        if (by != null || !won) ClearGangs(); // an admin's end or cancel removes every van and crew; a win by itself leaves what is still fleeing to run off
        if (!won) ResetLoot(); // a cancelled event never happened: everything taken goes back (a win keeps what was stolen, and lying Loot stays returnable)
        WithdrawIntel(ThievesEvent.IntelId);
        _events.End(EventBook.Thieves, won ? EventPhase.Won : EventPhase.Despawned, _elapsed);
        ChatToAll(ChatLineKind.System, "", won
            ? (by == null ? "Event won: the Thieves are gone from the village." : $"{by} ended the Thieves event as a win.")
            : $"{by} cancelled the Thieves event.");
        GiveThievesReward(won);
        GD.Print($"server: Thieves event {(won ? "won" : "cancelled")}{(by == null ? "" : $" by {by}")}");
    }

    /// <summary>Join: what a joiner needs to see of a running Thieves event. Nothing: the vans and crews are world entities, which a joiner is sent with the rest of the world (the intel board is sent by <see cref="SendIntelOnJoin"/>).</summary>
    void SendThievesOnJoin(Client c)
    {
        SendDishesOnJoin(c); // the dishes taken (ticket 06)
    }

    /// <summary>A new round clears the event (the world's entities went with it): a running run is despawned without a reward, the phase is Idle. What it stole is put back (<see cref="ResetLoot"/>).</summary>
    void ThievesRoundReset()
    {
        if (_thieves == null) return;
        var wasRunning = _thieves.Running;
        _thieves.Reset();
        ClearGangs();
        ResetLoot();
        ResetThievesReward();
        if (!wasRunning) return;
        WithdrawIntel(ThievesEvent.IntelId);
        _events.End(EventBook.Thieves, EventPhase.Despawned, _elapsed);
        ChatToAll(ChatLineKind.System, "", "The Thieves event is over.");
    }
}
