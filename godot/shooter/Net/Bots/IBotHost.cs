using Godot;
using Drycastle.Shooter.Corpses;
using Drycastle.Shooter.Crystals;
using Drycastle.Shooter.Dev;
using Drycastle.Shooter.Glass;
using Drycastle.Shooter.HeadsUp;
using Drycastle.Shooter.Hunts;
using Drycastle.Shooter.Maps;
using Drycastle.Shooter.Menu;
using Drycastle.Shooter.Net.Client;
using Drycastle.ShooterCore.Bots;
using Drycastle.ShooterCore.Camo;
using Drycastle.ShooterCore.CarePackages;
using Drycastle.ShooterCore.Casings;
using Drycastle.ShooterCore.Chat;
using Drycastle.ShooterCore.Citizens;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Companions;
using Drycastle.ShooterCore.Demon;
using Drycastle.ShooterCore.Effects;
using Drycastle.ShooterCore.Events;
using Drycastle.ShooterCore.Flight;
using Drycastle.ShooterCore.Fsm;
using Drycastle.ShooterCore.Glass;
using Drycastle.ShooterCore.Hud;
using Drycastle.ShooterCore.Hunts;
using Drycastle.ShooterCore.Items;
using Drycastle.ShooterCore.Killstreaks.Helicopter;
using Drycastle.ShooterCore.Killstreaks.Moab;
using Drycastle.ShooterCore.Killstreaks.Uav;
using Drycastle.ShooterCore.Killstreaks;
using Drycastle.ShooterCore.Match;
using Drycastle.ShooterCore.Minimap;
using Drycastle.ShooterCore.Mining;
using Drycastle.ShooterCore.Net;
using Drycastle.ShooterCore.Scud;
using Drycastle.ShooterCore.Status;
using Drycastle.ShooterCore.Thieves;
using Drycastle.ShooterCore.Turrets;
using Drycastle.ShooterCore.Vehicles;
using Drycastle.ShooterCore.Weapons;
using NVec3 = System.Numerics.Vector3;

namespace Drycastle.Shooter.Net.Bots;

/// <summary>A vehicle as the scripted bots see it (a copy of what the client drew from the last snapshot).</summary>
public readonly record struct BotCar(ushort Id, int Kind, ushort Driver, NVec3 Pos, float Kmh, int Stage, bool Destroyed, bool Exploded, Node3D Node, bool HasDriveBlock);

/// <summary>A remote player as drawn: whether alive, and where.</summary>
public sealed record BotRemote(EntityState State, Vector3 LastPos);

/// <summary>The reliable events a scripted bot checks its scenario against; the client fills them only while a bot role is on (<see cref="NetClient"/> KeepEventLogs).</summary>
public sealed class EventLogs
{
    public readonly List<(double T, ShotMsg M)> Shots = new();
    public readonly List<(double T, HitMsg M)> Hits = new();
    public readonly List<(double T, LaunchMsg M)> Launches = new();
    public readonly List<(double T, ExplosionMsg M)> Explosions = new();
    public readonly List<(double T, VehicleExplosionMsg M)> VehicleExplosions = new();
}

/// <summary>
/// What the scripted bots (<see cref="ClientBot"/>) read of the running client and the few things they do to it: the state their scenarios assert on, the event logs, the link's end. The client
/// implements it; the bots never see the client class itself.
/// </summary>
public interface IBotHost
{
    ClientContext Ctx { get; }
    /// <summary>Physics seconds since the first tick.</summary>
    double Elapsed { get; }
    /// <summary>How long the bot plays (<c>--bot-seconds</c>).</summary>
    double Seconds { get; }
    bool LinkEnded { get; }
    /// <summary>The own snapshot has been applied (the player stands where the server put them).</summary>
    bool OwnSpawned { get; }
    EntityState MyState { get; }
    bool Driving { get; }
    bool SandboxGranted { get; }
    /// <summary>The player holds the rozhlas set (the dictation table's mic).</summary>
    bool HoldsRozhlasSet { get; }
    AmmoMsg? Ammo { get; }
    /// <summary>The hotbar slot the bot wants selected (sent with every input).</summary>
    int WantSlot { set; }
    /// <summary>The newest decoded snapshot's records by entity id.</summary>
    IReadOnlyDictionary<ushort, EntityRecord> States { get; }
    EventLogs Logs { get; }
    IEnumerable<BotCar> Cars { get; }
    BotCar? Car(ushort id);
    IEnumerable<BotRemote> Remotes { get; }
    int RocketCount { get; }
    VehiclesFile? Vehicles { get; }

    // what the reports print
    int Snaps { get; }
    int Recon { get; }
    float MaxErr { get; }
    uint LastTick { get; }
    double LastArrival { get; }
    ushort Seq { get; }
    /// <summary>The metres the own predicted position spanned (x, z).</summary>
    float OwnTravel { get; }
    float BestRemoteTravel { get; }
    RubberStats Rubber { get; set; }
    NetSim? Sim { get; }
    int DriveCorrections { get; }
    /// <summary>Bytes received and sent since the last call.</summary>
    (double Received, double Sent) PopTraffic();

    /// <summary>The link is left (the run is over; the report follows).</summary>
    void Leave();
    void Quit(bool ok);
    /// <summary>Sends a chat line or command to the server.</summary>
    void SendChat(string text);
    /// <summary>Sends one packet of the rozhlas mic.</summary>
    void SendMic(byte[] packet);
}
