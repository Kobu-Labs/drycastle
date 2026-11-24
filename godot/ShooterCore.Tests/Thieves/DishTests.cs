using System.Numerics;
using Drycastle.ShooterCore.Colliders;
using Drycastle.ShooterCore.Data;
using Drycastle.ShooterCore.Thieves;
using Xunit;

namespace Drycastle.ShooterCore.Tests.Thieves;

/// <summary>The dish target of the Thieves event (docs/events/thieves/06): targets from placed objects, the taken set and its wire form, no collision for the asset. Synthetic objects: the village has no dish placed.</summary>
public class DishTests
{
    sealed class FakeDishes : IDishWorld
    {
        public readonly List<DishSite> List = new();
        public float Ground = 0.2f;
        public bool HasMesh = true;
        /// <summary>Points the mesh does not reach (flat distance from them to the mesh is this far): the mesh is "everywhere but" the listed discs.</summary>
        public readonly List<(Vector2 At, float R)> Holes = new();
        public int Snaps;
        public IReadOnlyList<DishSite> Sites => List;
        public float GroundAt(float x, float z) => Ground;
        public bool TrySnap(Vector3 p, out Vector3 snapped)
        {
            snapped = p;
            Snaps++;
            if (!HasMesh) return false;
            foreach (var (at, r) in Holes)
                if (Vector2.Distance(new Vector2(p.X, p.Z), at) <= r) snapped = new Vector3(p.X + 5f, p.Y, p.Z);
            return true;
        }
        public FakeDishes Add(string id, float x, float y, float z, float yawDeg) { List.Add(new DishSite(id, new Vector3(x, y, z), yawDeg)); return this; }
    }

    static DishSource Make(IDishWorld w, DishLootConfig? cfg = null) => new(cfg ?? new DishLootConfig(), w);

    [Fact]
    public void Zero_dishes_are_no_targets_and_no_places()
    {
        var s = Make(new FakeDishes());
        var into = new List<StealTarget>(); var places = new List<Vector2>();
        s.FreeNear(Vector3.Zero, 1000, into); s.Places(places);
        Assert.Empty(into); Assert.Empty(places);
        Assert.False(s.TryFind(0, out _));
        s.Take(0); s.Return(0); s.Reset(); // nothing happens, nothing throws
        Assert.Equal(0, s.TakenCount);
        Assert.Empty(s.TakenObjectIds);
    }

    [Theory]
    [InlineData(0f, 1f, 0f)]
    [InlineData(90f, 0f, -1f)]
    [InlineData(180f, -1f, 0f)]
    [InlineData(270f, 0f, 1f)]
    public void Target_comes_from_the_objects_pose(float yaw, float outX, float outZ)
    {
        var cfg = new DishLootConfig();
        var w = new FakeDishes().Add("o-1", 10, 4.2f, 20, yaw);
        var s = Make(w, cfg);
        Assert.True(s.TryFind(0, out var t));
        Assert.Equal(LootKind.Dish, t.Kind);
        Assert.Equal(new Vector3(10, 4.2f, 20), t.Position);
        // the base is out from the wall along the way it faces, on the ground
        Assert.Equal(10 + outX * cfg.BaseOutM, t.WorkSpot.X, 3);
        Assert.Equal(20 + outZ * cfg.BaseOutM, t.WorkSpot.Z, 3);
        Assert.Equal(w.Ground, t.WorkSpot.Y, 3);
        // the climb goes straight up from the base to the dish's height less the reach
        Assert.True(t.NeedsClimb);
        Assert.Equal(t.WorkSpot.X, t.Top!.Value.X, 4);
        Assert.Equal(t.WorkSpot.Z, t.Top.Value.Z, 4);
        Assert.Equal(4.2f - cfg.ReachBelowM, t.Top.Value.Y, 3);
        var into = new List<StealTarget>();
        s.FreeNear(new Vector3(10, 0, 20), 5, into);
        Assert.Single(into);
        var places = new List<Vector2>(); s.Places(places);
        Assert.Equal(new Vector2(t.WorkSpot.X, t.WorkSpot.Z), Assert.Single(places));
    }

    [Fact]
    public void A_low_dish_is_still_climbed_to()
    {
        var cfg = new DishLootConfig();
        var s = Make(new FakeDishes { Ground = 0 }.Add("o-1", 0, 1.0f, 0, 0), cfg);
        Assert.True(s.TryFind(0, out var t));
        Assert.Equal(cfg.MinClimbM, t.Top!.Value.Y - t.WorkSpot.Y, 3);
    }

