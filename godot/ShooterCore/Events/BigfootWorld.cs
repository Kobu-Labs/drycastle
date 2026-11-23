using System.Numerics;

namespace Drycastle.ShooterCore.Events;

/// <summary>A player as the Bigfoot sees them: where their feet are and whether they stand on the ground or ride a vehicle. Only living players are listed.</summary>
public readonly record struct BigfootPlayer(ushort Id, Vector3 Feet, bool OnFloor, bool InVehicle);

/// <summary>A vehicle it may throw: a ground vehicle that is not already flying. <see cref="Feet"/> is where it stands.</summary>
/// <summary>A vehicle the Bigfoot may take: where it stands and half its length (a bus is touched long before its middle is near).</summary>
public readonly record struct BigfootVehicle(ushort Id, Vector3 Feet, float HalfLength = 0);

/// <summary>What of the Bigfoot's damage a hurt was (the kill feed and the logs name it).</summary>
public enum BigfootHurt : byte { Trunk, Shockwave, Landing, LaunchHit }

/// <summary>The one-shot effects the clients play: a sound, a shake, a ring (<see cref="BigfootFx"/> event).</summary>
public enum BigfootFxKind : byte
{
    /// <summary>It appeared (the event began).</summary>
    Appear = 0,
    /// <summary>A phase began (the roar): u8 phase in the extra byte.</summary>
    Roar = 1,
    /// <summary>It began to wind up a stomp: the telegraph (a sound; the pose is in the entity's state).</summary>
    StompWindup = 2,
    /// <summary>The shockwave began at this point; clients draw a ring that grows at the shockwave's speed.</summary>
    Shockwave = 3,
    /// <summary>It released a log (a throw sound at its hand).</summary>
    TrunkThrow = 4,
    /// <summary>A vehicle was thrown: the vehicle's id (u16) follows, then the flight in tenths of a second (u8).</summary>
    VehicleThrow = 5,
    /// <summary>A thrown vehicle landed: its id (u16) follows.</summary>
    VehicleLand = 6,
    /// <summary>It picked a player up: the player's id (u16) follows.</summary>
    Grab = 7,
    /// <summary>It launched a player: the player's id (u16) follows.</summary>
    Launch = 8,
    /// <summary>It died.</summary>
    Death = 9,
    /// <summary>It healed after a wipe.</summary>
    Heal = 10,
}

/// <summary>
/// What the Bigfoot needs from the server: the ground and the solid world under it, the players and the vehicles, and the hands to hurt, hold, throw and
/// show. The server implements it (<c>Net/Server/BigfootEvent/BigfootServer.cs</c>); a test fakes it. Everything about the fight that needs the physics engine or a
/// connection is behind it, so the brain itself is plain code.
/// </summary>
public interface IBigfootWorld
{
    /// <summary>The ground height at (x, z) for something standing near height <paramref name="nearY"/>, or null where it must not go (no ground, water too deep). Trees are no ground.</summary>
    float? GroundY(float x, float z, float nearY);

    /// <summary>True when the solid world (a building, a wall: not a tree) stands between <paramref name="from"/> and <paramref name="to"/> for a body of <paramref name="radius"/>.</summary>
    bool Blocked(Vector3 from, Vector3 to, float radius);

    /// <summary>The living players now (on foot or riding), humans and bots alike.</summary>
    IReadOnlyList<BigfootPlayer> Players { get; }

    /// <summary>The ground vehicles that may be thrown now (not flying, not destroyed, not already thrown).</summary>
    IReadOnlyList<BigfootVehicle> Vehicles { get; }

    /// <summary>The Bigfoot made a log: the server spawns it as a world entity.</summary>
    void SpawnTrunk(Vector3 from, Vector3 velocity);

    /// <summary>The player takes <paramref name="damage"/> from the Bigfoot (2 to 5), the way <paramref name="how"/> says.</summary>
    void Hurt(ushort player, float damage, BigfootHurt how);

    /// <summary>The player is knocked with this velocity (the shockwave): an impulse on their body.</summary>
    void Knock(ushort player, Vector3 velocity);

    /// <summary>The player is held at <paramref name="at"/> this tick: the server puts their body there and ignores their input.</summary>
    void Hold(ushort player, Vector3 at);

    /// <summary>The player is let go and flies with <paramref name="velocity"/>; their fall damage is the launch's own for <paramref name="graceSeconds"/>.</summary>
    void Launch(ushort player, Vector3 velocity, float graceSeconds);

    /// <summary>The server lifts the vehicle from where it stands to <paramref name="hold"/> in <paramref name="seconds"/> and keeps it there, nobody steering, until <see cref="ThrowVehicle"/>.</summary>
    void LiftVehicle(ushort vehicle, Vector3 hold, float seconds);

    /// <summary>The Bigfoot lost the vehicle it lifted before it threw it (it was wiped, died or was removed): the server puts it down where it stood, at once.</summary>
    void DropVehicle(ushort vehicle);

    /// <summary>The server throws the vehicle on a scripted arc to <paramref name="landing"/>, in <paramref name="seconds"/>, turning at <paramref name="spinRadPerS"/>.</summary>
    void ThrowVehicle(ushort vehicle, Vector3 landing, float seconds, float spinRadPerS);

    /// <summary>A one-shot effect at a point; <paramref name="id"/> is the player or vehicle some of them name (0 otherwise).</summary>
    void Fx(BigfootFxKind kind, Vector3 at, ushort id = 0, byte extra = 0);
}
