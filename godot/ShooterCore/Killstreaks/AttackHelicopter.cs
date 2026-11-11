using System.Numerics;

namespace Suchohrad.ShooterCore;

/// <summary>
/// The attack helicopter killstreak (killstreaks.json "attack_helicopter", ticket #126): a helicopter circles the map and its
/// owner flies the turret's chain gun (the gunship's "cannon") from a gunner's point of view for the streak's duration.
/// </summary>
/// <remarks>
/// Decisions (documented in the ticket): the owner's body stands still and takes no damage while they hold the gun (it
/// is a sitting target otherwise, and the helicopter cannot be shot down in v2); the rockets are not part of this streak;
/// the circle is centred on the map's centre and the helicopter starts on the owner's bearing from it; the streak ends
/// at its duration (the gunner is returned to the body) or when the owner dies or leaves.
/// </remarks>
public static class AttackHelicopterStreak
{
    public const string StreakId = "attack_helicopter";

    /// <summary>The turret weapon the pilot fires (a weapon id from killstreaks.json "gunship").</summary>
    public const string GunWeaponId = "cannon";

    /// <summary>The killstreaks.json aircraft kind it flies.</summary>
    public const string AircraftKind = "helicopter";
}

/// <summary>What the attack helicopter needs from the server, so the streak runs without Godot.</summary>
public interface IAttackHelicopterWorld
{
    /// <summary>Centre of the helicopter's circle (the map's centre, on the ground).</summary>
    Vector3 Centre { get; }

    /// <summary>
    /// Spawns a helicopter circling <paramref name="centre"/> from <paramref name="phaseRad"/>, owned by
    /// <paramref name="owner"/>, with the owner's gunner input and the cannon only. Null when there is no room.
    /// </summary>
    AttackHelicopterLaunch? Launch(ushort owner, Vector3 centre, float phaseRad);

    /// <summary>Removes the helicopter (no-op when it is already gone).</summary>
    void Remove(ushort aircraftId);
}

/// <summary>A spawned helicopter: its world entity id and the gunner's input handler for its owner.</summary>
public sealed record AttackHelicopterLaunch(ushort Id, Action<NetInput> GunnerInput);

/// <summary>
/// The attack helicopter as an <see cref="IKillstreak"/>. Activation launches the helicopter, hands the owner the gun
/// (freezing and shielding their body) and ends the streak at its duration, which removes the helicopter.
/// </summary>
public sealed class AttackHelicopterStreakImpl : IKillstreak
{
    readonly IAttackHelicopterWorld _world;

    public AttackHelicopterStreakImpl(IAttackHelicopterWorld world) => _world = world;

    public string Id => AttackHelicopterStreak.StreakId;

    /// <summary>Its kills are credited under the special weapon id, so they pay the streak award and do not advance the counter.</summary>
    public IReadOnlySet<string> OwnedWeapons { get; } = new HashSet<string> { AttackHelicopterStreak.StreakId };

    public bool Activate(KillstreakUse use)
    {
        var centre = _world.Centre;
        // start on the owner's bearing from the centre, so the helicopter comes round towards them first
        var phase = MathF.Atan2(use.Position.Z - centre.Z, use.Position.X - centre.X);
        var launch = _world.Launch(use.Owner, centre, phase);
        if (launch == null)
        {
            use.Log("no room for the helicopter (entity cap or aircraft kind missing)");
            return false;
        }
        if (!use.TakeControl(launch.Id, launch.GunnerInput, freezesBody: true, shieldsBody: true))
        {
            _world.Remove(launch.Id);
            return false;
        }
        use.OnEnded(_ => _world.Remove(launch.Id));
        return true;
    }
}
