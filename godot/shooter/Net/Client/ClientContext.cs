using Godot;
using Drycastle.Shooter.Characters;
using Drycastle.Shooter.HeadsUp;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Waters;
using Drycastle.ShooterCore.Weapons;

namespace Drycastle.Shooter.Net.Client;

/// <summary>
/// What the client's feature classes (<c>MoabClient</c>, <c>ScudClient</c>, ...) may read and call of the rest of the client: the match, the local player, the node they
/// draw under, the HUD, the clock, a way to tell the server, the camera shake. <see cref="NetClient"/> owns it and keeps it current; a feature keeps everything else to itself
/// and reaches another feature only through that feature's own public members.
/// </summary>
public sealed class ClientContext
{
    Node3D? _owned;

    /// <summary>The client node: parent of the screen overlays a feature adds.</summary>
    public Node Root { get; }
    /// <summary>Match events stored for the HUD to come: phase, countdown, scoreboard rows, kill feed, own spawn/death.</summary>
    public ClientMatchState Match { get; } = new();
    public CameraShake Shake { get; }
    /// <summary>Who has the own view and body (on foot, a vehicle, a turret, the missile, a hold): <see cref="ClientView.Mode"/>.</summary>
    public ClientView View { get; }
    /// <summary>The first-person weapon of the local player.</summary>
    internal OwnViewmodel Viewmodel { get; }
    /// <summary>The newest decoded snapshot: the records by entity id, the ones that left.</summary>
    public SnapshotDecoder Snapshots { get; } = new();
    /// <summary>How often and how far the own body and vehicles are corrected (the loss test reads it through the bot host and may replace it).</summary>
    public RubberStats Rub = new();
    /// <summary>The map's water (water.json), shared with the prediction so swimming matches the server.</summary>
    public Water Water = Water.Empty;
    /// <summary>The vehicles of the world as drawn, and the seat the local player sits in.</summary>
    internal ClientCars Cars { get; }
    /// <summary>Replicated world entities (turrets, missiles, citizens, ...). Subscribe to Spawned/Despawned for visuals.</summary>
    public WorldReplica WorldEntities { get; } = new();
    /// <summary>The render clock of the remote entities (A-06): a filtered estimate of the server's tick, which never runs backwards.</summary>
    public RenderClock Clock { get; } = new();
    /// <summary>The tick of the newest snapshot; 0 before the first one.</summary>
    public uint LastTick;
    /// <summary>The other players and target dummies being drawn, by entity id.</summary>
    public Dictionary<ushort, RemotePlayer> Remotes { get; } = new();
    public Node3D World = null!;
    /// <summary>The loaded map (set by the host).</summary>
    public Drycastle.MapLoader.MapLoader? Map;
    /// <summary>The in-match HUD; null before the welcome and in headless runs.</summary>
    public Hud? Hud;
    /// <summary>The predicted local player; null before the welcome.</summary>
    public Player? Me;
    public ushort MyId;
    /// <summary>How many times the prediction was corrected by a server position (the telemetry, the report and the bots count them).</summary>
    public int Reconciles;
    /// <summary>The weapon slots of the spawn hotbar and the one in hand.</summary>
    public Hotbar Hotbar { get; } = new();
    public bool Dead;
    /// <summary>The local player drives a vehicle (set by the reconcile: a seat or a driver's place).</summary>
    public bool Driving;
    public string PlayerName = "player";
    public bool Bot;
    /// <summary>vehicles.json (set by the host): the type per snapshot record, visuals and the smoke's expected run-over damage.</summary>
    public VehiclesFile? Vehicles;
    /// <summary>weapons.json (set by the host): hotbar order, per-weapon values.</summary>
    public WeaponsFile? Weapons;
    /// <summary>killstreaks.json (set by the host): streak names, thresholds and the aircraft's defs.</summary>
    public KillstreakFile? Killstreaks;
    /// <summary>The local player sits in a vehicle (driving, or in a seat).</summary>
    public bool InVehicle() => View.InVehicle;
    /// <summary>True while a menu screen is open (set by the host).</summary>
    public Func<bool>? MenuOpen;
    /// <summary>Sends one reliable event to the server (nothing when not connected).</summary>
    public Action<EventKind, byte[]> Send = (_, _) => { };
    /// <summary>The names of the players and target dummies, by entity id (the join events and the target roster).</summary>
    public Dictionary<ushort, string> Names { get; } = new();
    /// <summary>Sends a player's request (not for a bot, which has no screens to ask from).</summary>
    public void SendHuman(EventKind kind, byte[] payload) { if (!Bot) Send(kind, payload); }
    /// <summary>Sends one reliable message at once, flushed (a profile request); false when the welcome has not come.</summary>
    public Func<ReliableMsg, bool> SendJoined = _ => false;
    /// <summary>True while there is a link to the server to send on.</summary>
    public Func<bool> Connected = () => false;

    public ClientContext(Node root)
    {
        Root = root;
        Shake = new CameraShake(this);
        View = new ClientView(this);
        Viewmodel = new OwnViewmodel(this);
        Cars = new ClientCars(this);
    }

    public double Now => Time.GetTicksMsec() / 1000.0;
    /// <summary>The fractional server tick the remote entities are drawn at this frame (about <see cref="NetClient.InterpDelayTicks"/> in the past).</summary>
    public double RenderTick => Clock.RenderTick(Now, NetClient.InterpDelayTicks);

    /// <summary>Where a player stands: the local one at the camera's body, another at their drawn node; null when not known.</summary>
    public Vector3? PositionOf(ushort id) => id == MyId ? Me?.GlobalPosition : Remotes.TryGetValue(id, out var r) ? r.Node.GlobalPosition : null;
    public bool MenuIsOpen => MenuOpen?.Invoke() == true;

    /// <summary>
    /// The node under <see cref="World"/> that holds everything this client adds to the persistent world (effects, pools, corpses, crates, turrets, aircraft, missiles,
    /// the POV camera). <see cref="FreeOwned"/> frees it all on leave (#169, T2-17).
    /// </summary>
    public Node3D Owned
    {
        get
        {
            if (_owned == null || !GodotObject.IsInstanceValid(_owned)) { _owned = new Node3D { Name = "ClientOwned" }; World.AddChild(_owned); }
            return _owned;
        }
    }

    /// <summary>Frees the <see cref="Owned"/> node and everything under it; the next use makes a fresh one.</summary>
    public void FreeOwned()
    {
        if (_owned != null && GodotObject.IsInstanceValid(_owned)) _owned.QueueFree();
        _owned = null;
    }
}
