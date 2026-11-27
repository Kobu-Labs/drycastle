using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Client.Peers;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Status;

namespace Drycastle.Shooter.Net.Client.HudFeeds;

/// <summary>
/// The Harvest's fill hold on the client (docs/cartel.md "Harvest"; cartel.json <c>jobs.harvest</c>). The server says what the player's hold of the interact key does in the plantation
/// (<see cref="EventKind.HarvestFill"/>, <see cref="FillReach"/>; it alone knows the van, the filled count and what else wins the key): ready, the prompt says to hold the key and the hold ring fills over
/// <c>fill_hold_seconds</c> (counted here as the server counts it, for the ring only); blocked, the prompt says why (a Duffel in the hands, the maximum filled, the van not at the plantation yet). Off
/// (no Harvest of the player's, out of the plantation): nothing, as the plantation's hold for High has always been. Everyone sees the filler bend and reach (<see cref="StatusKind.Filling"/>, drawn by
/// <see cref="StatusClient.SyncFx"/> and the own figure) and hears a rustle (<see cref="Rustle"/>) from each of them.
/// </summary>
public sealed class HarvestFillClient
{
    /// <summary>The rustle while a figure fills: the soft footstep (sand scuffs, sounds.json "footstep") a little quieter and lower, every <see cref="RustleEverySeconds"/>.</summary>
    public const string Rustle = "footstep";
    const float RustleEverySeconds = 0.45f;
    const float RustleDb = -3f;

    readonly ClientContext _ctx;
    readonly StatusClient _status;
    readonly float _holdSeconds;
    FillReach _reach;
    int _max;
    InteractHold _hold;
    /// <summary>When each filling player's next rustle is due (client time).</summary>
    readonly Dictionary<ushort, double> _next = new();
    readonly List<ushort> _done = new();

    public HarvestFillClient(ClientContext ctx, StatusClient status, CartelFile data)
    {
        _ctx = ctx; _status = status;
        _holdSeconds = DataFile.Section<HarvestData>(data.JobSection(HarvestData.KindId), "cartel.json: jobs.harvest").FillHoldSeconds;
    }

    public void OnEvent(EventKind kind, byte[] payload)
    {
        if (kind != EventKind.HarvestFill || !HarvestFillRules.TryDecode(payload, out var reach, out var max)) return;
        _reach = reach; _max = max; // the hold's own state follows the key, as the server's does (a completed hold waits for the key's release)
    }

    bool Ready => _reach == FillReach.Ready && !_ctx.Dead && !_ctx.Driving && !_ctx.InVehicle();

    /// <summary>The input tick: follows the hold of the interact key as the server counts it. Nothing is sent: the server sees the same key in the inputs.</summary>
    public void TickHold(bool use, double dt) => _hold.Tick(use, Ready, dt, _holdSeconds);

    /// <summary>How full the hold ring is for the fill (0 when not holding where the hold fills).</summary>
    public float HoldFraction => Ready && _hold.Held > 0 ? (float)Math.Min(1.0, _hold.Held / _holdSeconds) : 0f;

    /// <summary>
    /// The fill's prompt: to hold the key while it is not held (the ring carries the hold), or why the hold does nothing. A carried Duffel's line also says the key that throws it (the
    /// plantation's prompt comes before the Duffel's own carry hint, which it replaces here).
    /// </summary>
    public string? Prompt()
    {
        if (_ctx.Dead || _ctx.Driving || _ctx.InVehicle()) return null;
        return _reach switch
        {
            FillReach.Ready => _hold.Held <= 0 ? Strings.F("cartel.fill", InputBindings.Name("interact")) : null,
            FillReach.Carrying => Strings.F("cartel.fill.carrying", InputBindings.Name("duffel_throw")),
            FillReach.Full => Strings.F("cartel.fill.full", _max),
            FillReach.VanFirst => Strings.T("cartel.fill.van"),
            _ => null,
        };
    }

    /// <summary>Every frame: a rustle from each player the server says is filling, the own included.</summary>
    public void Render()
    {
        var now = _ctx.Now;
        _done.Clear();
        foreach (var id in _next.Keys) if (!Filling(id)) _done.Add(id);
        foreach (var id in _done) _next.Remove(id);
        if (_ctx.Me != null && Filling(_ctx.MyId)) Rustled(_ctx.MyId, _ctx.Me.GlobalPosition, now);
        foreach (var (id, r) in _ctx.Remotes)
            if (Filling(id)) Rustled(id, r.Node.GlobalPosition, now);
    }

    bool Filling(ushort id) => _status.Has(id, StatusKind.Filling);

    void Rustled(ushort id, Vector3 at, double now)
    {
        if (_next.TryGetValue(id, out var due) && now < due) return;
        _next[id] = now + RustleEverySeconds;
        Sfx.Play3D(Rustle, at, RustleDb);
    }

    /// <summary>The connection ended: no hold, no sounds.</summary>
    public void Clear()
    {
        _reach = FillReach.Off; _max = 0;
        _hold.Reset();
        _next.Clear();
    }
}
