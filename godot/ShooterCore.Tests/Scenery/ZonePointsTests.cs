using System.Numerics;
using Xunit;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Scenery;

namespace Drycastle.ShooterCore.Tests.Scenery;

/// <summary>The baked candidate points of the zone kinds (zone_points.json, ADR 0022) and the server's random pick from them.</summary>
public class ZonePointsTests
{
    // as the pipeline writes it: local metres, x east, y north, z the terrain height
    const string Json = """
    {
      "version": 2,
      "axes": "points: local metres, x east, y north (as parcels.json), z the terrain height (as terrain.json)",
      "zones": [
        { "kind": "bigfoot", "areas": ["A-0003", "Z-0001"], "area_m2": 52000.5,
          "points": [[10.0, 20.0, 101.5], [30.0, -40.0, 102.0], [-5.5, 7.25, 99.125]] },
        { "kind": "deer", "areas": [], "area_m2": 0.0, "points": [] }
      ]
    }
    """;

    [Fact]
    public void Points_are_converted_to_Godot_ground_axes()
    {
        var points = ZonePoints.Parse(Json).Points("bigfoot");
        Assert.Equal(3, points.Count);
        Assert.Equal(new Vector3(10f, 101.5f, -20f), points[0]); // north is -z, the height is y
        Assert.Equal(new Vector3(30f, 102f, 40f), points[1]);
        Assert.Equal(new Vector3(-5.5f, 99.125f, -7.25f), points[2]);
    }

    [Fact]
    public void Pick_returns_a_point_of_the_pool_and_follows_the_generator()
    {
        var pool = ZonePoints.Parse(Json);
        var all = pool.Points("bigfoot").ToHashSet();
        var a = new Random(42);
        var b = new Random(42);
        for (var i = 0; i < 50; i++)
        {
            var p = pool.Pick("bigfoot", a);
            Assert.NotNull(p);
            Assert.Contains(p!.Value, all);
            Assert.Equal(p, pool.Pick("bigfoot", b)); // the same generator state, the same point
        }
    }

    [Fact]
    public void Pick_reaches_every_point_of_the_pool()
    {
        var pool = ZonePoints.Parse(Json);
        var rng = new Random(1);
        var seen = new HashSet<Vector3>();
        for (var i = 0; i < 200; i++) seen.Add(pool.Pick("bigfoot", rng)!.Value);
        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public void A_kind_with_no_points_yields_no_point()
    {
        var pool = ZonePoints.Parse(Json);
        var rng = new Random(1);
        Assert.Null(pool.Pick("deer", rng)); // listed, but nothing usable was left of its areas
        Assert.Null(pool.Pick("nothing", rng)); // not listed: no zone of it
        Assert.Null(pool.Pick("player_spawn", rng)); // not exported as points
        Assert.Equal(0, pool.Count("deer"));
        Assert.Equal(3, pool.Count("bigfoot"));
    }

    [Fact]
    public void A_map_without_the_file_has_no_points_and_no_error()
    {
        var rng = new Random(1);
        Assert.Null(ZonePoints.FromJson("").Pick("bigfoot", rng));
        Assert.Null(ZonePoints.FromJson(null).Pick("bigfoot", rng));
        Assert.Null(ZonePoints.Empty.Pick("bigfoot", rng));
        Assert.Empty(ZonePoints.Empty.Points("bigfoot"));
    }

    [Fact]
    public void Unknown_fields_are_ignored_and_another_version_or_broken_file_is_refused()
    {
        var withExtras = Json.Replace("\"version\": 2,", "\"version\": 2, \"something_new\": {\"a\": 1},");
        Assert.Equal(3, ZonePoints.Parse(withExtras).Count("bigfoot"));

        Assert.Throws<DataFileException>(() => ZonePoints.Parse(Json.Replace("\"version\": 2,", "\"version\": 1,"))); // the old format
        Assert.Throws<DataFileException>(() => ZonePoints.Parse(Json.Replace("\"version\": 2,", "\"version\": 3,"))); // a newer one
        Assert.Throws<DataFileException>(() => ZonePoints.Parse(Json.Replace("\"version\": 2,", "")));
        Assert.Throws<DataFileException>(() => ZonePoints.Parse("not json"));
        Assert.Throws<DataFileException>(() => ZonePoints.Parse("[]"));
        Assert.Throws<DataFileException>(() => ZonePoints.Parse("""{ "version": 2 }"""));
        Assert.Throws<DataFileException>(() => ZonePoints.Parse(Json.Replace("[10.0, 20.0, 101.5]", "[10.0, 20.0]"))); // a point needs x, y, z
        Assert.Throws<DataFileException>(() => ZonePoints.Parse(Json.Replace("\"kind\": \"deer\"", "\"kind\": \"bigfoot\""))); // listed twice
        Assert.Throws<DataFileException>(() => ZonePoints.Parse(Json.Replace("\"kind\": \"deer\", ", ""))); // an entry without kind
    }

    [Fact]
    public void A_refused_file_is_reported_and_counts_as_no_points()
    {
        var messages = new List<string>();
        var pool = ZonePoints.FromJsonOrEmpty("""{ "version": 9, "zones": [] }""", messages.Add);
        Assert.Single(messages);
        Assert.Contains("zone_points.json", messages[0]);
        Assert.Null(pool.Pick("bigfoot", new Random(1)));
        var old = ZonePoints.FromJsonOrEmpty("""{ "version": 1, "attributes": [] }""", messages.Add);
        Assert.Equal(2, messages.Count);
        Assert.Null(old.Pick("bigfoot", new Random(1)));
        ZonePoints.FromJsonOrEmpty(Json, messages.Add);
        Assert.Equal(2, messages.Count); // a good file reports nothing
    }
}
