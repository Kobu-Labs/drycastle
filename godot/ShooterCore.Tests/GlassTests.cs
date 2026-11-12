using System.Numerics;
using Suchohrad.ShooterCore;
using Xunit;

namespace Suchohrad.ShooterCore.Tests;

/// <summary>Glass panes (ADR 0007): hit points and state on the server, the wire, the client's copy, the pane geometry and the data files.</summary>
public class GlassTests
{
    static readonly GlassPaneKey Pane = new(GlassPaneKey.Map, 3);
    static readonly float[] Stages = { 0.66f, 0.33f };

    // ---- hit points and state ----

    [Fact]
    public void AnUntouchedPaneIsWholeAndUnknownToTheStore()
    {
        var g = new GlassPanes();
        Assert.Equal(60, g.Hp(Pane, 60));
        Assert.False(g.IsShattered(Pane));
        Assert.Equal(0, g.DamagedCount);
        Assert.Empty(g.Damaged());
        Assert.Empty(g.TakeChanges());
    }

    [Fact]
    public void DamageAddsUpAndThePaneShattersAtZero()
    {
        var g = new GlassPanes();
        Assert.True(g.Damage(Pane, 25, 60));
        Assert.Equal(35, g.Hp(Pane, 60));
        Assert.False(g.IsShattered(Pane));
        Assert.True(g.Damage(Pane, 25, 60));
        Assert.True(g.Damage(Pane, 25, 60)); // more than is left: it stops at zero
        Assert.Equal(0, g.Hp(Pane, 60));
        Assert.True(g.IsShattered(Pane));
    }

