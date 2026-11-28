using System.Numerics;
using System.Text.Json;
using Drycastle.ShooterCore.Bunkers;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Mining;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Bunkers;

/// <summary>The radio tower (prim:radio_tower): the solids of its manifest entry and its ladder in object_ladders.json (#99) have to fit each other.</summary>
public class RadioTowerTests
{
    static readonly string Root = Path.Combine(AppContext.BaseDirectory, "../../../../../");

    static List<AssetSolid> Solids()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "assets/manifest.json")));
        var tower = doc.RootElement.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("id").GetString() == "prim:radio_tower");
        return ObjectColliders.ParseSolids(tower);
    }

    static Ladder TowerLadder(Vector2 origin, float y) =>
        ObjectLaddersFile.Parse(File.ReadAllText(Path.Combine(Root, "godot/shooter/data/object_ladders.json"))).Place("prim:radio_tower", origin, y, 0f, 1f).Single();

    // A mesh-frame point (x east, y north) of a tower placed at the origin with no turn: the ladder's ground z is minus the north.
    static bool Solid(IEnumerable<AssetSolid> solids, Vector2 groundXz, float height) =>
        solids.Any(s => s.Part is var p && MathF.Abs(groundXz.X - p.X) <= p.HalfU && MathF.Abs(-groundXz.Y - p.Y) <= p.HalfV && height >= p.Z0 && height <= p.Z1);

    [Fact]
    public void EveryTowerGetsOneLadderFromTheGroundToTheDeck()
    {
        var l = TowerLadder(new Vector2(100, 200), 50f);
        Assert.Equal(50f, l.Y0);
        Assert.Equal(50f + 33.92f, l.Y1, 2);
        Assert.Equal(new Vector2(100f, 201.55f), l.Rail);
        Assert.Equal(1f, l.Facing.Y, 3);
    }

    [Fact]
    public void TheLadderClimbsThroughTheHatchAndStepsOffOnTheSolidDeck()
    {
        var solids = Solids();
        var l = TowerLadder(Vector2.Zero, 0f);
        var deckTop = l.Y1;
        foreach (var h in new[] { 0.5f, 2f, 12f, 33f, deckTop - 0.05f })
            Assert.False(Solid(solids, l.Rail, h), $"the ladder is blocked at {h} m");
        var stepOff = l.Rail + l.Facing * Ladders.StepOffTop;
        Assert.True(Solid(solids, stepOff, deckTop - 0.01f), "the step-off at the top is not on the deck");
        Assert.False(Solid(solids, stepOff, deckTop + 0.5f), "no room to stand at the step-off");
        Assert.False(Solid(solids, l.Rail + l.Facing * Ladders.StepOffBottom, 0.5f), "the foot of the ladder is inside the cabin"); // the bottom step-off is the same spot, at the ground
    }

    [Fact]
    public void TheDeckIsBigWalledAllRoundAndHasOnlyTheHatchOpen()
    {
        var solids = Solids();
        var deck = solids.Where(s => MathF.Abs(s.Part.Z1 - 33.92f) < 1e-3f).Select(s => s.Part).ToList();
        Assert.True(deck.Max(p => p.X + p.HalfU) - deck.Min(p => p.X - p.HalfU) >= 4f);
        Assert.True(deck.Max(p => p.Y + p.HalfV) - deck.Min(p => p.Y - p.HalfV) >= 4f);
        Assert.False(Solid(solids, new Vector2(0f, -1.55f * -1f), 33.8f), "the hatch is closed"); // map (0, -1.55) is ground z +1.55
        Assert.True(Solid(solids, new Vector2(1.2f, 0.9f), 33.9f), "the deck beside the hatch is not solid");
        foreach (var (x, y) in new[] { (1.95f, 0f), (-1.95f, 0f), (0f, 2.95f), (0f, -2.95f) })
            Assert.True(Solid(solids, new Vector2(x, y), 34.8f), $"no railing at ({x}, {y})");
        Assert.True(Solid(solids, Vector2.Zero, 2.4f) && !Solid(solids, Vector2.Zero, 20f), "cabin solid to its roof, the lattice not");
    }

    [Fact]
    public void ThundernitFollowsNoStormRuleYet()
    {
        // #397: Thundernite is meant for a thunderstorm, but there is no weather (#230): its node counts as always active until MineralWorld learns thunder.
        Assert.True(MineralActivity.IsActive("thundernite", new MineralWorld()));
    }
}
