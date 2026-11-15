using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

/// <summary>
/// The village loudspeaker (rozhlas) on the server: <see cref="RozhlasBroadcast"/> owns the schedule (the server's wall clock, <see cref="RozhlasRules.OnAir"/>) and the
/// <see cref="RozhlasPhase"/> machine (Idle, OnAir, Hijacked); this file ticks it and tells the clients (<see cref="EventKind.RozhlasState"/>, also to a joiner, who plays from the
/// phase's offset). Not an EventBook event: it runs beside the others. <c>/event rozhlas</c> starts a detour broadcast now, <c>/rozhlas hijack</c> is the admin's stand-in for killing
/// the announcer until she exists. Intel and event announcements over the megaphones are client-side and never come here.
/// <para>
/// Hooks for later slices: <see cref="RozhlasBroadcast"/> (<see cref="RozhlasBroadcast.MicLive"/>, <see cref="RozhlasBroadcast.DetourOpen"/>, <see cref="RozhlasBroadcast.AnnouncerKilled"/>) and
/// <see cref="RozhlasPhaseChanged"/>.
/// </para>
/// </summary>
public partial class NetServer
{
    LazyInit<RozhlasBroadcast> _rozhlasBroadcast;

    /// <summary>The loudspeaker's machine and schedule (null when rozhlas.json cannot be read, which is logged once). Call <see cref="RozhlasBroadcast.AnnouncerKilled"/> when the announcer dies.</summary>
    public RozhlasBroadcast? RozhlasBroadcast => EnsureRozhlas();

    /// <summary>The loudspeaker changed phase: (from, to). Fired after the clients were told.</summary>
    public event Action<RozhlasPhase, RozhlasPhase>? RozhlasPhaseChanged;

    RozhlasBroadcast? EnsureRozhlas()
    {
        if (!_rozhlasBroadcast.Begin()) return _rozhlasBroadcast.Value;
        var json = Godot.FileAccess.Open("res://data/rozhlas.json", Godot.FileAccess.ModeFlags.Read)?.GetAsText();
        if (json == null) { GD.PrintErr($"server: rozhlas.json: cannot open ({Godot.FileAccess.GetOpenError()}); no rozhlas schedule"); return null; }
        RozhlasBroadcast r;
        try { r = new RozhlasBroadcast(RozhlasFile.Parse(json)); }
        catch (DataFileException e) { GD.PrintErr($"server: {e.Message}; no rozhlas schedule"); return null; }
        // The loudspeaker exists from here on, and everything below is wired or the server says why not: each part reads its own data file, logs a bad one once and leaves
        // only itself out (no live feed, no workers, no combat sounds), so one broken file never takes the phase messages to the clients with it.
        _rozhlasBroadcast.Ready(r);
        EnsureLiveFeed(); // subscribes to the phase: the feed starts and stops with the mic
        EnsureRozhlasWorkers(r); // the Urad workers sit down for a detour and are gone after it
        EnsureRozhlasSet(); // the set holder (NetServer.RozhlasSet.cs)
        EnsureRozhlasCombat(); // the combat mix: sample bank, workers' deaths (NetServer.RozhlasCombat.cs)
        EnsureRozhlasDoor(); // the Urad door opens and closes with the detour (NetServer.RozhlasDoor.cs)
        r.PhaseChanged += (from, to) =>
        {
            var m = r.Message;
            GD.Print($"server: rozhlas: {from} -> {to}" + (to == RozhlasPhase.OnAir ? $" (broadcast {m.Variant}, {m.OffsetS:0.#} s in, {m.LeftS:0.#} s left)" : to == RozhlasPhase.Hijacked ? $" (mic live for {m.LeftS:0.#} s)" : ""));
            BroadcastEvent(EventKind.RozhlasState, RozhlasCodec.Encode(m));
            RozhlasPhaseChanged?.Invoke(from, to);
        };
        return r;
    }

    /// <summary>Once a tick: the hijack clock, the end of a broadcast, and the start of a scheduled one (the server's own wall clock).</summary>
    void RozhlasTick(float dt)
    {
        EnsureRozhlas()?.Update(dt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
        RozhlasSetTick();
        RozhlasCombatTick(); // the combat test
    }

    /// <summary>Join: the phase as it is, when a detour broadcast is on (Idle is what a client starts with).</summary>
    void SendRozhlasOnJoin(Client c)
    {
        if (_rozhlasBroadcast.Value is { DetourOpen: true } r) Send(c, new EventMsg((byte)EventKind.RozhlasState, RozhlasCodec.Encode(r.Message)));
        SendRozhlasSetOnJoin(c); // and who is on the mic
    }

    /// <summary>"/event rozhlas": a detour broadcast now, from its beginning, as a scheduled one. Not while one is on.</summary>
    void ChatEventRozhlas(Client c)
    {
        if (EnsureRozhlas() is not { } r) { ChatTo(c, ChatLineKind.Error, "The rozhlas has no data (rozhlas.json could not be read)."); return; }
        if (r.DetourOpen) { ChatTo(c, ChatLineKind.Error, $"The rozhlas is already on ({r.Phase}). Wait for it to end."); return; }
        if (!r.StartNow(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, Random.Shared.Next(int.MaxValue))) { ChatTo(c, ChatLineKind.Error, "rozhlas.json lists no broadcast."); return; }
        ChatToAll(ChatLineKind.System, "", $"{c.Name} started the rozhlas.");
        GD.Print($"server: rozhlas started by {c.Name} #{c.Id}");
    }

    /// <summary>"/rozhlas [hijack]": the phase, or the announcer's death (the mic is free for <see cref="RozhlasBroadcast.HijackSeconds"/> s).</summary>
    void ChatRozhlas(Client c, IReadOnlyList<string> args)
    {
        if (args.Count > 0 && args[0] == "feedtest") { ChatRozhlasFeedTest(c); return; } // the live feed's test (NetServer.RozhlasFeed.cs)
        if (args.Count > 0 && args[0] == "combattest") { ChatRozhlasCombatTest(c); return; } // one shot, blast and death through the combat mix (NetServer.RozhlasCombat.cs)
        if (args.Count > 0 && ChatRozhlasSet(c, args[0])) return; // take / release the set (NetServer.RozhlasSet.cs)
        if (EnsureRozhlas() is not { } r) { ChatTo(c, ChatLineKind.Error, "The rozhlas has no data (rozhlas.json could not be read)."); return; }
        if (args.Count == 0)
        {
            ChatTo(c, ChatLineKind.Info, r.DetourOpen ? $"The rozhlas is {r.Phase}, {r.LeftS:0} s left." : "The rozhlas is idle. /event rozhlas starts a broadcast.");
            return;
        }
        if (!r.AnnouncerKilled()) { ChatTo(c, ChatLineKind.Error, r.Hijacked ? "The mic is already hijacked (one hijack per broadcast)." : "No detour broadcast is on air. /event rozhlas starts one."); return; }
        ChatToAll(ChatLineKind.System, "", $"{c.Name} killed the rozhlas announcer.");
    }
}
