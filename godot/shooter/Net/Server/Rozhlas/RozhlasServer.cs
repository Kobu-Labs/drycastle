using Godot;
using Drycastle.ShooterCore;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter;

/// <summary>
/// The village loudspeaker (rozhlas) on the server: <see cref="RozhlasBroadcast"/> owns the schedule (the server's wall clock, <see cref="RozhlasRules.OnAir"/>) and the
/// <see cref="RozhlasPhase"/> machine (Idle, OnAir, Hijacked); this class ticks it and tells the clients (<see cref="EventKind.RozhlasState"/>, also to a joiner, who plays from the
/// phase's offset). Not an EventBook event: it runs beside the others. <c>/event rozhlas</c> starts a detour broadcast now, <c>/rozhlas hijack</c> is the admin's stand-in for killing
/// the announcer until she exists. Intel and event announcements over the megaphones are client-side and never come here.
/// <para>
/// It owns the parts that follow its phase: the Obecny Urad with its workers and door (<see cref="UradServer"/>), the live feed with the combat mix (<see cref="RozhlasFeedServer"/>) and the set holder (<see cref="RozhlasSetServer"/>).
/// What the rest of the server calls is the short list below: <see cref="Tick"/>, <see cref="Pump"/>, <see cref="SendOnJoin"/>, <see cref="OnEvent"/>, <see cref="OnPlayerDied"/>, <see cref="OnPlayerLeft"/>,
/// <see cref="RoundReset"/>, <see cref="OnInput"/>, <see cref="OnMic"/>, <see cref="AddTargets"/>, <see cref="CanTakeSetAt"/>, <see cref="TableStood"/> and the two chat commands.
/// </para>
/// </summary>
internal sealed class RozhlasServer : IDisposable
{
    readonly ServerContext _ctx;
    readonly UradServer _urad;
    readonly RozhlasFeedServer _feed;
    readonly RozhlasSetServer _set;

    LazyInit<RozhlasBroadcast> _broadcast;

    public RozhlasServer(ServerContext ctx, bool arenaMode, string mapDir, HolyMassFile holyMass, WeaponsFile? weapons, Func<CitizenHitRouter> citizenHits, TableSeating seating)
    {
        _ctx = ctx;
        _urad = new UradServer(ctx, arenaMode, mapDir, holyMass, citizenHits);
        _feed = new RozhlasFeedServer(ctx, this, _urad, weapons);
        _set = new RozhlasSetServer(ctx, this, _urad, _feed, seating, holyMass);
    }

    /// <summary>The loudspeaker changed phase: (from, to). Fired after the clients were told.</summary>
    public event Action<RozhlasPhase, RozhlasPhase>? PhaseChanged;

    /// <summary>The loudspeaker's machine and schedule (null when rozhlas.json cannot be read, which is logged once).</summary>
    public RozhlasBroadcast? Loudspeaker => Ensure();

    /// <summary>The mic is live: a detour broadcast is on air or hijacked.</summary>
    public bool MicLive => _broadcast.Value is { MicLive: true };

    RozhlasBroadcast? Ensure()
    {
        if (!_broadcast.Begin()) return _broadcast.Value;
        var json = Godot.FileAccess.Open("res://data/rozhlas.json", Godot.FileAccess.ModeFlags.Read)?.GetAsText();
        if (json == null) { GD.PrintErr($"server: rozhlas.json: cannot open ({Godot.FileAccess.GetOpenError()}); no rozhlas schedule"); return null; }
        RozhlasBroadcast r;
        try { r = new RozhlasBroadcast(RozhlasFile.Parse(json)); }
        catch (DataFileException e) { GD.PrintErr($"server: {e.Message}; no rozhlas schedule"); return null; }
        // The loudspeaker exists from here on, and everything below is wired or the server says why not: each part reads its own data file, logs a bad one once and leaves
        // only itself out (no live feed, no workers, no combat sounds), so one broken file never takes the phase messages to the clients with it.
        _broadcast.Ready(r);
        _feed.EnsureFeed(); // the live feed: subscribes to the phase, so the feed starts and stops with the mic
        _urad.AttachWorkers(r); // the Urad workers sit down for a detour and are gone after it
        _set.Start(); // the set holder
        _feed.StartCombat(); // the combat mix: sample bank, workers' deaths
        PhaseChanged += (_, to) => _urad.SetDoor(to != RozhlasPhase.Idle); // the Urad door opens and closes with the detour
        r.PhaseChanged += (from, to) =>
        {
            var m = r.Message;
            GD.Print($"server: rozhlas: {from} -> {to}" + (to == RozhlasPhase.OnAir ? $" (broadcast {m.Variant}, {m.OffsetS:0.#} s in, {m.LeftS:0.#} s left)" : to == RozhlasPhase.Hijacked ? $" (mic live for {m.LeftS:0.#} s)" : ""));
            _ctx.Broadcast(EventKind.RozhlasState, RozhlasCodec.Encode(m));
            PhaseChanged?.Invoke(from, to);
        };
        return r;
    }

