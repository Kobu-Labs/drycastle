namespace Drycastle.ShooterCore;

/// <summary>
/// The kill-feed weapon ids of killstreak weapons (ticket #143). A kill made with one of them does not advance the Streak
/// counter, on the server (<see cref="KillstreakService"/>) or in the HUD tracker (<see cref="KillstreakTracker"/>).
/// The ids are the ones <see cref="SpecialWeapons"/> already names (turret 247, missile 248, moab 249), plus the three
/// aircraft streaks (helper 245, heli 246, ac130 244), reserved here so the aircraft tickets use the same ids.
/// The jeep's roof gun (<see cref="SpecialWeapons.VehicleGun"/>, 250) is NOT a streak weapon: its kills advance the
/// counter (correction on #133).
/// </summary>
public static class StreakWeapons
{
    /// <summary>AC-130 gunship weapon (the AC-130 streak's kills).</summary>
    public const byte Ac130 = SpecialWeapons.Ac130;
    /// <summary>The helicopter helper's rounds (its kills are the streak's).</summary>
    public const byte Helper = SpecialWeapons.Helper;
    /// <summary>Attack helicopter weapon (the attack helicopter streak's kills).</summary>
    public const byte Heli = SpecialWeapons.AttackHelicopter;

    /// <summary>Whether a kill-feed weapon id is a killstreak's own weapon.</summary>
    public static bool Is(byte weapon) =>
        weapon is Ac130 or Helper or Heli or SpecialWeapons.Turret or SpecialWeapons.Missile or SpecialWeapons.Moab;
}
