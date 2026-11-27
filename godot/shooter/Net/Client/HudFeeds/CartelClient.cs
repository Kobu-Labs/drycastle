using Godot;
using Drycastle.Shooter.Audio;
using Drycastle.Shooter.Cartel;
using Drycastle.Shooter.Menu;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;

namespace Drycastle.Shooter.Net.Client.HudFeeds;

/// <summary>
/// The Cartel on the client (docs/cartel.md "Contact", "Who runs a job"; #494): the player's standing (<see cref="EventKind.CartelStatus"/>), their Job and its objective
/// (<see cref="EventKind.CartelJob"/>, <see cref="EventKind.CartelObjective"/>) into the HUD tracker, and everyone's rank-ups (<see cref="EventKind.CartelRankUp"/>: a toast; the chat line
/// comes from the server). In reach of the map's Contact (cartel.json <c>contact_reach_m</c>, on foot and alive) the interact key is his when nothing before him in the prompt chain
/// wants it: the prompt shows and a press opens his panel (the server, for its part, keeps the plantation's hold off the key there: <see cref="KeyCompetitor.Contact"/>). Walking out of
/// reach closes the panel. The panel's requests go out as <see cref="EventKind.CartelRequest"/>; the server checks each again.
/// </summary>
public sealed class CartelClient
{
    readonly ClientContext _ctx;
    readonly CartelFile _data;
    readonly Func<bool> _admin;
    readonly Func<bool> _openPanel;
    readonly Dictionary<string, string> _kindNames;
    Drycastle.MapLoader.MapLoader? _mapSeen;
    ContactPose? _contact;
    InteractKey _use;
    /// <summary>The frame the Contact's prompt last showed: a press in the frame after it is his.</summary>
    ulong _promptFrame;

    /// <param name="admin">The player is an admin (the panel lists the test kind).</param>
    /// <param name="openPanel">Opens the Contact panel if the HUD allows it now (<see cref="OverlayArbiter"/>); true when it opened.</param>
    public CartelClient(ClientContext ctx, CartelFile data, Func<bool> admin, Func<bool> openPanel)
    {
        _ctx = ctx; _data = data; _admin = admin; _openPanel = openPanel;
        _kindNames = ContactPanelModel.KindsOf(data, admin: true).ToDictionary(k => k.Id, k => k.Name);
    }

    /// <summary>The player's standing; null until the server sent it.</summary>
    public CartelStatusMsg? Status { get; private set; }
    /// <summary>The player's Job as the HUD tracker shows it.</summary>
    public CartelTracker Tracker { get; } = new(ShooterTheme.JobCardSeconds);
    /// <summary>The standing or the Job changed (the panel redraws).</summary>
    public event Action? Changed;

    /// <summary>What players read for a kind id ("Delivery"); the id itself for a kind cartel.json does not name.</summary>
    public string KindName(string kind) => _kindNames.GetValueOrDefault(kind, kind);

    /// <summary>The player would lead a Job they start: alone (ffa, or in no Squad), or as their Squad's leader. The server decides again.</summary>
    public bool Leads => !_ctx.Match.IsTeamMode || _ctx.Match.SquadOf(_ctx.MyId) == 0 || _ctx.Match.IsSquadLeader(_ctx.MyId);

    /// <summary>What the panel shows now.</summary>
    public ContactPanelModel Model() => ContactPanelModel.Build(_data, Status, _admin(), Leads, Tracker.Phase == TrackerPhase.Running, _ctx.Match.Phase == MatchPhase.Running);

    /// <summary>What the panel's content depends on, cheap to compare each frame: the panel rebuilds when it changes.</summary>
    public (CartelStatusMsg? Status, TrackerPhase Phase, int Job, bool Leads, bool Round, bool Admin) Signature =>
        (Status, Tracker.Phase, Tracker.Job.JobId, Leads, _ctx.Match.Phase == MatchPhase.Running, _admin());

    public void OnEvent(EventKind kind, byte[] payload)
    {
        switch (kind)
        {
            case EventKind.CartelStatus when CartelCodec.TryDecodeStatus(payload, out var s):
                Status = s;
                Changed?.Invoke();
                break;
            case EventKind.CartelJob when CartelCodec.TryDecodeJob(payload, out var j):
                Tracker.OnJob(j);
                Changed?.Invoke();
                break;
            case EventKind.CartelObjective when CartelCodec.TryDecodeObjective(payload, out var o):
                Tracker.OnObjective(o);
                break;
            case EventKind.CartelRankUp when CartelCodec.TryDecodeRankUp(payload, out var who, out var rank):
                var name = _data.Ranks[Math.Clamp((int)rank, 0, _data.Ranks.Count - 1)].Name;
                UiShell.Instance?.Toast(who == _ctx.MyId ? Strings.F("cartel.rankup.you", name)
                    : Strings.F("cartel.rankup.other", _ctx.Names.GetValueOrDefault(who, Strings.F("player.unknown", who)), name));
                break;
        }
    }

    /// <summary>Sends a request to the Contact (the panel's buttons).</summary>
    public void Request(CartelRequest r) => _ctx.SendHuman(EventKind.CartelRequest, CartelCodec.EncodeRequest(r));

    /// <summary>Every frame: the tracker's clock, the Contact's key (a press while his prompt shows opens the panel) and the panel closing out of his reach.</summary>
    public void Render(double delta)
    {
        Tracker.Tick((float)delta);
        if (!ReferenceEquals(_mapSeen, _ctx.Map)) { _mapSeen = _ctx.Map; _contact = ContactMarker.PoseIn(_ctx.Map, ContactMarker.LoadConfig()); }
        var (pressed, _) = _use.Step(_ctx.InteractHeld);
        var reach = InReach();
        if (pressed && reach && _promptFrame + 1 >= Engine.GetProcessFrames() && _openPanel()) Sfx.Ui("ui.click");
        if (!reach && _ctx.Hud is { ContactPanelOpen: true } hud) hud.CloseContactPanel();
    }

    /// <summary>The Contact's prompt when the player stands in his reach (the prompt chain asks it after everything that wins the key before him), else null.</summary>
    public string? Prompt()
    {
        if (!InReach() || _ctx.Hud is { ContactPanelOpen: true }) return null;
        _promptFrame = Engine.GetProcessFrames();
        return Strings.F("cartel.prompt", InputBindings.Name("interact"));
    }

    bool InReach() =>
        _contact is { } c && _ctx.Me is { } me && !_ctx.Dead && !_ctx.Driving && !_ctx.InVehicle()
        && c.GroundDistance(new System.Numerics.Vector3(me.GlobalPosition.X, me.GlobalPosition.Y, me.GlobalPosition.Z)) <= _data.ContactReachM;

    /// <summary>The connection ended: no standing, no Job.</summary>
    public void Clear()
    {
        Status = null;
        Tracker.Clear();
    }
}
