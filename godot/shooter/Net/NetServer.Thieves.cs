using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The Thieves event on the server (GLOSSARY "Thieves event", "Raid"; the design is in docs/events/README.md, the tickets in docs/events/thieves/). An admin starts it
/// with <c>/event thieves</c>; <see cref="ThievesEvent"/> keeps its phase and this file puts it in the world. Plumbing only so far: it starts (the book, a chat
/// line, the intel entry and the Event announcement), ends itself after <see cref="PlaceholderSeconds"/> and is won; <c>/event current end</c> wins it,
/// <c>/event current cancel</c> removes it with no reward, a new round clears it. It cannot be lost. The Raids, the vans and the Loot plug in at the marked
/// places (<see cref="ThievesTick"/>, <see cref="ThievesFinish"/>, <see cref="SendThievesOnJoin"/>, <see cref="ThievesRoundReset"/>).
/// </summary>
public partial class NetServer
{
    /// <summary>thieves.json: every number of the event (set by the host).</summary>
    public ThievesFile ThievesData = new();

    /// <summary>PLACEHOLDER until the Raids exist: how long a run lasts before it ends itself (the real end is "the last van is gone"). Remove with the first Raid.</summary>
    const float PlaceholderSeconds = 60;

    ThievesEvent? _thieves;

    /// <summary>The Thieves event's phase; null until <c>_Ready</c>.</summary>
    public ThievesEvent? Thieves => _thieves;

    /// <summary>Called once from <c>_Ready</c>. The Raids' world entity types register here, when there are some.</summary>
    void InitThieves()
    {
        _thieves = new ThievesEvent();
        InitGangs();
        InitThievesReward();
    }

    /// <summary>"/event thieves": the run begins, the intel board and the village are told.</summary>
    void StartThieves(Client admin)
    {
        if (_events.WhyNotStart(EventBook.Thieves) is { } busy) { ChatTo(admin, ChatLineKind.Error, busy); return; }
        if (_match.Phase != MatchPhase.Running) { ChatTo(admin, ChatLineKind.Error, "No round is running."); return; }
        if (_nav == null) { ChatTo(admin, ChatLineKind.Error, "The Thieves need the navigation mesh, which is not built (no map, or it failed: see the server log)."); return; }
        // the dev fallback until the van exists (ticket 03): the crew appears on a road near the admin
        if (FindGangSpot(N(admin.Body.GlobalPosition)) is not { } spot) { ChatTo(admin, ChatLineKind.Error, "No road on the navigation mesh was found near you for the crew to appear on."); return; }
        _events.Start(EventBook.Thieves, _elapsed);
        _thieves!.Begin(PlaceholderSeconds);
        var crew = SpawnCrew(spot);
        if (crew > 0) ChatTo(admin, ChatLineKind.Info, $"The crew of {crew} appeared {Dist2D(spot, N(admin.Body.GlobalPosition)):0} m from you (dev fallback: no van yet).");
        AnnounceIntel(ThievesEvent.Intel(_elapsed));
        ChatToAll(ChatLineKind.System, "", $"{admin.Name} started the Thieves event. Thieves are coming to the village.");
        GD.Print($"server: Thieves event started by {admin.Name} #{admin.Id}");
    }

    /// <summary>Once a tick (after <see cref="MassTick"/>): the placeholder timer, and the routes the Gang members wait for. HOOK for the Raids: their clocks and the vans run here, and the last van gone wins the event.</summary>
    void ThievesTick(float dt)
    {
        GangsTick();
        GangAudioTick(dt); // the voice lines (ticket 09)
        if (_thieves is { Running: true } && _thieves.Tick(dt)) ThievesFinish(true, null);
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
    /// itself. HOOK for the Raids and the reward: what is still in the village (Gang members, vans) is removed here; the win's reward (ticket 10) is given here.
    /// </summary>
    void ThievesFinish(bool won, string? by)
    {
        _thieves!.Finish();
        ClearGangs(); // the crew goes with the event (ticket 03: the van's Raids end by their own rules)
        WithdrawIntel(ThievesEvent.IntelId);
        _events.End(EventBook.Thieves, won ? EventPhase.Won : EventPhase.Despawned, _elapsed);
        ChatToAll(ChatLineKind.System, "", won
            ? (by == null ? "Event won: the Thieves are gone from the village." : $"{by} ended the Thieves event as a win.")
            : $"{by} cancelled the Thieves event.");
        GiveThievesReward(won);
        GD.Print($"server: Thieves event {(won ? "won" : "cancelled")}{(by == null ? "" : $" by {by}")}");
    }

    /// <summary>Join: what a joiner needs to see of a running Thieves event. Nothing yet (the intel board is sent by <see cref="SendIntelOnJoin"/>). HOOK for the Raids: the vans' and crews' state.</summary>
    void SendThievesOnJoin(Client c) { }

    /// <summary>A new round clears the event (the world's entities went with it): a running run is despawned without a reward, the phase is Idle. HOOK for the Loot: what it stole is put back.</summary>
    void ThievesRoundReset()
    {
        if (_thieves == null) return;
        var wasRunning = _thieves.Running;
        _thieves.Reset();
        ClearGangs();
        ResetThievesReward();
        if (!wasRunning) return;
        WithdrawIntel(ThievesEvent.IntelId);
        _events.End(EventBook.Thieves, EventPhase.Despawned, _elapsed);
        ChatToAll(ChatLineKind.System, "", "The Thieves event is over.");
    }
}
