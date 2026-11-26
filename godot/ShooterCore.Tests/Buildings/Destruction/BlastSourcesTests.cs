using Drycastle.ShooterCore.Buildings.Destruction;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Flight;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Buildings.Destruction;

public class BlastSourcesTests
{
    static readonly GameData Data = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data"));

    [Fact]
    public void Rocket_sim_blasts_name_their_source()
    {
        var w = Data.Weapons.Weapons;
        Assert.Equal(BlastSource.Rpg, BlastSources.OfRocket(w.First(x => x.Id == "rpg"), false));
        Assert.Equal(BlastSource.Grenade, BlastSources.OfRocket(w.First(x => x.Id == "grenade"), false));
        Assert.Equal(BlastSource.GunshipRocket, BlastSources.OfRocket(w.First(x => x.Id == "rpg"), true)); // a gunship rocket flies as the rpg with its own blast
        Assert.Equal(BlastSource.Other, BlastSources.OfRocket(w.First(x => x.Id == "strela"), false));
    }

    [Fact]
    public void Gunship_rounds_name_their_source()
    {
        Assert.Equal(BlastSource.GunshipCannon, BlastSources.OfGunshipRound(GunshipWeaponKind.Shell, false));
        Assert.Equal(BlastSource.Cannon, BlastSources.OfGunshipRound(GunshipWeaponKind.Cannon, false));
        Assert.Equal(BlastSource.Flak, BlastSources.OfGunshipRound(GunshipWeaponKind.Shell, true));
    }

    [Fact]
    public void Shipped_radii_by_site()
    {
        var c = Data.HouseDestruction;
        var rpg = Data.Weapons.Weapons.First(x => x.Id == "rpg");
        Assert.Equal(2.0f, c.CarveRadius(BlastSources.OfRocket(rpg, false), rpg.Splash!.Radius));
        Assert.Equal(2.5f, c.CarveRadius(BlastSources.OfGunshipRound(GunshipWeaponKind.Shell, false), 14));
        Assert.Equal(0f, c.CarveRadius(BlastSources.OfGunshipRound(GunshipWeaponKind.Shell, true), 6)); // flak never carves
        Assert.Equal(10f, c.CarveRadius(BlastSource.Scud, 60));
        Assert.Equal(0f, c.CarveRadius(BlastSource.Demon, 4));
        Assert.Equal(6 * 0.2f, c.CarveRadius(BlastSources.OfRocket(Data.Weapons.Weapons.First(x => x.Id == "strela"), false), 6), 4);
    }
}
