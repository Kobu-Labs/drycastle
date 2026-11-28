using System.Numerics;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Fsm;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Cartel;

/// <summary>The Trap house heist's rules (#496, docs/cartel.md "Trap house"): cartel.json jobs.trap_house, the defenders, the table Duffels, the area, the line and the marker on the safe.</summary>
public class TrapHouseHeistTests
{
    public TrapHouseHeistTests() => StateMachineDiagnostics.Strict = true;

    static readonly CartelFile Shipped = GameData.Load(Path.Combine(AppContext.BaseDirectory, "data")).Cartel;
    const float Dt = 1f / 30;

    [Fact]
    public void TheShippedSectionIsTheDefaultsAndChecksOut()
    {
        var shipped = DataFile.Section<TrapHouseHeistData>(Shipped.JobSection("trap_house"), "jobs.trap_house");
        shipped.ValidateTrapHouse("jobs.trap_house", Shipped.Tiers.Count);
        var d = new TrapHouseHeistData();
        Assert.Equal("Trap house", shipped.Name);
        Assert.Equal(d.MinDuffels, shipped.MinDuffels);
        Assert.Equal(d.MaxDuffels, shipped.MaxDuffels);
        Assert.Equal(d.DefenderGunmen, shipped.DefenderGunmen);
        Assert.Equal(d.DefenderEnforcers, shipped.DefenderEnforcers);
        Assert.Equal((d.MinPerExtraMember, d.DefenderGunmenPerExtraMember, d.MaxDefenders, d.AreaM), (shipped.MinPerExtraMember, shipped.DefenderGunmenPerExtraMember, shipped.MaxDefenders, shipped.AreaM));
    }

    [Fact]
    public void ABadSectionIsRefused()
    {
        var d = new TrapHouseHeistData { DefenderGunmen = new[] { 1 } };
        Assert.Throws<DataFileException>(() => d.ValidateTrapHouse("x", 5));
        Assert.Throws<DataFileException>(() => new TrapHouseHeistData { AreaM = 0 }.ValidateTrapHouse("x", 5));
    }

    [Fact]
    public void DefendersScaleBySquadAndStopAtThePostsAndTheCap()
    {
        var d = new TrapHouseHeistData { DefenderGunmen = new[] { 2, 2, 3, 3, 4 }, DefenderEnforcers = new[] { 1, 2, 2, 3, 3 }, DefenderGunmenPerExtraMember = 1, MaxDefenders = 6 };
        Assert.Equal((2, 1), TrapHouseHeistRules.Defenders(d, 1, 1, 20));
        Assert.Equal((4, 1), TrapHouseHeistRules.Defenders(d, 1, 3, 20));
        Assert.Equal((4, 2), TrapHouseHeistRules.Defenders(d, 5, 1, 20)); // the cap: Gunmen first
        Assert.Equal((3, 0), TrapHouseHeistRules.Defenders(d, 3, 1, 3)); // only three posts
        Assert.Equal((0, 0), TrapHouseHeistRules.Defenders(d, 3, 1, 0));
    }

    [Fact]
    public void TheTablesHoldTheMaximumLessTheSafeWithinTheSpots()
    {
        Assert.Equal(3, TrapHouseHeistRules.TableDuffels(spots: 8, min: 2, max: 5, safeCash: 2));
        Assert.Equal(2, TrapHouseHeistRules.TableDuffels(spots: 8, min: 2, max: 3, safeCash: 4)); // never below the minimum
        Assert.Equal(4, TrapHouseHeistRules.TableDuffels(spots: 4, min: 2, max: 9, safeCash: 0)); // never past the spots
    }

    [Fact]
    public void TheCentreIsTheShellsMeanOnGodotAxesAndTheAreaCoversHouseAndRoad()
    {
        var house = new HouseFloorPlan("h", "P", 0, 3, new[] { new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10), new Vector2(0, 10) }, 0, 3, Array.Empty<PlanDoor>(), Array.Empty<PlanRoom>());
        var c = TrapHouseHeistRules.Centre(house);
        Assert.Equal(new Vector2(5, -5), c);
        var road = new Vector2(5, 30);
        Assert.True(TrapHouseHeistRules.InArea(c, road, 20, 5, 10));
        Assert.True(TrapHouseHeistRules.InArea(c, road, 20, 5, 45)); // by the road spot
        Assert.False(TrapHouseHeistRules.InArea(c, road, 20, 60, 0));
    }

    [Fact]
    public void TheLineAndTheMarkerFollowTheDrillWhileLoadingAtTheHouse()
    {
        var heist = new Heist(2, 5, 100);
        Assert.Equal("x", TrapHouseHeistRules.Line(heist, "x", DrillState.Armed)); // on the way: the template's
        Assert.False(TrapHouseHeistRules.MarkSafe(heist, DrillState.Running));
        heist.Step(new HeistFacts(false, true, false, 0), Dt);
        Assert.Equal(HeistPhase.AtObjective, heist.Phase);
        Assert.Contains("hold {interact} to set the drill", TrapHouseHeistRules.Line(heist, "x", DrillState.Armed)); // the client names the bound key
        Assert.Contains("fix it", TrapHouseHeistRules.Line(heist, "x", DrillState.Jammed));
        Assert.Equal("x", TrapHouseHeistRules.Line(heist, "x", DrillState.Done));
        Assert.Equal("x", TrapHouseHeistRules.Line(heist, "x", null));
        Assert.True(TrapHouseHeistRules.MarkSafe(heist, DrillState.Running));
        Assert.True(TrapHouseHeistRules.MarkSafe(heist, DrillState.Jammed));
        Assert.True(TrapHouseHeistRules.MarkSafe(heist, DrillState.Armed)); // to be set: the way to the safe, not the van
        Assert.False(TrapHouseHeistRules.MarkSafe(heist, DrillState.Done));
    }

    [Fact]
    public void TheHouseKeyIsItsOwn() => Assert.Equal("trap_house:w12", TrapHouseHeistRules.Key("w12"));
}
