namespace Suchohrad.ShooterCore;

/// <summary>
/// The kill-feed weapon ids of killstreak weapons (ticket #143). A kill made with one of them does not advance the Streak
/// counter, on the server (<see cref="KillstreakService"/>) or in the HUD tracker (<see cref="KillstreakTracker"/>).
/// The ids are the ones <see cref="SpecialWeapons"/> already names (turret 247, missile 248, moab 249, vehicle gun 250), plus
/// the three aircraft streaks (helper 245, heli 246, ac130 244), reserved here so the aircraft tickets use the same ids.
/// </summary>
public static class StreakWeapons
{
    /// <summary>AC-130 gunship weapon (reserved; the aircraft streak uses it).</summary>
    public const byte Ac130 = 244;
    /// <summary>Helper drone weapon (reserved).</summary>
    public const byte Helper = 245;
    /// <summary>Attack helicopter weapon (reserved).</summary>
    public const byte Heli = 246;

    /// <summary>Whether a kill-feed weapon id is a killstreak's own weapon.</summary>
    public static bool Is(byte weapon) =>
        weapon is Ac130 or Helper or Heli or SpecialWeapons.Turret or SpecialWeapons.Missile or SpecialWeapons.Moab or SpecialWeapons.VehicleGun;
}
