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
/// (<see cref="EventKind.CartelJob"/>, <see cref="EventKind.CartelObjective"/>) into the HUD tracker, and everyone's rank-ups (<see cref="EventKind.CartelRankUp"/>: a toast).
/// In reach of the map's Contact (cartel.json <c>contact_reach_m</c>, on foot and alive) the interact key is his when nothing before him in the prompt chain
/// wants it: the prompt shows and a press opens his panel (the server, for its part, keeps the plantation's hold off the key there: <see cref="KeyCompetitor.Contact"/>). Walking out of
/// reach closes the panel. The panel's requests go out as <see cref="EventKind.CartelRequest"/>; the server checks each again and answers a refusal (<see cref="EventKind.CartelRefused"/>),
/// which the panel shows. During a Delivery, carrying a Duffel within <c>jobs.delivery.radius_m</c> of the drop point, the prompt says to hold the key and the hold ring fills over
/// <c>hold_seconds</c> while it is held (the server counts the same hold and ends the Job). A Job of the party that succeeds raises the "Job done" banner and starts its cinematic (<see cref="Done"/>: the flashed camera moves, the player's own
/// music); the camera, the music and the controls' lock are <see cref="OwnPlayer.JobDoneClient"/>'s.
/// </summary>
public sealed class CartelClient
{
    readonly ClientContext _ctx;
    readonly CartelFile _data;
    readonly Func<bool> _admin;
    readonly Func<bool> _openPanel;
    readonly Dictionary<string, string> _kindNames;
    readonly DeliveryJobKind.Data _delivery;
    /// <summary>Seconds the interact key has been held at the drop point with a Duffel (the hand-over's ring); 0 away from it.</summary>
    float _handOver;
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
        _delivery = DeliveryJobKind.Read(data);
        Done = new JobDone(data.JobDoneCinematic);
    }

    /// <summary>The player's standing; null until the server sent it.</summary>
    public CartelStatusMsg? Status { get; private set; }
    /// <summary>The player's Job as the HUD tracker shows it.</summary>
    public CartelTracker Tracker { get; } = new(ShooterTheme.JobCardSeconds);
    /// <summary>The "Job done" banner and cinematic of a succeeded Job: its text, its clock, the camera's legs, the flash and the music's level.</summary>
    public JobDone Done { get; }
    /// <summary>Whether a Job done that starts now may take the camera and the controls (set by the client: not with reduce motion, only on foot or in a road vehicle).</summary>
    public Func<bool> CameraAllowed { get; set; } = () => false;
    /// <summary>The standing or the Job changed, or a request was refused (the panel redraws).</summary>
    public event Action? Changed;
    /// <summary>The server's last refusal of a request; null until one came.</summary>
    public CartelRefusalMsg? Refusal { get; private set; }
    /// <summary>Goes up with each refusal: the panel shows it once, in the build it causes.</summary>
    public int RefusalCount { get; private set; }
    /// <summary>How full the hold ring is for the Delivery's hand-over (0 when not holding at the drop point).</summary>
    public float HoldFraction => _handOver > 0 ? Math.Min(1f, _handOver / _delivery.HoldSeconds) : 0f;

    /// <summary>What players read for a kind id ("Delivery"); the id itself for a kind cartel.json does not name.</summary>
    public string KindName(string kind) => _kindNames.GetValueOrDefault(kind, kind);

    /// <summary>The player would lead a Job they start: alone (ffa, or in no Squad), or as their Squad's leader. The server decides again.</summary>
    public bool Leads => !_ctx.Match.IsTeamMode || _ctx.Match.SquadOf(_ctx.MyId) == 0 || _ctx.Match.IsSquadLeader(_ctx.MyId);

    /// <summary>What the panel shows now.</summary>
    public ContactPanelModel Model() => ContactPanelModel.Build(_data, Status, _admin(), Leads, Tracker.Phase == TrackerPhase.Running, _ctx.Match.Phase == MatchPhase.Running);

    /// <summary>What the panel's content depends on, cheap to compare each frame: the panel rebuilds when it changes.</summary>
    public (CartelStatusMsg? Status, TrackerPhase Phase, int Job, bool Leads, bool Round, bool Admin, int Refusals) Signature =>
        (Status, Tracker.Phase, Tracker.Job.JobId, Leads, _ctx.Match.Phase == MatchPhase.Running, _admin(), RefusalCount);

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
                Done.OnJob(j, CameraAllowed());
                Changed?.Invoke();
                break;
            case EventKind.CartelRefused when CartelCodec.TryDecodeRefusal(payload, out var no):
                Refusal = no;
                RefusalCount++;
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
        Done.Tick((float)delta);
        if (!ReferenceEquals(_mapSeen, _ctx.Map)) { _mapSeen = _ctx.Map; _contact = ContactMarker.PoseIn(_ctx.Map, ContactMarker.LoadConfig()); }
        var (pressed, _) = _use.Step(_ctx.InteractHeld);
        var reach = InReach();
        if (pressed && reach && _promptFrame + 1 >= Engine.GetProcessFrames() && _openPanel()) Sfx.Ui("ui.click");
        if (!reach && _ctx.Hud is { ContactPanelOpen: true } hud) hud.CloseContactPanel();
        _handOver = _ctx.InteractHeld && AtDrop() ? _handOver + (float)delta : 0f;
    }

    /// <summary>
    /// The Contact's prompt when the player stands in his reach, or the Delivery's hand-over at the drop point with a Duffel, else null. The prompt chain asks it after everything that
    /// wins the key before him and before the Duffel's carry hint (the hand-over is what the key does there).
    /// </summary>
    public string? Prompt()
    {
        if (AtDrop()) return Strings.F("cartel.handover", InputBindings.Name("interact"));
        if (!InReach() || _ctx.Hud is { ContactPanelOpen: true }) return null;
        _promptFrame = Engine.GetProcessFrames();
        return Strings.F("cartel.prompt", InputBindings.Name("interact"));
    }

    /// <summary>
    /// A Delivery of the player's runs, its marker is the drop point (it points there while the Duffel is carried) and the player, alive on foot, carries a Duffel within
    /// <c>radius_m</c> of it: the server's hand-over condition as the client sees it.
    /// </summary>
    bool AtDrop()
    {
        if (Tracker.Phase != TrackerPhase.Running || Tracker.Job.Kind != DeliveryJobKind.KindId || Tracker.Marker is not { } drop) return false;
        if (_ctx.Me is not { } me || _ctx.Dead || _ctx.Driving || _ctx.InVehicle()) return false;
        var at = me.GlobalPosition;
        if (new System.Numerics.Vector2(at.X - drop.X, at.Z - drop.Z).Length() > _delivery.RadiusM) return false;
        foreach (var e in _ctx.WorldEntities.Entities.Values) // only at the drop point: a handful of entities
            if (e.Type == DuffelRules.TypeId && DuffelRules.StateOf(e.State.State) == DuffelState.Carried && e.State.Owner == _ctx.MyId) return true;
        return false;
    }

    bool InReach() =>
        _contact is { } c && _ctx.Me is { } me && !_ctx.Dead && !_ctx.Driving && !_ctx.InVehicle()
        && c.GroundDistance(new System.Numerics.Vector3(me.GlobalPosition.X, me.GlobalPosition.Y, me.GlobalPosition.Z)) <= _data.ContactReachM;

    /// <summary>The connection ended: no standing, no Job.</summary>
    public void Clear()
    {
        Status = null;
        Refusal = null;
        _handOver = 0;
        Tracker.Clear();
        Done.Clear();
    }
}
