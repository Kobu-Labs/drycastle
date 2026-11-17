using Drycastle.ShooterCore;
using Xunit;

namespace Drycastle.ShooterCore.Tests;

/// <summary>
/// The round of a vehicle machine gun, the numbers the Humvee's roof gun, the amphibious transport's ring gun and the utility helicopter's door gun share
/// (the old jeep's roof gun, #325). Each of those types carries them in its own `gun` block in vehicles.json: the jeep is a truck without a gun now, so the
/// tests no longer read them off it. The helicopter fires the same interval and spread with a harder hit; the amphibious one a bigger belt.
/// </summary>
static class VehicleGunRound
{
    public const float Damage = 14, FireInterval = 0.1f, Range = 220, SpreadDegrees = 1.2f;
    public const int HumveeMagazine = 60, HumveeReserve = 240;
    public const float HumveeReloadTime = 2.5f;

    /// <summary>The Humvee's `gun` block is the round with its own belt.</summary>
    public static void AssertIsTheRound(VehicleGunDef g)
    {
        Assert.Equal((Damage, FireInterval, Range, SpreadDegrees), (g.Damage, g.FireInterval, g.Range, g.SpreadDegrees));
    }
}
