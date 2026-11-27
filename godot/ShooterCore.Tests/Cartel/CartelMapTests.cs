using System.Numerics;
using Drycastle.ShooterCore.Cartel;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Scenery;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Cartel;

/// <summary>The cartel's map data (docs/cartel.md): the Contact's pose from his marker, the Drop point and Hit spot pools and the picks from them.</summary>
public class CartelMapTests
{
    // as the pipeline writes it: local metres, x east, y north, z the terrain height
    const string Points = """
    {"version": 2, "zones": [
      {"kind": "bigfoot", "areas": ["Z-0001"], "area_m2": 1000, "points": [[500, 500, 1]]},
      {"kind": "cartel_drop", "areas": ["Z-0002"], "area_m2": 600, "points": [[10, 20, 100], [12, 20, 100], [200, 0, 101]]},
      {"kind": "cartel_hit", "areas": ["Z-0003"], "area_m2": 4000, "points": [[-50, -50, 99]]}
    ]}
    """;

    static readonly ContactFile Cfg = ContactFile.Parse("""{"version": 1, "facing": [0, 1]}""");

    [Fact]
    public void The_pools_are_the_cartel_kinds_in_Godot_axes()
    {
        var m = CartelMap.From(ZonePoints.Parse(Points), null);
        Assert.Equal(new[] { new Vector3(10, 100, -20), new Vector3(12, 100, -20), new Vector3(200, 101, 0) }, m.DropPoints);
        Assert.Equal(new Vector3(-50, 99, 50), Assert.Single(m.HitSpots));
    }

    [Fact]
    public void A_map_without_a_Contact_has_no_cartel_and_without_zones_no_points()
    {
        Assert.False(CartelMap.From(ZonePoints.Parse(Points), null).HasCartel);
        var bare = CartelMap.From(ZonePoints.Empty, new ContactPose(Vector3.Zero, new Vector2(0, 1)));
        Assert.True(bare.HasCartel);
        Assert.Empty(bare.DropPoints);
        Assert.Empty(bare.HitSpots);
        Assert.False(CartelMap.Empty.HasCartel);
    }

    [Fact]
    public void A_pick_takes_only_what_the_filter_allows_and_none_when_nothing_is_left()
    {
        var m = CartelMap.From(ZonePoints.Parse(Points), null);
        var taken = new[] { new Vector3(11, 100, -20) }; // a running job's drop point
        for (var seed = 0; seed < 20; seed++)
            Assert.Equal(new Vector3(200, 101, 0), CartelMap.Pick(m.DropPoints, new Random(seed), CartelMap.Apart(taken, 30f)));
        Assert.Null(CartelMap.Pick(m.DropPoints, new Random(1), _ => false));
        var seen = Enumerable.Range(0, 200).Select(s => CartelMap.Pick(m.DropPoints, new Random(s))!.Value).Distinct().Count();
        Assert.Equal(3, seen); // with no filter every point comes up
    }

    [Fact]
    public void Apart_measures_on_the_ground_only()
    {
        var far = CartelMap.Apart(new[] { new Vector3(0, 0, 0) }, 10f);
        Assert.False(far(new Vector3(6, 500, 6))); // 8.5 m on the ground, whatever the height
        Assert.True(far(new Vector3(10, 0, 0)));
    }

    [Fact]
    public void The_Contact_faces_the_markers_front_turned_with_it()
    {
        var straight = ContactPose.FromObject(new Vector3(5, 2, 7), 0f, Cfg);
        Assert.Equal(new Vector3(5, 2, 7), straight.Position);
        Assert.Equal(0f, straight.Facing.X, 4);
        Assert.Equal(1f, straight.Facing.Y, 4); // the marker's +Z (its arrow)
        Assert.Equal(180f, MathF.Abs(straight.YawDeg), 3); // a node facing +Z is turned half round
        var turned = ContactPose.FromObject(Vector3.Zero, 90f, Cfg);
        Assert.Equal(1f, turned.Facing.X, 4); // turned 90 deg, as a ladder of object_ladders.json
        Assert.Equal(0f, turned.Facing.Y, 4);
        Assert.Equal(-90f, turned.YawDeg, 3); // a node turned -90 deg looks along +X
        Assert.Equal(5f, straight.GroundDistance(new Vector3(8, 40, 11)), 4);
    }

    [Fact]
    public void The_marker_stops_nothing() => Assert.True(ObjectColliders.IsPassThrough(CartelZones.ContactAsset, solid: true));

    [Fact]
    public void Broken_files_are_refused()
    {
        Assert.Throws<DataFileException>(() => ContactFile.Parse("""{"version": 1, "facing": [0, 2]}"""));
        Assert.Throws<DataFileException>(() => ContactFile.Parse("""{"version": 1, "view": {"model": ""}}"""));
        Assert.Throws<DataFileException>(() => ContactFile.Parse("""{"version": 1, "view": {"scale": 0}}"""));
    }

    [Fact]
    public void ShippedFileParses() => ContactFile.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../shooter/data/contact.json")));
}