    [Fact]
    public void The_base_is_snapped_to_the_mesh()
    {
        var w = new FakeDishes().Add("o-1", 0, 4, 0, 0);
        w.Holes.Add((new Vector2(0.9f, 0), 0.5f)); // the base is in a hole: the mesh is 5 m off. Outside the limit.
        Assert.False(Make(w).TryFind(0, out _));
        var near = new FakeDishes().Add("o-1", 0, 4, 0, 0);
        var cfg = new DishLootConfig { SnapMaxM = 6f };
        near.Holes.Add((new Vector2(0.9f, 0), 0.5f));
        Assert.True(Make(near, cfg).TryFind(0, out var t));
        Assert.Equal(5.9f, t.WorkSpot.X, 3); // the work spot is the snapped point
    }

    [Fact]
    public void No_target_when_the_base_is_off_the_mesh_or_the_mesh_is_at_another_height()
    {
        var off = new FakeDishes().Add("a", 0, 4, 0, 0);
        off.Holes.Add((new Vector2(0.9f, 0), 0.5f));
        var s = Make(off);
        var into = new List<StealTarget>(); var places = new List<Vector2>();
        s.FreeNear(Vector3.Zero, 100, into); s.Places(places);
        Assert.Empty(into); Assert.Empty(places);
        Assert.False(s.TryFind(0, out _));

        // a mesh a floor above the ground under the dish: off by more than snap_max_dy_m
        var dy = new FakeDishes { Ground = 0 }.Add("a", 0, 4, 0, 0);
        Assert.False(Make(new HeightMesh(dy, 3f)).TryFind(0, out _));
    }

    sealed class HeightMesh : IDishWorld
    {
        readonly FakeDishes _w; readonly float _dy;
        public HeightMesh(FakeDishes w, float dy) { _w = w; _dy = dy; }
        public IReadOnlyList<DishSite> Sites => _w.Sites;
        public float GroundAt(float x, float z) => _w.GroundAt(x, z);
        public bool TrySnap(Vector3 p, out Vector3 snapped) { snapped = p + new Vector3(0, _dy, 0); return true; }
    }

    [Fact]
    public void No_mesh_yet_means_no_target_now_and_one_later()
    {
        var w = new FakeDishes { HasMesh = false }.Add("a", 0, 4, 0, 0);
        var s = Make(w);
        Assert.False(s.TryFind(0, out _));
        w.HasMesh = true; // the navigation mesh is built later: it was not remembered as a no
        Assert.True(s.TryFind(0, out _));
    }

    [Fact]
    public void The_target_is_worked_out_once()
    {
        var w = new FakeDishes().Add("a", 0, 4, 0, 0);
        var s = Make(w);
        for (var i = 0; i < 10; i++) Assert.True(s.TryFind(0, out _));
        Assert.Equal(1, w.Snaps);
    }

    [Fact]
    public void Take_Return_and_Reset()
    {
        var w = new FakeDishes().Add("o-1", 0, 4, 0, 0).Add("o-2", 30, 4, 0, 90);
        var s = Make(w);
        var changes = 0;
        s.TakenChanged += () => changes++;
        Assert.True(s.TryFind(1, out _));

        s.Take(1);
        Assert.True(s.IsTaken(1));
        Assert.False(s.TryFind(1, out _)); // gone from its place
        var into = new List<StealTarget>(); s.FreeNear(new Vector3(15, 0, 0), 100, into);
        Assert.Equal(0, Assert.Single(into).Id);
        var places = new List<Vector2>(); s.Places(places);
        Assert.Single(places);
        Assert.Equal(new[] { "o-2" }, s.TakenObjectIds);
        Assert.Equal(1, changes);

        s.Take(1); // already taken: no change
        Assert.Equal(1, changes);

        s.Return(1);
        Assert.False(s.IsTaken(1));
        Assert.True(s.TryFind(1, out _));
        Assert.Equal(2, changes);
        s.Return(1); // not taken: no change
        Assert.Equal(2, changes);

        s.Take(0); s.Take(1);
        Assert.Equal(new[] { "o-1", "o-2" }, s.TakenObjectIds);
        s.Reset();
        Assert.Equal(0, s.TakenCount);
        Assert.True(s.TryFind(0, out _) && s.TryFind(1, out _));
        Assert.Equal(5, changes); // the reset is one change; a reset of an empty set is none
        s.Reset();
        Assert.Equal(5, changes);
    }