    [Fact]
    public void AShatteredPaneTakesNoMoreDamageAndReportsNoChange()
    {
        var g = new GlassPanes();
        g.Damage(Pane, 100, 60);
        g.TakeChanges();
        Assert.False(g.Damage(Pane, 10, 60));
        Assert.Empty(g.TakeChanges());
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-5f)]
    [InlineData(float.NaN)]
    public void DamageThatIsNotPositiveChangesNothing(float amount)
    {
        var g = new GlassPanes();
        Assert.False(g.Damage(Pane, amount, 60));
        Assert.Equal(0, g.DamagedCount);
    }

    [Fact]
    public void TheChangesOfATickAreOneStatePerPane()
    {
        var g = new GlassPanes();
        var other = new GlassPaneKey(7, 0);
        g.Damage(Pane, 5, 60); g.Damage(Pane, 5, 60); g.Damage(Pane, 5, 60); // a shotgun's pellets on one pane
        g.Damage(other, 80, 60);
        var changes = g.TakeChanges().OrderBy(c => c.Key.Owner).ToList();
        Assert.Equal(2, changes.Count);
        Assert.Equal(Pane, changes[0].Key);
        Assert.Equal(GlassRules.Quantise(45, 60), changes[0].Hp);
        Assert.True(changes[1].Shattered);
        Assert.Empty(g.TakeChanges()); // handed out once
    }

    [Fact]
    public void AJoinerGetsEveryPaneThatIsNotWhole()
    {
        var g = new GlassPanes();
        g.Damage(Pane, 10, 60);
        g.Damage(new GlassPaneKey(9, 2), 60, 60);
        g.TakeChanges();
        var damaged = g.Damaged();
        Assert.Equal(2, damaged.Count);
        Assert.Contains(damaged, s => s.Key == Pane && !s.Shattered && s.Hp < GlassRules.Whole);
        Assert.Contains(damaged, s => s.Key == new GlassPaneKey(9, 2) && s.Shattered);
    }

    [Fact]
    public void RestoringAVehicleLeavesTheOtherPanesAsTheyAre()
    {
        var g = new GlassPanes();
        g.Damage(new GlassPaneKey(9, 0), 60, 60);
        g.Damage(new GlassPaneKey(9, 1), 10, 60);
        g.Damage(Pane, 10, 60);
        Assert.True(g.Restore(9));
        Assert.False(g.IsShattered(new GlassPaneKey(9, 0)));
        Assert.Equal(60, g.Hp(new GlassPaneKey(9, 1), 60));
        Assert.Equal(50, g.Hp(Pane, 60));
        Assert.Equal(Pane, Assert.Single(g.TakeChanges()).Key); // the restored panes are not reported as changes: the restore is its own message
        Assert.False(g.Restore(9)); // nothing left to restore
    }

    [Fact]
    public void ANewRoundMakesEveryPaneWhole()
    {
        var g = new GlassPanes();
        Assert.False(g.RestoreAll());
        g.Damage(Pane, 60, 60);
        g.Damage(new GlassPaneKey(9, 0), 10, 60);
        Assert.True(g.RestoreAll());
        Assert.Equal(0, g.DamagedCount);
        Assert.Empty(g.TakeChanges());
        Assert.True(g.Damage(Pane, 10, 60)); // and it can be broken again
    }

    // ---- the hp byte and the crack stages ----

    [Fact]
    public void TheHpByteIsWholeOnlyAtFullHpAndZeroOnlyWhenShattered()
    {
        Assert.Equal(255, GlassRules.Quantise(60, 60));
        Assert.Equal(254, GlassRules.Quantise(59.99f, 60)); // any damage shows
        Assert.Equal(1, GlassRules.Quantise(0.001f, 60));   // a pane that stands is never sent as shattered
        Assert.Equal(0, GlassRules.Quantise(0, 60));
        Assert.Equal(128, GlassRules.Quantise(30, 60));
    }

    [Theory]
    [InlineData(1f, 0)]
    [InlineData(0.99f, 1)]
    [InlineData(0.67f, 1)]
    [InlineData(0.66f, 2)]
    [InlineData(0.5f, 2)]
    [InlineData(0.33f, 3)]
    [InlineData(0.01f, 3)]
    public void TheCrackStageFollowsTheHpFraction(float fraction, int stage)
    {
        Assert.Equal(stage, GlassRules.CrackStage(fraction, Stages));
        Assert.Equal(3, GlassRules.MaxCrackStage(Stages));
    }

    [Fact]
    public void ADamagedPaneIsCrackedOnTheClientWhateverTheRounding()
    {
        // the smallest damage the byte can carry is still below full, so the client shows stage 1
        var s = new GlassPaneState(Pane, GlassRules.Quantise(59.9f, 60));
        Assert.Equal(1, GlassRules.CrackStage(s.Fraction, Stages));
        Assert.False(s.Shattered);
        Assert.True(new GlassPaneState(Pane, GlassRules.Quantise(0, 60)).Shattered);
    }

    // ---- pane geometry ----

    static GlassPaneRect Facing(Vector3 normal, float w = 2, float h = 1) => GlassPaneRect.Make(new Vector3(0, 1.5f, 0), normal, w, h);

    [Fact]
    public void TheAxesOfAPaneAreItsBasis()
    {
        var r = Facing(new Vector3(0, 0, 3)); // any length
        Assert.Equal(new Vector3(0, 0, 1), r.Normal);
        Assert.Equal(new Vector3(1, 0, 0), r.Right);
        Assert.Equal(new Vector3(0, 1, 0), r.Up);
        var slanted = Facing(new Vector3(0, 0.625f, -0.781f)); // a windscreen
        Assert.True(Vector3.Distance(Vector3.Cross(slanted.Right, slanted.Up), slanted.Normal) < 1e-5f);
        Assert.Equal(0, slanted.Right.Y, 5);  // the width is horizontal
        Assert.True(slanted.Up.Y > 0);        // the height runs up the pane
        var flat = Facing(Vector3.UnitY);     // a roof light
        Assert.Equal(Vector3.UnitX, flat.Right);
        Assert.True(Vector3.Distance(Vector3.Cross(flat.Right, flat.Up), flat.Normal) < 1e-5f);
    }

    [Fact]
    public void ARayCrossesAPaneOnlyFromOutsideAndInsideItsRectangle()
    {
        var r = Facing(Vector3.UnitZ);
        Assert.Equal(5f, r.Cross(new Vector3(0, 1.5f, 5), -Vector3.UnitZ)!.Value, 4);
        Assert.Equal(5f, r.Cross(new Vector3(0.99f, 1.99f, 5), -Vector3.UnitZ)!.Value, 4); // just inside the corner
        Assert.Null(r.Cross(new Vector3(1.01f, 1.5f, 5), -Vector3.UnitZ));  // past the side
        Assert.Null(r.Cross(new Vector3(0, 2.01f, 5), -Vector3.UnitZ));     // over the top
        Assert.Null(r.Cross(new Vector3(0, 1.5f, -5), Vector3.UnitZ));      // from the inside
        Assert.Null(r.Cross(new Vector3(0, 1.5f, 5), Vector3.UnitZ));       // pointing away
        Assert.Null(r.Cross(new Vector3(0, 1.5f, 5), Vector3.UnitX));       // along the pane
    }

    [Fact]
    public void TheFirstPaneCrossedInTheWindowIsTheOneHit()
    {
        var panes = new[]
        {
            GlassPaneRect.Make(new Vector3(0, 1, -10), Vector3.UnitZ, 2, 2),
            GlassPaneRect.Make(new Vector3(0, 1, -6), Vector3.UnitZ, 2, 2),
            GlassPaneRect.Make(new Vector3(5, 1, -6), Vector3.UnitZ, 2, 2),
        };
        var from = new Vector3(0, 1, 0); var dir = -Vector3.UnitZ;
        Assert.Equal(1, GlassRules.FirstCrossed(panes, from, dir, 0, 100));
        Assert.Equal(0, GlassRules.FirstCrossed(panes, from, dir, 0, 100, i => i == 1)); // the nearer one is unbreakable
        Assert.Equal(0, GlassRules.FirstCrossed(panes, from, dir, 9.9f, 10.5f));         // the bullet stopped at 10 m: only a pane there counts
        Assert.Equal(-1, GlassRules.FirstCrossed(panes, from, dir, 7, 9));               // it stopped on a wall between the two
        Assert.Equal(-1, GlassRules.FirstCrossed(panes, new Vector3(0, 5, 0), dir, 0, 100));
    }

    [Fact]
    public void ABuildingPaneBehindItsWallIsHitByTheBulletThatStoppedOnTheWall()
    {
        // the collision wall is the plain massing; the pane sits 0.15 m behind it in its reveal
        var cfg = new GlassConfig();
        var pane = new[] { GlassPaneRect.Make(new Vector3(0, 1.5f, -10.15f), Vector3.UnitZ, 1.4f, 1.2f) };
        var wall = 10f;
        Assert.Equal(0, GlassRules.FirstCrossed(pane, new Vector3(0.3f, 1.6f, 0), -Vector3.UnitZ, wall - 0.1f, wall + cfg.BuildingReach));
        Assert.Equal(-1, GlassRules.FirstCrossed(pane, new Vector3(0.3f, 1.6f, 0), -Vector3.UnitZ, 4 - 0.1f, 4 + cfg.BuildingReach)); // a fence in front took the bullet
        Assert.Equal(-1, GlassRules.FirstCrossed(pane, new Vector3(1.5f, 1.6f, 0), -Vector3.UnitZ, wall - 0.1f, wall + cfg.BuildingReach)); // the wall beside the window
    }

    [Fact]
    public void TheVehicleFrameIsTheHitBoxFrame()
    {
        var feet = new Vector3(10, 2, -4);
        const float yaw = 0.7f;
        var local = new Vector3(0.9f, 1.4f, -1.2f);
        var world = GlassRules.FromVehicle(local, feet, yaw);
        var back = GlassRules.ToVehicle(world, feet, yaw);
        Assert.True(Vector3.Distance(local, back) < 1e-4f);

        // a ray that VehicleBox reports entering the box at a local point gives the same point through this frame
        var origin = feet + new Vector3(6, 1.2f, 3); var dir = Vector3.Normalize(feet + new Vector3(0, 1, 0) - origin);
        var hit = VehicleBox.Ray(origin, dir, 100, feet, yaw, 1.28f, 2.18f, 2.2f)!.Value;
        var lo = GlassRules.ToVehicle(origin, feet, yaw); var ld = GlassRules.DirToVehicle(dir, yaw);
        Assert.True(Vector3.Distance(hit.Local, lo + ld * hit.Distance) < 1e-3f);
    }

    [Fact]
    public void AShotAtAYawedCarsSideWindowFindsThatPane()
    {
        var car = Shipped().Vehicles.First(v => v.Id == "car");
        var rects = car.GlassRects;
        var right = car.Glass.FindIndex(g => g.Id == "side_right");
        var feet = new Vector3(40, 0, 15); const float yaw = 1.1f;
        var target = GlassRules.FromVehicle(rects[right].Centre, feet, yaw);
        var origin = GlassRules.FromVehicle(new Vector3(8, 1.7f, -0.24f), feet, yaw); // level with the window, off to the right
        var dir = Vector3.Normalize(target - origin);
        var wall = VehicleBox.Ray(origin, dir, 100, feet, yaw, car.HalfWidth, car.HalfLength, car.Height)!.Value.Distance;
        var crossed = GlassRules.FirstCrossed(rects, GlassRules.ToVehicle(origin, feet, yaw), GlassRules.DirToVehicle(dir, yaw), wall - 0.1f, wall + new GlassConfig().VehicleReach);
        Assert.Equal(right, crossed);
        // the same shot at the door below the window crosses no pane
        var low = Vector3.Normalize(GlassRules.FromVehicle(new Vector3(0.867f, 0.8f, -0.24f), feet, yaw) - (origin with { Y = 0.8f }));
        var o2 = origin with { Y = 0.8f };
        var wall2 = VehicleBox.Ray(o2, low, 100, feet, yaw, car.HalfWidth, car.HalfLength, car.Height)!.Value.Distance;
        Assert.Equal(-1, GlassRules.FirstCrossed(rects, GlassRules.ToVehicle(o2, feet, yaw), GlassRules.DirToVehicle(low, yaw), wall2 - 0.1f, wall2 + new GlassConfig().VehicleReach));
    }

    [Theory]
    [InlineData(0f, 150f)]
    [InlineData(4.5f, 75f)]
    [InlineData(9f, 0f)]
    [InlineData(20f, 0f)]
    public void ABlastHurtsAPaneLessWithDistanceAndNotBeyondItsRadius(float distance, float expected) =>
        Assert.Equal(expected, GlassRules.BlastDamage(distance, 9, 150), 3);

    [Fact]
    public void ABlastWithoutARadiusHurtsNoGlass() => Assert.Equal(0, GlassRules.BlastDamage(0, 0, 150));

    // ---- wire ----

    [Fact]
    public void APaneStateRoundTrips()
    {
        var s = new GlassPaneState(new GlassPaneKey(4099, 15), 77);
        var bytes = GlassCodec.EncodePane(s);
        Assert.Equal(5, bytes.Length);
        Assert.Equal(s, GlassCodec.DecodePane(bytes));
        Assert.Null(GlassCodec.DecodePane(bytes.AsSpan(0, 4)));
        Assert.Null(GlassCodec.DecodePane(new byte[6]));
    }

    [Fact]
    public void ASyncRoundTripsWithItsScopeAndPanes()
    {
        var panes = new[] { new GlassPaneState(Pane, 0), new GlassPaneState(new GlassPaneKey(4100, 2), 200) };
        var m = GlassCodec.DecodeSync(GlassCodec.EncodeSync(GlassCodec.Everything, panes))!;
        Assert.Equal(GlassCodec.Everything, m.Scope);
        Assert.Equal(panes, m.Panes);
        var empty = GlassCodec.DecodeSync(GlassCodec.EncodeSync(4100, Array.Empty<GlassPaneState>()))!; // a vehicle that returned
        Assert.Equal(4100, empty.Scope);
        Assert.Empty(empty.Panes);
    }

    [Fact]
    public void AMalformedSyncIsRefused()
    {
        var bytes = GlassCodec.EncodeSync(0, new[] { new GlassPaneState(Pane, 9) });
        Assert.Null(GlassCodec.DecodeSync(bytes.AsSpan(0, bytes.Length - 1))); // a pane cut short
        Assert.Null(GlassCodec.DecodeSync(new byte[3]));
        bytes[2] = 2; // claims two panes, carries one
        Assert.Null(GlassCodec.DecodeSync(bytes));
    }

    [Fact]
    public void TheGlassEventKindsHaveTheirOwnRange()
    {
        Assert.Equal(170, (int)EventKind.GlassPane);
        Assert.Equal(171, (int)EventKind.GlassSync);
        Assert.True(Wire.ProtocolVersion >= 22);
    }

    // ---- the client's copy ----

    [Fact]
    public void TheClientKeepsWhatTheServerSentAndKnowsWhatItHadBefore()
    {
        var c = new GlassReplica();
        Assert.Equal(GlassRules.Whole, c.Hp(Pane));
        Assert.Equal(GlassRules.Whole, c.Set(new GlassPaneState(Pane, 120)));
        Assert.Equal(120, c.Set(new GlassPaneState(Pane, 0)));  // it was standing: this is the moment it shatters
        Assert.Equal(0, c.Set(new GlassPaneState(Pane, 0)));    // a repeat is not a second shatter
        Assert.Equal(0, c.Hp(Pane));
    }

    [Fact]
    public void ASyncScopeResetsOnlyItsOwnPanes()
    {
        var c = new GlassReplica();
        c.Set(new GlassPaneState(Pane, 10));
        c.Set(new GlassPaneState(new GlassPaneKey(9, 0), 0));
        c.Set(new GlassPaneState(new GlassPaneKey(9, 3), 50));
        Assert.Equal(2, c.Of(9).Count);
        var gone = c.Reset(9);
        Assert.Equal(2, gone.Count);
        Assert.All(gone, k => Assert.Equal(9, k.Owner));
        Assert.Equal(GlassRules.Whole, c.Hp(new GlassPaneKey(9, 0)));
        Assert.Equal(10, c.Hp(Pane));
        Assert.Single(c.Reset(GlassCodec.Everything));
        Assert.Equal(GlassRules.Whole, c.Hp(Pane));
    }

    [Fact]
    public void TheServersChangesBringAClientToTheSameState()
    {
        var server = new GlassPanes();
        var client = new GlassReplica();
        var a = new GlassPaneKey(9, 0); var b = new GlassPaneKey(GlassPaneKey.Map, 12);
        void Tick() { foreach (var s in server.TakeChanges()) client.Set(GlassCodec.DecodePane(GlassCodec.EncodePane(s))!.Value); }
        server.Damage(a, 20, 60); server.Damage(b, 70, 60); Tick();
        server.Damage(a, 20, 60); Tick();
        Assert.Equal(GlassRules.Quantise(20, 60), client.Hp(a));
        Assert.Equal(0, client.Hp(b));

        // a late joiner gets the same from the catch-up
        var joiner = new GlassReplica();
        var sync = GlassCodec.DecodeSync(GlassCodec.EncodeSync(GlassCodec.Everything, server.Damaged()))!;
        joiner.Reset(sync.Scope);
        foreach (var s in sync.Panes) joiner.Set(s);
        Assert.Equal(client.Hp(a), joiner.Hp(a));
        Assert.Equal(client.Hp(b), joiner.Hp(b));
    }

    // ---- the map's building panes ----

    const string Windows = """
        {"version":1,"windows":[
          {"breakable":false,"building":"w2","centre":[10,20,150.5],"height_m":1.2,"id":"b","node":"window_w2_b","normal":[0,1],"width_m":0.9},
          {"building":"w1","centre":[-183.792,238.392,190.15],"height_m":2.4,"id":"f1","node":"window_w1_f1","normal":[0.6725,0.7401],"width_m":1.4}
        ]}
        """;

    [Fact]
    public void TheMapsPanesComeInNodeOrderInTheGamesAxes()
    {
        var panes = MapGlass.Parse(Windows);
        Assert.Equal(new[] { "window_w1_f1", "window_w2_b" }, panes.Select(p => p.Node));
        Assert.True(panes[0].Breakable);   // the default
        Assert.False(panes[1].Breakable);
        // map (east, north, up) is the game's (x, up, -north)
        Assert.Equal(new Vector3(10, 150.5f, -20), panes[1].Rect.Centre);
        Assert.Equal(new Vector3(0, 0, -1), panes[1].Rect.Normal);
        Assert.Equal(0.9f, panes[1].Rect.Width);
        Assert.Equal(1.2f, panes[1].Rect.Height);
        Assert.Equal(Vector3.UnitY, panes[1].Rect.Up);
        Assert.Equal(1f, panes[0].Rect.Normal.Length(), 4);
    }

    [Fact]
    public void AMapWithoutWindowsHasNoPanes()
    {
        Assert.Empty(MapGlass.Parse(""));
        Assert.Empty(MapGlass.Parse("{\"version\":1,\"windows\":[]}"));
    }

    [Theory]
    [InlineData("{\"windows\":[{\"node\":\"w\",\"centre\":[1,2],\"normal\":[0,1],\"width_m\":1,\"height_m\":1}]}")]
    [InlineData("{\"windows\":[{\"node\":\"w\",\"centre\":[1,2,3],\"normal\":[0,0],\"width_m\":1,\"height_m\":1}]}")]
    [InlineData("{\"windows\":[{\"node\":\"w\",\"centre\":[1,2,3],\"normal\":[0,1],\"width_m\":0,\"height_m\":1}]}")]
    [InlineData("{\"panes\":[]}")]
    [InlineData("not json")]
    public void ABrokenWindowsFileIsADataFileError(string json) => Assert.Throws<DataFileException>(() => MapGlass.Parse(json));

    // ---- the fragments ----

    [Theory]
    [InlineData(1.4f, 2.4f, 28)]
    [InlineData(2.07f, 1.57f, 28)]
    [InlineData(0.56f, 1.7f, 12)]
    [InlineData(1f, 1f, 2)]
    public void TheFragmentsTileThePane(float w, float h, int target)
    {
        var shards = GlassShards.Cut(w, h, target, 42);
        Assert.Equal(w * h, shards.Sum(s => s.Area), 3);
        Assert.InRange(shards.Count, Math.Max(2, target / 2), target * 2);
        Assert.All(shards, s =>
        {
            Assert.True(s.Area > 0);
            foreach (var p in new[] { s.A, s.B, s.C })
            {
                Assert.InRange(p.X, -w / 2 - 1e-4f, w / 2 + 1e-4f);
                Assert.InRange(p.Y, -h / 2 - 1e-4f, h / 2 + 1e-4f);
            }
        });
    }

    [Fact]
    public void TheSameSeedCutsTheSameFragments()
    {
        Assert.Equal(GlassShards.Cut(1.4f, 2.4f, 28, 7), GlassShards.Cut(1.4f, 2.4f, 28, 7));
        Assert.NotEqual(GlassShards.Cut(1.4f, 2.4f, 28, 7), GlassShards.Cut(1.4f, 2.4f, 28, 8));
    }

    static readonly Vector2[] Trapezoid = { new(0.6f, -0.4f), new(0.6f, 0.4f), new(-0.2f, 0.4f), new(-0.6f, -0.4f) }; // counter-clockwise, in a 1.2 x 0.8 box
    static readonly Vector2[] Triangle = { new(-0.5f, -0.5f), new(0.5f, -0.5f), new(-0.5f, 0.5f) };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TheFragmentsOfAnOutlinedPaneTileItsOutlineExactly(int which)
    {
        var outline = which == 0 ? Trapezoid : Triangle;
        var (w, h) = which == 0 ? (1.2f, 0.8f) : (1f, 1f);
        var shards = GlassShards.Cut(w, h, 28, 42, outline);
        Assert.Equal(GlassShards.Area(outline), shards.Sum(s => s.Area), 3);
        Assert.NotEmpty(shards);
        Assert.All(shards, s =>
        {
            Assert.True(s.Area > 0);
            foreach (var p in new[] { s.A, s.B, s.C })
                for (var i = 0; i < outline.Length; i++)
                {
                    Vector2 a = outline[i], b = outline[(i + 1) % outline.Length];
                    Assert.True((b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X) >= -1e-4f, "a fragment lies outside the outline");
                }
        });
        Assert.InRange(shards.Count, 8, 80); // a pane that fills less of its box still breaks into about as many fragments
    }

    [Fact]
    public void AnOutlineOfTheWholeRectangleCutsLikeNoOutline()
    {
        var box = new[] { new Vector2(-0.7f, -1.2f), new Vector2(0.7f, -1.2f), new Vector2(0.7f, 1.2f), new Vector2(-0.7f, 1.2f) };
        Assert.Equal(1.4f * 2.4f, GlassShards.Cut(1.4f, 2.4f, 28, 7, box).Sum(s => s.Area), 3);
        Assert.Equal(GlassShards.Cut(1.4f, 2.4f, 28, 7).Count, GlassShards.Cut(1.4f, 2.4f, 28, 7, box).Count);
    }

    // ---- outlines ----

    [Fact]
    public void AShotMissesTheCornersOfTheRectangleOutsideTheOutline()
    {
        var pane = GlassPaneRect.Make(new Vector3(0, 1, 0), new Vector3(0, 0, 1), 1.2f, 0.8f, Trapezoid);
        var dir = new Vector3(0, 0, -1);
        Assert.NotNull(pane.Cross(new Vector3(0.3f, 1.1f, 5), dir));    // inside
        Assert.NotNull(pane.Cross(new Vector3(0.55f, 1.35f, 5), dir));  // near the long edge, inside
        Assert.Null(pane.Cross(new Vector3(-0.5f, 1.35f, 5), dir));     // the cut top left corner: in the rectangle, outside the pane
        Assert.Null(pane.Cross(new Vector3(0.7f, 1.0f, 5), dir));       // outside the rectangle
        var plain = GlassPaneRect.Make(new Vector3(0, 1, 0), new Vector3(0, 0, 1), 1.2f, 0.8f);
        Assert.NotNull(plain.Cross(new Vector3(-0.5f, 1.35f, 5), dir)); // without an outline the rectangle is the pane
    }

    [Fact]
    public void AnOutlineParsesAndTravelsWithTheRectangle()
    {
        var v = ParseGlass("[ { \"id\": \"a\", \"centre\": [0, 1, 0], \"normal\": [0, 0, 1], \"size\": [1.2, 0.8], \"outline\": [[0.6, -0.4], [0.6, 0.4], [-0.2, 0.4], [-0.6, -0.4]] }, { \"id\": \"b\", \"centre\": [0, 1, 1], \"normal\": [0, 0, 1], \"size\": [1, 1] } ]").Vehicles[0];
        Assert.Equal(4, v.GlassRects[0].Outline!.Count);
        Assert.Equal(new Vector2(-0.2f, 0.4f), v.GlassRects[0].Outline![2]);
        Assert.Null(v.GlassRects[1].Outline);
    }

    [Theory]
    [InlineData("[[0, 0], [1, 0]]")]                                        // two points
    [InlineData("[[0.6, -0.4], [-0.6, -0.4], [-0.6, 0.4], [0.6, 0.4]]")]    // clockwise
    [InlineData("[[0.6, -0.4], [0.2, 0], [0.6, 0.4], [-0.6, 0.4], [-0.6, -0.4]]")] // concave
    [InlineData("[[0.9, -0.4], [0.9, 0.4], [-0.6, 0.4], [-0.6, -0.4]]")]    // outside the size box
    [InlineData("[[0, 0], [0.1, 0.1], [0.2, 0.2]]")]                        // no area
    [InlineData("[[0.6, -0.4], [0.6]]")]                                    // not [u, v]
    public void ABadOutlineIsRefused(string outline) =>
        Assert.Throws<DataFileException>(() => ParseGlass("[ { \"id\": \"a\", \"centre\": [0, 1, 0], \"normal\": [0, 0, 1], \"size\": [1.2, 0.8], \"outline\": " + outline + " } ]"));

    [Fact]
    public void TheGeneratedVehiclesHaveAnOutlineOnEveryPaneAndTheirCountsMatchTheModels()
    {
        var counts = new Dictionary<string, int> { ["bus"] = 18, ["flak"] = 6, ["utility_helicopter"] = 4 };
        foreach (var v in Shipped().Vehicles.Where(v => counts.ContainsKey(v.Id)))
        {
            Assert.Equal(counts[v.Id], v.Glass.Count);
            Assert.All(v.Glass, g => Assert.NotNull(g.Outline));
            Assert.All(v.GlassRects, r => Assert.True(GlassShards.Area(r.Outline!) <= r.Width * r.Height + 1e-3f));
        }
        Assert.Equal(counts.Count, Shipped().Vehicles.Count(v => counts.ContainsKey(v.Id)));
    }

    // ---- data files ----

    static VehiclesFile Shipped() => VehiclesFile.Load(Path.Combine(AppContext.BaseDirectory, "data", "vehicles.json"));

    static VehiclesFile ParseGlass(string glass)
    {
        var json = "{ \"version\": 1, \"vehicles\": [ { \"id\": \"t\", \"top_speed\": 20, \"acceleration\": 3, \"brake\": 8, \"turn_radius\": 4, \"max_lateral_accel\": 8, \"hp\": 100, \"spawn_count\": 1, \"glass\": " + glass + " } ] }";
        return VehiclesFile.Validate(DataFile.Parse<VehiclesFile>(json, "test"));
    }

    [Fact]
    public void TheShippedGlassFileParsesAndMatchesTheDefaults()
    {
        var shipped = GlassConfig.Load(Path.Combine(AppContext.BaseDirectory, "data", "glass.json"));
        var defaults = new GlassConfig();
        Assert.Equal(defaults.BuildingPaneHp, shipped.BuildingPaneHp);
        Assert.Equal(defaults.VehiclePaneHp, shipped.VehiclePaneHp);
        Assert.Equal(defaults.CrackStages, shipped.CrackStages);
        Assert.Equal(defaults.BlastDamage, shipped.BlastDamage);
        Assert.Equal(defaults.ShatteredExposesRiders, shipped.ShatteredExposesRiders);
        Assert.Equal(defaults.RestoreOnRoundStart, shipped.RestoreOnRoundStart);
        Assert.Equal(defaults.Shards, shipped.Shards);
    }

    [Theory]
    [InlineData("\"building_pane_hp\": 0")]
    [InlineData("\"vehicle_pane_hp\": -1")]
    [InlineData("\"crack_stages\": [0.3, 0.6]")]
    [InlineData("\"crack_stages\": [1.0]")]
    [InlineData("\"crack_stages\": [0.9, 0.8, 0.7, 0.6, 0.5, 0.4, 0.3]")]
    [InlineData("\"blast_damage\": -1")]
    [InlineData("\"shards\": 1")]
    [InlineData("\"shard_life\": 0")]
    public void ABadGlassFileIsRefused(string field) =>
        Assert.Throws<DataFileException>(() => GlassConfig.Parse("{ \"version\": 1, " + field + " }"));

    [Fact]
    public void GameDataTakesTheGlassFileOrTheDefaults()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "data");
        Assert.Equal(60, GameData.Load(dir).Glass.BuildingPaneHp);
        Assert.Equal(25, GlassConfig.Parse("{ \"version\": 1, \"building_pane_hp\": 25 }").BuildingPaneHp);
        Assert.Equal(new GlassConfig().VehiclePaneHp, GlassConfig.Parse("{ \"version\": 1 }").VehiclePaneHp);
    }

    [Fact]
    public void AVehiclesGlassBlockParsesIntoRectangles()
    {
        var v = ParseGlass("[ { \"id\": \"windscreen\", \"centre\": [0, 1.7, -0.6], \"normal\": [0, 0.6, -0.8], \"size\": [1.4, 0.9], \"hp\": 90 }, { \"id\": \"rear\", \"centre\": [0, 1.7, 1.1], \"normal\": [0, 0, 2], \"size\": [1.4, 0.5] } ]").Vehicles[0];
        Assert.Equal(2, v.GlassRects.Count);
        Assert.Equal(90, v.Glass[0].Hp);
        Assert.Null(v.Glass[1].Hp); // takes glass.json vehicle_pane_hp
        Assert.Equal(new Vector3(0, 0, 1), v.GlassRects[1].Normal);
        Assert.Equal(1.4f, v.GlassRects[0].Width);
        Assert.Equal(0.9f, v.GlassRects[0].Height);
    }

    [Fact]
    public void AVehicleWithoutAGlassBlockHasNoPanes()
    {
        var json = "{ \"version\": 1, \"vehicles\": [ { \"id\": \"t\", \"top_speed\": 20, \"acceleration\": 3, \"brake\": 8, \"turn_radius\": 4, \"max_lateral_accel\": 8, \"hp\": 100, \"spawn_count\": 1 } ] }";
        Assert.Empty(VehiclesFile.Validate(DataFile.Parse<VehiclesFile>(json, "test")).Vehicles[0].GlassRects);
    }

    [Theory]
    [InlineData("[ { \"id\": \"a\", \"centre\": [0, 1], \"normal\": [0, 0, 1], \"size\": [1, 1] } ]")]
    [InlineData("[ { \"id\": \"a\", \"centre\": [0, 1, 0], \"normal\": [0, 0, 0], \"size\": [1, 1] } ]")]
    [InlineData("[ { \"id\": \"a\", \"centre\": [0, 1, 0], \"normal\": [0, 0, 1], \"size\": [1, 0] } ]")]
    [InlineData("[ { \"id\": \"a\", \"centre\": [0, 1, 0], \"normal\": [0, 0, 1], \"size\": [1, 1], \"hp\": 0 } ]")]
    [InlineData("[ { \"id\": \"a\", \"centre\": [0, 1, 0], \"normal\": [0, 0, 1], \"size\": [1, 1] }, { \"id\": \"a\", \"centre\": [0, 1, 0], \"normal\": [0, 0, -1], \"size\": [1, 1] } ]")]
    public void ABadGlassPaneIsRefused(string glass) => Assert.Throws<DataFileException>(() => ParseGlass(glass));

    [Fact]
    public void MoreThanTwentyFourPanesAreRefused()
    {
        var panes = string.Join(", ", Enumerable.Range(0, 25).Select(i => "{ \"id\": \"p" + i + "\", \"centre\": [0, 1, " + i + "], \"normal\": [1, 0, 0], \"size\": [1, 1] }"));
        Assert.Throws<DataFileException>(() => ParseGlass("[ " + panes + " ]"));
    }

    [Fact]
    public void TheShippedPanesLieInsideTheirVehiclesHitBoxAndFaceOutwards()
    {
        var withGlass = Shipped().Vehicles.Where(v => v.Glass.Count > 0).ToList();
        Assert.Contains(withGlass, v => v.Id == "car");
        Assert.Contains(withGlass, v => v.Id == "bus");
        foreach (var v in withGlass)
        foreach (var (g, r) in v.Glass.Zip(v.GlassRects))
        {
            // the server only looks for a pane where a bullet stopped on the hit box, so a pane outside it could never be hit
            Assert.True(MathF.Abs(r.Centre.X) <= v.HalfWidth + 0.01f && MathF.Abs(r.Centre.Z) <= v.HalfLength + 0.01f && r.Centre.Y > 0 && r.Centre.Y <= v.Height, $"{v.Id} {g.Id} is outside the hit box");
            // a pane faces away from the middle of the vehicle (a partition inside would not)
            var fromMiddle = r.Centre - new Vector3(0, r.Centre.Y, 0);
            Assert.True(Vector3.Dot(r.Normal, fromMiddle) > 0 || r.Normal.Y > 0.5f, $"{v.Id} {g.Id} faces inwards");
            // and it is within reach of a bullet that entered the box in front of it
            // (aimed at the middle of the outline, which is on the pane even when the middle of the rectangle is not)
            var aim = r.Outline is { } o ? new Vector2(o.Average(p => p.X), o.Average(p => p.Y)) : Vector2.Zero;
            var origin = r.Centre + r.Right * aim.X + r.Up * aim.Y + r.Normal * 20;
            var wall = VehicleBox.Ray(origin, -r.Normal, 100, Vector3.Zero, 0, v.HalfWidth, v.HalfLength, v.Height);
            Assert.True(wall is { } w && GlassRules.FirstCrossed(v.GlassRects, origin, -r.Normal, w.Distance - 0.1f, w.Distance + new GlassConfig().VehicleReach) >= 0, $"{v.Id} {g.Id} is out of a bullet's reach");
        }
    }
}
