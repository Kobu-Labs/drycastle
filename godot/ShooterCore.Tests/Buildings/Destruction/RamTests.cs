using System.Numerics;
using Drycastle.ShooterCore.Buildings.Destruction;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Buildings.Destruction;

/// <summary>HouseRam: a vehicle box against the fixture house's back wall (grid y = Ny - 2), driving +y.</summary>
public class RamTests
{
    static readonly DestructionConfig Cfg = DestructionConfig.Default;

    // a 32 x 24 x 20 box house; the back wall is the row y = 22, one cell thick, from z = 2 up to the ceiling at z = 12
    static HouseGrid House() => TestHouses.Box(32, 24, 20);

    /// <summary>A car-sized box (2.06 x 1.62 x 4.4 m) driving +y (north), its front <paramref name="gapM"/> short of the back wall's outer face... from inside: it drives out through it.</summary>
    static RamBox Car(HouseGrid g, float gapM, float halfWidth = 1.03f, float height = 1.62f)
    {
        var face = g.ToMap(new Vector3(16, 22, 2)); // the wall's inner face (y = 22), on top of the floor slab (z = 2)
        var centre = new Vector3(face.X, face.Y - gapM - 2.2f, face.Z + height / 2 + 0.02f);
        return new RamBox(centre, Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY, new Vector3(halfWidth, height / 2, 2.2f));
    }

    [Fact]
    public void Contact_is_the_wall_ahead_and_nothing_further()
    {
        var c = new HouseCells(House());
        var hit = HouseRam.Contact(c, Car(c.Grid, 0.05f), Vector3.UnitY, Cfg.Ram.ProbeM);
        Assert.NotNull(hit);
        Assert.InRange(hit.Value.Front, 2.2f, 2.3f);               // the face, 0.05 m past the box's front
        Assert.InRange(hit.Value.At.Y, 22 * 0.25f, 23 * 0.25f);    // in the wall's row
        Assert.Null(HouseRam.Contact(c, Car(c.Grid, 1.0f), Vector3.UnitY, Cfg.Ram.ProbeM));  // a metre short: out of reach
        Assert.Null(HouseRam.Contact(c, Car(c.Grid, 0.05f), -Vector3.UnitY, Cfg.Ram.ProbeM)); // driving away from it
    }

    [Fact]
    public void Hole_clears_the_cross_section_of_the_vehicle()
    {
        var g = House();
        var c = new HouseCells(g);
        var box = Car(g, 0.05f);
        var hit = HouseRam.Contact(c, box, Vector3.UnitY, Cfg.Ram.ProbeM)!.Value;
        var ops = HouseRam.Hole(g, box, Vector3.UnitY, hit.Front, Cfg.Ram);
        Assert.InRange(ops.Length, 1, Cfg.Ram.MaxOps);
        foreach (var op in ops) c.Apply(op, Cfg);
        Assert.Null(HouseRam.Contact(c, box, Vector3.UnitY, 1.5f)); // driving on through the wall meets nothing
        Assert.True(c.Solid(g.IndexOf(3, 22, 5)));   // the wall well to the side stands
    }

    [Fact]
    public void Momentum_decides_dent_or_through()
    {
        var g = House();
        var c = new HouseCells(g);
        var box = Car(g, 0.05f);
        var hit = HouseRam.Contact(c, box, Vector3.UnitY, Cfg.Ram.ProbeM)!.Value;
        var cost = HouseRam.Cost(c, HouseRam.Hole(g, box, Vector3.UnitY, hit.Front, Cfg.Ram), Cfg);
        Assert.InRange(cost, 40 * Cfg.Ram.MomentumPerCell, 120 * Cfg.Ram.MomentumPerCell); // the hole is some 60 to 90 wall cells
        // a car at 50 km/h has less than that: a dent smaller than the full one; a truck (6500 kg) at the same speed goes through
        var car = 1200f * 13.9f; var truck = 6500f * 13.9f;
        Assert.True(car < cost);
        Assert.True(truck > cost);
        var dent = HouseRam.Dent(g, hit.At, car, cost, Cfg.Ram)!.Value;
        Assert.InRange(dent.RadiusQ, 1, (int)(Cfg.Ram.DentMaxM / g.Cell * 4));
        Assert.Equal(13.9f - cost / 6500f, HouseRam.SpeedThrough(13.9f, cost, 6500f), 4);
        Assert.Equal(0f, HouseRam.SpeedThrough(13.9f, cost, 1200f));
    }

    [Fact]
    public void Cost_counts_each_cell_once_by_its_resist()
    {
        var g = House();
        var c = new HouseCells(g);
        var op = CarveOp.From(g, g.ToMap(new Vector3(16, 22.5f, 6)), 0.6f)!.Value;
        var once = HouseRam.Cost(c, [op], Cfg);
        Assert.Equal(once, HouseRam.Cost(c, [op, op], Cfg)); // the same cells twice count once
        var removed = c.Apply(op, Cfg).Carved.Length;
        Assert.Equal(removed * Cfg.Ram.MomentumPerCell, once, 1); // wall cells, resist 1.0
        Assert.Equal(0f, HouseRam.Cost(c, [op], Cfg));             // gone: nothing left to take
    }

    [Fact]
    public void Box_from_godot_faces_its_yaw()
    {
        var b = RamBox.FromGodot(new Vector3(1, 2, 3), 0, 1, 2, 1.6f);
        Assert.Equal(new Vector3(1, -3, 2.8f), b.Centre);
        Assert.Equal(0, Vector3.Distance(Vector3.UnitY, b.Fwd), 5); // yaw 0 faces -z in Godot: north
        Assert.Equal(new Vector3(5, 6, -7), RamBox.ToGodot(RamBox.ToMap(new Vector3(5, 6, -7))));
    }
}
