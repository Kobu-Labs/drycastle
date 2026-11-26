using Drycastle.ShooterCore.Buildings.Destruction;
using Drycastle.ShooterCore.Data;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Buildings.Destruction;

public class DestructionConfigTests
{
    static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "data");

    [Fact]
    public void Shipped_file_loads_into_integers()
    {
        var c = GameData.Load(DataDir).HouseDestruction;
        Assert.True(c.Enabled);
        Assert.Equal(16, c.Resist(CellMaterial.Wall));
        Assert.Equal(19, c.Resist(CellMaterial.Floor));     // 1.2 * 16 = 19.2
        Assert.Equal(0, c.Resist(CellMaterial.Foundation));
        Assert.Equal(4, c.Weight(CellMaterial.Wall));
        Assert.Equal(400, c.Strength(CellMaterial.Roof));
        Assert.Equal(4, c.Support.MaxPasses);
        Assert.Equal(24, c.Fracture.CellsPerPiece);
        Assert.Equal(RubbleMode.Fade, c.Debris.Rubble);
        Assert.Equal(2.0f, c.CarveRadius(BlastSource.Rpg, 5));
        Assert.Equal(1.6f, c.CarveRadius(BlastSource.GunshipRocket, 8));
        Assert.Equal(0f, c.CarveRadius(BlastSource.Flak, 5));
        Assert.Equal(1f, c.CarveRadius(BlastSource.Other, 5), 4);
        // the shipped file equals the built-in defaults
        var d = DestructionConfig.Default;
        foreach (var m in Enum.GetValues<CellMaterial>())
            Assert.Equal((d.Resist(m), d.Weight(m), d.Strength(m)), (c.Resist(m), c.Weight(m), c.Strength(m)));
    }

    [Fact]
    public void Unknown_source_or_material_is_refused()
    {
        Assert.Throws<DataFileException>(() => DestructionConfig.Parse("""{"version":1,"carve":{"by_source":{"nuke":3}}}"""));
        Assert.Throws<DataFileException>(() => DestructionConfig.Parse("""{"version":1,"materials":{"glass":{"resist":1}}}"""));
        Assert.Throws<DataFileException>(() => DestructionConfig.Parse("""{"version":1,"materials":{"wall":{"resist":1}}}""")); // the others missing
        Assert.Equal(RubbleMode.Stay, DestructionConfig.Parse("""{"version":1,"debris":{"rubble":"stay"}}""").Debris.Rubble);
    }
}
