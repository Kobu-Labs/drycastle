using Godot;
using Drycastle.ShooterCore;

namespace Drycastle.Shooter;

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
    public Node3D World = null!;
    /// <summary>The loaded map (set by the host).</summary>
    public Drycastle.MapLoader.MapLoader? Map;
    /// <summary>The in-match HUD; null before the welcome and in headless runs.</summary>
    public Hud? Hud;
    /// <summary>The predicted local player; null before the welcome.</summary>
    public Player? Me;
    public ushort MyId;
    public bool Dead;
    public string PlayerName = "player";
    public bool Bot;
    /// <summary>weapons.json (set by the host): hotbar order, per-weapon values.</summary>
    public WeaponsFile? Weapons;
    /// <summary>killstreaks.json (set by the host): streak names, thresholds and the aircraft's defs.</summary>
    public KillstreakFile? Killstreaks;
    /// <summary>True while a menu screen is open (set by the host).</summary>
    public Func<bool>? MenuOpen;
    /// <summary>Sends one reliable event to the server (nothing when not connected).</summary>
    public Action<EventKind, byte[]> Send = (_, _) => { };

    public ClientContext(Node root)
    {
        Root = root;
        Shake = new CameraShake(this);
    }

    public double Now => Time.GetTicksMsec() / 1000.0;
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
