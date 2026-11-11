using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>Ticket #143: kills made with a killstreak's own weapon never advance the Streak counter (HUD tracker and server).</summary>
public class StreakWeaponKillTests
{
    static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    static readonly KillstreakFile Streaks = GameData.Load(DataDir).Killstreaks;

    [Theory]
    [InlineData(244)]
    [InlineData(245)]
    [InlineData(246)]
    [InlineData(247)]
    [InlineData(248)]
    [InlineData(249)]
    public void StreakWeaponIdsAreStreakWeapons(byte weapon) => Assert.True(StreakWeapons.Is(weapon));

    [Theory]
    [InlineData(0)]
    [InlineData(243)]
    [InlineData(250)]  // jeep roof gun: not a streak weapon (correction on #133)
    [InlineData(251)]
    [InlineData(255)]
    public void OrdinaryWeaponsAndSpecialDeathsAreNot(byte weapon) => Assert.False(StreakWeapons.Is(weapon));

    [Fact]
    public void VehicleRoofGunKillsAdvanceTheCounter()
    {
        var t = new KillstreakTracker(Streaks);
        t.Arm(new[] { "uav", "", "" }, 0f);
        t.OnKill(StreakWeapons.Is(SpecialWeapons.VehicleGun));
        Assert.Equal(1, t.Kills);
    }

    [Fact]
    public void TrackerSkipsStreakWeaponKills()
    {
        var t = new KillstreakTracker(Streaks);
        t.Arm(new[] { "uav", "", "" }, 0f);
        t.OnKill(false);
        t.OnKill(StreakWeapons.Is(SpecialWeapons.Missile));
        t.OnKill(StreakWeapons.Is(SpecialWeapons.Turret));
        t.OnKill(StreakWeapons.Is(0));
        Assert.Equal(2, t.Kills);
    }

    [Fact]
    public void ServerCounterSkipsStreakWeaponKillsAndStillCountsOthers()
    {
        const ushort owner = 7, enemy = 8;
        var service = new KillstreakService(Streaks,
            Streaks.Streaks.Select(s => (IKillstreak)new LoggingKillstreak(s.Id)),
            (_, _, _) => { }, _ => { });
        service.Arm(owner, new[] { "uav", "", "" }, 0f);
        service.OnKill(owner, enemy, fromStreak: true);  // a streak's own weapon: not counted
        service.OnKill(owner, enemy, fromStreak: false);
        Assert.Equal(1, service.KillCount(owner));
    }
}