    [Fact]
    public void Take_of_a_dish_that_is_not_there_is_ignored()
    {
        var s = Make(new FakeDishes().Add("a", 0, 4, 0, 0));
        s.Take(7); s.Take(-1);
        Assert.Equal(0, s.TakenCount);
    }

    [Fact]
    public void Registry_resets_the_taken_set_and_lists_only_the_free_dishes()
    {
        var w = new FakeDishes().Add("a", 0, 4, 0, 0).Add("b", 10, 4, 0, 0);
        var s = Make(w);
        var reg = new LootRegistry();
        reg.Register(s);
        s.Take(0);
        var into = new List<StealTarget>(); reg.FreeNear(Vector3.Zero, 100, into);
        Assert.Equal(1, Assert.Single(into).Id);
        reg.Reset();
        into.Clear(); reg.FreeNear(Vector3.Zero, 100, into);
        Assert.Equal(2, into.Count);
    }

    [Fact]
    public void Taken_set_survives_a_join_and_the_codec_round_trip()
    {
        var w = new FakeDishes().Add("o-0007", 0, 4, 0, 0).Add("o-0003", 10, 4, 0, 0).Add("d-é", 20, 4, 0, 0);
        var server = Make(w);
        server.Take(0); server.Take(2);
        // what a joiner is sent: the whole set
        var wire = DishCodec.Encode(server.TakenObjectIds);
        var client = DishCodec.Decode(wire);
        Assert.NotNull(client);
        Assert.Equal(new[] { "d-é", "o-0007" }, client);
        // a change goes out as the whole set again, an empty one after a reset
        server.Reset();
        Assert.Equal(new List<string>(), DishCodec.Decode(DishCodec.Encode(server.TakenObjectIds)));
        Assert.Equal(2, DishCodec.Encode(Array.Empty<string>()).Length);
    }

    [Fact]
    public void Codec_refuses_what_is_not_a_set()
    {
        Assert.Null(DishCodec.Decode(Array.Empty<byte>()));
        Assert.Null(DishCodec.Decode(new byte[] { 1 }));
        Assert.Null(DishCodec.Decode(new byte[] { 1, 0 }));                  // one id promised, none there
        Assert.Null(DishCodec.Decode(new byte[] { 1, 0, 5, 65, 66 }));       // id cut short
        Assert.Null(DishCodec.Decode(new byte[] { 1, 0, 1, 65, 99 }));       // trailing byte
        Assert.Null(DishCodec.Decode(new byte[] { 1, 0, 0 }));               // empty id
        Assert.Equal(new List<string> { "A" }, DishCodec.Decode(new byte[] { 1, 0, 1, 65 }));
    }

    [Fact]
    public void The_dish_asset_has_no_collision_and_others_keep_theirs()
    {
        Assert.True(ObjectColliders.IsPassThrough(DishSource.AssetId));
        Assert.False(ObjectColliders.IsPassThrough("pack:drycastle-generated-models/bus"));
        Assert.Equal("pack:drycastle-generated-models/satellite_dish", DishSource.AssetId);
    }

    [Fact]
    public void An_object_placed_with_solid_false_has_no_collision()
    {
        const string bus = "pack:drycastle-generated-models/bus";
        Assert.False(ObjectColliders.IsPassThrough(bus, solid: true));
        Assert.True(ObjectColliders.IsPassThrough(bus, solid: false));
        Assert.True(ObjectColliders.IsPassThrough(DishSource.AssetId, solid: true));
    }

    [Fact]
    public void The_dish_config_is_in_thieves_json_and_checked()
    {
        var f = ThievesFile.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "thieves.json")));
        Assert.Same(f.Loot.Dish, f.Loot.Of(LootKind.Dish));
        Assert.True(f.Loot.Dish.BaseOutM > 0 && f.Loot.Dish.StealSeconds > 0);
        Assert.Throws<DataFileException>(() => new DishLootConfig { BaseOutM = 0 }.Validate("t.json", "dish"));
        Assert.Throws<DataFileException>(() => new DishLootConfig { SnapMaxM = -1 }.Validate("t.json", "dish"));
    }
}