    /// <summary>ffmpeg must not outlive the server.</summary>
    public void Dispose() => _feed.Dispose();

    /// <summary>Once a tick: the hijack clock, the end of a broadcast, and the start of a scheduled one (the server's own wall clock); then the set's rules and a running combat test.</summary>
    public void Tick(float dt)
    {
        Ensure()?.Update(dt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
        _set.Tick();
        _feed.Tick();
    }

    /// <summary>Every frame: the live feed's due frames go out (a thread feeds ffmpeg; the sending is here, on the game thread).</summary>
    public void Pump() => _feed.Pump();

    /// <summary>Join: the phase as it is, when a detour broadcast is on (Idle is what a client starts with), who is on the mic, and the Urad door when it is open.</summary>
    public void SendOnJoin(ServerClient c)
    {
        if (_broadcast.Value is { DetourOpen: true } r) _ctx.SendEvent(c, EventKind.RozhlasState, RozhlasCodec.Encode(r.Message));
        _set.SendOnJoin(c);
        _urad.SendDoorOnJoin(c);
    }

    /// <summary>Every broadcast event passes here: shots, launches and blasts inside the Urad go to the live feed while the mic is live.</summary>
    public void OnEvent(EventKind kind, byte[] payload) => _feed.OnEvent(kind, payload);

    /// <summary>A player died: the holder lets go of the set, and the death is heard in the feed.</summary>
    public void OnPlayerDied(ushort victim) { _set.Release(victim); _feed.OnPlayerDied(victim); }

    public void OnPlayerLeft(ushort id) => _set.Release(id);

    /// <summary>A player stood up from the dictation table: the set is free.</summary>
    public void TableStood(ushort id) => _set.Stood(id);

    /// <summary>A round reset cleared the entities: the workers of a detour still open sit down again.</summary>
    public void RoundReset() => _urad.RoundReset();

    /// <summary>Every input of a player with the interact key's state: the hold at the dictation table.</summary>
    public void OnInput(ServerClient c, bool useDown) => _set.Use(c, useDown);

    /// <summary>One upstream mic frame of the set holder.</summary>
    public void OnMic(ServerClient c, byte[] d) => _set.OnMic(c, d);

    public bool CanTakeSetAt(NVec3 at, bool inVehicle) => _set.CanTakeAt(at, inVehicle);

    /// <summary>The Urad workers are hit targets, sitting still.</summary>
    public void AddTargets(List<HitTarget> into) => _urad.AddTargets(into);

    /// <summary>"/event rozhlas": a detour broadcast now, from its beginning, as a scheduled one. Not while one is on.</summary>
    public void ChatEvent(ServerClient c)
    {
        if (Ensure() is not { } r) { _ctx.ChatTo(c, ChatLineKind.Error, "The rozhlas has no data (rozhlas.json could not be read)."); return; }
        if (r.DetourOpen) { _ctx.ChatTo(c, ChatLineKind.Error, $"The rozhlas is already on ({r.Phase}). Wait for it to end."); return; }
        if (!r.StartNow(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, Random.Shared.Next(int.MaxValue))) { _ctx.ChatTo(c, ChatLineKind.Error, "rozhlas.json lists no broadcast."); return; }
        _ctx.ChatToAll(ChatLineKind.System, "", $"{c.Name} started the rozhlas.");
        GD.Print($"server: rozhlas started by {c.Name} #{c.Id}");
    }

    /// <summary>"/rozhlas [hijack]": the phase, or the announcer's death (the mic is free for <see cref="RozhlasBroadcast.HijackSeconds"/> s).</summary>
    public void ChatCommand(ServerClient c, IReadOnlyList<string> args)
    {
        if (args.Count > 0 && args[0] == "feedtest") { _feed.ChatFeedTest(c); return; }
        if (args.Count > 0 && args[0] == "combattest") { _feed.ChatCombatTest(c); return; }
        if (args.Count > 0 && _set.Chat(c, args[0])) return; // take / release / goto
        if (Ensure() is not { } r) { _ctx.ChatTo(c, ChatLineKind.Error, "The rozhlas has no data (rozhlas.json could not be read)."); return; }
        if (args.Count == 0)
        {
            _ctx.ChatTo(c, ChatLineKind.Info, r.DetourOpen ? $"The rozhlas is {r.Phase}, {r.LeftS:0} s left." : "The rozhlas is idle. /event rozhlas starts a broadcast.");
            return;
        }
        if (!r.AnnouncerKilled()) { _ctx.ChatTo(c, ChatLineKind.Error, r.Hijacked ? "The mic is already hijacked (one hijack per broadcast)." : "No detour broadcast is on air. /event rozhlas starts one."); return; }
        _ctx.ChatToAll(ChatLineKind.System, "", $"{c.Name} killed the rozhlas announcer.");
    }
}
